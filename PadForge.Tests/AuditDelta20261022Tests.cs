using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Engine.RemoteLink;
using PadForge.Resources.Strings;
using PadForge.ViewModels;
using PadForge.Views;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The delta audit of 1e4775ec..fbc85214 (Oct. 13 to Oct. 22).
    ///
    /// <para>The capacitive sensor mask (31bb0c65) is a field of the device
    /// record that two of its copies never carried: the Remote Link device
    /// list, so a peer's copy of a Steam Deck or a 2026 Steam Controller
    /// listed no sensor under the pad and its Valve automap bound none, and
    /// the registry signature, which DeviceRegistryPersistenceTests holds.
    /// The touch sensor capsules and the Keyboard + Mouse preview built
    /// their labels once and kept them through a language switch.</para>
    /// </summary>
    [Collection("CultureSwitching")]
    public class AuditDelta20261022Tests
    {
        private static RemotePeerDeviceInfo Pad(string id, int capSense = 0) => new()
        {
            Slot = 0,
            PeerLocalDeviceId = id,
            Name = "Steam Deck",
            VendorId = 0x28DE,
            ProductId = 0x1205,
            NumAxes = 6,
            NumButtons = 22,
            RawButtonCount = 22,
            SupportedButtonIndices = Enumerable.Range(0, 18).ToArray(),
            SupportedAxisIndices = Enumerable.Range(0, 6).ToArray(),
            InputDeviceType = InputDeviceType.Gamepad,
            CapSenseChannelMask = capSense,
        };

        /// <summary>Where the v12 tail begins: the same list with no mask set
        /// encodes up to there and stops.</summary>
        private static int CapSenseTailStart(IEnumerable<RemotePeerDeviceInfo> devices)
            => LinkConnection.EncodeDeviceList(devices.Select(d =>
            {
                var bare = d.CloneForSlot(d.Slot);
                bare.CapSenseChannelMask = 0;
                return bare;
            }).ToList(), "").Length;

        [Fact]
        public void APadsCapacitiveSensorsCrossTheWire()
        {
            var plain = Pad("pad");
            var deck = Pad("deck", 0x3);
            var triton = Pad("triton", 0xF);
            var devices = new[] { plain, deck, triton };

            var payload = LinkConnection.EncodeDeviceList(devices, "");
            var list = LinkConnection.DecodeDeviceList(payload);
            Assert.Equal(0, list[0].CapSenseChannelMask);
            Assert.Equal(0x3, list[1].CapSenseChannelMask);
            Assert.Equal(0xF, list[2].CapSenseChannelMask);

            // The tail is the last thing on the wire: an older sender stops
            // before it, and its rows read as having no sensor.
            int start = CapSenseTailStart(devices);
            Assert.Equal(0xED, payload[start]);
            var older = LinkConnection.DecodeDeviceList(payload.Take(start).ToArray());
            Assert.All(older, d => Assert.Equal(0, d.CapSenseChannelMask));
            Assert.Equal(3, older.Count);

            // A record naming a row past the list is refused whole.
            var broken = (byte[])payload.Clone();
            broken[start + 2] = 9;
            var refused = LinkConnection.DecodeDeviceList(broken);
            Assert.All(refused, d => Assert.Equal(0, d.CapSenseChannelMask));
            Assert.Equal(3, refused.Count);
        }

        /// <summary>A list with no sensor anywhere is byte for byte what it was
        /// before the tail, so an older peer sees nothing new.</summary>
        [Fact]
        public void AListWithNoSensorCarriesNoTail()
        {
            var devices = new[] { Pad("a"), Pad("b") };
            var payload = LinkConnection.EncodeDeviceList(devices, "");
            Assert.Equal(CapSenseTailStart(devices), payload.Length);
        }

        /// <summary>The tail rides after the DualShock 3 identities, whose
        /// decoder now marks the cursor unreliable when it fails, as the v9
        /// and v10 decoders do. A decoder that refused a record cannot vouch
        /// for where its tail ended, so a broken identity costs the masks
        /// rather than reading them from what may be the wrong place.</summary>
        [Fact]
        public void TheTailFollowsTheIdentities()
        {
            var ds3 = Pad("ds3");
            ds3.VendorId = 0x054C;
            ds3.ProductId = 0x0268;
            ds3.Ds3Identity = "001fe2a1b2c3/direct";
            var deck = Pad("deck", 0x3);
            var devices = new[] { ds3, deck };
            var payload = LinkConnection.EncodeDeviceList(devices, "");
            var list = LinkConnection.DecodeDeviceList(payload);
            Assert.Equal("001fe2a1b2c3/direct", list[0].Ds3Identity);
            Assert.Equal(0x3, list[1].CapSenseChannelMask);

            // Empty the identity: its record fails the identity check with
            // the cursor on the v12 magic, where a decoder that went on would
            // read the masks.
            int start = CapSenseTailStart(devices);
            int len = System.Text.Encoding.UTF8.GetByteCount(ds3.Ds3Identity);
            Assert.Equal(new byte[] { (byte)len, 0 }, payload[(start - len - 2)..(start - len)]);
            var emptied = payload[..(start - len - 2)].Concat(new byte[] { 0, 0 }).Concat(payload[start..]).ToArray();
            Assert.Equal(0xED, emptied[start - len]);
            var refused = LinkConnection.DecodeDeviceList(emptied);
            Assert.Equal(2, refused.Count);
            Assert.Null(refused[0].Ds3Identity);
            Assert.Equal(0, refused[1].CapSenseChannelMask);
        }

        /// <summary>The peer's copy of the pad answers with the owner's mask,
        /// and its device record takes it on arrival, which is what the pad's
        /// own picker group and the Valve automap read.</summary>
        [Fact]
        public void APeersCopyOfAPadReportsItsSensors()
        {
            var info = LinkConnection.DecodeDeviceList(LinkConnection.EncodeDeviceList(new[] { Pad("deck", 0x3) }, "")).Single();
            info.PeerFingerprintHex = "0123456789abcdef0123456789abcdef";
            using var copy = new RemotePeerDevice(info);
            Assert.Equal(0x3, ((ISdlInputDevice)copy).CapSenseChannelMask);
            var row = new UserDevice();
            row.LoadFromExternalDevice(copy);
            Assert.Equal(0x3, row.CapSenseChannelMask);
        }

        /// <summary>The owner fills the mask from its own device, and a peer
        /// refreshes it on a row it already has, with the other facts the
        /// owner's periodic list carries.</summary>
        [Fact]
        public void TheOwnerSendsTheMaskAndThePeerRefreshesIt()
        {
            string service = System.IO.File.ReadAllText(Repo("PadForge.App", "Services", "InputService.cs"));
            Assert.Contains("CapSenseChannelMask = dev.CapSenseChannelMask,", service);

            string server = System.IO.File.ReadAllText(Repo("PadForge.Engine", "RemoteLink", "LinkServer.cs"));
            int reconcile = server.IndexOf("private void ReconcileRemoteDevices(", StringComparison.Ordinal);
            int end = server.IndexOf("private const string ListTooLarge", reconcile, StringComparison.Ordinal);
            Assert.True(reconcile >= 0 && end > reconcile, "the reconcile moved");
            Assert.Contains("existing.Info.CapSenseChannelMask = info.CapSenseChannelMask;", server[reconcile..end]);
        }

        // ── Labels that follow a language switch ──────────────────────────

        /// <summary>A touch capsule's label and tooltip follow a language
        /// switch. PadViewModel's own refresh raises none of the properties
        /// the strip rebuilds on, so the capsules kept the old language until
        /// the slot's type or profile changed.</summary>
        [Fact]
        public void TheTouchCapsulesFollowALanguageSwitch()
        {
            RunSta(() =>
            {
                var before = CultureInfo.CurrentUICulture;
                Window window = null;
                try
                {
                    Strings.ChangeCulture(CultureInfo.GetCultureInfo("en"));
                    var vm = new PadViewModel(0) { OutputType = VirtualControllerType.Extended };
                    // The field, not the setter: a live profile change moves
                    // the slot's bindings through the settings service, which
                    // the test host has none of, and the strip reads only the
                    // type and the profile.
                    typeof(PadViewModel).GetField("_profileId", BindingFlags.Instance | BindingFlags.NonPublic)
                        .SetValue(vm, "steam-controller-2");
                    var strip = new TouchSensorStrip { DataContext = vm };
                    window = Show(strip);
                    var english = Capsule(strip, 0);
                    Assert.Equal(Strings.Instance.Mapping_CapSenseLeftStickTouch, english.Text);

                    Strings.ChangeCulture(CultureInfo.GetCultureInfo("de"));
                    Pump(window);
                    var german = Capsule(strip, 0);
                    Assert.NotEqual(english.Text, german.Text);
                    Assert.Equal(Strings.Instance.Mapping_CapSenseLeftStickTouch, german.Text);
                    Assert.Equal(german.Text, ((Border)german.Parent).ToolTip);
                }
                finally
                {
                    window?.Close();
                    Strings.ChangeCulture(before);
                }
            });
        }

        /// <summary>The Keyboard + Mouse preview's tooltips follow a language
        /// switch. It built them once and redrew only for a new slot, type or
        /// device, so they kept the old language until another slot was
        /// picked.</summary>
        [Fact]
        public void TheKeyboardMousePreviewFollowsALanguageSwitch()
        {
            RunSta(() =>
            {
                var before = CultureInfo.CurrentUICulture;
                Window window = null;
                try
                {
                    Strings.ChangeCulture(CultureInfo.GetCultureInfo("en"));
                    var vm = new PadViewModel(0) { OutputType = VirtualControllerType.KeyboardMouse };
                    var view = new KBMPreviewView();
                    view.Resources["BodyFontFamily"] = new FontFamily("Segoe UI");
                    window = Show(view);
                    view.Bind(vm);
                    Pump(window);
                    string english = MoveTip(view);
                    Assert.Equal(Strings.Instance.Pad_MouseMovement, english);

                    Strings.ChangeCulture(CultureInfo.GetCultureInfo("de"));
                    Pump(window);
                    string german = MoveTip(view);
                    Assert.NotEqual(english, german);
                    Assert.Equal(Strings.Instance.Pad_MouseMovement, german);
                    view.Unbind();
                }
                finally
                {
                    window?.Close();
                    Strings.ChangeCulture(before);
                }
            });
        }

        private static TextBlock Capsule(TouchSensorStrip strip, int index)
            => (TextBlock)((Border)((StackPanel)strip.Content).Children[index]).Child;

        private static string MoveTip(KBMPreviewView view)
        {
            var field = typeof(KBMPreviewView).GetField("_moveCircle", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(field);
            return ((Ellipse)field.GetValue(view))?.ToolTip as string;
        }

        private static Window Show(UIElement content)
        {
            var window = new Window
            {
                Content = content, Width = 700, Height = 300, ShowActivated = false, ShowInTaskbar = false,
                Left = -10000, Top = -10000, WindowStyle = WindowStyle.None,
            };
            window.Show();
            Pump(window);
            return window;
        }

        private static void Pump(Window window)
            => window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

        private static void RunSta(Action body)
        {
            Exception failure = null;
            var t = new Thread(() =>
            {
                try { body(); }
                catch (Exception ex) { failure = ex; }
                finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.IsBackground = true;
            t.Start();
            Assert.True(t.Join(20000), "STA test body timed out");
            if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }

        private static string Repo(params string[] parts)
        {
            var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "PadForge.sln")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return System.IO.Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
        }
    }
}
