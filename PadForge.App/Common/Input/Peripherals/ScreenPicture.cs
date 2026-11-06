using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using PadForge.Views;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// A small picture a device's lights spread across (#559), each light
    /// showing the spot its own place on the device falls on. Cells row by row,
    /// each 0xRRGGBB. Made whole and never changed, so a key frame can carry one
    /// across threads and a backend can compare two by content.
    /// </summary>
    internal sealed class ScreenPicture : IEquatable<ScreenPicture>
    {
        private readonly int[] _cells;
        private readonly int _hash;

        public int Cols { get; }
        public int Rows { get; }

        public ScreenPicture(int cols, int rows, int[] cells)
        {
            Cols = cols;
            Rows = rows;
            _cells = cells;
            var hash = new HashCode();
            hash.Add(cols);
            hash.Add(rows);
            foreach (int cell in cells) hash.Add(cell);
            _hash = hash.ToHashCode();
        }

        /// <summary>The cell under a place from 0 to 1 across and down.</summary>
        public int At(float across, float down)
        {
            int x = Math.Clamp((int)(across * Cols), 0, Cols - 1);
            int y = Math.Clamp((int)(down * Rows), 0, Rows - 1);
            return _cells[y * Cols + x];
        }

        public bool SameCells(int cols, int rows, int[] cells)
            => cols == Cols && rows == Rows && cells.AsSpan().SequenceEqual(_cells);

        public bool Equals(ScreenPicture other)
            => ReferenceEquals(this, other)
                || (other != null && other._hash == _hash && SameCells(other.Cols, other.Rows, other._cells));

        public override bool Equals(object obj) => obj is ScreenPicture other && Equals(other);

        public override int GetHashCode() => _hash;
    }

    /// <summary>
    /// Where each light of a unit sits, from 0 to 1 across and down the device
    /// (#559), for a picture to spread over. Best first: the position the device
    /// reports, the QMK layout that holds the most of its keys, and then zones
    /// and edge lights in order from left to right. A light with none of these
    /// has no place and takes the device's one color.
    /// </summary>
    internal static class LightPlacement
    {
        private static readonly ConditionalWeakTable<LightMap, float[]> s_places = new();

        private static readonly LayoutKey[][] Layouts =
        {
            KeyboardFamilies.FullsizeAnsi, KeyboardFamilies.FullsizeIso, KeyboardFamilies.FullsizeExtended,
            KeyboardFamilies.Tkl, KeyboardFamilies.SeventyFive, KeyboardFamilies.SixtyFive,
            KeyboardFamilies.SixtyFiveBlocker, KeyboardFamilies.Sixty, KeyboardFamilies.Hhkb, KeyboardFamilies.HhkbJis,
            KeyboardFamilies.KeychronQ6,
        };

        /// <summary>The QMK layout that holds the most of a map's keys, the
        /// smaller of two that hold as many, or null when it holds none. The
        /// Key Colors picture draws a device with no positions on this one too.</summary>
        internal static LayoutKey[] BestLayout(LightMap map)
        {
            if (map.KeyCount == 0) return null;
            var usages = new HashSet<ushort>(map.Usages);
            LayoutKey[] best = null;
            int bestHits = 0;
            foreach (var layout in Layouts)
            {
                int hits = layout.Count(k => k.Page == 0x07 && usages.Contains(k.Id));
                if (hits > bestHits || (hits == bestHits && hits > 0 && layout.Length < best.Length))
                {
                    best = layout;
                    bestHits = hits;
                }
            }
            return best;
        }

        /// <summary>Two numbers per light in map order, across and then down,
        /// both NaN for a light with no place. Worked out once per map.</summary>
        public static float[] Of(LightMap map) => s_places.GetValue(map, Build);

        private static float[] Build(LightMap map)
        {
            var lights = map.Lights;
            var places = new float[lights.Count * 2];
            Array.Fill(places, float.NaN);
            if (lights.Any(l => l.HasPosition)) PlaceByPosition(lights, places);
            else PlaceByLayout(map, places);

            int zone = 0, edge = 0;
            for (int i = 0; i < lights.Count; i++)
            {
                if (!float.IsNaN(places[i * 2])) continue;
                var light = lights[i];
                if (light.Kind == LightKind.Zone && map.ZoneCount > 0)
                {
                    int number = light.Zone > 0 ? light.Zone : ++zone;
                    places[i * 2] = (number - 0.5f) / map.ZoneCount;
                    places[i * 2 + 1] = 0.5f;
                }
                else if (light.Kind == LightKind.Edge && map.EdgeCount > 0)
                {
                    edge++;
                    places[i * 2] = (edge - 0.5f) / map.EdgeCount;
                    places[i * 2 + 1] = 0.5f;
                }
            }
            return places;
        }

        private static void PlaceByPosition(IReadOnlyList<LightInfo> lights, float[] places)
        {
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            foreach (var light in lights)
            {
                if (!light.HasPosition) continue;
                minX = Math.Min(minX, light.X);
                minY = Math.Min(minY, light.Y);
                maxX = Math.Max(maxX, light.X + light.Width);
                maxY = Math.Max(maxY, light.Y + light.Height);
            }
            float width = maxX - minX, height = maxY - minY;
            for (int i = 0; i < lights.Count; i++)
            {
                var light = lights[i];
                if (!light.HasPosition) continue;
                places[i * 2] = width > 0 ? (light.X + light.Width / 2 - minX) / width : 0.5f;
                places[i * 2 + 1] = height > 0 ? (light.Y + light.Height / 2 - minY) / height : 0.5f;
            }
        }

        private static void PlaceByLayout(LightMap map, float[] places)
        {
            var layout = BestLayout(map);
            if (layout == null) return;
            double width = layout.Max(k => k.X + k.W), height = layout.Max(k => k.Y + k.H);
            var drawn = new HashSet<ushort>();
            foreach (var key in layout)
            {
                // A layout that draws one usage twice puts its lights on the
                // first, as the Key Colors picture does.
                if (key.Page != 0x07 || !drawn.Add(key.Id)) continue;
                foreach (int index in map.IndexesOf(key.Id))
                {
                    places[index * 2] = (float)((key.X + key.W / 2) / width);
                    places[index * 2 + 1] = (float)((key.Y + key.H / 2) / height);
                }
            }
        }
    }
}
