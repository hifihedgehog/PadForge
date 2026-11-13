using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using PadForge.Engine;
using PadForge.ViewModels;
using Xunit;
using Strings = PadForge.Resources.Strings.Strings;

namespace PadForge.Tests
{
    /// <summary>
    /// A profile's card on the Profiles page (#574): the chips that count its
    /// controllers by type, the game line that names what loads it, and the
    /// flags that decide its menu and its foot.
    /// </summary>
    public class ProfileCardTests
    {
        private static bool[] Created(params int[] slots)
        {
            var created = new bool[16];
            foreach (int s in slots) created[s] = true;
            return created;
        }

        [Fact]
        public void TypeChips_FollowTheSlotsOrder_AndCountEachType()
        {
            // The board's Default: Xbox, Keyboard + Mouse, Extended, in the
            // order of its slots, one each.
            var item = new ProfileListItem();
            item.SetTopology(Created(0, 1, 2), new[] { (int)VirtualControllerType.Xbox, (int)VirtualControllerType.KeyboardMouse, (int)VirtualControllerType.Extended });
            Assert.Equal(new[] { VirtualControllerType.Xbox, VirtualControllerType.KeyboardMouse, VirtualControllerType.Extended },
                item.TypeChips.Select(c => c.Type));
            Assert.All(item.TypeChips, c => Assert.Equal(1, c.Count));
            Assert.False(item.HasNoSlots);

            // A type counts at its first slot. Nintendo and VR are their own
            // types. An uncreated slot counts nothing.
            item.SetTopology(Created(0, 1, 3, 4, 5), new[] { 6, 5, 6, 4, 6, 5 });
            Assert.Equal(new[] { (VirtualControllerType.Vr, 2), (VirtualControllerType.Nintendo, 2), (VirtualControllerType.KeyboardMouse, 1) },
                item.TypeChips.Select(c => (c.Type, c.Count)));

            // A created slot past the type array, or a value outside the
            // enum, counts as Xbox, as the old counter did.
            item.SetTopology(Created(0, 1, 2), new[] { 99, 1 });
            Assert.Equal(new[] { (VirtualControllerType.Xbox, 2), (VirtualControllerType.PlayStation, 1) },
                item.TypeChips.Select(c => (c.Type, c.Count)));

            // No arrays: no chips, and the card says No Controllers.
            item.SetTopology(null, null);
            Assert.Empty(item.TypeChips);
            Assert.True(item.HasNoSlots);
            Assert.True(new ProfileListItem().HasNoSlots);
        }

        [Fact]
        public void AChip_CarriesItsTypesSymbolAndName()
        {
            var item = new ProfileListItem();
            item.SetTopology(Created(0, 1), new[] { (int)VirtualControllerType.PlayStation, (int)VirtualControllerType.Midi });
            var ps = item.TypeChips[0];
            Assert.NotNull(ps.TypeGeometry);
            Assert.Equal(string.Empty, ps.TypeGlyph);
            Assert.Equal(Strings.Instance.ControllerType_PlayStation, ps.TypeName);
            Assert.Equal(string.Format(Strings.Instance.Profiles_TypeCount_Format, ps.TypeName, 1), ps.SpokenName);
            var midi = item.TypeChips[1];
            Assert.Null(midi.TypeGeometry);
            Assert.NotEqual(string.Empty, midi.TypeGlyph);
            Assert.Equal(Strings.Instance.ControllerType_MIDI, midi.TypeName);
        }

        private static (string Lead, string Names, string Extra, string Tail) Line(ProfileListItem item)
            => (item.GameLead, item.GameNames, item.GameExtra, item.GameTail);

        [Fact]
        public void TheGameLine_NamesTwoGames_AndCountsTheRest()
        {
            var item = new ProfileListItem { SwitchesByGame = true };
            item.ExecutablePaths = @"C:\Games\a.exe";
            Assert.Equal("a.exe", item.GameNames);
            Assert.Equal(string.Empty, item.GameExtra);
            item.ExecutablePaths = @"C:\Games\a.exe|D:\b.exe";
            Assert.Equal("a.exe, b.exe", item.GameNames);
            Assert.Equal(string.Empty, item.GameExtra);
            item.ExecutablePaths = @"C:\Games\a.exe|D:\b.exe|E:\c.exe|F:\d.exe";
            Assert.Equal("a.exe, b.exe", item.GameNames);
            Assert.Equal("+2", item.GameExtra);
            item.ExecutablePaths = string.Empty;
            Assert.Equal(string.Empty, item.GameNames);
        }

        [Fact]
        public void TheGameLine_SaysLoadsForWhileSwitchingIsOn_AndSetForWhileOff()
        {
            string Joined(ProfileListItem item) => item.GameLead + item.GameNames + item.GameExtra + item.GameTail;
            var item = new ProfileListItem { ExecutablePaths = @"C:\Games\RocketLeague.exe" };
            item.SwitchesByGame = true;
            Assert.Equal(string.Format(Strings.Instance.Profiles_Card_LoadsFor_Format, "RocketLeague.exe"), Joined(item));
            item.SwitchesByGame = false;
            Assert.Equal(string.Format(Strings.Instance.Profiles_Card_SetFor_Format, "RocketLeague.exe"), Joined(item));
        }

        [Fact]
        public void TheMenusFlags_FollowActiveAndDefault()
        {
            var raised = new List<string>();
            var named = new ProfileListItem { Id = "p1", Name = "Rocket League" };
            named.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
            Assert.True(named.CanEdit);
            Assert.True(named.ShowsLoad);
            Assert.False(named.ShowsSaveAs);
            Assert.True(named.ShowsSaveHere);

            named.IsActive = true;
            Assert.False(named.ShowsLoad);
            Assert.True(named.ShowsSaveAs);
            Assert.False(named.ShowsSaveHere);
            Assert.Contains(nameof(ProfileListItem.ShowsLoad), raised);
            Assert.Contains(nameof(ProfileListItem.ShowsSaveAs), raised);
            Assert.Contains(nameof(ProfileListItem.ShowsSaveHere), raised);

            var builtIn = new ProfileListItem { Id = ProfileListItem.DefaultProfileId };
            Assert.True(builtIn.IsDefault);
            Assert.False(builtIn.CanEdit);
            Assert.False(builtIn.ShowsSaveHere);
            builtIn.IsActive = true;
            Assert.True(builtIn.ShowsSaveAs);
            Assert.False(builtIn.ShowsLoad);
        }

        [Fact]
        public void ALanguageChange_ReadsTheChipsAndTheLineAgain()
        {
            var item = new ProfileListItem { SwitchesByGame = true, ExecutablePaths = @"C:\a.exe" };
            item.SetTopology(Created(0), new[] { (int)VirtualControllerType.Xbox });
            var before = item.TypeChips;
            var raised = new List<string>();
            item.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
            item.RaiseCultureDependentText();
            Assert.NotSame(before, item.TypeChips);
            Assert.Equal(VirtualControllerType.Xbox, Assert.Single(item.TypeChips).Type);
            foreach (string name in new[] { nameof(ProfileListItem.TypeChips), nameof(ProfileListItem.GameLead), nameof(ProfileListItem.GameTail), nameof(ProfileListItem.GameNames) })
                Assert.Contains(name, raised);
        }
    }
}
