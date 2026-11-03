using System;
using System.Collections.Generic;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// The keys of a SteelSeries Apex board (#557), from OpenRGB's tables
    /// (<see cref="OpenRgbCommit"/>, generated into SteelSeriesApexLayouts.g.cs).
    ///
    /// <para>Every per-key Apex board takes one frame of the same 112 key
    /// codes, and "the firmware will pick the keys applicable to it,
    /// ignoring the rest" (SteelSeriesApexRegions.h:22-32). Which keys a
    /// board has follows its form and its region. OpenRGB reads them from
    /// the first five characters of the board's serial, its SKU, and patches
    /// one ANSI grid with a TKL or Mini patch and then an ISO or JIS patch
    /// (SetSkuRegion, lines 444-461). A SKU its table lacks gets no patch
    /// there, which leaves a TKL board on the full layout. Here such a SKU,
    /// or a board that gave none, takes the patch of its product's form, so
    /// the Key Colors card draws the board the user has.</para>
    ///
    /// <para>A key is named by its code, which is its HID usage on the
    /// keyboard page for every key but two: 0xF0, the right Fn key, and
    /// 0xFB, play and pause. Those two are lights of their own, with a
    /// label. A light's address is its code, the byte the frame carries
    /// for it.</para>
    /// </summary>
    internal static partial class SteelSeriesApexLayouts
    {
        internal readonly record struct Patch(int Row, int Column, int Value);

        /// <summary>A board's form, which picks the patch a SKU outside the
        /// table takes.</summary>
        internal enum Form
        {
            Full,
            Tkl,
            Mini,
        }

        internal const int Rows = 6;
        internal const int Columns = 22;

        /// <summary>The right Fn key and play and pause (led_names 87 and
        /// 111).</summary>
        internal const byte FnCode = 0xF0;
        internal const byte PlayPauseCode = 0xFB;

        /// <summary>The grid of a board: the ANSI layout with the SKU's base
        /// patch and then its region patch on it, each cell an index into
        /// <see cref="Keys"/> or -1. A SKU the table lacks takes the patch of
        /// <paramref name="form"/>.</summary>
        internal static sbyte[] Grid(string sku, Form form)
        {
            var grid = (sbyte[])Ansi.Clone();
            Patch[] basePatch = form switch { Form.Tkl => TklUs, Form.Mini => MiniUs, _ => null };
            Patch[] regionPatch = null;
            if (sku != null)
                foreach (var entry in Skus)
                    if (entry.Sku == sku)
                    {
                        basePatch = entry.Base;
                        regionPatch = entry.Region;
                        break;
                    }
            Apply(grid, basePatch);
            Apply(grid, regionPatch);
            return grid;
        }

        private static void Apply(sbyte[] grid, Patch[] patch)
        {
            if (patch == null) return;
            foreach (var p in patch) grid[p.Row * Columns + p.Column] = (sbyte)p.Value;
        }

        /// <summary>Whether the table holds <paramref name="sku"/>.</summary>
        internal static bool Knows(string sku)
        {
            if (sku == null) return false;
            foreach (var entry in Skus)
                if (entry.Sku == sku) return true;
            return false;
        }

        /// <summary>The lights of a board with <paramref name="grid"/>, row by
        /// row from the top.</summary>
        internal static IReadOnlyList<LightInfo> Lights(sbyte[] grid)
        {
            var lights = new List<LightInfo>();
            foreach (sbyte index in grid)
                if (index >= 0) lights.Add(Light(Keys[index]));
            return lights;
        }

        /// <summary>The Apex M750's lights, row by row from the bottom as its
        /// frame takes them, each with its slot in the frame as its
        /// address.</summary>
        internal static IReadOnlyList<LightInfo> M750Lights()
        {
            var lights = new List<LightInfo>();
            for (int slot = 0; slot < M750.Length; slot++)
                if (M750[slot] >= 0)
                    lights.Add(Light(Keys[M750[slot]]) with { Address = slot });
            return lights;
        }

        /// <summary>The light of one key code.</summary>
        internal static LightInfo Light(byte code) => code switch
        {
            FnCode => new LightInfo(code, LightKind.Other, 0, LightKeySource.None) { Label = LightLabel.Fn },
            PlayPauseCode => new LightInfo(code, LightKind.Other, 0, LightKeySource.None) { Label = LightLabel.PlayPause },
            _ => new LightInfo(code, LightKind.Key, code, LightKeySource.Vendor),
        };

        /// <summary>
        /// The SKU OpenRGB gives a board that has no serial to read, by
        /// product (SteelSeriesApexController::GetSerial,
        /// SteelSeriesApexController.cpp:351-419, product IDs from
        /// SteelSeriesDevices.h:90-108): the Apex 9 TKL and Mini theirs, and
        /// the other boards of the second and third generations the TKL US
        /// SKU, save the wired Apex Pro TKL Gen 3, which answers the region
        /// query. Null for a board whose SKU comes from the board.
        /// </summary>
        internal static string FixedSku(ushort productId) => productId switch
        {
            0x1634 => "64847",  // Apex 9 TKL
            0x1620 => "64837",  // Apex 9 Mini
            0x1628 or 0x1630 or 0x1632 or 0x1644 or 0x1646 => TklUsSku,
            _ => null,
        };

        /// <summary>The SKU of a TKL US board, the one OpenRGB gives a
        /// second-generation board it reads none from.</summary>
        internal const string TklUsSku = "64865";

        /// <summary>The form of each per-key board, for a SKU the board did
        /// not give or the table lacks. The Apex Pro (0x1610), Apex 7
        /// (0x1612), Apex 5 (0x161C) and Apex Pro Gen 3 (0x1640) are full
        /// size, the Apex 9 Mini (0x1620) is a Mini, and the rest are TKL
        /// boards (SteelSeriesDevices.h:90-108).</summary>
        internal static Form FormOf(ushort productId) => productId switch
        {
            0x1610 or 0x1612 or 0x161C or 0x1640 => Form.Full,
            0x1620 => Form.Mini,
            _ => Form.Tkl,
        };

        /// <summary>Whether a board answers the serial query: the boards
        /// OpenRGB leaves at its first generation, every per-key board
        /// outside its protocol_map (SteelSeriesApexController.cpp:52-73),
        /// while the board's own firmware is older than 1.19.7.</summary>
        internal static bool AsksSerial(ushort productId)
            => productId is 0x1610 or 0x1612 or 0x1618 or 0x161C or 0x1614 or 0x1640;

        /// <summary>The wired Apex Pro TKL Gen 3, which answers the region
        /// query (SteelSeriesApexController.cpp:377-410).</summary>
        internal static bool AsksRegion(ushort productId) => productId == 0x1642;

        /// <summary>
        /// The SKU in the reply to the serial query of a first-generation
        /// board: its first five characters, digits all, or null
        /// (SteelSeriesApexBaseController::GetSerial,
        /// SteelSeriesApexBaseController.cpp:43-70, and SetupZones,
        /// RGBController_SteelSeriesApex.cpp:105). <paramref name="reply"/>
        /// is the report's data, with no report ID in front.
        /// </summary>
        internal static string SkuOfSerial(ReadOnlySpan<byte> reply)
        {
            if (reply.Length < 5) return null;
            var chars = new char[5];
            for (int i = 0; i < 5; i++)
            {
                if (reply[i] < (byte)'0' || reply[i] > (byte)'9') return null;
                chars[i] = (char)reply[i];
            }
            return new string(chars);
        }

        /// <summary>
        /// The SKU of the region byte a third-generation Apex Pro TKL
        /// answers with: F5, a byte, then the region (SteelSeriesApexController.cpp:377-410).
        /// Null for a reply that is not one or a region OpenRGB does not
        /// name. <paramref name="reply"/> is the report's data, with no
        /// report ID in front.
        /// </summary>
        internal static string SkuOfRegion(ReadOnlySpan<byte> reply)
        {
            if (reply.Length < 3 || reply[0] != RegionCommand) return null;
            return reply[2] switch
            {
                0x1 => "64740",
                0x3 => "64741",
                0x4 => "64743",
                0x6 => "64744",
                0xA => "64742",
                0xD => "64745",
                _ => null,
            };
        }

        /// <summary>The serial query of a first-generation board and the
        /// region query of a third-generation Apex Pro TKL
        /// (SteelSeriesApexBaseController.h:56 and 61).</summary>
        internal const byte SerialCommand = 0xFF;
        internal const byte RegionCommand = 0xF5;
    }
}
