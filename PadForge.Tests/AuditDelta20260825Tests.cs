using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using Xunit;
using PadForge.Common.Input;
using PadForge.Engine.Common;
using PadForge.ViewModels;

namespace PadForge.Tests
{
    /// <summary>
    /// Contracts locked by the 2026-08-25 delta audit (f2682b73..HEAD).
    /// Each test fails if its fix is removed, which is the only thing that
    /// makes the suite evidence rather than ceremony.
    /// </summary>
    /// <summary>The firewall rule is written, not read back. Parsing netsh's
    /// own dump to decide whether the rule exists cannot be done portably: the
    /// labels come from the firewall's MUI resources and are translated on a
    /// localized Windows, so a label match silently never fires and every call
    /// adds another rule. Delete-then-add needs no parsing, is idempotent, and
    /// sweeps up the per-port pile-up the old path built.</summary>
    public class AuditDelta20260825FirewallTests
    {
        private static string RepoText(params string[] parts)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PadForge.sln")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return File.ReadAllText(Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray()));
        }

        [Fact]
        public void TheRuleIsDeletedByNameBeforeItIsAdded()
        {
            string web = RepoText("PadForge.App", "Services", "WebControllerServer.cs");
            int at = web.IndexOf("internal static void EnsureInboundFirewallRule", StringComparison.Ordinal);
            Assert.True(at > 0);
            string body = web.Substring(at, 2200);
            int del = body.IndexOf("firewall delete rule", StringComparison.Ordinal);
            int add = body.IndexOf("firewall add rule", StringComparison.Ordinal);
            Assert.True(del > 0, "the rule is never deleted, so per-port duplicates accumulate");
            Assert.True(add > del, "the add must follow the delete");
        }

        [Fact]
        public void TheRuleDumpIsNeverReadBack()
        {
            // The real contract: nothing parses netsh output. Both earlier
            // shapes did, and both were wrong. The substring test matched
            // "42" inside "4242"; the LocalPort label match reads an
            // English-only label and silently never fires elsewhere.
            string web = RepoText("PadForge.App", "Services", "WebControllerServer.cs");
            Assert.DoesNotContain("RuleNamesPort", web);
            Assert.DoesNotContain("check.Contains(port.ToString())", web);
            int at = web.IndexOf("internal static void EnsureInboundFirewallRule", StringComparison.Ordinal);
            string body = web.Substring(at, 2200);
            Assert.DoesNotContain("firewall show rule", body);
        }
    }

    /// <summary>The _WDG parser is the only thing standing between a stored
    /// definition and a WMI class the firmware never declared, so its bounds
    /// are a safety contract, not a parsing detail.</summary>
    public class AuditDelta20260825WdgBoundTests
    {
        private static readonly Guid EventGuid = new("8FC0DE0C-B4E4-43FD-B0F3-8871711C1294");

        /// <summary>A Name(_WDG, Buffer(declared) { … }) whose declared size
        /// is honest, followed by unrelated AML the parser must not read.</summary>
        private static byte[] Table(int declaredSize, int actualEntries, int pkgLenOverride = -1, int trailing = 64)
        {
            var body = new List<byte>();
            for (int i = 0; i < actualEntries; i++)
            {
                body.AddRange(EventGuid.ToByteArray());
                body.Add(0x80);             // notify id
                body.Add(0x00);             // object id tail
                body.Add(0x01);             // instance count
                body.Add(AcpiWmi.FlagEvent);
            }
            // BufferSize as a WordConst so the size is explicit.
            var contents = new List<byte> { 0x0B, (byte)(declaredSize & 0xFF), (byte)(declaredSize >> 8) };
            contents.AddRange(body);
            int pkgLen = pkgLenOverride >= 0 ? pkgLenOverride : contents.Count + 1;
            var aml = new List<byte>();
            aml.AddRange(Encoding.ASCII.GetBytes("_WDG"));
            aml.Add(0x11);                  // BufferOp
            aml.Add((byte)(pkgLen & 0x3F)); // one-byte PkgLength
            aml.AddRange(contents);
            // Trailing AML that must never be read as a guid_block.
            aml.AddRange(Enumerable.Repeat((byte)0xAB, trailing));
            return aml.ToArray();
        }

        [Fact]
        public void AnHonestBufferParses()
        {
            var blocks = new List<AcpiWmi.Block>();
            AcpiWmi.ParseWdg(Table(declaredSize: 20, actualEntries: 1), blocks);
            Assert.Single(blocks);
            Assert.Equal(EventGuid, blocks[0].Guid);
            Assert.True(blocks[0].IsEvent);
        }

        [Fact]
        public void ABufferSizeReachingPastItsOwnPackageReadsOnlyWhatThePackageHolds()
        {
            // One real entry, a declared size claiming four. ACPI 19.6.10
            // zero-fills a Buffer whose declared size exceeds its ByteList,
            // so this shape is legal and its real block must survive. What
            // must NOT happen is the parser walking into the trailing AML
            // and handing three invented GUIDs to the subscription gate.
            var blocks = new List<AcpiWmi.Block>();
            AcpiWmi.ParseWdg(Table(declaredSize: 80, actualEntries: 1), blocks);
            Assert.Single(blocks);
            Assert.Equal(EventGuid, blocks[0].Guid);
        }

        [Fact]
        public void TrailingAmlIsNeverReadAsAGuidBlock()
        {
            // The overrun case with NOTHING real in it: a declared size of
            // four entries in a package that holds none. Dropping the bound
            // turns the 0xAB padding into blocks.
            var blocks = new List<AcpiWmi.Block>();
            AcpiWmi.ParseWdg(Table(declaredSize: 80, actualEntries: 0), blocks);
            Assert.Empty(blocks);
        }

        [Fact]
        public void APackageLengthPastTheEndOfTheTableIsRejected()
        {
            var blocks = new List<AcpiWmi.Block>();
            // A one-byte PkgLength maxes at 63, so the table has to be
            // short enough for 63 to reach past its end.
            AcpiWmi.ParseWdg(Table(declaredSize: 20, actualEntries: 1, pkgLenOverride: 0x3F, trailing: 0), blocks);
            Assert.Empty(blocks);
        }
    }

    /// <summary>Per-device configs are anchored by reference: the tab edits
    /// one instance. Every path that drops an instance has to move the anchor
    /// or the user edits an object nothing reads.</summary>
    [Collection("SettingsManagerStatics")]
    public class AuditDelta20260825RekeyAnchorTests
    {
        [Fact]
        public void RekeyMovesTheInstanceAndTheAnchorFollows()
        {
            var vm = new PadViewModel(0);
            var oldGuid = Guid.NewGuid();
            var newGuid = Guid.NewGuid();
            var cfg = vm.GetOrCreateDeviceConfig(oldGuid);
            vm.DeviceConfig = cfg;

            vm.RekeyDeviceConfig(oldGuid, newGuid);

            Assert.False(vm.PerDeviceSlotConfigs.ContainsKey(oldGuid));
            Assert.Same(cfg, vm.PerDeviceSlotConfigs[newGuid]);
            Assert.Same(cfg, vm.DeviceConfig);
        }

        [Fact]
        public void OnACollisionTheAnchorMovesToTheWinner()
        {
            // Destination wins by policy. The instance the tab was bound to
            // is the loser, so the tab has to follow the winner instead of
            // keeping an orphan.
            var vm = new PadViewModel(0);
            var oldGuid = Guid.NewGuid();
            var newGuid = Guid.NewGuid();
            var loser = vm.GetOrCreateDeviceConfig(oldGuid);
            var winner = vm.GetOrCreateDeviceConfig(newGuid);
            winner.AudioEqEnabled = true;
            vm.DeviceConfig = loser;

            vm.RekeyDeviceConfig(oldGuid, newGuid);

            Assert.False(vm.PerDeviceSlotConfigs.ContainsKey(oldGuid));
            Assert.Same(winner, vm.PerDeviceSlotConfigs[newGuid]);
            Assert.Same(winner, vm.DeviceConfig);
            Assert.NotSame(loser, vm.DeviceConfig);
        }

        [Fact]
        public void ACollisionThatDoesNotTouchTheAnchorLeavesItAlone()
        {
            // Positive control: without this, "the anchor moved" would be
            // vacuously true for a VM whose anchor was never the loser.
            var vm = new PadViewModel(0);
            var oldGuid = Guid.NewGuid();
            var newGuid = Guid.NewGuid();
            var thirdGuid = Guid.NewGuid();
            vm.GetOrCreateDeviceConfig(oldGuid);
            vm.GetOrCreateDeviceConfig(newGuid);
            var bound = vm.GetOrCreateDeviceConfig(thirdGuid);
            vm.DeviceConfig = bound;

            vm.RekeyDeviceConfig(oldGuid, newGuid);

            Assert.Same(bound, vm.DeviceConfig);
        }
    }

    /// <summary>The head tracker reads its config version before the settings
    /// that version stamps, so a change can never be captured as already
    /// applied.</summary>
    public class AuditDelta20260825HeadTrackerConfigTests
    {
        [Fact]
        public void ADeviceBuiltFromSettingsCarriesTheCurrentPortAndVersion()
        {
            int savedPort = HeadTrackingRuntime.UdpPort;
            bool savedFt = HeadTrackingRuntime.FreeTrackEnabled;
            try
            {
                HeadTrackingRuntime.UdpPort = 4243;
                HeadTrackingRuntime.FreeTrackEnabled = false;
                int version = HeadTrackingRuntime.Version;

                using var dev = HeadTrackerDevice.FromCurrentSettings();

                Assert.Equal(4243, dev.UdpPort);
                Assert.False(dev.FreeTrackEnabled);
                Assert.Equal(version, dev.ConfigVersion);
            }
            finally
            {
                HeadTrackingRuntime.UdpPort = savedPort;
                HeadTrackingRuntime.FreeTrackEnabled = savedFt;
            }
        }

        [Fact]
        public void AConfigChangeBumpsTheVersionSoTheSweepReopens()
        {
            int savedPort = HeadTrackingRuntime.UdpPort;
            try
            {
                using var before = HeadTrackerDevice.FromCurrentSettings();
                HeadTrackingRuntime.UdpPort = savedPort == 4242 ? 4244 : 4242;
                Assert.NotEqual(before.ConfigVersion, HeadTrackingRuntime.Version);
            }
            finally { HeadTrackingRuntime.UdpPort = savedPort; }
        }

        [Fact]
        public void FreeTrackFailedIsFalseWhenFreeTrackWasNeverAskedFor()
        {
            using var dev = new HeadTrackerDevice(4242, freeTrack: false, configVersion: 0, now: null);
            Assert.False(dev.FreeTrackFailed);
        }
    }

    /// <summary>Source-text locks for wiring that has no runtime seam. Each
    /// one names a defect that shipped: a setting absent from the dirty gate
    /// is silently discarded, and a control gated on the model alone shows on
    /// a transport that cannot carry its byte.</summary>
    public class AuditDelta20260825WiringTests
    {
        private static string RepoFile(params string[] parts)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PadForge.sln")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return File.ReadAllText(Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray()));
        }

        [Fact]
        public void EverySettingsPropertyTheSaveWritesAlsoReachesTheDirtyGate()
        {
            // The dirty-gate trap: load, save and the DTO all carry a
            // setting while the MarkDirty allowlist does not, so the value
            // works all session and reverts on restart.
            string mainWindow = RepoFile("PadForge.App", "MainWindow.xaml.cs");
            foreach (string prop in new[]
            {
                "AssignOfferNewDevice", "AssignOfferEmptySlot",
                // The sibling set of the same defect: these three sit
                // directly above the assign-offer pair in the Settings page
                // and had the identical gap.
                "BatteryNotifyEnabled", "BatteryNotifyThreshold", "BatteryNotifyVibrate",
                "HandheldButtonsEnabled",
            })
                Assert.Contains("nameof(SettingsViewModel." + prop + ")", mainWindow);
            // Head tracking moved to the Dashboard (#355 move), so its five
            // ride the Dashboard allowlist with the DSU server's pair.
            foreach (string prop in new[]
            {
                "HeadTrackingEnabled", "HeadTrackingUdpPort", "HeadTrackingFreeTrack",
                "HeadTrackingRotationRange", "HeadTrackingTranslationRange",
            })
                Assert.Contains("nameof(DashboardViewModel." + prop + ")", mainWindow);
        }

        [Fact]
        public void TheAudioBufferSliderIsGatedOnTheTransportItsByteRidesOn()
        {
            string padPage = RepoFile("PadForge.App", "Views", "PadPage.xaml");
            Assert.Contains("SelectedDeviceHasDs5AudioBuffer", padPage);
            string vm = RepoFile("PadForge.App", "ViewModels", "PadViewModel.cs");
            // The gate must test the Bluetooth transport, not the PID alone.
            int at = vm.IndexOf("public bool SelectedDeviceHasDs5AudioBuffer", StringComparison.Ordinal);
            Assert.True(at > 0);
            Assert.Contains("{00001124", vm.Substring(at, 900));
        }

        [Fact]
        public void TheWmiSubscribeConsultsTheFirmwareGateItself()
        {
            // The guard belongs to the operation, not to one of its callers:
            // a caller written later has to inherit it.
            string runtime = RepoFile("PadForge.App", "Common", "Input", "WmiEventRuntime.cs");
            int at = runtime.IndexOf("public static void Sync(", StringComparison.Ordinal);
            Assert.True(at > 0);
            string body = runtime.Substring(at, 3000);
            Assert.Contains("EnumerateEventClasses()", body);
            Assert.Contains("refusing to watch WMI class", body);
            // A refused class never enters _watchers, so without a memory of
            // the refusal the sweep re-asks and re-logs it every four
            // seconds for the life of the process.
            Assert.Contains("_refused.Add(cls)", body);
        }

        [Fact]
        public void TurningTheHandheldFeatureOffStopsTheChordRuntime()
        {
            string step1 = RepoFile("PadForge.App", "Common", "Input", "InputManager.Step1.UpdateDevices.cs");
            int at = step1.IndexOf("private bool UpdateHandheldDevices()", StringComparison.Ordinal);
            Assert.True(at > 0);
            int end = step1.IndexOf("private void HandheldSweep()", at, StringComparison.Ordinal);
            Assert.True(end > at);
            string method = step1.Substring(at, end - at);

            // The off branch retires the rows AND stops the chord runtime.
            Assert.Contains("changed |= RetireHandheldRows();", method);
            Assert.Contains("HandheldChordRuntime.Stop()", method);

            // And the stop happens OUTSIDE _handheldLock, the way
            // ShutdownHandheldInputs does it. This method runs on the poll
            // thread and Stop joins a worker, so holding the lock across it
            // would nest two locks and stall the thread holding the rate.
            int stop = method.IndexOf("HandheldChordRuntime.Stop()", StringComparison.Ordinal);
            int depth = 0, lockDepth = -1;
            for (int i = 0; i < stop; i++)
            {
                if (method[i] == '{') depth++;
                else if (method[i] == '}') { depth--; if (depth < lockDepth) lockDepth = -1; }
                else if (lockDepth < 0 && method.AsSpan(i).StartsWith("lock (_handheldLock)")) lockDepth = depth + 1;
            }
            Assert.True(lockDepth < 0, "the chord stop runs while _handheldLock is held");
        }
    }

    /// <summary>The house rule is Title Case for the two locales that use it.
    /// The 4.3.2 sweep did en and missed three pt-BR values.</summary>
    public class AuditDelta20260825CasingTests
    {
        private static string Value(string locale, string key)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PadForge.sln")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            string file = locale == "en" ? "Strings.resx" : $"Strings.{locale}.resx";
            var doc = XDocument.Load(Path.Combine(dir.FullName, "PadForge.App", "Resources", "Strings", file));
            var node = doc.Root.Elements("data").FirstOrDefault(e => (string)e.Attribute("name") == key);
            Assert.NotNull(node);
            return node.Element("value").Value;
        }

        [Theory]
        [InlineData("ManageProfiles_DeleteTitle", "Excluir Perfil Importado?")]
        [InlineData("Pad_Combine_XOR_Name", "Apenas Uma")]
        [InlineData("Pad_Formula_Preset_AUnlessIdle_Name", "a a Menos que Ocioso")]
        public void ThePtBrTitleCaseSweepCoversTheseKeys(string key, string expected)
        {
            Assert.Equal(expected, Value("pt-BR", key));
        }

        [Fact]
        public void TheHeadTrackerStatusStringsExistInEveryLocale()
        {
            foreach (string loc in new[] { "en", "pt-BR", "es", "fr", "it", "nl", "de", "ja", "ko", "zh-Hans" })
                Assert.Contains("{0}", Value(loc, "HeadTracker_StatusFreeTrackFailed_Format"));
        }
    }
}
