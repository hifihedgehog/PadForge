using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PadForge.Common.Input;
using PadForge.Common.Input.Peripherals;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>Writes each plugin row's frames for tools/verify_plugin_rows.py, which holds them
    /// against what the plugins send when run. Does nothing unless the release prep names a file
    /// in PADFORGE_PLUGIN_ROWS_TSV.</summary>
    public class PluginRowFramesDump
    {
        private static string Steps(IEnumerable<PluginStep> steps)
            => string.Join(";", (steps ?? Array.Empty<PluginStep>()).Where(s => s.Bytes != null)
                .Select(s => s.Kind.ToString()[0] + ":" + BitConverter.ToString(s.Bytes).Replace("-", "")));

        [Fact]
        public void WriteTheRows()
        {
            string path = Environment.GetEnvironmentVariable("PADFORGE_PLUGIN_ROWS_TSV");
            if (string.IsNullOrEmpty(path)) return;
            var lines = new List<string>();
            foreach (var d in PluginDeviceLighting.Devices)
            {
                string picture = "";
                string note = "";
                try
                {
                    IReadOnlyList<LightInfo> lights = d.Lights;
                    int slots = d.Slots;
                    var draw = d.Picture;
                    if (d.AskVariant != null)
                    {
                        var io = new FakeReportChannel { OutputReportLength = (ushort)d.OutputLength, FeatureReportLength = (ushort)d.FeatureLength, InputReportLength = 65 };
                        var variant = d.AskVariant(io, Array.Empty<IHidReportChannel>(), _ => { });
                        if (variant != null)
                        {
                            lights = variant.Lights;
                            slots = variant.Slots;
                            if (variant.Picture != null) draw = variant.Picture;
                        }
                    }
                    if (draw != null && slots > 0)
                    {
                        var byAddress = new int[slots];
                        foreach (var light in lights)
                            if (light.Address >= 0 && light.Address < slots) byAddress[light.Address] = 0x112233;
                        picture = Steps(draw(byAddress));
                    }
                    else note = "nopicture";
                }
                catch (Exception e) { note = e.GetType().Name; }
                lines.Add($"{d.ProductId:X4}\t{d.VendorId:X4}\t{d.Plugin}\t{d.Source}\t{Steps(d.Start)}\t{picture}\t{note}\t{(d.AskVariant != null ? 1 : 0)}\t{d.StripMaxLeds.Count}\t{(d.Answers != null ? 1 : 0)}");
            }
            File.WriteAllLines(path, lines);
        }
    }
}
