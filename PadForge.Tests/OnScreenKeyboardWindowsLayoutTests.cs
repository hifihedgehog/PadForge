using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using PadForge.Common.Input.OnScreenKeyboard;
using PadForge.Views;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The On-Screen Keyboard's reader of Windows keyboard layouts (#566), run
    /// on the layouts this PC has installed. Nothing is loaded or changed: the
    /// reader asks Windows to leave the keyboard state alone. Windows' US
    /// layout is checked key by key, and the Brazilian ABNT2 layout too where
    /// it is installed. Also the button names the keyboard's hints print.
    /// </summary>
    public class OnScreenKeyboardWindowsLayoutTests
    {
        [DllImport("user32.dll")]
        private static extern int GetKeyboardLayoutList(int nBuff, [Out] IntPtr[] lpList);

        private static IntPtr[] Installed()
        {
            int n = GetKeyboardLayoutList(0, null);
            var list = new IntPtr[Math.Max(n, 0)];
            n = GetKeyboardLayoutList(list.Length, list);
            return list.Take(Math.Max(n, 0)).ToArray();
        }

        private static IntPtr Find(long id) => Installed().FirstOrDefault(h => ((long)h & 0xFFFFFFFF) == id);

        private static bool Drawn(List<OskKey[]> rows, ushort scan)
            => rows.SelectMany(r => r).Any(k => k.Kind == OskKeyKind.Layout && k.Scan == scan);

        [Fact]
        public void EveryInstalledLayout_DrawsNoExtraKeyThatRepeatsAnother()
        {
            var installed = Installed();
            Assert.NotEmpty(installed);
            var main = OskLayoutFrame.CharRows.SelectMany(r => r).ToArray();
            foreach (var hkl in installed)
            {
                var layout = new OskWindowsLayout(hkl);
                var rows = OskLayoutFrame.Rows(layout, null, functionRow: false);
                foreach (ushort extra in new[] { OskLayoutFrame.IsoKey, OskLayoutFrame.JisYen, OskLayoutFrame.JisRo })
                    if (Drawn(rows, extra))
                        Assert.DoesNotContain(main, s => OskLayoutFrame.Duplicates(layout.Text(extra), layout.Text(s)));
                if (((long)hkl & 0xFFFFFFFF) == 0x04160416) CheckAbnt2(layout, rows);
            }
        }

        [Fact]
        public void TheUsLayout_ReadsAsWindowsTypesIt()
        {
            var hkl = Find(0x04090409);
            Assert.True(hkl != IntPtr.Zero, "This test reads Windows' US English layout, which is not installed.");
            var us = new OskWindowsLayout(hkl);
            Assert.Equal("en-US", us.LanguageTag);
            Assert.False(us.HasAltGr);
            Assert.Equal(new OskKeyText("q", "Q", null, null), us.Text(0x10));
            Assert.True(us.Text(0x10).CapsAffects);
            Assert.Equal(new OskKeyText("6", "^", null, null), us.Text(0x07));
            Assert.Equal(new OskKeyText("'", "\"", null, null), us.Text(0x28));

            // Windows gives the ISO key the backslash key's \ and |, so the
            // grid draws one backslash key.
            Assert.Equal(us.Text(0x2B), us.Text(OskLayoutFrame.IsoKey));
            var rows = OskLayoutFrame.Rows(us, null, functionRow: false);
            Assert.False(Drawn(rows, OskLayoutFrame.IsoKey));
            Assert.Single(rows.SelectMany(r => r), k => k.Kind == OskKeyKind.Layout && k.LayoutText.Base == "\\");
            // The ABNT2 key types nothing on the US layout.
            Assert.True(us.Text(OskLayoutFrame.JisRo).IsEmpty);
            Assert.False(Drawn(rows, OskLayoutFrame.JisRo));
        }

        /// <summary>Read with ToUnicodeEx on 2026-11-07: ~ is a dead tilde with
        /// a dead circumflex shifted, ´ a dead acute with a dead grave shifted,
        /// Shift+6 a dead diaeresis, and the two keys ANSI boards lack type
        /// their own \ | and / ?.</summary>
        private static void CheckAbnt2(OskWindowsLayout br, List<OskKey[]> rows)
        {
            Assert.Equal("pt-BR", br.LanguageTag);
            Assert.True(br.HasAltGr);
            Assert.Equal(new OskKeyText("~", "^", null, null, Dead: true, DeadShift: true), br.Text(0x28));
            Assert.Equal(new OskKeyText("´", "`", null, null, Dead: true, DeadShift: true), br.Text(0x1A));
            Assert.Equal(new OskKeyText("6", "¨", "¬", null, DeadShift: true), br.Text(0x07));
            Assert.Equal(new OskKeyText("\\", "|", null, null), br.Text(OskLayoutFrame.IsoKey));
            Assert.Equal(new OskKeyText("/", "?", "°", null), br.Text(OskLayoutFrame.JisRo));
            Assert.True(Drawn(rows, OskLayoutFrame.IsoKey));
            Assert.True(Drawn(rows, OskLayoutFrame.JisRo));
        }

        /// <summary>ABNT2's /? key sits where the JIS Ro key does, and its
        /// virtual key, VK_ABNT_C1, is in neither of the HID key map's tables.
        /// A slot's keyboard device still sends it, at its scan code's place,
        /// International1.</summary>
        [Fact]
        public void Abnt2sSlashKey_ReachesAKeyboardDeviceAtItsPlace()
        {
            var hkl = Find(0x04160416);
            if (hkl == IntPtr.Zero) return;
            ushort vk = OskWindowsOutput.VirtualKeyOf(OskLayoutFrame.JisRo, hkl);
            Assert.Equal(0xC1, vk);
            Assert.True(PadForge.Common.Input.KbmHidKeyMap.Nominal(vk).IsNone);
            Assert.Equal(new PadForge.Common.Input.KbmHidKeyMap.HidKey(0x07, 0x87),
                PadForge.Common.Input.KbmHidKeyMap.Resolve(vk, hkl));
        }

        [Theory]
        [InlineData(OskFamily.Xbox, "A", "B", "View", "Menu", "LT")]
        [InlineData(OskFamily.PlayStation, "✕", "○", "Create", "Options", "L2")]
        [InlineData(OskFamily.Nintendo, "B", "A", "−", "+", "ZL")]
        [InlineData(OskFamily.GameCube, "A", "X", "View", "Start", "L")]
        public void TheHints_NameEachFamilysButtons(OskFamily family, string south, string east, string view, string menu, string lt)
        {
            Assert.Equal(south, OnScreenKeyboardWindow.GlyphText(OskInput.South, family));
            Assert.Equal(east, OnScreenKeyboardWindow.GlyphText(OskInput.East, family));
            Assert.Equal(view, OnScreenKeyboardWindow.GlyphText(OskInput.View, family));
            Assert.Equal(menu, OnScreenKeyboardWindow.GlyphText(OskInput.Menu, family));
            Assert.Equal(lt, OnScreenKeyboardWindow.GlyphText(OskInput.LeftTrigger, family));
        }
    }
}
