using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Xml.Linq;
using PadForge.Common;
using PadForge.Resources.Strings;
using PadForge.Services;
using PadForge.Services.Automation;
using PadForge.ViewModels;
using PadForge.Views;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The selected device's panel on Devices: the class each driver button
    /// asks with, the folder note under a touchpad's switch, and the script
    /// ID every selected device shows (#597, #600).
    /// </summary>
    [Collection("WpfApplicationSingleton")]
    public class DevicesPanelTests : IDisposable
    {
        private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        private const string Exe = @"C:\Users\Public\Downloads\PadForge.exe";

        public void Dispose()
        {
        }

        private static string PageText()
            => File.ReadAllText(Path.Combine(ReachMarkup.AppRoot, "Views", "DevicesPage.xaml"));

        /// <summary>The reason of the nearest Asks on the way up, as
        /// Reach.Of reads it.</summary>
        private static ReachReason WhyOf(XElement e)
        {
            for (var at = e; at != null; at = at.Parent)
                if (ReachMarkup.Attr(at, "Reach.Class") == "Asks")
                    return Enum.TryParse(ReachMarkup.Attr(at, "Reach.Why"), out ReachReason why) ? why : ReachReason.None;
            return ReachReason.None;
        }

        /// <summary>The device tools' bind and restore move a device that
        /// does not hide (an Xbox 360 pad's Chatpad, a Wii U adapter) to
        /// PadForge's WinUSB driver and back: a driver change, which the
        /// card names as one. The Input Hiding pair, a keyboard's or a
        /// mouse's, is Use Only in PadForge.</summary>
        [Fact]
        public void TheDeviceToolsDriverPair_AsksAsADriverChange_AndTheHidingPairAsUseOnly()
        {
            var read = ReachMarkup.Read(PageText());
            bool InTools(XElement e) => e.Ancestors().Any(a => (string)a.Attribute(Xaml + "Name") == "DeviceTools");
            var driverButtons = read.Where(r => r.Element.Name.LocalName == "Button" && (ReachMarkup.Attr(r.Element, "Click") ?? string.Empty).Contains("Driver", StringComparison.Ordinal)).ToList();
            var tools = driverButtons.Where(r => InTools(r.Element)).ToList();
            var hiding = driverButtons.Where(r => !InTools(r.Element)).ToList();
            Assert.Equal(2, tools.Count);
            Assert.Equal(2, hiding.Count);
            Assert.All(tools, r =>
            {
                Assert.Equal((ReachClass.Asks, ReachReason.Drivers), (r.Class, WhyOf(r.Element)));
                Assert.Equal(Strings.Instance.Automation_Ask_Drivers, AssistantToolWords.Reason(WhyOf(r.Element)));
            });
            Assert.All(hiding, r => Assert.Equal((ReachClass.Asks, ReachReason.UseOnly), (r.Class, WhyOf(r.Element))));
            // Each pair has handlers of its own, so the ask-first sweep pins
            // each by what it reaches.
            Assert.Empty(tools.Select(r => ReachMarkup.Attr(r.Element, "Click")).Intersect(hiding.Select(r => ReachMarkup.Attr(r.Element, "Click"))));
        }

        /// <summary>A device that is not connected has no readout, so its
        /// Test Inputs card stays hidden, and a script can still name it
        /// (AutomationDevices lists it with online 0): its panel shows the
        /// script ID with Copy all the same (#600 item 26).</summary>
        [Fact]
        public void ADeviceThatIsNotConnected_ShowsItsScriptId_WithCopy()
        {
            PageRenderDumpPages.WithApp((app, dispatcher) =>
            {
                var vm = new DevicesViewModel();
                var away = new DeviceRowViewModel
                {
                    InstanceGuid = Guid.NewGuid(), DeviceName = "DualShock 3", ProductName = "DualShock 3",
                    DeviceTypeKey = "Gamepad", DevicePath = "XInput#1", IsOnline = false,
                };
                vm.Devices.Add(away);
                vm.RefreshCounts();
                var page = new DevicesPage { DataContext = vm };
                var window = new Window { Width = 1280, Height = 1400, ShowActivated = false, ShowInTaskbar = false, Left = -10000, Top = -10000, Content = page };
                try
                {
                    window.Show();
                    vm.SelectedDevice = away;
                    foreach (string fold in new[] { "DetailsFold", "TestMore" })
                        ((System.Windows.Controls.Expander)page.FindName(fold)).IsExpanded = true;
                    PageRenderDump.Settle(dispatcher);
                    window.UpdateLayout();
                    Assert.False(((FrameworkElement)page.FindName("TestCard")).IsVisible, "control: a device with no readout shows no Test Inputs card");
                    FrameworkElement Find(string id) => Assert.Single(PadForge.Search.SearchLocator.LogicalDescendants(page).OfType<FrameworkElement>(),
                        e => System.Windows.Automation.AutomationProperties.GetAutomationId(e) == id);
                    var scriptId = (System.Windows.Controls.TextBlock)Find("Devices_ScriptId");
                    Assert.Equal(away.ScriptId, scriptId.Text);
                    Assert.True(scriptId.IsVisible, "the script ID is not shown");
                    Assert.True(Find("Devices_CopyScriptId").IsVisible, "Copy is not shown");
                }
                finally
                {
                    window.Close();
                }
            }, TimeSpan.FromMinutes(3), "The script ID test timed out.");
        }
    }
}
