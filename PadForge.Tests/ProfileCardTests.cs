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
    /// A profile's card on the Profiles page (#574): the controllers it shows a
    /// picture of, the game line that names what loads it, and the flags that
    /// decide its menu and its foot.
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
        public void Controllers_FollowTheSlotsOrder_EachNumberedAmongItsType()
        {
            // The board's Default: Xbox, Keyboard + Mouse, Extended, in the
            // order of its slots, the first of each type.
            var item = new ProfileListItem();
            item.SetTopology(Created(0, 1, 2), new[] { (int)VirtualControllerType.Xbox, (int)VirtualControllerType.KeyboardMouse, (int)VirtualControllerType.Extended });
            Assert.Equal(new[] { VirtualControllerType.Xbox, VirtualControllerType.KeyboardMouse, VirtualControllerType.Extended },
                item.Controllers.Select(c => c.Type));
            Assert.All(item.Controllers, c => Assert.Equal("#1", c.Number));
            Assert.Equal(new[] { 0, 1, 2 }, item.Controllers.Select(c => c.Slot));
            Assert.False(item.HasNoSlots);

            // Every created slot is a controller, numbered among its type in
            // the slots' order. Nintendo and VR are their own types. An
            // uncreated slot is none, and a controller keeps its slot.
            item.SetTopology(Created(0, 1, 3, 4, 5), new[] { 6, 5, 6, 4, 6, 5 });
            Assert.Equal(new[]
                {
                    (VirtualControllerType.Vr, "#1", 0), (VirtualControllerType.Nintendo, "#1", 1),
                    (VirtualControllerType.KeyboardMouse, "#1", 3), (VirtualControllerType.Vr, "#2", 4),
                    (VirtualControllerType.Nintendo, "#2", 5),
                },
                item.Controllers.Select(c => (c.Type, c.Number, c.Slot)));

            // A created slot past the type array, or a value outside the
            // enum, is an Xbox controller, as the engine makes it.
            item.SetTopology(Created(0, 1, 2), new[] { 99, 1 });
            Assert.Equal(new[] { (VirtualControllerType.Xbox, "#1"), (VirtualControllerType.PlayStation, "#1"), (VirtualControllerType.Xbox, "#2") },
                item.Controllers.Select(c => (c.Type, c.Number)));

            // No arrays: no controllers, and the card says No Controllers.
            item.SetTopology(null, null);
            Assert.Empty(item.Controllers);
            Assert.True(item.HasNoSlots);
            Assert.True(new ProfileListItem().HasNoSlots);
        }

        [Fact]
        public void AController_CarriesItsTypesSymbolAndName_ItsPresetAndItsFinishes()
        {
            var item = new ProfileListItem();
            item.SetTopology(Created(0, 1),
                new[] { (int)VirtualControllerType.PlayStation, (int)VirtualControllerType.Midi },
                new[] { "dualsense-composite" },
                new[] { "DUALSENSE=CosmicRed, XBOXSERIES = VelocityGreen" });
            var ps = item.Controllers[0];
            Assert.NotNull(ps.TypeGeometry);
            Assert.Equal(string.Empty, ps.TypeGlyph);
            Assert.Equal(Strings.Instance.ControllerType_PlayStation, ps.TypeName);
            Assert.Equal(ps.TypeName + " #1", ps.SpokenName);
            // The picture is an item of the card's row, and a screen reader
            // reads an item by its ToString(). Without it the picture read
            // the first text it shows, "#1" alone (seen 2026-11-15).
            Assert.Equal(ps.SpokenName, ps.ToString());
            Assert.Equal("dualsense-composite", ps.PresetId);
            // The finish of a family is read as the controller's page reads it.
            Assert.Equal("CosmicRed", ps.FinishOf("DUALSENSE"));
            Assert.Equal("VelocityGreen", ps.FinishOf("XBOXSERIES"));
            Assert.Null(ps.FinishOf("DS4"));
            Assert.Null(ps.FinishOf(null));
            Assert.Equal("CosmicRed", new PadViewModel(0) { Model3DAppearances = "DUALSENSE=CosmicRed" }.GetModelAppearance("DUALSENSE"));

            // A slot the arrays do not reach has no preset and no finish.
            var midi = item.Controllers[1];
            Assert.Null(midi.TypeGeometry);
            Assert.NotEqual(string.Empty, midi.TypeGlyph);
            Assert.Equal(Strings.Instance.ControllerType_MIDI, midi.TypeName);
            Assert.Equal(string.Empty, midi.PresetId);
            Assert.Null(midi.FinishOf("DUALSENSE"));
        }

        [Fact]
        public void AControllersPicture_IsSaidOnce_AndItsPlaceWaitsForOneThatIsComing()
        {
            var controller = ProfileController.Build(Created(0), new[] { 0 })[0];
            var raised = new List<string>();
            controller.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
            Assert.Null(controller.Picture);
            Assert.False(controller.HasPicture);

            // A still is being made: no picture yet, and the type's symbol does not stand in.
            controller.SetPicture(null, coming: true);
            Assert.True(controller.HasPicture);
            Assert.Equal(new[] { nameof(ProfileController.HasPicture) }, raised);

            raised.Clear();
            var picture = new System.Windows.Media.Imaging.WriteableBitmap(2, 2, 96, 96, System.Windows.Media.PixelFormats.Pbgra32, null);
            controller.SetPicture(picture, coming: false);
            Assert.Same(picture, controller.Picture);
            Assert.True(controller.HasPicture);
            Assert.Equal(new[] { nameof(ProfileController.Picture) }, raised);

            // The same picture again says nothing.
            raised.Clear();
            controller.SetPicture(picture, coming: false);
            Assert.Empty(raised);

            // A type with no picture: nothing, and nothing coming.
            controller.SetPicture(null, coming: false);
            Assert.False(controller.HasPicture);
            Assert.Contains(nameof(ProfileController.HasPicture), raised);
        }

        [Fact]
        public void TheSameSlotsAgain_LeaveTheCardAsItIs()
        {
            var item = new ProfileListItem();
            var created = Created(0, 2);
            var types = new[] { 0, 0, 1 };
            var presets = new[] { "xbox-360-wired", "", "dualsense-composite" };
            var finishes = new[] { "", "", "DUALSENSE=CosmicRed" };
            item.SetTopology(created, types, presets, finishes);
            var before = item.Controllers;
            var raised = new List<string>();
            item.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

            // Equal arrays, though not the same ones: the controllers stay, with their pictures.
            item.SetTopology((bool[])created.Clone(), (int[])types.Clone(), (string[])presets.Clone(), (string[])finishes.Clone());
            Assert.Same(before, item.Controllers);
            Assert.Empty(raised);

            // Another preset, another finish, another type or another slot each make them again.
            item.SetTopology(created, types, new[] { "xbox-one-s-bt", "", "dualsense-composite" }, finishes);
            Assert.NotSame(before, item.Controllers);
            Assert.Contains(nameof(ProfileListItem.Controllers), raised);
            before = item.Controllers;
            item.SetTopology(created, types, new[] { "xbox-one-s-bt", "", "dualsense-composite" }, new[] { "", "", "DUALSENSE=White" });
            Assert.NotSame(before, item.Controllers);
            before = item.Controllers;
            item.SetTopology(created, new[] { 0, 0, 3 }, new[] { "xbox-one-s-bt", "", "dualsense-composite" }, new[] { "", "", "DUALSENSE=White" });
            Assert.NotSame(before, item.Controllers);
            before = item.Controllers;
            item.SetTopology(Created(0), new[] { 0, 0, 3 }, new[] { "xbox-one-s-bt", "", "dualsense-composite" }, new[] { "", "", "DUALSENSE=White" });
            Assert.NotSame(before, item.Controllers);
            Assert.Single(item.Controllers);

            // Presets that were there and are not given any more are a change too.
            before = item.Controllers;
            item.SetTopology(Created(0), new[] { 0, 0, 3 });
            Assert.NotSame(before, item.Controllers);
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
        public void ALanguageChange_ReadsTheControllersAndTheLineAgain()
        {
            var item = new ProfileListItem { SwitchesByGame = true, ExecutablePaths = @"C:\a.exe" };
            item.SetTopology(Created(0), new[] { (int)VirtualControllerType.Xbox });
            var before = item.Controllers;
            var raised = new List<string>();
            item.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
            item.RaiseCultureDependentText();
            Assert.NotSame(before, item.Controllers);
            Assert.Equal(VirtualControllerType.Xbox, Assert.Single(item.Controllers).Type);
            foreach (string name in new[] { nameof(ProfileListItem.Controllers), nameof(ProfileListItem.GameLead), nameof(ProfileListItem.GameTail), nameof(ProfileListItem.GameNames) })
                Assert.Contains(name, raised);
        }
    }
}
