using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using PadForge.Common.Input;
using PadForge.Common.Input.Peripherals;
using PadForge.Common.Input.Peripherals.HidFamilies;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>Writes each plugin row's frames for tools/verify_plugin_rows.py, which holds them
    /// against what the plugins send when run. A row that asks its device what it is gets a line for
    /// each answer that changes its frames. A light with a place shows a color made of that place,
    /// so a light on the wrong LED shows in the frames. Does nothing unless the release prep names a
    /// file in PADFORGE_PLUGIN_ROWS_TSV.</summary>
    public class PluginRowFramesDump
    {
        private const int Shown = 0x112233;

        private static int Shade(LightInfo light, int rank) => light.HasPosition
            ? ((int)light.X & 0xFF) << 16 | ((int)light.Y & 0xFF) << 8 | 0x55
            : (0x10 + rank) << 16 | 0x20 << 8 | (0x30 + rank);

        private static string Steps(IEnumerable<PluginStep> steps)
            => string.Join(";", (steps ?? Array.Empty<PluginStep>()).Where(s => s.Bytes != null)
                .Select(s => s.Kind.ToString()[0] + ":" + BitConverter.ToString(s.Bytes).Replace("-", "")));

        private static byte[] Answer(int length, params (int At, byte[] Bytes)[] parts)
        {
            var answer = new byte[length];
            foreach (var (at, bytes) in parts) bytes.CopyTo(answer, at);
            return answer;
        }

        private static byte[] Ascii(string text) => Encoding.ASCII.GetBytes(text);

        private static FakeReportChannel Channel(PluginDevice d, string product = null, Func<byte[]> input = null)
            => new()
            {
                OutputReportLength = (ushort)d.OutputLength,
                FeatureReportLength = (ushort)d.FeatureLength,
                InputReportLength = 65,
                ProductString = product,
                Input = input ?? (() => null),
            };

        /// <summary>The answers a row tells apart, each with the channel that gives it and its
        /// name in the file, which the runner gives the plugin too.</summary>
        private static IEnumerable<(string Model, FakeReportChannel Io, IHidReportChannel[] Siblings)> Askings(PluginDevice d)
        {
            var none = Array.Empty<IHidReportChannel>();
            string file = d.Plugin.Split('/').Last();
            IEnumerable<(string, FakeReportChannel, IHidReportChannel[])> ByProductString(IReadOnlyList<PluginBoard> boards)
                => boards.Select(b => (b.Model, Channel(d, b.Model), none));
            switch (file)
            {
                case "SONIX_Keyboard_Controller_2.js": return ByProductString(PluginLibraryTables.Sonix2);
                case "EVision_Keyboard_Controller.js": return ByProductString(PluginLibraryTables.EVision);
                case "BYTech_Keyboard_Controller.js": return ByProductString(PluginLibraryTables.ByTech);
                case "Nuvoton_Keyboard_Controller.js":
                    return PluginLibraryTables.Nuvoton.Select(b => (b.Model, Channel(d, input: () => Answer(64, (0, Ascii($"x,x,x,x,{b.Model},1,")))), none));
                case "Xiaohua_Keyboard_Controller.js":
                    return PluginLibraryTables.Xiaohua.Select(b => (b.Model, Channel(d, input: () => Answer(64, (0, Ascii($"x,x,x,x,{b.Model},1,")))), none));
                case "Nuvoton_Keyboard2_Controller.js":
                    return PluginLibraryTables.Nuvoton2.Select(b => (b.Model, Channel(d, input: () => Answer(32, (4, Ascii(b.Model)))), none));
                case "Sinowealth_Keyboard_Controller2.js":
                    return PluginLibraryTables.Sinowealth2.Select(b =>
                    {
                        int model = int.Parse(b.Model);
                        var query = new FakeReportChannel { FeatureReportLength = 8, Feature = _ => Answer(8, (6, new[] { (byte)(model / 255), (byte)(model % 255) })) };
                        return (b.Model, Channel(d), new IHidReportChannel[] { query });
                    });
                case "ASUS_ROG_Claymore_II.js":
                    return new[] { ("Right", (byte)0x01), ("Left", (byte)0x00) }.Select(side =>
                        (side.Item1, Channel(d, input: () => Answer(65, (17, new[] { side.Item2 }))), none));
                case "NZXT_Function_Keyboard.js":
                    return new[] { ("old", new byte[] { 1, 3, 70 }), ("new", new byte[] { 1, 3, 71 }) }.Select(version =>
                        (version.Item1, Channel(d, input: () => Answer(64, (3, version.Item2))), none));
                case "ASUS_Omni_Device.js":
                    return d.UsagePage == 0xFF00
                        ? PluginLibraryTables.AsusOmniKeyboard.Select(b => (b.Model, Channel(d, input: () => Answer(64, (5, Ascii(b.Model)))), none))
                        : new[] { "R3MPGDD21800", "SAMPGDD00EAH", "R9MPGDD81805", "E503FF3290C8" }.Select(serial =>
                            (serial, Channel(d, input: () => Answer(64, (5, Ascii(serial)))), none));
            }
            var board = PluginQmkTables.Boards.FirstOrDefault(b => b.Plugin == d.Plugin && b.Variants != null);
            if (board != null)
                return board.Variants.Select(v => v.Unique ?? (byte)0).Select(unique =>
                    (unique.ToString(), Channel(d, input: () => Answer(33, (0, new byte[] { 0x00, 0x23, 0x01, 0x02, unique }))), none));
            return new[] { ("", Channel(d), none) };
        }

        private static string Row(PluginDevice d, string model, string picture, string note, int stripLeds, string shading = "flat", bool holds = false)
        {
            string start = string.Join(";", new[] { Steps(d.SiblingStart), Steps(d.Start) }.Where(s => s.Length > 0));
            return $"{d.ProductId:X4}\t{d.VendorId:X4}\t{d.Plugin}\t{d.Source}\t{start}\t{picture}\t{note}\t{(d.AskVariant != null ? 1 : 0)}\t{d.StripMaxLeds.Count}\t{(d.Answers != null ? 1 : 0)}\t{model}\t{stripLeds}\t{(holds ? 0 : PluginDeviceLighting.PictureAgainMsOf(d))}\t{shading}";
        }

        private static string StripRow(PluginDevice d)
        {
            int count = d.StripMaxLeds.Count;
            var byAddress = new int[d.Slots];
            for (int s = 0; s < count; s++)
                for (int l = 0; l < d.StripDefaultLeds; l++) byAddress[s * PluginDeviceLighting.StripSpan + l] = Shown;
            var leds = Enumerable.Repeat(d.StripDefaultLeds, count).ToArray();
            return Row(d, "", Steps(d.StripPicture(byAddress, leds)), "", d.StripDefaultLeds);
        }

        private static string AskedRow(PluginDevice d, string model, FakeReportChannel io, IHidReportChannel[] siblings)
        {
            try
            {
                IReadOnlyList<LightInfo> lights = d.Lights;
                int slots = d.Slots;
                var draw = d.Picture;
                bool holds = false;
                if (d.AskVariant != null)
                {
                    var variant = d.AskVariant(io, siblings, _ => { });
                    if (variant != null)
                    {
                        lights = variant.Lights;
                        slots = variant.Slots;
                        if (variant.Picture != null) draw = variant.Picture;
                        holds = variant.PictureHolds;
                    }
                }
                if (draw == null || slots <= 0) return Row(d, model, "", "nopicture", 0);
                // A key table from the firmware numbers its LEDs its own way, so all that is left
                // to hold against the plugin is the command. The Claymore's left layout moves the
                // places with the number pad, and its keys go by where the lists have them.
                bool firmware = PluginQmkTables.Boards.Any(b => b.Firmware != null && b.Plugin == d.Plugin && b.Products.Contains(d.ProductId));
                bool byRank = model == "Left" || !lights.Any(l => l.HasPosition);
                var byAddress = new int[slots];
                for (int rank = 0; rank < lights.Count; rank++)
                {
                    var light = lights[rank];
                    if (light.Address < 0 || light.Address >= slots) continue;
                    byAddress[light.Address] = firmware ? Shown : byRank ? Shade(light with { HasPosition = false }, rank) : Shade(light, rank);
                }
                return Row(d, model, Steps(draw(byAddress)), "", 0, firmware ? "firmware" : byRank ? "rank" : "place", holds);
            }
            catch (Exception e) { return Row(d, model, "", e.GetType().Name, 0); }
        }

        [Fact]
        public void WriteTheRows()
        {
            string path = Environment.GetEnvironmentVariable("PADFORGE_PLUGIN_ROWS_TSV");
            if (string.IsNullOrEmpty(path)) return;
            var lines = new List<string>();
            foreach (var d in PluginDeviceLighting.Devices)
            {
                if (d.StripMaxLeds.Count > 0) lines.Add(StripRow(d));
                else foreach (var (model, io, siblings) in Askings(d)) lines.Add(AskedRow(d, model, io, siblings));
            }
            File.WriteAllLines(path, lines);
        }
    }
}
