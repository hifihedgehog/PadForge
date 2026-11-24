using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using PadForge.Common.Input;

namespace PadForge.Tests
{
    /// <summary>What the controller database tests build their cases from: a
    /// baseline of a few rows, and a community file around them. Nothing here
    /// reads the real baseline or reaches the network.</summary>
    internal static class ControllerDatabaseFixture
    {
        internal const string Fork = "1111111111111111111111111111111111111111";
        internal const string Pin = "2222222222222222222222222222222222222222";
        internal const string PinTime = "2026-09-24T16:11:31Z";
        internal const string Bindings = "a:b0,b:b1,back:b8,dpdown:h0.4,leftx:a0,lefty:a1~,lefttrigger:+a2,start:b9";

        /// <summary>The controller ID of a vendor and a product in the USB
        /// form. The IDs are written little-endian, as SDL writes them.</summary>
        internal static string Id(int vendor, int product) =>
            "03000000" + Le(vendor) + "0000" + Le(product) + "000000000000";

        private static string Le(int value) =>
            (value & 0xFF).ToString("x2", CultureInfo.InvariantCulture) + ((value >> 8) & 0xFF).ToString("x2", CultureInfo.InvariantCulture);

        internal static string Row(int vendor, int product, string name = "Test Pad", string bindings = Bindings) =>
            Id(vendor, product) + "," + name + "," + bindings + ",platform:Windows,";

        internal static ControllerDatabaseRow Parsed(string text)
        {
            if (!ControllerDatabaseRow.TryParse(text, out var row))
                throw new InvalidOperationException("not a row: " + text);
            return row;
        }

        /// <summary>A baseline of the rows given, each with its verdict: K,
        /// S, O or N. A null row text with an ID is an ID of SDL's own
        /// Windows section that the pinned copy has no row for.</summary>
        internal static ControllerDatabaseBaseline Baseline(params (char Verdict, string Row)[] pinned) =>
            Baseline(Pin, PinTime, pinned);

        internal static ControllerDatabaseBaseline Baseline(string pin, string pinTime, params (char Verdict, string Row)[] pinned)
        {
            var text = new StringBuilder();
            text.Append("# a test baseline\r\n");
            text.Append("format 1\r\n");
            text.Append("fork ").Append(Fork).Append("\r\n");
            text.Append("pin ").Append(pin).Append(' ').Append(pinTime).Append("\r\n");
            text.Append("sha256 ").Append(new string('a', 64)).Append("\r\n");
            text.Append("windows-rows ").Append(pinned.Length.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
            text.Append("sdl-windows-rows 313\r\n");
            foreach (var (verdict, row) in pinned)
            {
                bool idOnly = row.Length == 32;
                text.Append(verdict).Append(' ').Append(row.Substring(0, 32)).Append(' ')
                    .Append(idOnly ? "-" : ControllerDatabaseRow.HashOf(row)).Append("\r\n");
            }
            return ControllerDatabaseBaseline.Parse(text.ToString());
        }

        /// <summary>A community file of the Windows rows given, among rows of
        /// other platforms, comments and enough of them to pass the size
        /// floor.</summary>
        internal static byte[] CommunityFile(IEnumerable<string> windowsRows, string lineEnd = "\n")
        {
            var text = new StringBuilder();
            text.Append("# Game Controller DB for SDL in 2.0.16 format").Append(lineEnd);
            text.Append("# Source: https://github.com/mdqinc/SDL_GameControllerDB").Append(lineEnd).Append(lineEnd);
            text.Append("# Windows").Append(lineEnd);
            foreach (string row in windowsRows) text.Append(row).Append(lineEnd);
            text.Append(lineEnd).Append("# Mac OS X").Append(lineEnd);
            text.Append("030000008f0e00000300000009010000,2 In 1 Joystick,a:b2,b:b1,platform:Mac OS X,").Append(lineEnd);
            text.Append(lineEnd).Append("# Linux").Append(lineEnd);
            for (int i = 0; text.Length < ControllerDatabaseDownload.MinBytes + 4096; i++)
                text.Append("05000000").Append(i.ToString("x8", CultureInfo.InvariantCulture))
                    .Append("0000000000000000,Pad ").Append(i.ToString(CultureInfo.InvariantCulture))
                    .Append(",a:b0,b:b1,back:b8,leftx:a0,lefty:a1,start:b9,platform:Linux,").Append(lineEnd);
            return Encoding.UTF8.GetBytes(text.ToString());
        }

        /// <summary>Many pinned rows the fork kept, so a file can keep or
        /// lose a share of them.</summary>
        internal static List<string> KeptRows(int count) =>
            Enumerable.Range(0, count).Select(i => Row(0x1000 + i, 0x0001, "Kept " + i.ToString(CultureInfo.InvariantCulture))).ToList();
    }
}
