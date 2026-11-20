using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reflection;
using System.Windows;
using PadForge.Common;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Services;
using PadForge.ViewModels;
using Xunit;
using Strings = PadForge.Resources.Strings.Strings;

namespace PadForge.Tests
{
    /// <summary>
    /// Home's cards and the profile cards: what the UI timer reads for them,
    /// when it stops reading, and what a card tells its picture.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class HomeLaneAuditTests
    {
        private static string Text(params string[] parts) => ExtrasPageTests.RepoText(parts).Replace("\r", "");

        private static object Call(object target, string name)
        {
            var method = target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            Assert.True(method != null, "no method " + name);
            return method.Invoke(target, null);
        }

        private static void Set(object target, string field, object value)
            => target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(target, value);

        private static System.Windows.Media.ImageSource Swatch()
        {
            var picture = System.Windows.Media.Imaging.BitmapSource.Create(4, 4, 96, 96,
                System.Windows.Media.PixelFormats.Pbgra32, null, new byte[4 * 4 * 4], 16);
            picture.Freeze();
            return picture;
        }

        /// <summary>A service whose window is behind another program's, with
        /// "Continue polling when window loses focus" off.</summary>
        private static (MainViewModel Main, InputService Service) Behind()
        {
            var main = new MainViewModel();
            var service = new InputService(main);
            main.Settings.EnablePollingOnFocusLoss = false;
            // The foreground answer is read once and held.
            Set(service, "_fgCached", false);
            Set(service, "_fgCachedTick", long.MaxValue);
            return (main, service);
        }

        /// <summary>With the setting off and a game in front, the display
        /// mirrors stop as every other UI lane does: the controllers' page
        /// mirrors and Home's cards. With it on they run.</summary>
        [Fact]
        public void BehindAGame_TheMirrorsStop_WhenTheSettingSaysSo()
        {
            var (main, service) = Behind();
            Assert.True((bool)Call(service, "DisplayMirrorsSuspended"));
            main.Settings.EnablePollingOnFocusLoss = true;
            Assert.False((bool)Call(service, "DisplayMirrorsSuspended"));

            string tick = Text("PadForge.App", "Services", "InputService.cs");
            Assert.Contains("bool skipPadVmMirrors = DisplayMirrorsSuspended();", tick);
        }

        /// <summary>Home's cards stand still behind a game while the setting
        /// is off, and read what is held again with it on.</summary>
        [Fact]
        public void HomesCards_StandStillBehindAGame_WhenTheSettingSaysSo()
        {
            var (main, service) = Behind();
            var card = new SlotSummary(0);
            var picture = Swatch();
            card.SetPicture("p", VirtualControllerType.Xbox, "", false, picture, picture, false,
                new[] { new StillMark("ButtonA", new Rect(0.6, 0.4, 0.05, 0.08)) });
            main.Dashboard.SlotSummaries.Add(card);
            main.Pads[0].ButtonA = true;

            Call(service, "UpdateHomeHeld");
            Assert.Equal(new byte[] { 0 }, card.HeldLevels);

            main.Settings.EnablePollingOnFocusLoss = true;
            Call(service, "UpdateHomeHeld");
            Assert.Equal(new byte[] { 15 }, card.HeldLevels);
        }

        /// <summary>A compact card's "+N more" note is made when the count
        /// changes, never again for the same count on every tick.</summary>
        [Fact]
        public void TheMoreNote_IsMadeOnlyWhenTheCountChanges()
        {
            var devices = new ObservableCollection<PadViewModel.MappedDeviceInfo>
            {
                new() { InstanceGuid = Guid.NewGuid(), Name = "Pad", IsOnline = true },
                new() { InstanceGuid = Guid.NewGuid(), Name = "Keys", IsOnline = true },
            };
            var card = new SlotSummary(0) { MappedDevices = devices };
            string more = Strings.Instance.Common_MoreCount_Format;
            card.ReadFirstDevice();
            Assert.Equal(string.Format(more, 1), card.FirstDeviceNote);

            card.ReadFirstDevice();
            long before = GC.GetAllocatedBytesForCurrentThread();
            card.ReadFirstDevice();
            Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);

            devices.Add(new PadViewModel.MappedDeviceInfo { InstanceGuid = Guid.NewGuid(), Name = "Wheel", IsOnline = true });
            card.ReadFirstDevice();
            Assert.Equal(string.Format(more, 2), card.FirstDeviceNote);
            devices.RemoveAt(2);
            card.ReadFirstDevice();
            Assert.Equal(string.Format(more, 1), card.FirstDeviceNote);
        }

        /// <summary>A slot's device that has no record left is named in the
        /// language in use, from the strings.</summary>
        [Fact]
        public void AnUnknownDevice_IsNamedFromTheStrings()
        {
            string service = Text("PadForge.App", "Services", "InputService.cs");
            Assert.DoesNotContain("\"Unknown device\"", service);
            Assert.Contains("LocalizedDeviceName(ud) ?? Strings.Instance.Mapping_UnknownDevice;", service);
            Assert.Contains("<data name=\"Mapping_UnknownDevice\" xml:space=\"preserve\"><value>Unknown device</value></data>",
                Text("PadForge.App", "Resources", "Strings", "Strings.resx"));
        }

        /// <summary>A profile with no slots, given no slots again, says
        /// nothing: its card and its pictures stay as they are.</summary>
        [Fact]
        public void AProfileWithNoSlots_GivenNoneAgain_SaysNothing()
        {
            var item = new ProfileListItem();
            var said = new List<string>();
            item.PropertyChanged += (_, e) => said.Add(e.PropertyName);
            item.SetTopology(null, null);
            item.SetTopology(null, null);
            Assert.Empty(said);
            Assert.True(item.HasNoSlots);

            // Slots and then none again still tell the card.
            item.SetTopology(new[] { true }, new[] { (int)VirtualControllerType.Xbox });
            Assert.Contains(nameof(ProfileListItem.Controllers), said);
            said.Clear();
            item.SetTopology(null, null);
            Assert.Equal(new[] { nameof(ProfileListItem.Controllers), nameof(ProfileListItem.HasNoSlots) }, said);
            Assert.True(item.HasNoSlots);
        }

        /// <summary>When the computer's drawing tier changes, as when a
        /// remote desktop session starts, each card says again whether it
        /// shows its model moving, Home asks every card, and a card's stage
        /// builds or drops its model for the new tier.</summary>
        [Fact]
        public void ANewDrawingTier_IsToldToEveryCard()
        {
            var dashboard = new MainViewModel().Dashboard;
            var card = new SlotSummary(0);
            dashboard.SlotSummaries.Add(card);
            var said = new List<string>();
            card.PropertyChanged += (_, e) => said.Add(e.PropertyName);
            Call(dashboard, "RenderTierChanged");
            Assert.Equal(new[] { nameof(SlotSummary.ShowsLiveModel), nameof(SlotSummary.PictureMoves) }, said);

            string home = Text("PadForge.App", "Views", "HomePage.xaml.cs");
            Assert.Contains("System.Windows.Media.RenderCapability.TierChanged += OnRenderTierChanged;", home);
            Assert.Contains("System.Windows.Media.RenderCapability.TierChanged -= OnRenderTierChanged;", home);
            string stage = Text("PadForge.App", "Controls", "LiveStage.cs");
            Assert.Contains("RenderCapability.TierChanged += OnTierChanged;", stage);
            Assert.Contains("RenderCapability.TierChanged -= OnTierChanged;", stage);
        }
    }
}
