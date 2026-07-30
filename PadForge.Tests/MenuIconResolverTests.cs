using System;
using System.IO;
using PadForge.Engine.Menus;

namespace PadForge.Tests
{
    /// <summary>Menu cell icon resolution (#9, translator v21): authored
    /// Steam icon names resolve against the LOCAL Steam client's art at
    /// display time, are never shipped, and degrade to the text label
    /// (null here) whenever the name, the install, or the file is
    /// absent. These pins drive the resolver through a fake Steam root
    /// so no test depends on a real install.</summary>
    public class MenuIconResolverTests : IDisposable
    {
        /// <summary>The smallest valid PNG (1x1 transparent), so the
        /// positive pin exercises the real decode path.</summary>
        private static readonly byte[] TinyPng =
        {
            0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
            0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
            0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01,
            0x08, 0x06, 0x00, 0x00, 0x00, 0x1F, 0x15, 0xC4,
            0x89, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x44, 0x41,
            0x54, 0x78, 0x9C, 0x62, 0x00, 0x01, 0x00, 0x00,
            0x05, 0x00, 0x01, 0x0D, 0x0A, 0x2D, 0xB4, 0x00,
            0x00, 0x00, 0x00, 0x49, 0x45, 0x4E, 0x44, 0xAE,
            0x42, 0x60, 0x82,
        };

        private readonly string _root;

        public MenuIconResolverTests()
        {
            _root = Path.Combine(Path.GetTempPath(),
                "padforge-icon-tests-" + Guid.NewGuid().ToString("N"));
            string icons = Path.Combine(_root, "tenfoot", "resource", "images",
                "library", "controller", "binding_icons");
            Directory.CreateDirectory(icons);
            File.WriteAllBytes(Path.Combine(icons, "ghost_050_menu_0030.png"), TinyPng);
            PadForge.Common.MenuIconResolver.SteamRootOverride = _root;
        }

        public void Dispose()
        {
            PadForge.Common.MenuIconResolver.SteamRootOverride = null;
            try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
        }

        [Fact]
        public void ValidName_PresentOnDisk_ResolvesToAFrozenImage()
        {
            var img = PadForge.Common.MenuIconResolver.Resolve("ghost_050_menu_0030.png");
            Assert.NotNull(img);
            Assert.True(img.IsFrozen);
            // Cached: the second read returns the same instance.
            Assert.Same(img, PadForge.Common.MenuIconResolver.Resolve("ghost_050_menu_0030.png"));
        }

        [Fact]
        public void MissingFile_ResolvesNull_TheTextLabelFallback()
        {
            Assert.Null(PadForge.Common.MenuIconResolver.Resolve("ghost_075_utility_010.png"));
        }

        [Fact]
        public void NoSteamInstall_ResolvesNull()
        {
            PadForge.Common.MenuIconResolver.SteamRootOverride =
                Path.Combine(_root, "does-not-exist");
            Assert.Null(PadForge.Common.MenuIconResolver.Resolve("ghost_050_menu_0030.png"));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData(".png")]
        [InlineData("ghost.bmp")]
        [InlineData("art/ghost_050_menu_0030.png")]
        [InlineData(@"..\..\secrets.png")]
        [InlineData("C:aim.png")]
        [InlineData("ghost 050.png")]
        public void InvalidNameShapes_NeverResolve_AndNeverProbeDisk(string name)
        {
            Assert.False(MenuItemDefinition.IsValidIconName(name));
            Assert.Null(PadForge.Common.MenuIconResolver.Resolve(name));
        }

        [Theory]
        [InlineData("ghost_050_menu_0030.png")]
        [InlineData("ghost_040_act_0321a.png")]
        [InlineData("genesis_a.png")]
        [InlineData("special_blank.png")]
        [InlineData("GHOST_050_MENU_0030.PNG")]
        public void ClientArtNameShapes_AreValid(string name)
        {
            // Census over the shipped binding_icons directory (460 files,
            // 2026-07-18): letters, digits, '_', '-', '.', nothing else.
            Assert.True(MenuItemDefinition.IsValidIconName(name));
        }
    }
}
