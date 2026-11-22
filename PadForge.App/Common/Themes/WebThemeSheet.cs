using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;

namespace PadForge.Common.Themes
{
    /// <summary>
    /// The web controller's colors under a theme (#591). The pages take
    /// every color from one sheet, <c>css/padforge.css</c>, whose values
    /// are the app's dark brushes, each named in a comment beside its
    /// value. Under a theme the server sends the same names again after the
    /// sheet with the theme's values, and each page's <c>theme-color</c>
    /// becomes the theme's ground, so a phone's pages look like the program
    /// they drive.
    ///
    /// The values are read from the sheet's own comments, so the sheet
    /// stays the one list of what a page's color is. Under System, Light
    /// and Dark nothing is added and the pages are as they were.
    ///
    /// <see cref="Refresh"/> runs on the UI thread, where the brushes are.
    /// The server reads <see cref="Current"/> from its own threads.
    /// </summary>
    internal static class WebThemeSheet
    {
        internal const string SheetPath = "/css/padforge.css";
        private const string SheetResource = "PadForge.WebAssets.css.padforge.css";

        /// <summary>The ground the pages name in their theme-color, as the stock sheet has it.</summary>
        internal const string StockGround = "#0a0e15";

        /// <summary>The colors a page made from another: the ember's light
        /// around a lit tile, and the ground as a veil over a page.</summary>
        private const double GlowAlpha = 0.30, VeilAlpha = 0.82;

        /// <summary>What the server adds: the rule that follows the sheet, and the ground's color.</summary>
        internal sealed record Dressing(string Css, string Ground);

        private static volatile Dressing _current;
        private static string _stockSheet;

        /// <summary>The theme's dressing, or null under PadForge's own look.</summary>
        internal static Dressing Current => _current;

        /// <summary>Reads the look in force. Called when a look is put on.</summary>
        internal static void Refresh()
        {
            var app = Application.Current;
            var theme = ThemeManager.Current;
            if (app == null || theme == null)
            {
                _current = null;
                return;
            }
            _stockSheet ??= ReadStockSheet();
            _current = _stockSheet == null ? null : Build(_stockSheet, key => app.TryFindResource(key), theme.Dark);
        }

        private static string ReadStockSheet()
        {
            using var stream = typeof(WebThemeSheet).Assembly.GetManifestResourceStream(SheetResource);
            if (stream == null) return null;
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return reader.ReadToEnd();
        }

        /// <summary>One row of the sheet's root rule: a name, its stock
        /// value, and the brush its comment names first, or null when the
        /// comment is in words.</summary>
        internal readonly record struct Row(string Name, string StockValue, string Brush);

        /// <summary>The colors the sheet's root rule sets.</summary>
        internal static List<Row> Rows(string stockSheet)
        {
            var rows = new List<Row>();
            foreach (Match row in Regex.Matches(stockSheet ?? string.Empty,
                         @"(--[a-z0-9-]+):\s*(#[0-9a-fA-F]{6}|rgba\([^)]*\));\s*/\*\s*([^*]+?)\s*\*/"))
            {
                string first = row.Groups[3].Value.Split(',')[0].Trim();
                rows.Add(new Row(row.Groups[1].Value, row.Groups[2].Value, Regex.IsMatch(first, @"^[A-Z]\w+$") ? first : null));
            }
            return rows;
        }

        /// <summary>The dressing for a look whose brushes <paramref name="find"/>
        /// gives. A color the stock sheet has solid stays solid: a brush
        /// that lets its ground through is laid over the ground first, as
        /// the app draws it.</summary>
        internal static Dressing Build(string stockSheet, Func<string, object> find, bool dark)
        {
            Color? Of(string key) => find(key) switch
            {
                SolidColorBrush brush => brush.Color,
                Color color => color,
                _ => null,
            };
            Color backdrop = dark ? Colors.Black : Colors.White;
            Color ground = Over(Of("SteelGroundBrush") ?? ThemeEngine.StockGround(dark), backdrop);
            Color ember = Over(Of("EmberBrush") ?? ThemeEngine.StockAccent, ground);

            var css = new StringBuilder();
            css.Append("\n/* The theme in force (#591): the names above, with its values. */\n:root {\n");
            css.Append("    color-scheme: ").Append(dark ? "dark" : "light").Append(";\n");
            foreach (var row in Rows(stockSheet))
            {
                bool solid = row.StockValue.StartsWith("#", StringComparison.Ordinal);
                string value;
                if (row.Brush != null)
                {
                    Color? color = Of(row.Brush);
                    if (color == null) continue;
                    value = solid ? Hex(Over(color.Value, ground)) : Rgba(color.Value);
                }
                else if (row.Name == "--ember-glow") value = Rgba(Color.FromArgb((byte)Math.Round(GlowAlpha * 255), ember.R, ember.G, ember.B));
                else if (row.Name == "--veil") value = Rgba(Color.FromArgb((byte)Math.Round(VeilAlpha * 255), ground.R, ground.G, ground.B));
                else continue;
                css.Append("    ").Append(row.Name).Append(": ").Append(value).Append(";\n");
            }
            css.Append("}\n");
            return new Dressing(css.ToString(), Hex(ground));
        }

        /// <summary>A color laid over a solid ground.</summary>
        private static Color Over(Color color, Color ground)
        {
            if (color.A == 255) return color;
            double a = color.A / 255.0;
            static byte Mix(byte top, byte under, double share) => (byte)Math.Round(top * share + under * (1 - share));
            return Color.FromRgb(Mix(color.R, ground.R, a), Mix(color.G, ground.G, a), Mix(color.B, ground.B, a));
        }

        private static string Hex(Color c) => "#" + c.R.ToString("x2", CultureInfo.InvariantCulture)
            + c.G.ToString("x2", CultureInfo.InvariantCulture) + c.B.ToString("x2", CultureInfo.InvariantCulture);

        private static string Rgba(Color c) => string.Format(CultureInfo.InvariantCulture, "rgba({0}, {1}, {2}, {3:0.##})",
            c.R, c.G, c.B, c.A / 255.0);

        /// <summary>A served file as the theme dresses it: the sheet with
        /// the theme's rule after it, a page with the theme's ground in its
        /// theme-color, and every other file as it is.</summary>
        internal static byte[] Dress(string path, byte[] body)
        {
            var dressing = _current;
            if (dressing == null || body == null || path == null) return body;
            if (string.Equals(path, SheetPath, StringComparison.OrdinalIgnoreCase))
            {
                byte[] rule = Encoding.UTF8.GetBytes(dressing.Css);
                var whole = new byte[body.Length + rule.Length];
                Buffer.BlockCopy(body, 0, whole, 0, body.Length);
                Buffer.BlockCopy(rule, 0, whole, body.Length, rule.Length);
                return whole;
            }
            if (path.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
            {
                string stock = "<meta name=\"theme-color\" content=\"" + StockGround + "\">";
                string page = Encoding.UTF8.GetString(body);
                if (!page.Contains(stock, StringComparison.Ordinal)) return body;
                return Encoding.UTF8.GetBytes(page.Replace(stock, "<meta name=\"theme-color\" content=\"" + dressing.Ground + "\">", StringComparison.Ordinal));
            }
            return body;
        }
    }
}
