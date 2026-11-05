using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Serialization;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Services;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// tools/capture_all.ps1 builds the settings file the published
    /// screenshots are taken from. These tests run the script's own code in
    /// Windows PowerShell, the host the harness runs in, and start no
    /// capture: the synthetic device injection against a fixture settings
    /// file, the check STEP 0 makes of the list it saved and a tail or
    /// Direct run makes of the file it reuses, the reach of the try/finally
    /// that puts the owner's settings back, and the line the reused-file
    /// block logs. The code is taken from the script through
    /// PowerShell's parser, so an edit to the script is what each test sees.
    /// The driver runs a statement only when every command in it is one that
    /// statement is known to need.
    ///
    /// <para>A capture run swaps the owner's settings file for one of its
    /// own, and three more scripts under tools write that file too: the
    /// diagnostics sweep and the two staging scripts. All four load the
    /// functions that guard it from settings_guard.ps1. Every screenshot,
    /// the web controller's pages and the colorways included, comes from
    /// capture_all.ps1 alone. The rules that keep the owner's file from
    /// being lost are held here by position in the syntax tree, with
    /// nothing of the four scripts run:
    /// the copy taken before a file is overwritten or deleted, the marker
    /// and the stop for a backup no marker speaks for, the order of the
    /// restore, and how a settings file is read and written. So is the exit
    /// code a run that skipped a shot or failed ends with.</para>
    /// </summary>
    public class CaptureHarnessSetupTests
    {
        // Locally administered addresses (the 0x02 bit of the first octet),
        // which no vendor assigns, stand in for the owner's real ones.
        private static readonly Guid SteamControllerGuid = new("5a1e0000-0000-4000-8000-000000000001");
        private static readonly Guid DualSenseEdgeGuid = new("5a1e0000-0000-4000-8000-000000000002");
        private static readonly Guid RealMoveGuid = new("5a1e0000-0000-4000-8000-000000000003");
        private static readonly Guid RealDs3Guid = new("5a1e0000-0000-4000-8000-000000000004");
        private const string SteamSerial = "020000000001";
        private const string EdgeSerial = "02-00-00-00-00-02";
        private const string MoveSerial = "020000000003";
        private const string RealDs3Serial = "020000000004";
        private const string Ds3Guid = "ffff1111-2222-3333-4444-555566667777";
        private const string DualSenseGuid = "bbbb2222-3333-4444-5555-666677778888";
        private const string NfcGuid = "22220000-2222-3333-4444-555566667777";
        private const string MidiGuid = "bbbb1111-2222-3333-4444-555566667777";
        private const string MoveGuid = "bbbb3333-3333-4444-5555-666677778888";

        /// <summary>
        /// No synthetic row keeps what the row it was cloned from records
        /// about its own hardware and settings. The template is the first row
        /// of the owner's list, and on 2026-10-08 the published analog
        /// keyboard, NFC reader and MIDI keyboard shots showed the owner's
        /// Steam Controller address as SERIAL, its SDL GUID and a Power
        /// section, and the NFC reader and MIDI keyboard had Hide from Games
        /// ticked.
        ///
        /// <para>The fixture has the shape of that list: a Bluetooth Steam
        /// Controller first, the row New-SyntheticDevice clones, and a
        /// DualSense Edge, the gamepad row New-WiiDevice clones. Every element
        /// UserDevice serializes is set on both to a value no new record
        /// holds, so a value that survives into a synthetic row is a leak
        /// whichever element it is, an element added to UserDevice later
        /// included. The elements each helper sets, and the gamepad shape
        /// New-WiiDevice keeps on purpose, are the only exceptions.</para>
        ///
        /// <para>The list also holds an NFC reader row under the synthetic's
        /// own GUID, as an interrupted run of the older script leaves one: a
        /// clone of the Steam Controller under the NFC reader's identity,
        /// still holding its serial, SDL GUID and settings. The script
        /// skipped a synthetic whose GUID was already in the list, so that
        /// row kept them. It is rebuilt like the rest, and the check STEP 0
        /// makes of the saved list passes.</para>
        ///
        /// <para>The DualShock 3 and DualSense stand-ins carry RFC 7042
        /// documentation addresses instead, so the dossier's SERIAL row and
        /// the Quick Charge row still draw for them.</para>
        ///
        /// <para>The script lists the GUIDs of its synthetic rows, because a
        /// run that reuses a staged file checks that file with no injection
        /// to ask. No real device in this fixture stands where a synthetic
        /// would go, so the injection builds every row, and the list has to
        /// name exactly those.</para>
        /// </summary>
        [Fact]
        public void SyntheticRowsKeepNothingTheirTemplatesRecordAboutThemselves()
        {
            var leftover = Leftover(NfcGuid, "NFC Reader", 0x072F, 0x2200, @"HID\VID_072F&PID_2200\dummy", InputDeviceType.Nfc);
            var run = RunDriver("inject", new SettingsFileData { Devices = new[] { SteamTemplate(), EdgeTemplate(), leftover } });
            AssertRan(run);

            XDocument inDoc = XDocument.Parse(run.InXml);
            XDocument outDoc = XDocument.Parse(run.OutXml);
            var templates = inDoc.Root.Element("Devices").Elements("Device").ToList();
            var rows = outDoc.Root.Element("Devices").Elements("Device").ToList();
            var data = Deserialize(run.OutXml);

            // Positive control: the owner's own rows come through untouched,
            // and the leftover row went in holding the Steam Controller's
            // values, so the fixture really carries what the checks look for.
            var steamOut = data.Devices.Single(d => d.InstanceGuid == SteamControllerGuid);
            Assert.Equal(SteamSerial, steamOut.SerialNumber);
            Assert.True(steamOut.HidHideEnabled);
            Assert.Equal(EdgeSerial, data.Devices.Single(d => d.InstanceGuid == DualSenseEdgeGuid).SerialNumber);
            var leftoverIn = templates.Single(t => (string)t.Element("InstanceGuid") == NfcGuid);
            Assert.Equal(SteamSerial, (string)leftoverIn.Element("SerialNumber"));
            Assert.Equal("true", (string)leftoverIn.Element("HidHideEnabled"));

            var synthetic = data.Devices
                .Where(d => d.InstanceGuid != SteamControllerGuid && d.InstanceGuid != DualSenseEdgeGuid)
                .ToList();
            foreach (string name in new[]
                     {
                         "Logitech G29 Driving Force Racing Wheel", "MIDI Keyboard", "NFC Reader",
                         "Razer Huntsman V3 Pro", "DualShock 3", "DualSense Wireless Controller",
                     })
                Assert.Contains(synthetic, d => d.ProductName == name);

            // One row per GUID, the leftover's included.
            Assert.Equal(rows.Count, rows.Select(r => (string)r.Element("InstanceGuid")).Distinct().Count());
            Assert.Contains(run.Strings("Host"), l => l.Contains(NfcGuid) && l.Contains("rebuilt"));

            // The script's list, read before the injection ran, names the
            // rows the injection built and no other, each once, and the
            // injection had nothing to add to it.
            var listed = run.Strings("Listed");
            foreach (var ud in synthetic)
                Assert.True(listed.Contains(ud.InstanceGuid.ToString(), StringComparer.OrdinalIgnoreCase),
                    $"{ud.ProductName} is injected and $script:SyntheticGuids does not list {ud.InstanceGuid}, so the check of a reused file would take the row for a real device");
            foreach (string guid in listed)
                Assert.True(synthetic.Any(d => d.InstanceGuid == new Guid(guid)),
                    $"$script:SyntheticGuids lists {guid} and the injection builds no such row");
            Assert.Equal(listed.Count, listed.Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Assert.Equal(listed, run.Strings("SyntheticGuids"));

            foreach (var ud in synthetic)
            {
                string who = ud.ProductName;
                bool gamepadClone = ud.CapType == InputDeviceType.Gamepad;

                // Every element a template carries either changed or is one
                // the helper sets or keeps on purpose.
                var row = rows.Single(r => (string)r.Element("InstanceGuid") == ud.InstanceGuid.ToString());
                var allowed = gamepadClone ? KeptByGamepadClone : SetBySyntheticClone;
                foreach (var template in templates)
                {
                    foreach (var element in template.Elements())
                    {
                        string tag = element.Name.LocalName;
                        if (allowed.Contains(tag)) continue;
                        var mine = row.Element(tag);
                        if (mine == null) continue;
                        Assert.False(XNode.DeepEquals(element, mine),
                            $"{who} keeps the {tag} of a template row ({template.Element("ProductName").Value} carries the same): {mine}");
                    }
                }

                AssertNewRecord(ud);
                if (!gamepadClone)
                {
                    Assert.True(ud.CapButtonIndices == null || ud.CapButtonIndices.Length == 0, who);
                    Assert.True(ud.CapAxisIndices == null || ud.CapAxisIndices.Length == 0, who);
                    Assert.True(ud.DeviceObjects == null || ud.DeviceObjects.Length == 0, who);
                    Assert.False(ud.HasExtraGenericAxes, who);
                    Assert.Equal(ud.CapAxeCount, ud.RawAxisCount);
                }

                string guid = ud.InstanceGuid.ToString();
                if (guid == Ds3Guid || guid == DualSenseGuid)
                {
                    Assert.True(BluetoothLinkHelper.TryParseAddress(ud.SerialNumber, out long address),
                        $"{who}: '{ud.SerialNumber}' is not an address");
                    // RFC 7042 2.1.2: 00-00-5E-00-53-00 through -FF.
                    Assert.Equal(0x00005E005300L, address & ~0xFFL);
                    Assert.True(DeviceRowViewModel.ComputeShowQuickCharge(ud.DevicePath, ud.VendorId, ud.ProdId, ud.SerialNumber), who);
                }
                else
                {
                    Assert.Equal(string.Empty, ud.SerialNumber);
                }
            }

            // The script's own check of the list agrees.
            Assert.True(run.Result.GetProperty("Clean").GetBoolean());
            Assert.Empty(run.Strings("StateFailures"));
            Assert.False(run.Result.GetProperty("StagingFailed").GetBoolean());
        }

        /// <summary>
        /// A synthetic row an earlier run left in the list never survives as
        /// it was. Two rows under one synthetic GUID become the one fresh
        /// row, since duplicate InstanceGuids degrade the Devices list at
        /// run time. Where the real device has joined the list since, the
        /// older run's stand-in is removed and the real row is left alone,
        /// so the device is not listed twice. A synthetic whose real device
        /// is in the list is still not added.
        /// </summary>
        [Fact]
        public void ARowAnEarlierRunLeftIsRebuiltOnceOrGivesWayToTheRealDevice()
        {
            const string moveName = "PlayStation Move Motion Controller";
            var realMove = Template(RealMoveGuid, moveName, 0x054C, 0x03D5,
                @"\\?\bthps3bus#{53f88889-1aaf-4353-a047-556b69ec6da6}&dev&vid_054c&pid_03d5#a&020000000003&1&bt",
                MoveSerial, "template-sdl-guid-move");
            var realDs3 = Template(RealDs3Guid, "DualShock 3", 0x054C, 0x0268,
                @"\\?\bthps3bus#{53f88889-1aaf-4353-a047-556b69ec6da6}&dev&vid_054c&pid_0268#a&020000000004&1&bt",
                RealDs3Serial, "template-sdl-guid-ds3");

            var run = RunDriver("inject", new SettingsFileData
            {
                Devices = new[]
                {
                    SteamTemplate(), EdgeTemplate(),
                    Leftover(MidiGuid, "MIDI Keyboard", 0x1235, 0x0016, @"HID\VID_1235&PID_0016\dummy", InputDeviceType.Midi),
                    Leftover(MidiGuid, "MIDI Keyboard", 0x1235, 0x0016, @"HID\VID_1235&PID_0016\dummy", InputDeviceType.Midi),
                    Leftover(MoveGuid, moveName, 0x054C, 0x03D5, @"HID\VID_054C&PID_03D5\dummy", InputDeviceType.Gamepad),
                    realMove, realDs3,
                },
            });
            AssertRan(run);

            var data = Deserialize(run.OutXml);
            var host = run.Strings("Host");

            // Positive control: both MIDI rows and the Move stand-in went in.
            var input = XDocument.Parse(run.InXml).Root.Element("Devices").Elements("Device").ToList();
            Assert.Equal(2, input.Count(r => (string)r.Element("InstanceGuid") == MidiGuid));
            Assert.Single(input, r => (string)r.Element("InstanceGuid") == MoveGuid);

            var midi = Assert.Single(data.Devices, d => d.InstanceGuid == new Guid(MidiGuid));
            AssertNewRecord(midi);
            Assert.Equal(string.Empty, midi.SerialNumber);
            Assert.Contains(host, l => l.Contains(MidiGuid) && l.Contains("rebuilt"));

            Assert.DoesNotContain(data.Devices, d => d.InstanceGuid == new Guid(MoveGuid));
            var move = Assert.Single(data.Devices, d => d.ProductName == moveName);
            Assert.Equal(RealMoveGuid, move.InstanceGuid);
            Assert.Equal(MoveSerial, move.SerialNumber);
            Assert.Contains(host, l => l.Contains(moveName) && l.Contains("is removed"));

            Assert.DoesNotContain(data.Devices, d => d.InstanceGuid == new Guid(Ds3Guid));
            var ds3 = Assert.Single(data.Devices, d => d.ProductName == "DualShock 3");
            Assert.Equal(RealDs3Guid, ds3.InstanceGuid);
            Assert.Equal(RealDs3Serial, ds3.SerialNumber);
            Assert.Contains(host, l => l.Contains("'DualShock 3'") && l.Contains("synthetic skipped"));

            Assert.Equal(data.Devices.Length, data.Devices.Select(d => d.InstanceGuid).Distinct().Count());
            Assert.True(run.Result.GetProperty("Clean").GetBoolean());
            Assert.Empty(run.Strings("StateFailures"));
        }

        /// <summary>
        /// Add-DeviceOnce adds the GUID of a row it is handed to the list
        /// when the list lacks it. The check STEP 0 makes of its save then
        /// covers a synthetic row that was added to the injection and not to
        /// the list. Here the injection starts from an empty list.
        /// </summary>
        [Fact]
        public void TheInjectionAddsAGuidTheListLacks()
        {
            var run = RunDriver("inject", new SettingsFileData { Devices = new[] { SteamTemplate(), EdgeTemplate() } }, "unlisted");
            AssertRan(run);

            // Positive control: the script's list had entries, and the driver
            // emptied it before the injection ran.
            Assert.NotEmpty(run.Strings("Listed"));
            Assert.Equal(0, run.Result.GetProperty("ListedAtStart").GetInt32());

            var recorded = run.Strings("SyntheticGuids");
            var synthetic = Deserialize(run.OutXml).Devices
                .Where(d => d.InstanceGuid != SteamControllerGuid && d.InstanceGuid != DualSenseEdgeGuid)
                .ToList();
            Assert.NotEmpty(synthetic);
            foreach (var ud in synthetic)
                Assert.True(recorded.Contains(ud.InstanceGuid.ToString(), StringComparer.OrdinalIgnoreCase),
                    $"{ud.ProductName} was injected and its GUID is not in the list the check reads");
            Assert.Equal(recorded.Count, recorded.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        }

        /// <summary>
        /// STEP 0 checks the device list it saved: a synthetic row that holds
        /// a serial or an SDL GUID a real row also holds fails the run by
        /// name and stops every later PadForge shot, since that is a real
        /// device's identity about to be photographed on a stand-in. Values
        /// two real rows share, or two synthetic rows, are not a leak, and a
        /// row with neither value is clean.
        /// </summary>
        [Fact]
        public void ASyntheticRowThatCarriesARealRowsIdentityFailsTheRun()
        {
            const string sdl = "05000000de2800000313000000016800";
            const string realGuid = "5a1e0000-0000-4000-8000-000000000001";
            const string otherReal = "5a1e0000-0000-4000-8000-000000000005";
            var run = RunDriver("check", "plain", dir =>
            {
                WriteCase(dir, "case-1-leak", new[] { NfcGuid, MidiGuid },
                    (realGuid, "Steam Controller", SteamSerial, sdl),
                    (NfcGuid, "NFC Reader", SteamSerial, sdl),
                    (MidiGuid, "MIDI Keyboard", "", ""));
                WriteCase(dir, "case-2-clean", new[] { NfcGuid, Ds3Guid, DualSenseGuid },
                    (realGuid, "Steam Controller", SteamSerial, sdl),
                    (otherReal, "HID Keyboard Device", SteamSerial, sdl),
                    (NfcGuid, "NFC Reader", "", ""),
                    (Ds3Guid, "DualShock 3", "00005e005302", ""),
                    (DualSenseGuid, "DualSense Wireless Controller", "00-00-5e-00-53-01", ""));
                WriteCase(dir, "case-3-synthetic-pair", new[] { NfcGuid, MidiGuid },
                    (realGuid, "Steam Controller", SteamSerial, sdl),
                    (NfcGuid, "NFC Reader", "00005e005302", ""),
                    (MidiGuid, "MIDI Keyboard", "00005e005302", ""));
                WriteCase(dir, "case-4-letter-case", new[] { NfcGuid },
                    (realGuid, "Steam Controller", "02000000000a", ""),
                    (NfcGuid, "NFC Reader", "02000000000A", ""));
                WriteCase(dir, "case-5-nothing-synthetic", Array.Empty<string>(),
                    (realGuid, "Steam Controller", SteamSerial, sdl),
                    (NfcGuid, "NFC Reader", SteamSerial, sdl));
            });
            Assert.Empty(run.Strings("ParseErrors"));
            AssertNothingRefused(run);

            var cases = run.Result.GetProperty("Cases").EnumerateArray()
                .ToDictionary(c => c.GetProperty("Name").GetString());
            Assert.Equal(5, cases.Count);

            var leak = cases["case-1-leak"];
            Assert.False(leak.GetProperty("Clean").GetBoolean());
            Assert.True(leak.GetProperty("StagingFailed").GetBoolean());
            var failures = leak.GetProperty("StateFailures").EnumerateArray().Select(e => e.GetString()).ToList();
            Assert.Equal(
                new[]
                {
                    "the synthetic 'NFC Reader' row carries the serial of 'Steam Controller'",
                    "the synthetic 'NFC Reader' row carries the SDL GUID of 'Steam Controller'",
                },
                failures);
            var lines = leak.GetProperty("Lines").EnumerateArray().Select(e => e.GetString()).ToList();
            Assert.Equal(2, lines.Count(l => l.EndsWith("No more PadForge shots are saved.", StringComparison.Ordinal)));

            foreach (string name in new[] { "case-2-clean", "case-3-synthetic-pair", "case-5-nothing-synthetic" })
            {
                Assert.True(cases[name].GetProperty("Clean").GetBoolean(), name);
                Assert.False(cases[name].GetProperty("StagingFailed").GetBoolean(), name);
                Assert.Equal(0, cases[name].GetProperty("StateFailures").GetArrayLength());
                Assert.Equal(0, cases[name].GetProperty("Lines").GetArrayLength());
            }

            // The same address in the other letter case is the same address.
            Assert.False(cases["case-4-letter-case"].GetProperty("Clean").GetBoolean());
            Assert.Equal(1, cases["case-4-letter-case"].GetProperty("StateFailures").GetArrayLength());
        }

        /// <summary>
        /// The check runs on every settings file a capture starts PadForge
        /// on, before PadForge starts: on the list STEP 0 saved, straight
        /// after the save, and on the file a tail or Direct run reuses, in
        /// the block that prepares that file. Both read the script's list
        /// of synthetic GUIDs.
        /// </summary>
        [Fact]
        public void TheCheckRunsOnEveryFilePadForgeStartsOn()
        {
            var run = RunDriver("wiring", null);
            Assert.Empty(run.Strings("ParseErrors"));
            Assert.Equal(1, run.Result.GetProperty("Saves").GetInt32());
            Assert.Equal(2, run.Result.GetProperty("Calls").GetInt32());

            Assert.True(run.Result.GetProperty("CallFollowsTheSave").GetBoolean(),
                "Assert-SyntheticRowsClean does not follow Save-SettingsXml $xml $PadForgeXml in the same block");
            Assert.All(run.Strings("Between"), name => Assert.Equal("Write-Host", name));
            string saved = run.Result.GetProperty("SavedCallText").GetString();
            Assert.Contains("$ns.SelectSingleNode(\"Devices\")", saved);
            Assert.Contains("$script:SyntheticGuids", saved);

            string reused = run.Result.GetProperty("ReusedCallText").GetString();
            Assert.True(reused.Length > 0, "the reused-file block does not call Assert-SyntheticRowsClean");
            Assert.Contains("$tx.PadForgeSettings.SelectSingleNode(\"Devices\")", reused);
            Assert.Contains("$script:SyntheticGuids", reused);

            // Positive control: the scan found STEP 1's Start-Process.
            Assert.True(run.Result.GetProperty("StartLine").GetInt32() > 0, "no Start-Process $PadForgeExe follows the save");
            Assert.True(run.Result.GetProperty("PadForgeStartsAfterBoth").GetBoolean(),
                "a check sits after the Start-Process that opens PadForge on the file");
        }

        /// <summary>
        /// A tail run and a Direct run skip STEP 0 and start PadForge on a
        /// settings file an earlier run staged, so the injection never
        /// rebuilds that file's synthetic rows and STEP 0's check never
        /// reads them. The block that prepares the file makes the same
        /// check. A synthetic row that holds a real row's serial fails the
        /// run by name, stops every later PadForge shot and says what
        /// rebuilds the file, and the block still sets the starting view. A
        /// clean file passes without a line.
        /// </summary>
        [Fact]
        public void AReusedFileGetsTheCheckStepZeroMakesOfItsOwnSave()
        {
            var run = RunDriver("reused", "plain", ArrangeReusedFiles);
            Assert.Empty(run.Strings("ParseErrors"));
            AssertNothingRefused(run);
            var cases = run.Result.GetProperty("Cases").EnumerateArray()
                .ToDictionary(c => c.GetProperty("Name").GetString());

            foreach (var (name, label, rebuild) in new[]
                     {
                         ("reused-direct-leak", "Direct", "A run with -StageAll rebuilds the snapshot."),
                         ("reused-tail-leak", "Tail mode", "The next run rebuilds the file."),
                     })
            {
                var c = cases[name];
                var lines = c.GetProperty("Lines").EnumerateArray().Select(e => e.GetString()).ToList();
                Assert.True(c.GetProperty("StagingFailed").GetBoolean(), $"{name}: the leaked serial did not fail the run");
                Assert.Equal(
                    new[] { "the synthetic 'NFC Reader' row carries the serial of 'Steam Controller'" },
                    c.GetProperty("StateFailures").EnumerateArray().Select(e => e.GetString()));
                Assert.Contains($"  !! {label}: the reused file failed that check. {rebuild}", lines);
                Assert.Contains(lines, l => l.StartsWith($"  {label}: set Use2DControllerView=false", StringComparison.Ordinal));
            }

            foreach (string name in new[] { "reused-direct-clean", "reused-tail-clean" })
            {
                var c = cases[name];
                var lines = c.GetProperty("Lines").EnumerateArray().Select(e => e.GetString()).ToList();
                Assert.False(c.GetProperty("StagingFailed").GetBoolean(), name);
                Assert.Equal(0, c.GetProperty("StateFailures").GetArrayLength());
                Assert.DoesNotContain(lines, l => l.Contains("!!", StringComparison.Ordinal));
            }
        }

        /// <summary>
        /// The injection test runs the injection whatever encloses it. It
        /// used to run the nearest try statement around New-SyntheticDevice.
        /// That was the injection's own try/catch, and the capture's
        /// try/finally now sits behind it: with the inner one gone, the same
        /// walk returns the whole capture, whose finally stops PadForge.
        ///
        /// <para>Here the script is given to the driver with the inner
        /// try/catch taken away. The driver still runs the injection alone
        /// and produces the same list, and the statement the old walk would
        /// have returned is one it refuses to run.</para>
        /// </summary>
        [Fact]
        public void TheInjectionRunsTheSameWithoutItsOwnTryCatch()
        {
            var devices = new[]
            {
                SteamTemplate(), EdgeTemplate(),
                Leftover(NfcGuid, "NFC Reader", 0x072F, 0x2200, @"HID\VID_072F&PID_2200\dummy", InputDeviceType.Nfc),
            };
            var plain = RunDriver("inject", new SettingsFileData { Devices = devices });
            AssertRan(plain);

            var bare = RunDriver("inject", new SettingsFileData { Devices = devices }, "unwrapped");
            // Positive control: the injection now sits directly in a try
            // that has a finally, the capture's own.
            Assert.True(bare.Result.GetProperty("EnclosingTryHasFinally").GetBoolean(),
                "taking the inner try/catch away did not leave the injection inside a try with a finally");
            AssertRan(bare);
            Assert.Equal(plain.OutXml, bare.OutXml);

            var outer = bare.Strings("EnclosingTryRefused");
            foreach (string command in new[] { "Stop-Process", "Start-Process", "Copy-Item", "Remove-Item" })
                Assert.Contains(command, outer);
        }

        /// <summary>
        /// The driver runs a statement from the script only when every
        /// call in it is one the statement is known to need. One harmless
        /// statement of each kind it turns away, a command of another name,
        /// an object that is no collection and a static method, placed in
        /// the injection or in the reused-file block, is enough for the
        /// block not to run. A block that came to hold Stop-Process or
        /// Copy-Item would not run either. The check and the list of
        /// synthetic GUIDs run in the driver too, and a command placed in
        /// either stops every mode that would run it.
        /// </summary>
        [Fact]
        public void TheDriverRunsNoBlockThatCallsAnythingElse()
        {
            var devices = new SettingsFileData { Devices = new[] { SteamTemplate(), EdgeTemplate() } };
            foreach (var (variant, call) in new[]
                     {
                         ("canary", "Get-Date"),
                         ("canary-object", "New-Object System.Text.StringBuilder"),
                         ("canary-static", "[System.IO.Path]::GetTempPath()"),
                     })
            {
                var inject = RunDriver("inject", devices, variant);
                Assert.True(inject.Strings("Refused").Any(l => l.EndsWith(": " + call, StringComparison.Ordinal)),
                    $"the driver did not refuse the injection for {call}");
                Assert.False(inject.Result.GetProperty("Ran").GetBoolean(), variant);
                Assert.Null(inject.OutXml);
                Assert.Empty(inject.Strings("Host"));
            }

            foreach (var (variant, call) in new[]
                     {
                         ("canary", "Get-Date"),
                         ("canary-static", "[System.IO.Path]::GetTempPath()"),
                     })
            {
                var reused = RunDriver("reused", variant, ArrangeReusedFiles);
                Assert.True(reused.Strings("Refused").Any(l => l.EndsWith(": " + call, StringComparison.Ordinal)),
                    $"the driver did not refuse the reused-file block for {call}");
                Assert.Equal(0, reused.Result.GetProperty("Cases").GetArrayLength());
                Assert.Empty(reused.Strings("Host"));
            }

            foreach (string variant in new[] { "canary-check", "canary-list" })
            {
                var inject = RunDriver("inject", devices, variant);
                Assert.True(inject.Strings("Refused").Any(l => l.EndsWith(": Get-Date", StringComparison.Ordinal)),
                    $"the driver did not refuse the injection run for {variant}");
                Assert.False(inject.Result.GetProperty("Ran").GetBoolean(), variant);
                Assert.Null(inject.OutXml);

                var reused = RunDriver("reused", variant, ArrangeReusedFiles);
                Assert.True(reused.Strings("Refused").Any(l => l.EndsWith(": Get-Date", StringComparison.Ordinal)),
                    $"the driver did not refuse the reused-file run for {variant}");
                Assert.Equal(0, reused.Result.GetProperty("Cases").GetArrayLength());
                Assert.Empty(reused.Strings("Host"));
            }

            var check = RunDriver("check", "canary-check", dir =>
                WriteCase(dir, "case-1-clean", new[] { NfcGuid }, (NfcGuid, "NFC Reader", "", "")));
            Assert.True(check.Strings("Refused").Any(l => l.EndsWith(": Get-Date", StringComparison.Ordinal)),
                "the driver did not refuse the check for a command placed in it");
            Assert.Equal(0, check.Result.GetProperty("Cases").GetArrayLength());
        }

        /// <summary>
        /// Every write to the settings file or its backup sits inside the
        /// try whose finally runs STEP 4's restore. The Direct path swapped in
        /// the staged snapshot, and STEP 0 backed up, deleted and rewrote the
        /// file, before that try opened, so an error or CTRL+C there left the
        /// capture file in place of the owner's. The finally restores only a
        /// backup that exists and that the run was given word for, a
        /// marker's or the owner's, and the backup's path is set before the
        /// try so the finally can test it under StrictMode.
        /// </summary>
        [Fact]
        public void TheRestoreFinallyCoversEverySettingsFileWrite()
        {
            var run = RunDriver("guard", null);
            Assert.Empty(run.Strings("ParseErrors"));

            // Positive control: the scan finds the writes it places.
            Assert.True(run.Result.GetProperty("WritesFound").GetInt32() >= 8,
                $"the scan found {run.Result.GetProperty("WritesFound").GetInt32()} writes");

            var outside = run.Strings("WritesOutsideRestore");
            Assert.True(outside.Count == 0,
                "settings-file writes outside the restore try/finally:\n" + string.Join("\n", outside));
            Assert.True(run.Result.GetProperty("BackupPathSetBeforeTry").GetBoolean(),
                "$xmlBak is not set before the restore try opens");

            int copies = run.Result.GetProperty("FinallyRestoreCopies").GetInt32();
            int guarded = run.Result.GetProperty("GuardedRestoreCopies").GetInt32();
            Assert.True(copies >= 1, "the finally restores nothing");
            Assert.True(copies == guarded, $"{copies - guarded} restore copies in the finally do not first test for the backup and for the word that it holds the settings to put back");
        }

        /// <summary>
        /// The block that sets the starting view in a reused settings file
        /// logs the mode that ran. The Direct path reuses the staging run's
        /// snapshot through the same block, and it logged "Tail mode" in a
        /// run that was not one.
        /// </summary>
        [Fact]
        public void TheReusedFileBlockNamesTheModeThatRan()
        {
            var run = RunDriver("reused", "plain", ArrangeReusedFiles);
            Assert.Empty(run.Strings("ParseErrors"));
            AssertNothingRefused(run);
            var cases = run.Result.GetProperty("Cases").EnumerateArray()
                .Where(c => c.GetProperty("Name").GetString().EndsWith("app", StringComparison.Ordinal))
                .ToList();
            Assert.Equal(4, cases.Count);

            foreach (var c in cases)
            {
                bool direct = c.GetProperty("Direct").GetBoolean();
                bool app = !c.GetProperty("Name").GetString().EndsWith("-noapp", StringComparison.Ordinal);
                var lines = c.GetProperty("Lines").EnumerateArray().Select(e => e.GetString()).ToList();
                string label = direct ? "Direct" : "Tail mode";
                string other = direct ? "Tail mode" : "Direct";
                if (app)
                {
                    Assert.Contains(lines, l => l.StartsWith($"  {label}: set Use2DControllerView=false", StringComparison.Ordinal));
                    Assert.False(c.GetProperty("ViewUnknown").GetBoolean());
                }
                else
                {
                    Assert.Contains(lines, l => l.StartsWith($"  !! {label}: no AppSettings", StringComparison.Ordinal));
                    Assert.True(c.GetProperty("ViewUnknown").GetBoolean());
                }
                Assert.DoesNotContain(lines, l => l.Contains(other + ":", StringComparison.OrdinalIgnoreCase));
            }
        }

        /// <summary>
        /// The help of -Only says what an -Only run takes. Such a run ends
        /// at the focused pass, and the per-page passes and the tail do not
        /// run after it, so it takes a shot only when that pass has a recipe
        /// for it. The help offered -SkipToTail with -Only as the way to tail
        /// shots, from the time -Only made every other Cap a no-op, and that
        /// run takes none of them: nfc-register, which the tail alone takes,
        /// is reported as missed and left as it was.
        ///
        /// <para>The help's example names shots the focused pass has
        /// recipes for, and the shot it sends to a -SkipToTail run with no
        /// -Only is one the tail takes and neither the focused pass nor a
        /// function names. A recipe added for that shot fails this until the
        /// help names another.</para>
        /// </summary>
        [Fact]
        public void TheOnlyHelpSaysWhatAnOnlyRunTakes()
        {
            var run = RunDriver("focus", null);
            Assert.Empty(run.Strings("ParseErrors"));

            Assert.True(run.Result.GetProperty("PassesSitBehindTheFlag").GetBoolean(),
                "the per-page passes are no longer inside the block the focused pass's flag skips");
            var focused = run.Strings("FocusedStrings");
            var functions = run.Strings("FunctionStrings");
            var perPage = run.Strings("PerPageShots");
            var tail = run.Strings("TailShots");
            // Positive control: the scan finds the shots of both passes.
            Assert.True(perPage.Count > 20 && tail.Count > 20, $"found {perPage.Count} per-page shots and {tail.Count} tail shots");
            Assert.Contains("dashboard", perPage);

            string only = run.Result.GetProperty("OnlyHelp").GetString();
            Assert.DoesNotContain("-SkipToTail -Only", only);
            var example = Regex.Match(only, @"e\.g\. -Only ([a-z0-9-]+(?:,[a-z0-9-]+)*)");
            Assert.True(example.Success, "the -Only help gives no example: " + only);
            foreach (string shot in example.Groups[1].Value.Split(','))
                Assert.True(focused.Contains(shot), $"the -Only help's example names {shot}, and the focused pass has no recipe for it");

            Assert.Contains("ends at the focused pass", only);
            Assert.Contains("The per-page passes and the tail do not run after it", only);
            Assert.Contains("-SkipToTail adds no shot to an -Only run", only);
            Assert.Contains("ends with exit code 1 when a shot was refused or a state check failed", only);
            var sent = Regex.Match(only, @"([a-z0-9-]+) is such a name: the tail takes it");
            Assert.True(sent.Success, "the -Only help names no shot that only the tail takes: " + only);
            string tailOnly = sent.Groups[1].Value;
            Assert.Equal("nfc-register", tailOnly);
            Assert.Contains(tailOnly, tail);
            Assert.DoesNotContain(tailOnly, focused);
            Assert.DoesNotContain(tailOnly, functions);

            string skip = run.Result.GetProperty("SkipToTailHelp").GetString();
            Assert.Contains("STEP 0 runs first", skip);
            Assert.Contains("only while that run's marker names the backup beside it", skip);
        }

        // ─── The owner's settings file ───

        /// <summary>
        /// A script under tools that writes the owner's settings file. Each
        /// names its files its own way: the settings file, the backup, and
        /// PadForge's exe, and it stops PadForge with Stop-Process or its own
        /// Kill-PadForge. The counts are the fewest the scan has to find, as a
        /// positive control: statements that overwrite or delete a settings
        /// file, copies taken first, and loads and saves of one. Backups is how
        /// many backups the script takes, and ClosingRestores how many restores
        /// put the owner's settings back for good.
        ///
        /// <para>prep_xml_for_capture.ps1 takes a backup and leaves it, marked,
        /// with the file it prepared in place. add_slots_via_ui.ps1 puts that
        /// backup back before it adds its slots and takes none of its
        /// own.</para>
        /// </summary>
        public sealed record SettingsScript(
            string Name, string Settings, string Backup, string Exe, string Kill,
            int FewestWrites, int FewestCopies, int Backups, int ClosingRestores, int FewestLoads, int FewestSaves);

        /// <summary>Every tool that makes a real click asks first who holds the
        /// focus, since a click while another program's window is in front goes
        /// to that window, whoever is using the machine. ForceFG's answer is not
        /// enough to go on: SetForegroundWindow is allowed to refuse.</summary>
        [Theory]
        [InlineData("diag-sweep.ps1", "if (-not (InFront))", "Click-At ([int]($r.X")]
        [InlineData("capture_all.ps1", "if ($front -ne $own)", "[Win32]::ClickAt($X, $Y)")]
        [InlineData("a11y_uia_audit.ps1", "if (-not (Front)) { return 'not in front' }", "[A11yWin]::mouse_event(2, 0, 0, 0, [UIntPtr]::Zero)")]
        [InlineData("a11y_nvda_tab_pass.ps1", "elseif ([NvdaPassWin]::GetForegroundWindow() -ne $h)", "[NvdaPassWin]::mouse_event(2, 0, 0, 0, [UIntPtr]::Zero)")]
        [InlineData("add_slots_via_ui.ps1", "if ([AddSlotsWin]::GetForegroundWindow() -ne $proc.MainWindowHandle)", "[AddSlotsWin]::mouse_event(2, 0, 0, 0, [UIntPtr]::Zero)")]
        [InlineData("probe_macro_list.ps1", "if ([PW]::GetForegroundWindow() -ne $hwnd)", "[PW]::ClickAt([int]($r.X")]
        public void EveryToolThatClicks_AsksWhoHoldsTheFocusFirst(string script, string guard, string click)
        {
            string text = AuditDelta20261002Tests.RepoText("tools", script);
            int asked = text.IndexOf(guard, StringComparison.Ordinal);
            int clicked = text.IndexOf(click, StringComparison.Ordinal);
            Assert.True(asked > 0 && clicked > asked, $"{script}: {asked} {clicked}");
        }

        /// <summary>A sweep that stopped short says so. The word PASS on a
        /// harvest that reached one page in five read as the whole walk.</summary>
        [Fact]
        public void TheSweep_NamesWhatItDidNotReach()
        {
            string text = AuditDelta20261002Tests.RepoText("tools", "diag-sweep.ps1");
            Assert.Contains("if (-not $ok) { $notReached += $page }", text);
            Assert.Contains("$notReached += 'Workshop browse'", text);
            Assert.Contains("$notReached += 'Map Key Lights'", text);
            Assert.Contains("$verdict = \"PARTIAL: $verdict, for what ran. Not reached:", text);
        }

        private static readonly SettingsScript[] SettingsScriptList =
        {
            new("capture_all.ps1", "PadForgeXml", "xmlBak", "PadForgeExe", "Stop-Process", 19, 4, 2, 1, 15, 6),
            new("diag-sweep.ps1", "pfXml", "pfBak", "dst", "Stop-Process", 6, 4, 1, 1, 1, 1),
            new("prep_xml_for_capture.ps1", "XmlPath", "BakPath", "ExePath", "Stop-Process", 4, 2, 1, 0, 1, 1),
            new("add_slots_via_ui.ps1", "XmlPath", "BakPath", "ExePath", "Stop-Process", 3, 3, 0, 1, 0, 0),
        };

        private static SettingsScript ScriptNamed(string name) => SettingsScriptList.Single(s => s.Name == name);

        /// <summary>Every script under tools that writes the owner's settings
        /// file, by name.</summary>
        public static IEnumerable<object[]> SettingsScripts() => SettingsScriptList.Select(s => new object[] { s.Name });

        private static List<JsonElement> Exits(DriverRun run, string condition) =>
            run.Result.GetProperty("Exits").EnumerateArray()
                .Where(e => e.GetProperty("Condition").GetString() == condition).ToList();

        private static List<string> Strings(JsonElement element, string name) =>
            element.GetProperty(name).EnumerateArray().Select(e => e.GetString()).ToList();

        /// <summary>
        /// No statement of a script under tools overwrites or deletes a
        /// settings file, the owner's or its backup, until that file has been
        /// copied into the capture folder. A Save-SettingsCopy of the same
        /// file runs first on every way to the statement, or the statement
        /// sits where its file was just tested absent, and Save-SettingsXml
        /// takes the copy as its first statement after the one that makes
        /// any failure stop the run. A restore used to copy a backup over
        /// whatever lay beside it and keep nothing of what it replaced, so a
        /// backup left behind after the settings were put back by hand cost
        /// every setting changed since. Five of the eight scripts did that
        /// until they took the shared guard.
        ///
        /// <para>The path a copy returns is always taken. Left bare in
        /// Set-SlotPreset or one of its three siblings it would join the
        /// function's result, and the caller's -not would take a path and a
        /// false for success.</para>
        /// </summary>
        [Theory]
        [MemberData(nameof(SettingsScripts))]
        public void NoSettingsFileIsOverwrittenOrDeletedUntilItIsCopiedAside(string script)
        {
            var s = ScriptNamed(script);
            var run = ReadTree("settings", script);

            // Positive control: the scan finds the writes, the backup's
            // deletion and a restore among them.
            var writes = run.Strings("Writes");
            Assert.True(writes.Count >= s.FewestWrites, $"{script}: the scan found {writes.Count} writes:\n" + string.Join("\n", writes));
            Assert.Contains("Remove-Item $" + s.Backup, run.Strings("WriteKinds"));
            Assert.NotEmpty(run.Strings("Restores"));
            Assert.True(run.Result.GetProperty("Copies").GetInt32() >= s.FewestCopies,
                $"{script}: the scan found {run.Result.GetProperty("Copies").GetInt32()} copies");

            var uncovered = run.Strings("Uncovered");
            Assert.True(uncovered.Count == 0,
                $"{script} overwrites or deletes a settings file it has not copied aside first:\n" + string.Join("\n", uncovered));
            var bare = run.Strings("BareCalls");
            Assert.True(bare.Count == 0,
                $"{script} leaves the path a copy returns for the enclosing function's result:\n" + string.Join("\n", bare));

            var saver = ReadTree("encoding", script);
            Assert.Equal(new[] { "$ErrorActionPreference = 'Stop'", "$kept = Save-SettingsCopy $Path" }, saver.Strings("SaverOpening"));
        }

        /// <summary>
        /// A backup is written only where none is, and marked there. A copy
        /// cut short then never leaves half a file over a good backup, which
        /// the finally would have put over the owner's settings. Each backup
        /// stands right behind the guard that settles a backup already
        /// there, and the marker is written by the statement that follows
        /// it, so no backup a run takes is without one and no marker names
        /// a backup the run did not take. add_slots_via_ui.ps1 takes no
        /// backup and sets no marker.
        /// </summary>
        [Theory]
        [MemberData(nameof(SettingsScripts))]
        public void ABackupIsWrittenOnlyWhereNoneIsAndMarkedThere(string script)
        {
            var s = ScriptNamed(script);
            var run = ReadTree("settings", script);

            var backups = run.Strings("Backups");
            Assert.True(backups.Count == s.Backups, $"{script}: the scan found {backups.Count} backups:\n" + string.Join("\n", backups));
            Assert.All(run.Strings("BackupSources"), source => Assert.Equal("$" + s.Settings, source));
            Assert.Equal(backups.Count, run.Strings("BackupSources").Count);

            var over = run.Strings("BackupsOverAFile");
            Assert.True(over.Count == 0, $"{script} writes a backup where one may already be:\n" + string.Join("\n", over));
            var unmarked = run.Strings("BackupsNotMarked");
            Assert.True(unmarked.Count == 0, $"{script} takes a backup and does not mark it in the next statement:\n" + string.Join("\n", unmarked));
            Assert.Equal(backups.Count, run.Result.GetProperty("MarkersSet").GetInt32());
            Assert.All(run.Strings("BeforeEachBackup"), guard => Assert.Equal("Test-Path $" + s.Backup, guard));
        }

        /// <summary>
        /// A backup no marker speaks for stops the run before it has changed
        /// anything. The file beside such a backup may be the owner's own,
        /// put back by hand since, and the run cannot tell. It prints both
        /// files and ends with exit code 1, and nothing no function holds
        /// has stopped or started PadForge, written the registry or written
        /// a settings file by then.
        ///
        /// <para>capture_all.ps1 asks in its pre-flight, ahead of the toast
        /// values, and again at each guard inside the try, where PadForge
        /// has been stopped, the same question is the first statement and
        /// the stop comes before any write of the block. Tail mode takes a
        /// file for staged on the same word and no other. The other seven
        /// ask again once they have stopped PadForge: a save it made after
        /// the first question can make two files that were the same
        /// differ.</para>
        /// </summary>
        [Theory]
        [MemberData(nameof(SettingsScripts))]
        public void ABackupNoMarkerSpeaksForStopsTheRunBeforeItChangesAnything(string script)
        {
            var s = ScriptNamed(script);
            var run = ReadTree("settings", script);
            bool full = script == "capture_all.ps1";
            string stopLines = full ? "$preflightStop" : "$stop";
            string doubt = full ? "$preflightDoubt" : "$doubt";

            // What is asked, and what fills the lines the stop prints.
            Assert.Contains($"{doubt} = Get-BackupDoubt ${s.Settings} ${s.Backup} 'Ask'", run.Strings("DoubtAssignments"));
            Assert.Contains($"{stopLines} under $KeepSettingsFrom -eq 'Ask' -and {doubt}", run.Strings("StopLinesFilledUnder"));

            var first = Assert.Single(Exits(run, stopLines + ".Count -gt 0"));
            Assert.Equal("1", first.GetProperty("Code").GetString());
            Assert.False(first.GetProperty("InTheTry").GetBoolean());
            Assert.False(first.GetProperty("KillBefore").GetBoolean(), $"{script} stops PadForge before it asks");
            Assert.True(Strings(first, "ChangesInBlock").Count == 0,
                $"{script}: the stop itself changes something:\n" + string.Join("\n", Strings(first, "ChangesInBlock")));
            Assert.True(Strings(first, "ChangesBefore").Count == 0,
                $"{script} has changed something by the time it stops for the owner's decision:\n" + string.Join("\n", Strings(first, "ChangesBefore")));

            // Positive control: the scan knows a change when it sees one. The
            // script has them, and the first comes after the stop.
            var changes = run.Strings("TopLevelChanges");
            Assert.True(changes.Count >= 4, $"{script}: the scan found {changes.Count} changes");
            int firstChange = int.Parse(Regex.Match(changes[0], @"^line (\d+):").Groups[1].Value);
            Assert.True(firstChange > first.GetProperty("Line").GetInt32(),
                $"{script}: {changes[0]} comes before the stop at line {first.GetProperty("Line").GetInt32()}");
            Assert.Contains(changes, c => Regex.IsMatch(c, @"^line \d+: " + Regex.Escape(s.Kill) + @"\b"));

            if (!full)
            {
                // Asked again once PadForge is down, since a save it made
                // after the first question can make two files that were the
                // same differ. That stop touches neither file and starts
                // PadForge again, and only the kill comes before it.
                Assert.Contains($"$doubt = Get-BackupDoubt ${s.Settings} ${s.Backup} $KeepSettingsFrom", run.Strings("DoubtAssignments"));
                var again = Assert.Single(Exits(run, "$doubt"));
                Assert.Equal("1", again.GetProperty("Code").GetString());
                Assert.True(again.GetProperty("KillBefore").GetBoolean(), $"{script} does not ask again after it stops PadForge");
                string restart = Assert.Single(Strings(again, "ChangesInBlock"));
                Assert.Matches(@"^line \d+: Start-Process (-FilePath )?\$" + s.Exe + "$", restart);
                var before = Strings(again, "ChangesBefore");
                Assert.NotEmpty(before);
                Assert.All(before, c => Assert.Matches(@"^line \d+: " + Regex.Escape(s.Kill) + @"\b", c));
                Assert.True(again.GetProperty("Line").GetInt32() > first.GetProperty("Line").GetInt32());
                return;
            }

            var guards = Exits(run, "$doubt");
            Assert.Equal(2, guards.Count);
            foreach (var guard in guards)
            {
                Assert.Equal("1", guard.GetProperty("Code").GetString());
                Assert.True(guard.GetProperty("InTheTry").GetBoolean());
                Assert.True(guard.GetProperty("KillBefore").GetBoolean(), "an in-try guard asks before PadForge is stopped");
                Assert.Equal("Test-Path $xmlBak", guard.GetProperty("OuterCondition").GetString());
                Assert.Equal("$doubt = Get-BackupDoubt $PadForgeXml $xmlBak $KeepSettingsFrom", guard.GetProperty("FirstInOuterBlock").GetString());
                Assert.Empty(Strings(guard, "ChangesInBlock"));
                Assert.Empty(Strings(guard, "ChangesBefore"));
            }
            // Positive control for the two lists: the exit taken when
            // PadForge does not start copies the backup back in its block.
            Assert.Contains(run.Result.GetProperty("Exits").EnumerateArray(), e => Strings(e, "ChangesInBlock").Count > 0);

            string reuse = run.Result.GetProperty("TailReuseCondition").GetString();
            Assert.Contains("$KeepSettingsFrom -eq 'Ask'", reuse);
            Assert.Contains("-not (Test-SameSettings $PadForgeXml $xmlBak)", reuse);
            Assert.Contains("-not (Get-BackupDoubt $PadForgeXml $xmlBak 'Ask')", reuse);

            // The finally reads these under StrictMode whenever the run stops.
            var set = run.Strings("SetBeforeTheTry");
            foreach (string name in new[] { "$xmlBak", "$script:BackupHoldsSettings", "$script:BackupTaken", "$script:FocusedPassRan" })
                Assert.Contains(name, set);
        }

        /// <summary>
        /// The restore in the finally runs in one order. PadForge is stopped
        /// first, because a live app saves its own state over a restored
        /// file, and started last. The marker is removed by the statement
        /// that follows the copy and the backup goes after it: a run that
        /// dies between the two leaves a backup with no marker beside a file
        /// with the same bytes. The other order leaves a marker that speaks
        /// for a backup the owner has since moved on from.
        ///
        /// <para>capture_all.ps1 restores only a backup it was given word
        /// for, and replaces a file it did not write only once that file is
        /// copied aside. add_slots_via_ui.ps1 has no finally: it puts the
        /// backup prep_xml_for_capture.ps1 took back first thing, in the
        /// same order, and prep_xml_for_capture.ps1 leaves its backup and
        /// marker in place for it.</para>
        /// </summary>
        [Theory]
        [MemberData(nameof(SettingsScripts))]
        public void TheRestoreStopsPadForgeFirstAndClearsTheMarkerBeforeTheBackup(string script)
        {
            var s = ScriptNamed(script);
            var run = ReadTree("settings", script);

            Assert.Equal(s.ClosingRestores, run.Result.GetProperty("FinalRestores").GetInt32());
            if (s.ClosingRestores == 0) return;
            Assert.True(run.Result.GetProperty("StopComesBeforeTheRestore").GetBoolean(),
                $"{script}: nothing stops PadForge before the backup is copied over the settings file");
            Assert.True(run.Result.GetProperty("StartComesAfterTheRestore").GetBoolean(),
                $"{script}: PadForge is not started after the restore, or is started before it");
            Assert.True(run.Result.GetProperty("MarkerGoesRightAfterTheRestore").GetBoolean(),
                $"{script}: Remove-BackupMarker ${s.Backup} is not the statement that follows the restore copy");
            Assert.True(run.Result.GetProperty("BackupGoesAfterTheMarker").GetBoolean(),
                $"{script}: the backup ${s.Backup} is removed before its marker, or more than once");

            if (script != "capture_all.ps1") return;
            Assert.Equal(
                new[]
                {
                    "$kept -or $script:BackupTaken -or -not (Test-Path $PadForgeXml)",
                    "(Test-Path $xmlBak) -and $script:BackupHoldsSettings",
                },
                run.Strings("RestoreConditions"));
        }

        /// <summary>
        /// An -Only run ends at the focused pass by a flag and never by
        /// leaving the script. The pass ended with a return at the top level
        /// of the try: the finally ran and the three lines below it did not,
        /// so the transcript stayed open, capture.lock kept the PID and the
        /// run ended with exit code 0 whatever it had refused. Nothing at
        /// the top level of the try returns, the pass sets the flag as its
        /// last statement, and everything behind it in the try sits in the
        /// one block that runs while the flag is clear.
        ///
        /// <para>The script's last statements stop the transcript, free the
        /// lock and write the exit code out, 1 when a shot was refused or a
        /// state check failed and 0 otherwise. A script that simply ends
        /// leaves $LASTEXITCODE as its last native command set it, which is
        /// the value the wrapper passes on.</para>
        /// </summary>
        [Fact]
        public void AFocusedRunEndsByAFlagAndTheLinesBelowTheFinallyRun()
        {
            var run = ReadTree("focus");

            var returns = run.Strings("TopLevelReturnsInTheTry");
            Assert.True(returns.Count == 0, "a return at the top level of the cleanup try leaves the script:\n" + string.Join("\n", returns));
            // Positive control: the scan finds the returns the try's functions
            // hold, 91 of them when this was written.
            Assert.True(run.Result.GetProperty("ReturnsInTheTry").GetInt32() >= 50,
                $"the scan found {run.Result.GetProperty("ReturnsInTheTry").GetInt32()} returns inside the try");

            Assert.True(run.Result.GetProperty("FocusedPassIsInTheCleanupTry").GetBoolean());
            Assert.True(run.Result.GetProperty("FocusedPassSetsTheFlag").GetBoolean(),
                "the focused pass does not end by setting $script:FocusedPassRan");
            Assert.True(run.Result.GetProperty("PassesRunWhileTheFlagIsClear").GetBoolean(),
                "the statement behind the focused pass is not if (-not $script:FocusedPassRan)");
            Assert.True(run.Result.GetProperty("NothingElseFollowsInTheTry").GetBoolean(),
                "a statement of the try follows the block the flag skips, so a focused run would run it");
            Assert.True(run.Result.GetProperty("PassesSitBehindTheFlag").GetBoolean());
            Assert.Equal(1, run.Result.GetProperty("FlagClearedBeforeTheTry").GetInt32());

            Assert.Equal(
                new[]
                {
                    "Stop-Transcript | Out-Null",
                    "Remove-Item $lockPath -Force -EA SilentlyContinue",
                    "if ((Get-Count $script:RefusedShots) -gt 0 -or (Get-Count $script:StateFailures) -gt 0) { exit 1 }",
                    "exit 0",
                },
                run.Strings("ClosingLines"));
        }

        /// <summary>
        /// A run that ended in this console does not refuse the next one. A
        /// run that stops on an exit inside its try, on an error or on
        /// CTRL+C runs the finally and not the lines below it, so the lock
        /// keeps a PID that is still alive when the script was started from
        /// a console, and its transcript keeps running. The stop for a
        /// backup no marker speaks for is such an exit, and it tells the
        /// owner what to run next. A lock that names this very process is
        /// therefore not another run, and the transcript that run left is
        /// stopped before the new one starts: Windows PowerShell 5.1 starts
        /// no second transcript on a file one is still writing.
        /// </summary>
        [Fact]
        public void ARunThatEndedInThisConsoleDoesNotRefuseTheNext()
        {
            var run = ReadTree("settings");
            Assert.Equal("$alive -and -not $endedHere", run.Result.GetProperty("SecondRunRefusedUnder").GetString());
            Assert.Equal(
                new[] { "$endedHere = $false", "$endedHere = $alive -and ([int]$otherPid -eq $PID)" },
                run.Strings("EndedHereAssignments"));
            Assert.True(run.Result.GetProperty("LeftoverTranscriptStoppedBeforeTheStart").GetBoolean(),
                "a transcript an earlier run left running is not stopped before Start-Transcript");
        }

        /// <summary>
        /// A wrapper under tools ends with the exit code of the script it
        /// runs. Both ended with 0 whatever that script did, a fatal error
        /// included, so a caller of capture_all_wrapper.ps1 could not tell a
        /// refused shot from a clean run, and one of
        /// prep_xml_for_capture_wrapper.ps1 could not tell a prep that
        /// stopped over a settings backup from one that prepared the file.
        /// The code starts at 1, is taken from $LASTEXITCODE by the statement
        /// that follows the call, and is what the last statement exits with.
        /// </summary>
        [Theory]
        [InlineData("capture_all_wrapper.ps1", "capture_all.ps1")]
        [InlineData("prep_xml_for_capture_wrapper.ps1", "prep_xml_for_capture.ps1")]
        public void TheWrapperEndsWithTheExitCodeOfTheScriptItRuns(string wrapper, string script)
        {
            var run = ReadTree("wrapper", wrapper);
            Assert.Equal(script, run.Result.GetProperty("Called").GetString());
            Assert.Equal("$code = $LASTEXITCODE", run.Result.GetProperty("AfterTheCall").GetString());
            Assert.Equal(new[] { "$code = 1", "$code = $LASTEXITCODE" }, run.Strings("CodeAssignments"));
            Assert.Equal("exit $code", run.Result.GetProperty("LastStatement").GetString());
        }

        /// <summary>
        /// A run of add_slots_via_ui.ps1 that failed ends with exit code 1.
        /// It holds its work in a try whose catch logs the error and keeps it
        /// from ending the script, so it ended with exit code 0 after a
        /// failure, as capture_mouse_gestures.ps1 did before its one shot
        /// moved into capture_all.ps1. The catch now sets a flag
        /// first, the flag is cleared before the try opens, and the exit
        /// code is written out at the end.
        /// </summary>
        [Theory]
        [InlineData("add_slots_via_ui.ps1")]
        public void AFailureTheCatchKeptEndsTheRunWithExitCodeOne(string script)
        {
            var run = ReadTree("ending", script);
            Assert.Equal(new[] { "$script:failed = $false" }, run.Strings("FlagSetBeforeTheTry"));
            Assert.Equal(new[] { "$script:failed = $true" }, run.Strings("CatchFirst"));
            Assert.Equal(new[] { "$script:failed => 1", "always => 0" }, run.Strings("ClosingExits"));
            Assert.Equal("exit 0", run.Result.GetProperty("LastStatement").GetString());
        }

        /// <summary>
        /// The five functions that write the settings file they are handed
        /// through -XmlPath, and every function that reaches one of them,
        /// are called only inside the try whose finally puts the owner's
        /// settings back. The scan of the try's reach skips writes inside
        /// functions, so a call ahead of the try would write the file with
        /// no restore behind it and no test would see it.
        /// </summary>
        [Fact]
        public void TheFunctionsThatWriteTheSettingsFileAreCalledOnlyInsideTheTry()
        {
            var run = ReadTree("settings");
            Assert.Equal(
                new[] { "Ensure-DeviceAssigned", "Ensure-MacrosLoaded", "Seed-AudioDsp", "Set-SlotPreset", "Write-StagedScene" },
                run.Strings("XmlPathWriters"));
            // Positive control: the reach holds a function that only calls one.
            Assert.Contains("Capture-Ds3Preset", run.Strings("ReachTheWriters"));
            Assert.True(run.Result.GetProperty("WriterCallsInTheTry").GetInt32() >= 18,
                $"the scan found {run.Result.GetProperty("WriterCallsInTheTry").GetInt32()} calls inside the try");

            var outside = run.Strings("WriterCallsOutsideTheTry");
            Assert.True(outside.Count == 0, "a function that writes the settings file is called outside the cleanup try:\n" + string.Join("\n", outside));
        }

        /// <summary>
        /// Every settings load reads the encoding the file declares, and
        /// every save writes what PadForge writes. A script read the file
        /// with [xml]$x = Get-Content, and Windows PowerShell 5.1 reads a
        /// UTF-8 file with no byte order mark as ANSI, so a device or profile
        /// name with a letter outside ASCII reached the capture file as two
        /// or three wrong ones and a frame showed it that way. Loads go
        /// through Read-SettingsXml, which hands XmlDocument a stream opened
        /// for sharing with a writer, as Get-Content's is, so a read while
        /// PadForge runs cannot fail PadForge's own save. Saves go through
        /// Save-SettingsXml, UTF-8 with no byte order mark, where
        /// XmlDocument.Save(path) adds one. Before they took the shared
        /// loader and saver, capture_mouse_gestures.ps1 and
        /// prep_xml_for_capture.ps1 read the file with [xml] Get-Content and
        /// saved it with XmlDocument.Save(path). capture_web.ps1 and
        /// diag-sweep.ps1 read it with Get-Content -Raw and wrote it back with
        /// Set-Content -Encoding UTF8, which in Windows PowerShell 5.1 adds the
        /// mark too.
        /// </summary>
        [Theory]
        [MemberData(nameof(SettingsScripts))]
        public void EverySettingsLoadReadsTheEncodingTheFileDeclares(string script)
        {
            var s = ScriptNamed(script);
            var run = ReadTree("encoding", script);

            // Positive control: the scan finds the loads and the saves.
            Assert.True(run.Strings("Loads").Count >= s.FewestLoads, $"{script}: {run.Strings("Loads").Count} loads");
            Assert.True(run.Strings("Saves").Count >= s.FewestSaves, $"{script}: {run.Strings("Saves").Count} saves");

            var casts = run.Strings("XmlCasts");
            Assert.True(casts.Count == 0, $"{script} converts text to XML, which reads a file with no byte order mark as ANSI:\n" + string.Join("\n", casts));
            var asText = run.Strings("SettingsReadAsText");
            Assert.True(asText.Count == 0, $"{script} reads a settings file through Get-Content:\n" + string.Join("\n", asText));
            var direct = run.Strings("DirectSaves");
            Assert.True(direct.Count == 0, $"{script} saves a settings file with XmlDocument.Save(path), which adds a byte order mark:\n" + string.Join("\n", direct));
            var textWrites = run.Strings("SettingsWrittenAsText");
            Assert.True(textWrites.Count == 0, $"{script} writes a settings file as text:\n" + string.Join("\n", textWrites));

            Assert.Equal(new[] { "New-Object" }, run.Strings("LoaderCommands"));
            var loaderObjects = run.Strings("LoaderObjects");
            Assert.Contains("New-Object System.Xml.XmlDocument", loaderObjects);
            Assert.Contains(loaderObjects, o => o.StartsWith("New-Object System.IO.FileStream($full, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read,", StringComparison.Ordinal));
            Assert.Equal(new[] { "ReadWrite" }, run.Strings("LoaderShares"));
            Assert.Equal(new[] { "$doc.Load($stream)" }, run.Strings("LoaderLoads"));

            Assert.Contains("New-Object System.Text.UTF8Encoding($false)", run.Strings("SaverObjects"));
            Assert.Contains(run.Strings("SaverObjects"), o => o.StartsWith("New-Object System.Xml.XmlTextWriter($full,", StringComparison.Ordinal));
            Assert.Equal(new[] { "$Doc.Save($writer)" }, run.Strings("SaverSaves"));
        }

        /// <summary>
        /// PadForge writes its settings file as UTF-8 with no byte order
        /// mark and names the encoding in the declaration: SaveToFile
        /// serializes into a MemoryStream and writes those bytes. That is
        /// the file Windows PowerShell 5.1 reads as ANSI through
        /// Get-Content, and what a capture script's own save has to match.
        /// A change to how SaveToFile writes fails here, where the scripts'
        /// loader and saver can be checked against it.
        /// </summary>
        [Fact]
        public void PadForgeWritesItsSettingsAsUtf8WithNoByteOrderMark()
        {
            string src = File.ReadAllText(AuditDelta20260823Tests.FindRepoFile(
                Path.Combine("PadForge.App", "Services", "SettingsService.cs")));
            Assert.Contains("new(typeof(SettingsFileData));", src);
            Assert.Contains("PadForge.Engine.Common.XmlSafeText.Serialize(serializer, data);", src);
            Assert.Contains("WriteWhole(filePath, serializedBytes);", src);
            // WriteWhole puts those bytes on disk as they are, through a
            // temporary file that replaces the settings file.
            Assert.Contains("stream.Write(bytes, 0, bytes.Length);", src);

            byte[] bytes = SerializeAsPadForgeDoes(new SettingsFileData { Devices = new[] { NamedOutsideAscii() } });
            Assert.False(bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, "the settings file starts with a byte order mark");
            string text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
            Assert.StartsWith("<?xml version=\"1.0\" encoding=\"utf-8\"?>", text);
            Assert.Contains("<ProductName>" + NameOutsideAscii + "</ProductName>", text);
        }

        /// <summary>
        /// The block that prepares a reused settings file keeps a name with
        /// letters outside ASCII and writes the file as PadForge does. The
        /// fixture is written the way SaveToFile writes, and the block runs
        /// on it through the script's own loader and saver: the name comes
        /// back letter for letter and the file has no byte order mark. The
        /// saver asks for the copy of the file before it writes, once, and
        /// a file the block does not save is not copied.
        /// </summary>
        [Fact]
        public void TheReusedFileBlockKeepsANameOutsideAsciiAndCopiesTheFileFirst()
        {
            var run = RunDriver("reused", "plain", ArrangeReusedFiles);
            Assert.Empty(run.Strings("ParseErrors"));
            AssertNothingRefused(run);
            var cases = run.Result.GetProperty("Cases").EnumerateArray()
                .ToDictionary(c => c.GetProperty("Name").GetString());

            var named = cases["reused-direct-name"];
            string expected = string.Join(" ", NameOutsideAscii.Select(ch => ((int)ch).ToString("X4")));
            Assert.Equal(new[] { expected }, Strings(named, "NamesHex"));
            Assert.False(named.GetProperty("ByteOrderMark").GetBoolean(), "the block's save added a byte order mark");
            Assert.Contains(Strings(named, "Lines"), l => l.StartsWith("  Direct: set Use2DControllerView=false", StringComparison.Ordinal));

            foreach (var (name, c) in cases)
            {
                var asked = Strings(c, "CopyAsked");
                if (name.EndsWith("-noapp", StringComparison.Ordinal))
                    Assert.Empty(asked);
                else
                    Assert.Equal(name + ".xml", Path.GetFileName(Assert.Single(asked)));
                Assert.False(c.GetProperty("ByteOrderMark").GetBoolean(), name);
            }
        }

        /// <summary>
        /// A staging that failed saves no snapshot. A later -Only run loads
        /// the snapshot as staged and skips its own staging, so one saved
        /// after a failed step put a slot short of a device, or a synthetic
        /// row with a real row's serial, under every targeted shot taken
        /// from it. The copy sits in the else of the test for a failed
        /// staging, and the other branch writes nothing and says so.
        /// </summary>
        [Fact]
        public void AFailedStagingSavesNoSnapshot()
        {
            var run = ReadTree("snapshot");
            Assert.Equal(1, run.Result.GetProperty("SnapshotSaves").GetInt32());
            // Positive control: the Direct path still loads what is saved.
            Assert.Equal(1, run.Result.GetProperty("SnapshotLoads").GetInt32());
            Assert.True(run.Result.GetProperty("SavedOnlyWhenTheStagingHeld").GetBoolean(),
                "the snapshot is not saved in the else of if ($script:StagingFailed)");
            Assert.Empty(run.Strings("FailedBranchWrites"));
            string says = run.Result.GetProperty("FailedBranchSays").GetString();
            Assert.Contains("A staging step failed, so this staging is not saved as the snapshot for targeted runs.", says);
            Assert.Contains("-ForegroundColor Red", says);
        }

        /// <summary>
        /// Every script that writes the owner's settings file guards it with
        /// the same code: settings_guard.ps1, dot-sourced once among its
        /// top-level statements and ahead of the first call of one of its
        /// functions. No script defines one of those functions again or sets
        /// one of the guard's values, so there is one marker rule, one folder
        /// of copies and one loader and saver, and a fix to any of them
        /// reaches every script that writes the file. Before the guard was
        /// shared, capture_all.ps1 and two capture scripts since folded into
        /// it carried the block word for word, and the rest carried none of
        /// it.
        /// </summary>
        [Theory]
        [MemberData(nameof(SettingsScripts))]
        public void EveryScriptThatWritesTheSettingsFileLoadsTheSharedGuardOnce(string script)
        {
            var run = ReadTree("shared", script);
            Assert.Equal(new[] { ". (Join-Path $PSScriptRoot 'settings_guard.ps1')" }, run.Strings("DotSources"));
            Assert.Equal(1, run.Result.GetProperty("DotSourcedAtTheTop").GetInt32());
            // Positive control: the script calls the guard's functions.
            Assert.True(run.Result.GetProperty("GuardCalls").GetInt32() >= 4, $"{script}: {run.Result.GetProperty("GuardCalls").GetInt32()} calls");
            Assert.True(run.Result.GetProperty("LoadedBeforeTheFirstCall").GetBoolean(), $"{script} calls a guard function before it loads the guard");
            var defined = run.Strings("GuardFunctionsDefinedHere");
            Assert.True(defined.Count == 0, $"{script} defines a guard function of its own:\n" + string.Join("\n", defined));
            var set = run.Strings("GuardValuesSetHere");
            Assert.True(set.Count == 0, $"{script} sets a value the guard keeps:\n" + string.Join("\n", set));
        }

        /// <summary>
        /// The scripts that name the owner's settings file in their code are
        /// the four the guard tests cover. A fifth that names it fails here
        /// until it is added to the list, and with it to the tests of the
        /// copies, the marker, the stop and the encoding. A name in a comment
        /// does not count: the two wrappers mention the file and never touch
        /// it.
        /// </summary>
        [Fact]
        public void EveryScriptUnderToolsThatNamesTheSettingsFileIsGuarded()
        {
            var run = ReadTree("names");
            var named = run.Strings("NameTheSettingsFile").OrderBy(n => n, StringComparer.Ordinal).ToList();
            Assert.Equal(SettingsScriptList.Select(s => s.Name).OrderBy(n => n, StringComparer.Ordinal), named);
        }

        /// <summary>
        /// Every screenshot comes from capture_all.ps1. The web controller's
        /// pages and the colorways were separate scripts carrying copies of
        /// its recipes, and the copies fell behind: four web pages shipped
        /// stale in 5.0.0, and the colorways showed one controller in the
        /// sidebar where every other frame shows seven. So no other script
        /// under tools grabs the screen into the docs' image folder, the web
        /// pages and the colorways are tables in capture_all.ps1 that both
        /// its passes read, and its shot function refuses a PadForge frame
        /// whose sidebar does not show every staged slot.
        /// </summary>
        [Fact]
        public void EveryScreenshotComesFromTheOneCaptureScript()
        {
            string capture = AuditDelta20260823Tests.FindRepoFile(Path.Combine("tools", "capture_all.ps1"));
            string tools = Path.GetDirectoryName(capture);
            var takers = Directory.GetFiles(tools, "*.ps1")
                .Where(f => !string.Equals(Path.GetFileName(f), "capture_all.ps1", StringComparison.OrdinalIgnoreCase))
                .Where(f =>
                {
                    string text = File.ReadAllText(f);
                    return text.Contains("CopyFromScreen", StringComparison.Ordinal)
                        && Regex.IsMatch(text, @"wiki[\\/]+images", RegexOptions.IgnoreCase);
                })
                .Select(Path.GetFileName)
                .ToList();
            Assert.True(takers.Count == 0, "scripts other than capture_all.ps1 take screenshots for the docs: " + string.Join(", ", takers));

            string script = File.ReadAllText(capture);
            // Positive control: capture_all.ps1 grabs the screen into the docs' folder.
            Assert.Contains("CopyFromScreen", script, StringComparison.Ordinal);
            Assert.Matches(@"wiki[\\/]+images", script);
            foreach (string page in new[] { "web-landing", "web-controller", "web-dualsense", "web-steamdeck", "web-switch2pro", "web-custom" })
                Assert.Contains($"Name = '{page}'", script, StringComparison.Ordinal);
            Assert.Contains("$script:ColorwayScenes = @(", script, StringComparison.Ordinal);

            // Both passes run the same recipes: the focused pass and the full
            // run each call the popup's, the web pages' and the colorways'.
            Assert.Equal(2, Regex.Matches(script, @"^\s*Capture-AddControllerPopup\s*$", RegexOptions.Multiline).Count);
            Assert.Equal(2, Regex.Matches(script, @"^\s*Capture-WebShots\s*$", RegexOptions.Multiline).Count);
            Assert.Equal(2, Regex.Matches(script, @"^\s*Capture-Colorways\s*$", RegexOptions.Multiline).Count);
            Assert.Equal(2, Regex.Matches(script, @"^\s*Capture-StarterGallery\s*$", RegexOptions.Multiline).Count);

            // A web page is taken by Edge's visible frame. The window's own
            // rectangle counts the invisible resize border, and the frames
            // the recipe took that way showed the desktop down two edges.
            int web = script.IndexOf("function Cap-Web {", StringComparison.Ordinal);
            int webEnd = script.IndexOf("\nfunction ", web + 1, StringComparison.Ordinal);
            Assert.True(web >= 0 && webEnd > web, "no Cap-Web function");
            string webBody = script.Substring(web, webEnd - web);
            int bounds = webBody.IndexOf("[Win32]::DwmGetWindowAttribute($ehwnd, 9,", StringComparison.Ordinal);
            int webGrab = webBody.IndexOf("CopyFromScreen", StringComparison.Ordinal);
            Assert.True(bounds >= 0 && webGrab > bounds, "Cap-Web does not take the window's visible frame");

            // The shot function checks the sidebar against the staged count.
            int cap = script.IndexOf("function Cap {", StringComparison.Ordinal);
            int capEnd = script.IndexOf("\nfunction ", cap + 1, StringComparison.Ordinal);
            Assert.True(cap >= 0 && capEnd > cap, "no Cap function");
            string capBody = script.Substring(cap, capEnd - cap);
            Assert.Contains("Get-SidebarSlotCount", capBody, StringComparison.Ordinal);
            Assert.Contains("$script:ExpectedSlots", capBody, StringComparison.Ordinal);

            // A PadForge frame is the window's visible frame clipped to the
            // monitor's work area. The window rectangle of a maximized window
            // adds 11 px of black on three sides and 11 px of the taskbar at
            // the bottom, which every frame of 5.0.0 carried.
            int dwm = capBody.IndexOf("[Win32]::DwmGetWindowAttribute(", StringComparison.Ordinal);
            int work = capBody.IndexOf(".WorkingArea", StringComparison.Ordinal);
            int capGrab = capBody.IndexOf("CopyFromScreen", StringComparison.Ordinal);
            Assert.True(dwm >= 0 && work > dwm && capGrab > work, "Cap does not clip its frame to the visible window and the work area");
        }

        /// <summary>
        /// Each colorway row of capture_all.ps1 names its colorway as the 3D
        /// view's picker lists it, since the recipe picks the row by that
        /// name: the AppearanceNames entry beside the row's App in its
        /// family's AppearanceIds. A colorway the app renames or drops fails
        /// here, not as a refused shot at the end of a run.
        /// </summary>
        [Fact]
        public void EachColorwayRowNamesItsColorwayAsThePickerDoes()
        {
            string script = File.ReadAllText(AuditDelta20260823Tests.FindRepoFile(Path.Combine("tools", "capture_all.ps1")));
            int rowCount = Regex.Matches(script, @"@\{ Shot = ""colorway-").Count;
            var rows = Regex.Matches(script,
                @"Shot = ""(colorway-[a-z0-9-]+)"";\s+Type = \d; Profile = ""[a-z0-9-]+""; Fam = ""(\w+)"";\s+App = ""(\w+)""; Name = ""([^""]+)""");
            // Positive control: the table is there, and every row reads whole.
            Assert.True(rowCount >= 20, $"found {rowCount} colorway rows");
            Assert.Equal(rowCount, rows.Count);
            foreach (Match m in rows)
            {
                string shot = m.Groups[1].Value, family = m.Groups[2].Value, id = m.Groups[3].Value;
                (string[] ids, string[] names) = family switch
                {
                    "DualSense" => (PadForge.Models3D.ControllerModelDualSense.AppearanceIds, PadForge.Models3D.ControllerModelDualSense.AppearanceNames),
                    "XboxSeries" => (PadForge.Models3D.ControllerModelXboxSeries.AppearanceIds, PadForge.Models3D.ControllerModelXboxSeries.AppearanceNames),
                    _ => (null, null),
                };
                Assert.True(ids != null, $"{shot}: no colorway list for the family {family}");
                int i = Array.IndexOf(ids, id);
                Assert.True(i >= 0, $"{shot}: {id} is not a {family} colorway");
                Assert.Equal(names[i], m.Groups[4].Value);
            }
        }

        /// <summary>
        /// No frame shows a control lit by the pointer. On 2026-10-23 a run
        /// showed a lit trigger on the controller model: the click that
        /// opened the slot left the pointer over its page's model, which
        /// lights what sits under the pointer. capture_all.ps1 moves the
        /// pointer onto the main window's empty title bar after every click
        /// and before every frame, into the dialog's own head while a modal
        /// dialog is up, and before a frame waits for a hover where the
        /// pointer was to fade out. Every click goes through Click-At, the
        /// one function that clicks the screen and then parks. The web pages'
        /// frames move the pointer onto Edge's title bar, off the page. No
        /// park in the window's bottom-right corner is left: that corner
        /// holds the status bar's engine pill.
        /// </summary>
        [Fact]
        public void ThePointerRestsOffEveryControlBeforeEachFrame()
        {
            string script = File.ReadAllText(AuditDelta20260823Tests.FindRepoFile(Path.Combine("tools", "capture_all.ps1")));
            string Body(string name)
            {
                int at = script.IndexOf("function " + name + " {", StringComparison.Ordinal);
                Assert.True(at >= 0, "no function " + name);
                int end = script.IndexOf("\nfunction ", at + 1, StringComparison.Ordinal);
                return end > at ? script.Substring(at, end - at) : script.Substring(at);
            }

            string park = Body("Park-Pointer");
            Assert.Contains("IsWindowEnabled", park, StringComparison.Ordinal);
            Assert.Contains("[UiSettle]::ActiveDialog(", park, StringComparison.Ordinal);
            Assert.Contains("[UiSettle]::WaitStableBox(", park, StringComparison.Ordinal);

            string cap = Body("Cap");
            int settle = cap.IndexOf("Park-Pointer -Settle", StringComparison.Ordinal);
            int grab = cap.IndexOf("CopyFromScreen", StringComparison.Ordinal);
            Assert.True(settle >= 0 && grab > settle, "Cap does not park the pointer and wait before it grabs the screen");
            Assert.DoesNotContain("[Win32]::MoveTo(", cap, StringComparison.Ordinal);

            string clickAt = Body("Click-At");
            int press = clickAt.IndexOf("[Win32]::ClickAt(", StringComparison.Ordinal);
            int parked = press < 0 ? -1 : clickAt.IndexOf("Park-Pointer", press, StringComparison.Ordinal);
            Assert.True(press >= 0 && parked > press, "Click-At does not park the pointer after its click");
            Assert.Single(Regex.Matches(script, @"\[Win32\]::ClickAt\("));
            Assert.Contains("Click-At (", Body("Click-El"), StringComparison.Ordinal);

            string web = Body("Cap-Web");
            int move = web.IndexOf("[Win32]::MoveTo(", StringComparison.Ordinal);
            int webGrab = web.IndexOf("CopyFromScreen", StringComparison.Ordinal);
            Assert.True(move >= 0 && webGrab > move, "Cap-Web does not move the pointer off the page before it grabs the screen");

            Assert.DoesNotContain("Bottom - 15)", script, StringComparison.Ordinal);
        }

        /// <summary>
        /// The frames after the Workshop recipe show the staged slots. The
        /// recipe applies the profile it imports, which brings only the slots
        /// of the config it came from, and on 2026-10-23 every frame after it
        /// showed one slot in the sidebar. Its own frame is the one exempt
        /// from the sidebar check, and the Default profile, which holds the
        /// staged slots, is loaded back before the next frame.
        /// </summary>
        [Fact]
        public void TheWorkshopRecipeLoadsTheStagedSlotsBackBeforeTheNextFrame()
        {
            string script = File.ReadAllText(AuditDelta20260823Tests.FindRepoFile(Path.Combine("tools", "capture_all.ps1")));
            int cap = script.IndexOf("Cap \"workshop-applied\"", StringComparison.Ordinal);
            Assert.True(cap >= 0, "no workshop-applied frame");
            int restore = script.IndexOf("Restore-DefaultProfile", cap, StringComparison.Ordinal);
            int next = script.IndexOf("Cap \"", cap + 1, StringComparison.Ordinal);
            Assert.True(restore > cap && (next < 0 || restore < next), "the Default profile is not loaded back before the next frame");
            string before = script.Substring(Math.Max(0, cap - 400), Math.Min(400, cap));
            Assert.Contains("$script:ExpectedSlots = 0", before, StringComparison.Ordinal);
            // That frame is the only place inside the run that turns the check off.
            Assert.Single(Regex.Matches(script, @"^[ \t]+\$script:ExpectedSlots = 0\s*$", RegexOptions.Multiline));
        }

        /// <summary>
        /// settings_guard.ps1 is ASCII, since a script with no byte order
        /// mark is read as ANSI by Windows PowerShell 5.1, and the file it
        /// guards nothing until a function of it is called: loading it sets
        /// four values and runs no command. Each of its functions makes any
        /// failure stop the script that called it as its first statement.
        /// Two of the scripts run with ErrorActionPreference Continue, and a
        /// copy that failed there was followed by the write it was taken
        /// for.
        /// </summary>
        [Fact]
        public void TheSharedGuardIsAsciiRunsNothingWhenLoadedAndStopsOnAnyFailure()
        {
            byte[] bytes = File.ReadAllBytes(AuditDelta20260823Tests.FindRepoFile(Path.Combine("tools", "settings_guard.ps1")));
            Assert.True(bytes.All(b => b <= 127), "settings_guard.ps1 holds a byte outside ASCII");

            var run = ReadTree("block");
            Assert.Equal(
                new[]
                {
                    "Read-SettingsXml", "Save-SettingsXml", "Save-SettingsCopy", "Get-BackupMarkerPath", "Set-BackupMarker",
                    "Remove-BackupMarker", "Test-SameSettings", "Get-BackupDoubt", "Get-BackupStopLines",
                },
                run.Strings("GuardFunctions"));
            Assert.Equal(9, run.Result.GetProperty("GuardFunctionsInAll").GetInt32());
            Assert.All(run.Strings("GuardFirstStatements"), first => Assert.Equal("$ErrorActionPreference = 'Stop'", first));
            Assert.False(run.Result.GetProperty("GuardHasParamBlock").GetBoolean());
            Assert.Equal(
                new[]
                {
                    "$script:CaptureStateDir = Join-Path $env:TEMP \"PadForge_Capture\"",
                    "$script:BackupMarkerMaxAgeHours = 12",
                    "$script:SettingsCopied = @()",
                    "$script:SettingsGuardCaller = $MyInvocation.PSCommandPath",
                },
                run.Strings("GuardLoadStatements"));
        }

        /// <summary>
        /// Each backup has a marker of its own, named after the backup's full
        /// path. The four scripts keep their backups in three places: the
        /// capture beside the settings file, the two staging scripts beside
        /// it under a name of their own, the diagnostics sweep in TEMP. With
        /// one marker file for all of them, a run of one script would replace
        /// the marker of a backup another had left, and that script's next
        /// run would stop over its own backup. Scripts that keep their backup
        /// in the same place share its marker, as the two staging scripts do.
        /// The marker paths are worked out for the three backups under this
        /// test's folder, by the guard's own function, which touches no file:
        /// three backups, three markers, all in the folder the guard keeps
        /// them in.
        /// </summary>
        [Fact]
        public void EachBackupHasAMarkerOfItsOwn()
        {
            string[] names =
            {
                Path.Combine("PadForge", "PadForge.xml.bak"),
                Path.Combine("PadForge", "PadForge.xml.bak-capture"),
                Path.Combine("Temp", "PadForge.xml.diagsweep.bak"),
            };
            string root = null;
            var run = RunDriver("markerpath", "plain", dir =>
            {
                root = dir;
                File.WriteAllLines(Path.Combine(dir, "backups.txt"), names.Select(n => Path.Combine(dir, n)), new UTF8Encoding(false));
            });
            Assert.Empty(run.Strings("ParseErrors"));
            AssertNothingRefused(run);

            var markers = run.Result.GetProperty("MarkerPaths").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString());
            Assert.Equal(names.Length, markers.Count);
            Assert.Equal(names.Length, markers.Values.Distinct(StringComparer.OrdinalIgnoreCase).Count());
            string folder = Path.Combine(root, "state") + Path.DirectorySeparatorChar;
            foreach (var (backup, marker) in markers)
            {
                Assert.StartsWith(folder + "settings-backup-marker-", marker, StringComparison.Ordinal);
                Assert.EndsWith(".json", marker, StringComparison.Ordinal);
                Assert.Contains(Path.GetFileName(backup), marker, StringComparison.Ordinal);
            }
        }

        /// <summary>
        /// A marker speaks only for its own backup and only while it is
        /// fresh. Get-BackupDoubt finds no doubt where no backup is, where
        /// the owner said which file to keep and where the two files hold
        /// the same bytes, and asks the marker after that: no marker, one
        /// that cannot be read, one that names another backup and one older
        /// than BackupMarkerMaxAgeHours each leave the doubt standing. A
        /// hand restore leaves the marker where it is, so without the age a
        /// marker would speak for a backup the owner had moved on from days
        /// before. The help states the same number of hours.
        /// </summary>
        [Fact]
        public void AMarkerSpeaksOnlyForItsOwnBackupAndOnlyWhileFresh()
        {
            var run = ReadTree("block");
            var returns = run.Strings("DoubtReturns");
            Assert.Equal(9, returns.Count);
            Assert.All(returns.Take(3), r => Assert.Equal("\"\"", r));
            Assert.All(returns.Skip(3).Take(5), r => Assert.NotEqual("\"\"", r));
            Assert.Equal("\"no run left a marker for it\"", returns[3]);
            Assert.Equal("\"\"", returns[8]);

            Assert.True(run.Result.GetProperty("SameBytesAreAskedBeforeTheMarker").GetBoolean());
            Assert.Equal(1, run.Result.GetProperty("ComparesTheMarkersPath").GetInt32());
            Assert.Equal(1, run.Result.GetProperty("ComparesTheMarkersAge").GetInt32());
            Assert.Equal(new[] { "Backup", "TakenUtc", "Script" }, run.Strings("MarkerKeys"));

            Assert.Equal("12", run.Result.GetProperty("MaxAgeHours").GetString());
            string header = HelpHeader();
            Assert.Contains("no older than twelve hours names it", header);
            Assert.Contains("-KeepSettingsFrom File or -KeepSettingsFrom Backup", header);
            Assert.Contains("ends with exit code 1", header);
        }

        /// <summary>
        /// Twenty copies are kept at most, and a run's own capture files
        /// never push the owner's out. The first copy a run takes of a path
        /// is of a file it found, and the newest fourteen of those are kept.
        /// A later copy of the same path is of a file the run wrote, and
        /// the newest six are kept. The colorway step of capture_all.ps1
        /// writes the settings file twenty-three times in one run, so a
        /// single count of twenty would have dropped the copy of the owner's
        /// file before that run ended. The new copy is in place before anything is removed.
        ///
        /// <para>The newest are found by name, so the time in a name is UTC
        /// and fixed in width. A local time runs through one hour twice when
        /// the clocks go back, and a copy taken in the second pass sorted
        /// before an older one taken in the first.</para>
        /// </summary>
        [Fact]
        public void TwentyCopiesAreKeptAndARunsOwnNeverPushTheOwnersOut()
        {
            var run = ReadTree("block");
            Assert.Equal("if ($kind -eq \"found\") { 14 } else { 6 }", run.Result.GetProperty("KeepRule").GetString());
            var counts = run.Result.GetProperty("KeepCounts").EnumerateArray().Select(e => e.GetInt32()).ToList();
            Assert.Equal(new[] { 14, 6 }, counts);
            Assert.Equal(20, counts.Sum());
            Assert.True(run.Result.GetProperty("PrunesTheOldestByName").GetBoolean());
            Assert.True(run.Result.GetProperty("CopyComesBeforeThePrune").GetBoolean());
            Assert.Equal("[datetime]::UtcNow.ToString(\"yyyyMMdd'T'HHmmss'.'fff'Z'\", [cultureinfo]::InvariantCulture)",
                run.Result.GetProperty("StampText").GetString());

            string header = HelpHeader();
            Assert.Contains("under a name that carries the UTC time. Twenty copies", header);
            Assert.Contains("the newest fourteen of files a run found and the newest six of files a run wrote itself", header);
        }

        /// <summary>The help at the top of capture_all.ps1, with each run of
        /// white space made one space, so a rewrap of the text is not a
        /// change to what it says.</summary>
        private static string HelpHeader()
        {
            string script = File.ReadAllText(AuditDelta20260823Tests.FindRepoFile(Path.Combine("tools", "capture_all.ps1")));
            return Regex.Replace(script.Substring(0, script.IndexOf("#>", StringComparison.Ordinal)), @"\s+", " ");
        }

        /// <summary>
        /// Two settings files are the same only when their bytes are, and the
        /// check reads each through a stream shared for writing. It runs
        /// while PadForge may still be up, before a run has stopped it.
        /// Get-FileHash opens a file for reading alone, and an autosave
        /// PadForge made in that moment was refused and not tried again
        /// until the next change: SettingsService stops its autosave timer
        /// before it saves. Here the check runs alone on files the test
        /// wrote. A file another process is writing is still read, a file
        /// nobody may read stops the check instead of passing for its twin,
        /// and a missing file or one changed byte is not the same.
        /// </summary>
        [Fact]
        public void TwoSettingsFilesAreTheSameOnlyByTheirBytesAndTheCheckSharesTheFile()
        {
            byte[] bytes = SerializeAsPadForgeDoes(new SettingsFileData { Devices = new[] { SteamTemplate() } });
            byte[] changed = (byte[])bytes.Clone();
            int at = Encoding.ASCII.GetString(bytes).IndexOf("Steam Controller", StringComparison.Ordinal);
            Assert.True(at > 0, "the fixture holds no 'Steam Controller'");
            changed[at] = (byte)'s';

            var run = RunDriver("same", "plain", dir =>
            {
                File.WriteAllBytes(Path.Combine(dir, "same-a.xml"), bytes);
                File.WriteAllBytes(Path.Combine(dir, "same-b.xml"), bytes);
                File.WriteAllBytes(Path.Combine(dir, "same-c.xml"), changed);
            });
            Assert.Empty(run.Strings("ParseErrors"));
            Assert.DoesNotContain("Get-FileHash", run.Strings("SameCommands"));
            Assert.Equal(new[] { "ReadWrite" }, run.Strings("SameShares"));
            AssertNothingRefused(run);

            var cases = run.Result.GetProperty("Cases");
            Assert.Equal("True", cases.GetProperty("same bytes").GetString());
            Assert.Equal("False", cases.GetProperty("one byte differs").GetString());
            Assert.Equal("False", cases.GetProperty("one file missing").GetString());
            Assert.Equal("True", cases.GetProperty("the other being written").GetString());
            Assert.Equal("stopped", cases.GetProperty("the other unreadable").GetString());
        }

        // ─── Template rows ───

        /// <summary>Elements New-SyntheticDevice sets itself, from its own
        /// arguments or to fixed values, plus the two dates, which nothing
        /// shows or reads.</summary>
        private static readonly HashSet<string> SetBySyntheticClone = new(StringComparer.Ordinal)
        {
            "InstanceGuid", "InstanceName", "ProductGuid", "ProductName", "VendorId", "ProdId",
            "DevicePath", "CapType", "IsEnabled", "IsHidden", "HasGyro", "HasAccel", "HasTouchpad",
            "HasRumbleTriggers", "DateCreated", "DateUpdated",
            "CapAxeCount", "CapButtonCount", "RawButtonCount", "CapPovCount", "RawAxisCount",
        };

        /// <summary>New-WiiDevice's set: the same identity, and the gamepad
        /// shape it keeps on purpose so auto-map has rows to build from.</summary>
        private static readonly HashSet<string> KeptByGamepadClone = new(StringComparer.Ordinal)
        {
            "InstanceGuid", "InstanceName", "ProductGuid", "ProductName", "VendorId", "ProdId",
            "DevicePath", "CapType", "IsEnabled", "IsHidden", "HasGyro", "HasAccel", "HasTouchpad",
            "HasRumbleTriggers", "DateCreated", "DateUpdated",
            "CapAxeCount", "CapButtonCount", "CapButtonIndices", "CapAxisIndices", "RawButtonCount",
            "RawAxisCount", "HasExtraGenericAxes", "CapPovCount", "DeviceObjects",
        };

        /// <summary>What a device PadForge has just met holds.</summary>
        private static void AssertNewRecord(UserDevice ud)
        {
            string who = ud.ProductName;
            Assert.Equal(string.Empty, ud.SdlGuid);
            Assert.Equal(string.Empty, ud.DisplayName);
            Assert.Equal(string.Empty, ud.GunCalibration);
            Assert.Equal(string.Empty, ud.FalconCalibration);
            Assert.False(ud.HidHideEnabled, who);
            Assert.Empty(ud.HidHideInstanceIds);
            Assert.False(ud.ConsumeInputEnabled, who);
            Assert.False(ud.ForceRawJoystickMode, who);
            Assert.False(ud.QuickChargeEnabled, who);
            Assert.False(ud.TouchpadOnlyInPadForge, who);
            Assert.False(ud.TouchpadKeepPending, who);
            Assert.Equal(0, ud.IdleDisconnectSeconds);
            Assert.Equal(0, ud.PeripheralOutputs);
            Assert.False(ud.HasAccelAux, who);
            Assert.False(ud.HasGyroAux, who);
            Assert.Equal(0, ud.CapTouchpadCount);
            Assert.True(ud.CapTouchpadFingerCounts == null || ud.CapTouchpadFingerCounts.Length == 0, who);
            Assert.Null(ud.CapTouchpadPressure);
            Assert.Null(ud.CapTouchpadClick);
        }

        private static UserDevice SteamTemplate() =>
            Template(SteamControllerGuid, "Steam Controller", 0x28DE, 0x1303,
                @"\\?\HID#{00001812-0000-1000-8000-00805f9b34fb}_Dev_VID&0228de_PID&1303_REV&0100_" + SteamSerial,
                SteamSerial, "template-sdl-guid-steam");

        private static UserDevice EdgeTemplate() =>
            Template(DualSenseEdgeGuid, "DualSense Edge Wireless Controller", 0x054C, 0x0DF2,
                @"\\?\HID#{00001124-0000-1000-8000-00805f9b34fb}_VID&0002054c_PID&0df2#a&020000000002",
                EdgeSerial, "template-sdl-guid-edge");

        /// <summary>A row as an interrupted run of the older script leaves
        /// it under a synthetic GUID: a clone of the Steam Controller under
        /// the synthetic's identity and type, so it still holds that row's
        /// serial, SDL GUID and settings.</summary>
        private static UserDevice Leftover(string guid, string name, ushort vid, ushort pid, string path, int capType)
        {
            var d = Template(new Guid(guid), name, vid, pid, path, SteamSerial, "template-sdl-guid-steam");
            d.CapType = capType;
            return d;
        }

        /// <summary>A gamepad row with every serialized element set to a
        /// value a new record never holds.</summary>
        private static UserDevice Template(Guid guid, string name, ushort vid, ushort pid,
                                           string path, string serial, string sdlGuid)
        {
            var d = new UserDevice();
            foreach (var p in SerializedProperties())
                p.SetValue(d, SampleValue(p));
            d.InstanceGuid = guid;
            d.ProductGuid = guid;
            d.InstanceName = name;
            d.ProductName = name;
            d.VendorId = vid;
            d.ProdId = pid;
            d.DevicePath = path;
            d.SerialNumber = serial;
            d.SdlGuid = sdlGuid;
            d.CapType = InputDeviceType.Gamepad;
            d.IsEnabled = true;
            d.IsHidden = false;
            return d;
        }

        private static IEnumerable<PropertyInfo> SerializedProperties() =>
            typeof(UserDevice).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.CanRead && p.GetSetMethod() != null && p.GetIndexParameters().Length == 0
                            && p.GetCustomAttribute<XmlIgnoreAttribute>() == null);

        private static object SampleValue(PropertyInfo p)
        {
            Type t = p.PropertyType;
            if (t == typeof(string)) return "template-" + p.Name;
            if (t == typeof(bool)) return true;
            if (t == typeof(bool?)) return (bool?)true;
            if (t == typeof(int)) return 7;
            if (t == typeof(ushort)) return (ushort)0x1234;
            if (t == typeof(Guid)) return new Guid("0badf00d-0000-4000-8000-000000000007");
            if (t == typeof(DateTime)) return new DateTime(2026, 1, 2, 3, 4, 5);
            if (t == typeof(int[])) return new[] { 1, 2 };
            if (t == typeof(List<string>)) return new List<string> { @"HID\VID_28DE&PID_1303\TEMPLATE" };
            if (t == typeof(DeviceObjectItem[])) return new[] { new DeviceObjectItem { Name = "Button 0", InputIndex = 0 } };
            throw new InvalidOperationException(
                $"UserDevice.{p.Name} ({t}) has no sample value. Give it one, so the synthetic-row check covers it.");
        }

        private static SettingsFileData Deserialize(string xml)
        {
            var serializer = new XmlSerializer(typeof(SettingsFileData));
            using var reader = new StringReader(xml);
            return (SettingsFileData)serializer.Deserialize(reader);
        }

        /// <summary>One device list for the saved-list check, with the GUIDs
        /// the injection would have recorded beside it.</summary>
        private static void WriteCase(string dir, string name, string[] syntheticGuids,
                                      params (string Guid, string Name, string Serial, string SdlGuid)[] rows)
        {
            var doc = new XDocument(new XElement("PadForgeSettings", new XElement("Devices",
                rows.Select(r => new XElement("Device",
                    new XElement("InstanceGuid", r.Guid),
                    new XElement("ProductName", r.Name),
                    new XElement("SerialNumber", r.Serial),
                    new XElement("SdlGuid", r.SdlGuid))))));
            File.WriteAllText(Path.Combine(dir, name + ".xml"), doc.ToString(), new UTF8Encoding(false));
            File.WriteAllLines(Path.Combine(dir, name + ".guids"), syntheticGuids, new UTF8Encoding(false));
        }

        /// <summary>The settings files the reused-file block is run on, each
        /// as a Direct run and as a tail run reuses it: with and without
        /// AppSettings for the starting view, and with a device list where
        /// the NFC reader stand-in holds the Steam Controller's serial or
        /// holds none.</summary>
        private static void ArrangeReusedFiles(string dir)
        {
            const string app = "<AppSettings><Language>en</Language></AppSettings>";
            string Devices(string nfcSerial) =>
                "<Devices>"
                + $"<Device><InstanceGuid>{SteamControllerGuid}</InstanceGuid><ProductName>Steam Controller</ProductName><SerialNumber>{SteamSerial}</SerialNumber><SdlGuid /></Device>"
                + $"<Device><InstanceGuid>{NfcGuid}</InstanceGuid><ProductName>NFC Reader</ProductName><SerialNumber>{nfcSerial}</SerialNumber><SdlGuid /></Device>"
                + $"<Device><InstanceGuid>{Ds3Guid}</InstanceGuid><ProductName>DualShock 3</ProductName><SerialNumber>00005e005302</SerialNumber><SdlGuid /></Device>"
                + "</Devices>";
            var bodies = new Dictionary<string, string>
            {
                ["app"] = app,
                ["noapp"] = "<Devices />",
                ["leak"] = Devices(SteamSerial) + app,
                ["clean"] = Devices("") + app,
            };
            foreach (string mode in new[] { "direct", "tail" })
                foreach (var (name, body) in bodies)
                    File.WriteAllText(Path.Combine(dir, $"reused-{mode}-{name}.xml"),
                        $"<PadForgeSettings>{body}</PadForgeSettings>", new UTF8Encoding(false));

            // One more, written the way PadForge writes its settings file,
            // with a device whose name has letters outside ASCII.
            File.WriteAllBytes(Path.Combine(dir, "reused-direct-name.xml"),
                SerializeAsPadForgeDoes(new SettingsFileData
                {
                    Devices = new[] { NamedOutsideAscii() },
                    AppSettings = new AppSettingsData(),
                }));
        }

        /// <summary>Five letters outside ASCII, three that take two bytes in
        /// UTF-8 and two that take three. Read as ANSI they come back as
        /// twelve characters, so the 21 of this name become 28.</summary>
        private const string NameOutsideAscii = "Contr\u00F4leur \u00DCn\u00EFcode \u624B\u67C4";

        private static UserDevice NamedOutsideAscii()
        {
            var d = SteamTemplate();
            d.ProductName = NameOutsideAscii;
            d.InstanceName = NameOutsideAscii;
            return d;
        }

        /// <summary>The bytes SettingsService.SaveToFile writes: the
        /// serializer into a MemoryStream, then those bytes to the file.</summary>
        private static byte[] SerializeAsPadForgeDoes(SettingsFileData data)
        {
            return PadForge.Engine.Common.XmlSafeText.Serialize(new XmlSerializer(typeof(SettingsFileData)), data);
        }

        // ─── The driver ───

        private sealed class DriverRun
        {
            public JsonElement Result;
            public string InXml;
            public string OutXml;

            public List<string> Strings(string name) =>
                Result.GetProperty(name).EnumerateArray().Select(e => e.GetString()).ToList();
        }

        private static void AssertNothingRefused(DriverRun run)
        {
            var refused = run.Strings("Refused");
            Assert.True(refused.Count == 0,
                "the driver would not run the block, which calls something it is not known to need:\n" + string.Join("\n", refused));
        }

        private static void AssertRan(DriverRun run)
        {
            Assert.Empty(run.Strings("ParseErrors"));
            AssertNothingRefused(run);
            Assert.True(run.Result.GetProperty("Ran").GetBoolean(), "the driver did not run the injection");
            Assert.NotNull(run.OutXml);
        }

        private static string PowerShellExe() => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe");

        private static DriverRun RunDriver(string mode, SettingsFileData input, string variant = "plain") =>
            RunDriver(mode, variant, dir =>
            {
                if (input == null) return;
                var settings = new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = true };
                using var writer = XmlWriter.Create(Path.Combine(dir, "in.xml"), settings);
                new XmlSerializer(typeof(SettingsFileData)).Serialize(writer, input);
            });

        /// <summary>A mode that reads a script's syntax tree and runs none of
        /// it, on the script named.</summary>
        private static DriverRun ReadTree(string mode, string scriptName = "capture_all.ps1")
        {
            var run = RunDriver(mode, "plain", _ => { }, scriptName);
            Assert.Empty(run.Strings("ParseErrors"));
            return run;
        }

        private static DriverRun RunDriver(string mode, string variant, Action<string> arrange, string scriptName = "capture_all.ps1")
        {
            string script = AuditDelta20260823Tests.FindRepoFile(Path.Combine("tools", scriptName));
            string dir = Path.Combine(Path.GetTempPath(), "PadForge.Tests", "CaptureHarness", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                string driver = Path.Combine(dir, "driver.ps1");
                File.WriteAllText(driver, Driver, new UTF8Encoding(true));
                var run = new DriverRun();
                arrange(dir);
                string inPath = Path.Combine(dir, "in.xml");
                if (File.Exists(inPath)) run.InXml = File.ReadAllText(inPath);

                var psi = new ProcessStartInfo(PowerShellExe(),
                    $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{driver}\" -Mode {mode} -ScriptPath \"{script}\" -WorkDir \"{dir}\" -Variant {variant}")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                using var p = Process.Start(psi);
                var text = new StringBuilder();
                p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (text) text.AppendLine(e.Data); };
                p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (text) text.AppendLine(e.Data); };
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                if (!p.WaitForExit(120_000))
                {
                    try { p.Kill(entireProcessTree: true); } catch { }
                    Assert.Fail("the PowerShell driver timed out");
                }
                p.WaitForExit();
                string log;
                lock (text) log = text.ToString();
                Assert.True(p.ExitCode == 0, $"the PowerShell driver exited {p.ExitCode}:\n{log}");

                string resultPath = Path.Combine(dir, "result.json");
                Assert.True(File.Exists(resultPath), "the driver wrote no result:\n" + log);
                using (var json = JsonDocument.Parse(File.ReadAllText(resultPath)))
                    run.Result = json.RootElement.Clone();
                string outPath = Path.Combine(dir, "out.xml");
                if (File.Exists(outPath)) run.OutXml = File.ReadAllText(outPath);
                return run;
            }
            finally
            {
                try { Directory.Delete(dir, recursive: true); } catch { }
            }
        }

        /// <summary>
        /// Windows PowerShell side. It parses the script it is given, and
        /// settings_guard.ps1 beside it, takes the statements a test needs
        /// from the syntax tree, and runs or measures them. Write-Host is
        /// replaced so the lines the script logs are collected for the test to
        /// read.
        ///
        /// <para>Nothing from the script runs before Get-Refused has read it.
        /// A statement may call the functions and script blocks it defines
        /// itself and the commands its mode lists, may build a generic
        /// collection and the objects its mode names in full and no other,
        /// and may call no static method. The capture stops and starts
        /// PadForge and copies the owner's settings file, and a test that ran
        /// any of that would do it to the PC the tests run on.</para>
        ///
        /// <para>Five modes run statements, each against files the test wrote
        /// in its own folder: inject, check, reused, same and markerpath. The
        /// reused-file block calls the guard's loader and saver, so those two
        /// run with it, and the copy the saver takes first goes to a stand-in
        /// that copies nothing. Same runs the comparison of two settings
        /// files, which writes nothing. Markerpath runs the function that
        /// names a backup's marker, which builds a path and touches no file.
        /// Every other mode (wiring, guard, focus, settings, block, shared,
        /// names, ending, encoding, snapshot, wrapper) reads the syntax tree
        /// and runs none of it, on capture_all.ps1 or on the script it is
        /// given. Names parses every script beside that one.</para>
        /// </summary>
        private const string Driver = """
using namespace System.Management.Automation.Language
param(
    [Parameter(Mandatory = $true)][string]$Mode,
    [Parameter(Mandatory = $true)][string]$ScriptPath,
    [Parameter(Mandatory = $true)][string]$WorkDir,
    [string]$Variant = 'plain'
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$script:hostLines = New-Object 'System.Collections.Generic.List[string]'
function Write-Host { $script:hostLines.Add([string]($args | Select-Object -First 1)) }

$text = [System.IO.File]::ReadAllText($ScriptPath)
$tokens = $null
$parseErrors = $null
$ast = [Parser]::ParseInput($text, [ref]$tokens, [ref]$parseErrors)
$result = [ordered]@{ ParseErrors = @($parseErrors | ForEach-Object { "line $($_.Extent.StartLineNumber): $($_.Message)" }) }

# The functions every script under tools dot-sources for the settings file,
# from the file beside the script. Parsed, never run as a whole.
$guardPath = Join-Path (Split-Path $ScriptPath) 'settings_guard.ps1'
$guardText = [System.IO.File]::ReadAllText($guardPath)
$guardTokens = $null
$guardErrors = $null
$guardAst = [Parser]::ParseInput($guardText, [ref]$guardTokens, [ref]$guardErrors)
$result.ParseErrors += @($guardErrors | ForEach-Object { "settings_guard.ps1 line $($_.Extent.StartLineNumber): $($_.Message)" })

# The script again, after a test changed its text.
function Read-Harness([string]$changed) {
    $t = $null
    $e = $null
    $tree = [Parser]::ParseInput($changed, [ref]$t, [ref]$e)
    if (@($e).Count -gt 0) { throw "the changed script does not parse: line $($e[0].Extent.StartLineNumber): $($e[0].Message)" }
    return $tree
}

function Test-Within($inner, $outer) {
    return ($null -ne $outer) -and ($inner.Extent.StartOffset -ge $outer.Extent.StartOffset) -and ($inner.Extent.EndOffset -le $outer.Extent.EndOffset)
}

function Get-EnclosingTry($node) {
    $p = $node.Parent
    while ($null -ne $p -and -not ($p -is [TryStatementAst])) { $p = $p.Parent }
    return $p
}

# The statement a node belongs to, among the statements of its block.
function Get-Statement($node) {
    $s = $node
    while ($null -ne $s.Parent -and -not (($s.Parent -is [StatementBlockAst]) -or ($s.Parent -is [NamedBlockAst]))) { $s = $s.Parent }
    return $s
}

function Get-FunctionDefinition($tree, [string]$name) {
    $fn = $tree.Find({ param($a) $a -is [FunctionDefinitionAst] -and $a.Name -eq $name }, $true)
    if ($null -eq $fn) { throw "the script has no $name" }
    return $fn
}

# The if ($tmplDev) statement that holds New-SyntheticDevice.
function Get-InjectionIf($tree) {
    $inject = (Get-FunctionDefinition $tree 'New-SyntheticDevice').Parent
    while ($null -ne $inject -and -not (($inject -is [IfStatementAst]) -and $inject.Clauses[0].Item1.Extent.Text -eq '$tmplDev')) { $inject = $inject.Parent }
    if ($null -eq $inject) { throw 'no if ($tmplDev) statement holds New-SyntheticDevice' }
    return $inject
}

# The injection is that statement with the assignments right before it, which
# find the device list and the row to clone. They are taken as statements, so
# the try, catch or finally around them is never part of what runs.
function Get-InjectionStatements($tree) {
    $inject = Get-InjectionIf $tree
    $siblings = $inject.Parent.Statements
    $at = $siblings.IndexOf($inject)
    $first = $at
    while ($first -gt 0 -and $siblings[$first - 1] -is [AssignmentStatementAst]) { $first-- }
    return ,@($siblings[$first..$at])
}

# The script's list of the synthetic rows' GUIDs: one plain assignment among
# the top-level statements, so it is set whether or not STEP 0 runs.
function Get-GuidList($tree) {
    $found = @($tree.EndBlock.Statements | Where-Object { $_ -is [AssignmentStatementAst] -and $_.Left.Extent.Text -eq '$script:SyntheticGuids' -and $_.Operator -eq [TokenKind]::Equals })
    if ($found.Count -ne 1) { throw "the script sets `$script:SyntheticGuids among its top-level statements $($found.Count) times, not once" }
    return $found[0]
}

# The block that prepares the settings file a tail or Direct run reuses.
function Get-ReusedFileBlock($tree) {
    $block = $tree.Find({ param($a) $a -is [IfStatementAst] -and $a.Clauses[0].Item1.Extent.Text -eq '-not $script:doSetup' -and $a.Extent.Text.Contains('Use2DControllerView') }, $true)
    if ($null -eq $block) { throw 'no reused-file block' }
    return $block
}

# The settings file, read from the syntax tree alone. Nothing below runs a
# statement of the script.

# The names the four scripts give the settings file and its backup. $XmlPath
# is the settings file as capture_all.ps1's five functions that write it are
# handed it, and as prep_xml_for_capture.ps1 and add_slots_via_ui.ps1 name it.
$settingsVars = @('PadForgeXml', 'XmlPath', 'pfXml', 'xmlBak', 'bak', 'pfBak', 'BakPath')
$backupVars = @('xmlBak', 'bak', 'pfBak', 'BakPath')
# The commands that stop PadForge, and the names a script gives its exe.
$killNames = @('Stop-Process', 'Kill-PadForge', 'taskkill')
$exePattern = '\$(PadForgeExe|exe|dst|ExePath)\b'

function Get-FunctionOf($node) {
    $p = $node.Parent
    while ($null -ne $p -and -not ($p -is [FunctionDefinitionAst])) { $p = $p.Parent }
    return $p
}

# The try whose finally puts the owner's settings back: the one no function
# holds whose finally copies a backup over the settings file. Null for a
# script that has none (prep_xml_for_capture.ps1, add_slots_via_ui.ps1).
function Get-CleanupTry($tree) {
    $found = @($tree.EndBlock.Statements | Where-Object {
        $_ -is [TryStatementAst] -and $null -ne $_.Finally -and
        @($_.Finally.FindAll({ param($a) $a -is [CommandAst] -and $a.Extent.Text -match '^Copy-Item \$(xmlBak|bak|pfBak|BakPath) \$(PadForgeXml|pfXml|XmlPath)\b' }, $true)).Count -gt 0 })
    if ($found.Count -gt 1) { throw "the script has $($found.Count) top-level try statements whose finally restores the settings file, not one" }
    if ($found.Count -eq 0) { return $null }
    return $found[0]
}

# The value a command gives one of the named parameters, or else its
# positional argument at $Position.
function Get-Argument($command, [string[]]$Named, [int]$Position) {
    $takesValue = @('Destination', 'LiteralPath', 'Path', 'FilePath', 'ErrorAction', 'EA', 'Filter', 'Encoding', 'Value', 'ForegroundColor')
    $elements = $command.CommandElements
    $positional = @()
    for ($i = 1; $i -lt $elements.Count; $i++) {
        $e = $elements[$i]
        if ($e -is [CommandParameterAst]) {
            $value = $e.Argument
            if ($null -eq $value -and ($takesValue -contains $e.ParameterName) -and ($i + 1 -lt $elements.Count)) { $i++; $value = $elements[$i] }
            if ($Named -contains $e.ParameterName) { return $value }
            continue
        }
        $positional += $e
    }
    if ($Position -lt $positional.Count) { return $positional[$Position] }
    return $null
}

function Get-SettingsVar($node) {
    if (($node -is [VariableExpressionAst]) -and ($settingsVars -contains $node.VariablePath.UserPath)) { return $node.VariablePath.UserPath }
    return ''
}

# Every statement that overwrites or deletes a settings file, with the name
# the script gives that file. Save-SettingsXml is one: the copy it takes
# first is read off its own body.
function Get-SettingsWrites($tree) {
    $writes = @()
    foreach ($c in $tree.FindAll({ param($a) $a -is [CommandAst] }, $true)) {
        $name = $c.GetCommandName()
        $targets = @()
        if ($name -eq 'Copy-Item') { $targets += Get-Argument $c @('Destination') 1 }
        elseif ($name -eq 'Move-Item') { $targets += Get-Argument $c @('Destination') 1; $targets += Get-Argument $c @('LiteralPath', 'Path') 0 }
        elseif ($name -eq 'Remove-Item') { $targets += Get-Argument $c @('LiteralPath', 'Path') 0 }
        elseif (@('Set-Content', 'Add-Content', 'Out-File') -contains $name) { $targets += Get-Argument $c @('LiteralPath', 'Path', 'FilePath') 0 }
        elseif ($name -eq 'Save-SettingsXml') { $targets += Get-Argument $c @('Path') 1 }
        foreach ($t in $targets) {
            $var = Get-SettingsVar $t
            if ($var) { $writes += [pscustomobject]@{ Node = $c; Name = $name; Var = $var } }
        }
    }
    foreach ($m in $tree.FindAll({ param($a) $a -is [InvokeMemberExpressionAst] -and $a.Member.Extent.Text -eq 'Save' -and $null -ne $a.Arguments -and $a.Arguments.Count -eq 1 }, $true)) {
        $var = Get-SettingsVar $m.Arguments[0]
        if ($var) { $writes += [pscustomobject]@{ Node = $m; Name = '.Save'; Var = $var } }
    }
    return ,$writes
}

# True when a Save-SettingsCopy of the same file runs before the write on
# every way to it: the call comes earlier in the same function or, outside
# any, in the script, nothing writes the file between the two, and every
# block that holds the copy and not the write is the body of a try, which
# runs whatever follows it.
function Test-CopiedFirst($write, $copies, $writes) {
    $w = $write.Node
    $before = @($copies | Where-Object {
        $_.Var -eq $write.Var -and $_.Node.Extent.EndOffset -le $w.Extent.StartOffset -and
        [object]::ReferenceEquals((Get-FunctionOf $_.Node), (Get-FunctionOf $w)) } | Sort-Object { $_.Node.Extent.StartOffset })
    if ($before.Count -eq 0) { return $false }
    $c = $before[-1].Node
    foreach ($other in $writes) {
        if ($other.Var -eq $write.Var -and $other.Node.Extent.StartOffset -gt $c.Extent.EndOffset -and $other.Node.Extent.EndOffset -lt $w.Extent.StartOffset) { return $false }
    }
    $p = $c.Parent
    while ($null -ne $p) {
        if (Test-Within $w $p) { return $true }
        if (($p -is [StatementBlockAst]) -and -not (($p.Parent -is [TryStatementAst]) -and [object]::ReferenceEquals($p.Parent.Body, $p))) { return $false }
        $p = $p.Parent
    }
    return $false
}

# The block a statement sits in, when that block is the body of
# if (-not (Test-Path <file>)) for the file the statement writes, alone or
# joined by -and to a further condition: there is then no file for it to
# overwrite.
function Test-WrittenWhereNoneIs($write) {
    $p = $write.Node.Parent
    while ($null -ne $p -and -not ($p -is [StatementBlockAst])) { $p = $p.Parent }
    if ($null -eq $p -or -not ($p.Parent -is [IfStatementAst])) { return $false }
    $if = $p.Parent
    if (-not [object]::ReferenceEquals($if.Clauses[0].Item2, $p)) { return $false }
    $condition = $if.Clauses[0].Item1.Extent.Text -replace '\s+', ' '
    $absent = '-not (Test-Path $' + $write.Var + ')'
    return ($condition -eq $absent) -or $condition.StartsWith($absent + ' -and ', [StringComparison]::Ordinal)
}

function Get-Line($node) { return "line $($node.Extent.StartLineNumber): $(($node.Extent.Text -split "`r?`n")[0])" }

# The statement that follows a statement in its block, or $null.
function Get-NextStatement($statement) {
    $siblings = $statement.Parent.Statements
    $at = $siblings.IndexOf($statement)
    if ($at + 1 -lt $siblings.Count) { return $siblings[$at + 1] }
    return $null
}

function Get-PreviousStatement($statement) {
    $siblings = $statement.Parent.Statements
    $at = $siblings.IndexOf($statement)
    if ($at -gt 0) { return $siblings[$at - 1] }
    return $null
}

# What the statements call beyond what they are known to need: a command that
# is neither in $Allowed nor a function the statements define, a New-Object of
# anything but a generic collection, an invocation of anything but a script
# block they assign, and any static method.
function Get-Refused($statements, [string[]]$Allowed) {
    $defined = @()
    $closures = @()
    foreach ($s in $statements) {
        $defined += @($s.FindAll({ param($a) $a -is [FunctionDefinitionAst] }, $true) | ForEach-Object { $_.Name })
        $closures += @($s.FindAll({ param($a) $a -is [AssignmentStatementAst] -and $a.Left -is [VariableExpressionAst] -and $a.Right.Extent.Text.Trim().StartsWith('{') -and $a.Right.Extent.Text.Trim().EndsWith('}') }, $true) |
            ForEach-Object { $_.Left.VariablePath.UserPath })
    }
    $refused = @()
    foreach ($s in $statements) {
        foreach ($c in $s.FindAll({ param($a) $a -is [CommandAst] }, $true)) {
            $name = $c.GetCommandName()
            if ($null -eq $name) {
                $first = $c.CommandElements[0]
                if ($c.InvocationOperator -eq [TokenKind]::Ampersand -and $first -is [VariableExpressionAst] -and ($closures -contains $first.VariablePath.UserPath)) { continue }
                $name = $c.Extent.Text
            }
            elseif ($name -eq 'New-Object' -and ($Allowed -contains $name)) {
                $type = if ($c.CommandElements.Count -gt 1) { $c.CommandElements[1].Extent.Text.Trim("'") } else { '' }
                if ($type.StartsWith('System.Collections.Generic.')) { continue }
                # One object a mode names in full, and no other.
                if ($Allowed -contains "New-Object $type") { continue }
                $name = "New-Object $type"
            }
            elseif (($Allowed -contains $name) -or ($defined -contains $name)) { continue }
            $refused += "line $($c.Extent.StartLineNumber): $name"
        }
        foreach ($m in $s.FindAll({ param($a) $a -is [InvokeMemberExpressionAst] -and $a.Static }, $true)) {
            $refused += "line $($m.Extent.StartLineNumber): $($m.Extent.Text)"
        }
    }
    $refused
}

$injectionNeeds = @('Write-Host', 'New-Object', 'Out-Null')
$checkNeeds = @('Write-Host', 'New-Object')
$listNeeds = @()
$reusedFileNeeds = @('Write-Host', 'Out-Null', 'Assert-SyntheticRowsClean', 'Read-SettingsXml', 'Save-SettingsXml')
# The guard's loader and saver, which the reused-file block calls. Each may
# build the objects named here and no other. The saver's call to
# Save-SettingsCopy reaches a stand-in the driver defines, which copies
# nothing: the real one copies a file into %TEMP%\PadForge_Capture and
# deletes older copies there, and no test may do that to the PC it runs on.
$loaderNeeds = @('New-Object', 'New-Object System.Xml.XmlDocument', 'New-Object System.IO.FileStream')
$saverNeeds = @('New-Object', 'New-Object System.Xml.XmlTextWriter', 'New-Object System.Text.UTF8Encoding', 'Save-SettingsCopy')
# The guard's comparison of two settings files, which reads them and writes
# nothing.
$sameNeeds = @('Test-Path', 'Get-Item', 'ForEach-Object', 'New-Object', 'New-Object System.IO.FileStream', 'New-Object System.Security.Cryptography.SHA256CryptoServiceProvider')
# Statements no block needs and that change nothing, one for each kind of
# call Get-Refused turns away, for the tests of the refusal itself.
$canaries = @{
    'canary' = ' Get-Date | Out-Null; '
    'canary-object' = ' New-Object System.Text.StringBuilder | Out-Null; '
    'canary-static' = ' [void][System.IO.Path]::GetTempPath(); '
}
# The check and the list run in more than one mode, so a canary for either
# goes in before the mode is chosen.
if ($Variant -eq 'canary-check') {
    $text = $text.Insert((Get-FunctionDefinition $ast 'Assert-SyntheticRowsClean').Body.ParamBlock.Extent.EndOffset, $canaries['canary'])
    $ast = Read-Harness $text
}
elseif ($Variant -eq 'canary-list') {
    $right = (Get-GuidList $ast).Right.Extent
    if (-not $right.Text.StartsWith('@(')) { throw 'the list of synthetic GUIDs is not an array expression' }
    $text = $text.Insert($right.StartOffset + 2, $canaries['canary'])
    $ast = Read-Harness $text
}

if ($Mode -eq 'inject') {
    if ($Variant -eq 'unwrapped') {
        $own = Get-EnclosingTry (Get-FunctionDefinition $ast 'New-SyntheticDevice')
        if ($null -ne $own -and $null -eq $own.Finally) {
            $body = $own.Body.Extent
            $text = $text.Substring(0, $own.Extent.StartOffset) + $text.Substring($body.StartOffset + 1, $body.EndOffset - $body.StartOffset - 2) + $text.Substring($own.Extent.EndOffset)
            $ast = Read-Harness $text
        }
        $outer = Get-EnclosingTry (Get-FunctionDefinition $ast 'New-SyntheticDevice')
        $result.EnclosingTryHasFinally = ($null -ne $outer) -and ($null -ne $outer.Finally)
        $result.EnclosingTryRefused = @()
        if ($null -ne $outer) {
            $result.EnclosingTryRefused = @((Get-Refused @($outer) $injectionNeeds) | ForEach-Object { ($_ -split ': ', 2)[1] } | Sort-Object -Unique)
        }
    }
    elseif ($canaries.ContainsKey($Variant)) {
        $text = $text.Insert((Get-InjectionIf $ast).Clauses[0].Item2.Extent.StartOffset + 1, $canaries[$Variant])
        $ast = Read-Harness $text
    }
    $statements = Get-InjectionStatements $ast
    $check = Get-FunctionDefinition $ast 'Assert-SyntheticRowsClean'
    $list = Get-GuidList $ast
    $result.Refused = @(Get-Refused $statements $injectionNeeds) + @(Get-Refused @($check) $checkNeeds) + @(Get-Refused @($list) $listNeeds)
    $result.Ran = $false
    if ($result.Refused.Count -eq 0) {
        $xml = New-Object System.Xml.XmlDocument
        $xml.Load((Join-Path $WorkDir 'in.xml'))
        $ns = $xml.DocumentElement
        $script:StateFailures = @()
        $script:StagingFailed = $false
        . ([scriptblock]::Create($list.Extent.Text))
        $result.Listed = @($script:SyntheticGuids)
        # The injection as it runs when the list names none of its rows.
        if ($Variant -eq 'unlisted') { $script:SyntheticGuids = @() }
        $result.ListedAtStart = @($script:SyntheticGuids).Count
        . ([scriptblock]::Create((@($statements | ForEach-Object { $_.Extent.Text }) -join "`r`n")))
        . ([scriptblock]::Create($check.Extent.Text))
        $result.Clean = [bool](Assert-SyntheticRowsClean ($ns.SelectSingleNode('Devices')) $script:SyntheticGuids)
        $result.SyntheticGuids = @($script:SyntheticGuids)
        $result.StateFailures = @($script:StateFailures)
        $result.StagingFailed = [bool]$script:StagingFailed
        $xml.Save((Join-Path $WorkDir 'out.xml'))
        $result.Ran = $true
    }
}
elseif ($Mode -eq 'check') {
    $check = Get-FunctionDefinition $ast 'Assert-SyntheticRowsClean'
    $result.Refused = @(Get-Refused @($check) $checkNeeds)
    $cases = @()
    if ($result.Refused.Count -eq 0) {
        . ([scriptblock]::Create($check.Extent.Text))
        foreach ($file in @(Get-ChildItem -Path $WorkDir -Filter 'case-*.xml' | Sort-Object Name)) {
            $script:hostLines.Clear()
            $script:StateFailures = @()
            $script:StagingFailed = $false
            $doc = New-Object System.Xml.XmlDocument
            $doc.Load($file.FullName)
            $guids = New-Object 'System.Collections.Generic.List[string]'
            foreach ($line in [System.IO.File]::ReadAllLines([System.IO.Path]::ChangeExtension($file.FullName, '.guids'))) {
                if ($line) { $guids.Add($line) }
            }
            $clean = [bool](Assert-SyntheticRowsClean ($doc.DocumentElement.SelectSingleNode('Devices')) $guids)
            $cases += [ordered]@{ Name = $file.BaseName; Clean = $clean; StateFailures = @($script:StateFailures); StagingFailed = [bool]$script:StagingFailed; Lines = @($script:hostLines) }
        }
    }
    $result.Cases = $cases
}
elseif ($Mode -eq 'wiring') {
    $saves = @($ast.FindAll({ param($a) $a -is [CommandAst] -and $a.Extent.Text -eq 'Save-SettingsXml $xml $PadForgeXml' }, $true))
    $calls = @($ast.FindAll({ param($a) $a -is [CommandAst] -and $a.GetCommandName() -eq 'Assert-SyntheticRowsClean' }, $true))
    $reused = Get-ReusedFileBlock $ast
    $result.Saves = $saves.Count
    $result.Calls = $calls.Count
    $result.CallFollowsTheSave = $false
    $result.Between = @()
    $result.SavedCallText = ''
    $result.ReusedCallText = ''
    $result.StartLine = 0
    $result.PadForgeStartsAfterBoth = $false
    if ($saves.Count -eq 1) {
        $saved = Get-Statement $saves[0]
        $placed = @()
        foreach ($call in $calls) {
            $checked = Get-Statement $call
            if ([object]::ReferenceEquals($saved.Parent, $checked.Parent) -and $checked.Extent.StartOffset -gt $saved.Extent.EndOffset) {
                $result.CallFollowsTheSave = $true
                $result.SavedCallText = $call.Extent.Text
                $between = @($saved.Parent.Statements | Where-Object { $_.Extent.StartOffset -gt $saved.Extent.EndOffset -and $_.Extent.EndOffset -lt $checked.Extent.StartOffset })
                $result.Between = @($between | ForEach-Object { $_.FindAll({ param($a) $a -is [CommandAst] }, $true) } | ForEach-Object { $_.GetCommandName() })
                $placed += $call
            }
            elseif (Test-Within $call $reused) {
                $result.ReusedCallText = $call.Extent.Text
                $placed += $call
            }
        }
        # STEP 1's Start-Process: the first one on the app after the save
        # that no function holds.
        $start = $ast.FindAll({ param($a) $a -is [CommandAst] -and $a.GetCommandName() -eq 'Start-Process' -and $a.Extent.Text -match '\$PadForgeExe\b' }, $true) |
            Where-Object {
                $p = $_.Parent
                while ($null -ne $p -and -not ($p -is [FunctionDefinitionAst])) { $p = $p.Parent }
                ($null -eq $p) -and ($_.Extent.StartOffset -gt $saved.Extent.EndOffset)
            } | Sort-Object { $_.Extent.StartOffset } | Select-Object -First 1
        if ($null -ne $start) {
            $result.StartLine = $start.Extent.StartLineNumber
            $result.PadForgeStartsAfterBoth = ($placed.Count -eq 2) -and (@($placed | Where-Object { $_.Extent.EndOffset -ge $start.Extent.StartOffset }).Count -eq 0)
        }
    }
}
elseif ($Mode -eq 'guard') {
    $cleanup = $ast.Find({ param($a) $a -is [TryStatementAst] -and $null -ne $a.Finally -and $a.Finally.Extent.Text.Contains('STEP 4: Cleanup') }, $true)
    if ($null -eq $cleanup) { throw 'no try statement whose finally runs STEP 4' }
    $writes = $ast.FindAll({
        param($a)
        if ($a -is [CommandAst]) {
            return (@('Copy-Item', 'Remove-Item', 'Move-Item', 'Set-Content', 'Out-File', 'Save-SettingsXml') -contains $a.GetCommandName()) -and ($a.Extent.Text -match '\$(PadForgeXml|xmlBak)\b')
        }
        if ($a -is [InvokeMemberExpressionAst]) {
            return ($a.Member.Extent.Text -eq 'Save') -and ($a.Extent.Text -match '\$PadForgeXml\b')
        }
        return $false
    }, $true)
    $found = 0
    $outside = @()
    foreach ($w in $writes) {
        $p = $w.Parent
        $inFunction = $false
        while ($null -ne $p) {
            if ($p -is [FunctionDefinitionAst]) { $inFunction = $true; break }
            $p = $p.Parent
        }
        if ($inFunction) { continue }
        $found++
        if (-not ((Test-Within $w $cleanup.Body) -or (Test-Within $w $cleanup.Finally))) {
            $outside += "line $($w.Extent.StartLineNumber): $($w.Extent.Text)"
        }
    }
    $result.WritesFound = $found
    $result.WritesOutsideRestore = $outside
    $copies = @($cleanup.Finally.FindAll({ param($a) $a -is [CommandAst] -and $a.GetCommandName() -eq 'Copy-Item' -and $a.Extent.Text -match '\$xmlBak\b' }, $true))
    # The backup is there, and the run was given a marker's word or the
    # owner's that it holds the settings to put back.
    $guards = @($cleanup.Finally.FindAll({ param($a) $a -is [IfStatementAst] -and $a.Clauses[0].Item1.Extent.Text -match '^\(Test-Path\s+\$xmlBak\)\s+-and\s+\$script:BackupHoldsSettings$' }, $true))
    $guarded = 0
    foreach ($c in $copies) {
        foreach ($g in $guards) { if (Test-Within $c $g.Clauses[0].Item2) { $guarded++; break } }
    }
    $result.FinallyRestoreCopies = $copies.Count
    $result.GuardedRestoreCopies = $guarded
    $assignments = @($ast.FindAll({ param($a) $a -is [AssignmentStatementAst] -and $a.Left.Extent.Text -eq '$xmlBak' }, $true) |
        Where-Object { $_.Extent.EndOffset -le $cleanup.Extent.StartOffset })
    $result.BackupPathSetBeforeTry = $assignments.Count -gt 0
}
elseif ($Mode -eq 'focus') {
    # The pass an -Only run takes its shots in, and the passes behind it.
    $focused = $ast.Find({ param($a) $a -is [IfStatementAst] -and $a.Clauses[0].Item1.Extent.Text -eq '$Only.Count -gt 0' -and $a.Clauses[0].Item2.Extent.Text.Contains('=== FOCUSED PASS') }, $true)
    if ($null -eq $focused) { throw 'no focused pass' }
    $inPass = $focused.Clauses[0].Item2.Statements
    # The pass ends an -Only run by a flag and never by leaving the script:
    # its last statement sets the flag, the statement right behind it runs
    # only while the flag is clear, and that statement holds everything else
    # the try runs.
    $cleanup = Get-CleanupTry $ast
    $last = if ($inPass.Count -gt 0) { $inPass[$inPass.Count - 1] } else { $null }
    $result.FocusedPassSetsTheFlag = ($focused.Clauses.Count -eq 1) -and ($null -eq $focused.ElseClause) -and ($last -is [AssignmentStatementAst]) -and
        ($last.Left.Extent.Text -eq '$script:FocusedPassRan') -and ($last.Right.Extent.Text -eq '$true')
    $result.FocusedPassIsInTheCleanupTry = [object]::ReferenceEquals($focused.Parent, $cleanup.Body)
    $siblings = $focused.Parent.Statements
    $at = $siblings.IndexOf($focused)
    $behind = if ($at + 1 -lt $siblings.Count) { $siblings[$at + 1] } else { $null }
    $result.PassesRunWhileTheFlagIsClear = ($behind -is [IfStatementAst]) -and ($behind.Clauses.Count -eq 1) -and ($null -eq $behind.ElseClause) -and
        ($behind.Clauses[0].Item1.Extent.Text -eq '-not $script:FocusedPassRan')
    $result.NothingElseFollowsInTheTry = ($at + 2 -eq $siblings.Count)
    $perPage = $ast.Find({ param($a) $a -is [IfStatementAst] -and $a.Clauses[0].Item1.Extent.Text -eq '-not $SkipToTail' -and $a.Extent.StartOffset -gt $focused.Extent.EndOffset }, $true)
    if ($null -eq $perPage) { throw 'no per-page block behind the focused pass' }
    $result.PassesSitBehindTheFlag = ($null -ne $behind) -and (Test-Within $perPage $behind)
    $result.FlagClearedBeforeTheTry = @($ast.EndBlock.Statements | Where-Object {
        $_ -is [AssignmentStatementAst] -and $_.Left.Extent.Text -eq '$script:FocusedPassRan' -and $_.Right.Extent.Text -eq '$false' -and $_.Extent.EndOffset -le $cleanup.Extent.StartOffset }).Count
    # A return at the top level of the try leaves the script with the finally
    # run and the lines below it skipped. One inside a function or a script
    # block returns from that alone.
    $isTopLevel = {
        param($node)
        $p = $node.Parent
        while ($null -ne $p -and -not [object]::ReferenceEquals($p, $cleanup)) {
            if (($p -is [FunctionDefinitionAst]) -or ($p -is [ScriptBlockExpressionAst])) { return $false }
            $p = $p.Parent
        }
        return $true
    }
    $returns = @($cleanup.Body.FindAll({ param($a) $a -is [ReturnStatementAst] }, $true))
    $result.ReturnsInTheTry = $returns.Count
    $result.TopLevelReturnsInTheTry = @($returns | Where-Object { & $isTopLevel $_ } | ForEach-Object { "line $($_.Extent.StartLineNumber): $($_.Extent.Text)" })
    $result.ClosingLines = @($ast.EndBlock.Statements | Where-Object { $_.Extent.StartOffset -gt $cleanup.Extent.EndOffset } | ForEach-Object { $_.Extent.Text })
    $strings = { param($node) @($node.FindAll({ param($a) $a -is [StringConstantExpressionAst] }, $true) | ForEach-Object { $_.Value }) }
    $result.FocusedStrings = @(& $strings $focused | Sort-Object -Unique)
    $result.FunctionStrings = @($ast.FindAll({ param($a) $a -is [FunctionDefinitionAst] }, $true) | ForEach-Object { & $strings $_ } | Sort-Object -Unique)
    # The shots a run takes only past the focused pass: Cap calls behind
    # it that no function holds.
    $later = @($ast.FindAll({ param($a) $a -is [CommandAst] -and $a.GetCommandName() -eq 'Cap' -and $a.CommandElements.Count -gt 1 -and $a.CommandElements[1] -is [StringConstantExpressionAst] }, $true) |
        Where-Object {
            $p = $_.Parent
            while ($null -ne $p -and -not ($p -is [FunctionDefinitionAst])) { $p = $p.Parent }
            ($null -eq $p) -and ($_.Extent.StartOffset -gt $focused.Extent.EndOffset)
        })
    $result.PerPageShots = @($later | Where-Object { Test-Within $_ $perPage } | ForEach-Object { $_.CommandElements[1].Value } | Sort-Object -Unique)
    $result.TailShots = @($later | Where-Object { $_.Extent.StartOffset -gt $perPage.Extent.EndOffset } | ForEach-Object { $_.CommandElements[1].Value } | Sort-Object -Unique)
    # A parameter's help is the comments above it.
    $parameters = $ast.ParamBlock.Parameters
    $help = @{}
    for ($i = 0; $i -lt $parameters.Count; $i++) {
        $from = if ($i -eq 0) { $ast.ParamBlock.Extent.StartOffset } else { $parameters[$i - 1].Extent.EndOffset }
        $to = $parameters[$i].Extent.StartOffset
        $lines = @($tokens | Where-Object { $_.Kind -eq [TokenKind]::Comment -and $_.Extent.StartOffset -ge $from -and $_.Extent.EndOffset -le $to } |
            ForEach-Object { $_.Text.TrimStart('#').Trim() })
        $help[$parameters[$i].Name.VariablePath.UserPath] = ($lines -join ' ')
    }
    $result.OnlyHelp = [string]$help['Only']
    $result.SkipToTailHelp = [string]$help['SkipToTail']
}
elseif ($Mode -eq 'reused') {
    $tail = Get-ReusedFileBlock $ast
    if ($canaries.ContainsKey($Variant)) {
        $text = $text.Insert($tail.Clauses[0].Item2.Extent.StartOffset + 1, $canaries[$Variant])
        $ast = Read-Harness $text
        $tail = Get-ReusedFileBlock $ast
    }
    $check = Get-FunctionDefinition $ast 'Assert-SyntheticRowsClean'
    $list = Get-GuidList $ast
    $loader = Get-FunctionDefinition $guardAst 'Read-SettingsXml'
    $saver = Get-FunctionDefinition $guardAst 'Save-SettingsXml'
    $result.Refused = @(Get-Refused @($tail) $reusedFileNeeds) + @(Get-Refused @($check) $checkNeeds) + @(Get-Refused @($list) $listNeeds) +
        @(Get-Refused @($loader) $loaderNeeds) + @(Get-Refused @($saver) $saverNeeds)
    $cases = @()
    if ($result.Refused.Count -eq 0) {
        . ([scriptblock]::Create($check.Extent.Text))
        # The stand-in for the copy the saver takes first. It notes the path
        # it was asked for and copies nothing.
        $script:copyAsked = New-Object 'System.Collections.Generic.List[string]'
        function Save-SettingsCopy { param([string]$Path) $script:copyAsked.Add($Path); return 'a copy this test did not take' }
        . ([scriptblock]::Create($loader.Extent.Text))
        . ([scriptblock]::Create($saver.Extent.Text))
        # One file for each case, named reused-direct-* or reused-tail-* for
        # the run that reuses it.
        foreach ($file in @(Get-ChildItem -Path $WorkDir -Filter 'reused-*.xml' | Sort-Object Name)) {
            $script:hostLines.Clear()
            $script:copyAsked.Clear()
            . ([scriptblock]::Create($list.Extent.Text))
            $PadForgeXml = $file.FullName
            $script:doSetup = $false
            $script:Direct = $file.BaseName.StartsWith('reused-direct-')
            $script:Start2D = $false
            $script:ViewUnknown = $false
            $script:StateFailures = @()
            $script:StagingFailed = $false
            . ([scriptblock]::Create($tail.Extent.Text))
            # The file as the block left it, read by its own declaration.
            $bytes = [System.IO.File]::ReadAllBytes($file.FullName)
            $after = New-Object System.Xml.XmlDocument
            $after.Load($file.FullName)
            $names = @($after.DocumentElement.SelectNodes('Devices/Device/ProductName') | ForEach-Object { (@($_.InnerText.ToCharArray() | ForEach-Object { '{0:X4}' -f [int]$_ }) -join ' ') })
            $cases += [ordered]@{
                Name = $file.BaseName; Direct = [bool]$script:Direct; Lines = @($script:hostLines); ViewUnknown = [bool]$script:ViewUnknown
                StateFailures = @($script:StateFailures); StagingFailed = [bool]$script:StagingFailed
                CopyAsked = @($script:copyAsked); ByteOrderMark = ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF)
                NamesHex = $names
            }
        }
    }
    $result.Cases = $cases
}
elseif ($Mode -eq 'settings') {
    # Where a script writes the settings file and its backup, and what
    # stands around each write. Read off the tree: nothing here runs.
    $cleanup = Get-CleanupTry $ast
    $cleanupBody = if ($null -ne $cleanup) { $cleanup.Body } else { $null }
    $cleanupFinally = if ($null -ne $cleanup) { $cleanup.Finally } else { $null }
    $writes = Get-SettingsWrites $ast
    $copies = @($ast.FindAll({ param($a) $a -is [CommandAst] -and $a.GetCommandName() -eq 'Save-SettingsCopy' }, $true) | ForEach-Object {
        $v = Get-SettingsVar (Get-Argument $_ @('Path') 0)
        if ($v) { [pscustomobject]@{ Node = $_; Var = $v } } })
    $result.Writes = @($writes | ForEach-Object { Get-Line $_.Node })
    # Each write as its command and the file it overwrites or deletes.
    $result.WriteKinds = @($writes | ForEach-Object { $_.Name + ' $' + $_.Var })
    $result.Copies = $copies.Count
    $result.Uncovered = @($writes | Where-Object { $_.Name -ne 'Save-SettingsXml' -and -not (Test-CopiedFirst $_ $copies $writes) -and -not (Test-WrittenWhereNoneIs $_) } | ForEach-Object { Get-Line $_.Node })
    # A call whose value nothing takes. The path it returns would join the
    # result of the function it sits in and hide a false behind it.
    $result.BareCalls = @($ast.FindAll({ param($a) $a -is [CommandAst] -and (@('Save-SettingsCopy', 'Save-SettingsXml') -contains $a.GetCommandName()) }, $true) | Where-Object {
        $pipe = $_.Parent
        ($pipe -is [PipelineAst]) -and ($pipe.PipelineElements.Count -eq 1) -and (($pipe.Parent -is [StatementBlockAst]) -or ($pipe.Parent -is [NamedBlockAst]))
    } | ForEach-Object { Get-Line $_ })

    # The backup: written only where none is, and marked right there.
    $backups = @($writes | Where-Object { $_.Name -eq 'Copy-Item' -and ($backupVars -contains $_.Var) })
    $result.Backups = @($backups | ForEach-Object { Get-Line $_.Node })
    # The file each backup is taken from.
    $result.BackupSources = @($backups | ForEach-Object { '$' + (Get-SettingsVar (Get-Argument $_.Node @('LiteralPath', 'Path') 0)) })
    $result.BackupsOverAFile = @($backups | Where-Object { -not (Test-WrittenWhereNoneIs $_) } | ForEach-Object { Get-Line $_.Node })
    $result.MarkersSet = @($ast.FindAll({ param($a) $a -is [CommandAst] -and $a.GetCommandName() -eq 'Set-BackupMarker' }, $true)).Count
    $notMarked = @()
    $before = @()
    foreach ($b in $backups) {
        $if = (Get-Statement $b.Node).Parent.Parent
        $next = if ($if -is [IfStatementAst]) { Get-NextStatement $if } else { $null }
        if (($null -eq $next) -or ($next.Extent.Text -ne ('Set-BackupMarker $' + $b.Var))) { $notMarked += Get-Line $b.Node }
        $prev = if ($if -is [IfStatementAst]) { Get-PreviousStatement $if } else { $null }
        $before += if ($prev -is [IfStatementAst]) { ($prev.Clauses[0].Item1.Extent.Text -replace '\s+', ' ') } else { '' }
    }
    $result.BackupsNotMarked = $notMarked
    $result.BeforeEachBackup = $before

    # Every copy of a backup over the settings file.
    $restores = @($writes | Where-Object { $_.Name -eq 'Copy-Item' -and ($backupVars -notcontains $_.Var) -and ($backupVars -contains (Get-SettingsVar (Get-Argument $_.Node @('LiteralPath', 'Path') 0))) })
    $result.Restores = @($restores | ForEach-Object { Get-Line $_.Node })
    # The restore that puts the owner's settings back for good, in one order:
    # PadForge stopped first, the marker gone as soon as the settings are
    # back, the backup after it, PadForge started last. It is the one in the
    # finally of the cleanup try. A script with no such try has it where no
    # function holds it and the marker's removal follows it. A restore of a
    # backup an earlier run left, which the run backs up again and marks, is
    # not one.
    if ($null -ne $cleanup) {
        $final = @($restores | Where-Object { Test-Within $_.Node $cleanupFinally })
        $scope = $cleanupFinally
    } else {
        $final = @($restores | Where-Object {
            $next = Get-NextStatement (Get-Statement $_.Node)
            ($null -eq (Get-FunctionOf $_.Node)) -and ($null -ne $next) -and
                ($next.Extent.Text -eq ('Remove-BackupMarker $' + (Get-SettingsVar (Get-Argument $_.Node @('LiteralPath', 'Path') 0)))) })
        $scope = $ast.EndBlock
    }
    $result.FinalRestores = $final.Count
    $result.StopComesBeforeTheRestore = $false
    $result.StartComesAfterTheRestore = $false
    $result.MarkerGoesRightAfterTheRestore = $false
    $result.BackupGoesAfterTheMarker = $false
    $result.RestoreConditions = @()
    if ($final.Count -eq 1) {
        $r = $final[0].Node
        $bakVar = Get-SettingsVar (Get-Argument $r @('LiteralPath', 'Path') 0)
        $restoreBlock = (Get-Statement $r).Parent
        # A start in a block that ends the script never runs ahead of a
        # statement below it.
        $reachesOn = {
            param($node)
            $block = $node.Parent
            while ($null -ne $block -and -not (($block -is [StatementBlockAst]) -or ($block -is [NamedBlockAst]))) { $block = $block.Parent }
            @($block.Statements | Where-Object { $_ -is [ExitStatementAst] }).Count -eq 0
        }
        $stops = @($scope.FindAll({ param($a) $a -is [CommandAst] -and ($killNames -contains $a.GetCommandName()) }, $true) | Where-Object { $null -eq (Get-FunctionOf $_) })
        $starts = @($scope.FindAll({ param($a) $a -is [CommandAst] -and $a.GetCommandName() -eq 'Start-Process' -and $a.Extent.Text -match $exePattern }, $true) |
            Where-Object { ($null -eq (Get-FunctionOf $_)) -and (& $reachesOn $_) })
        $unmarks = @($restoreBlock.FindAll({ param($a) $a -is [CommandAst] -and $a.GetCommandName() -eq 'Remove-BackupMarker' }, $true))
        $removes = @($writes | Where-Object { $_.Name -eq 'Remove-Item' -and $_.Var -eq $bakVar -and (Test-Within $_.Node $restoreBlock) })
        $result.StopComesBeforeTheRestore = (@($stops | Where-Object { $_.Extent.EndOffset -le $r.Extent.StartOffset }).Count -gt 0)
        $result.StartComesAfterTheRestore = ($starts.Count -gt 0) -and (@($starts | Where-Object { $_.Extent.StartOffset -lt $r.Extent.EndOffset }).Count -eq 0)
        $after = Get-NextStatement (Get-Statement $r)
        $result.MarkerGoesRightAfterTheRestore = ($null -ne $after) -and ($after.Extent.Text -eq ('Remove-BackupMarker $' + $bakVar))
        $result.BackupGoesAfterTheMarker = ($removes.Count -eq 1) -and ($unmarks.Count -eq 1) -and ($removes[0].Node.Extent.StartOffset -gt $unmarks[0].Extent.EndOffset)
        $conditions = @()
        $p = $r.Parent
        while ($null -ne $p -and -not [object]::ReferenceEquals($p, $scope)) {
            if ($p -is [IfStatementAst]) { $conditions += ($p.Clauses[0].Item1.Extent.Text -replace '\s+', ' ') }
            $p = $p.Parent
        }
        $result.RestoreConditions = $conditions
    }

    # Every exit no function holds, with what its block changes and what the
    # block around it has changed by then. A run that stops for the owner's
    # decision may have changed nothing: it may not have stopped or started
    # PadForge, written the registry or written a settings file.
    $changeNames = @($killNames) + @('Start-Process', 'Set-ItemProperty', 'Remove-ItemProperty')
    $changes = @($ast.FindAll({ param($a) $a -is [CommandAst] -and ($changeNames -contains $a.GetCommandName()) }, $true)) + @($writes | ForEach-Object { $_.Node })
    $changes = @($changes | Where-Object { $null -eq (Get-FunctionOf $_) } | Sort-Object { $_.Extent.StartOffset })
    $result.TopLevelChanges = @($changes | ForEach-Object { Get-Line $_ })
    # The commands that stop PadForge, no function holding them.
    $kills = @($ast.FindAll({ param($a) $a -is [CommandAst] -and ($killNames -contains $a.GetCommandName()) }, $true) | Where-Object { $null -eq (Get-FunctionOf $_) })
    $exits = @($ast.FindAll({ param($a) $a -is [ExitStatementAst] }, $true) | Where-Object { $null -eq (Get-FunctionOf $_) })
    $result.Exits = @($exits | ForEach-Object {
        $exit = $_
        $if = $exit.Parent
        while ($null -ne $if -and -not ($if -is [IfStatementAst])) { $if = $if.Parent }
        $entry = [ordered]@{
            Line = $exit.Extent.StartLineNumber; Code = $(if ($null -ne $exit.Pipeline) { $exit.Pipeline.Extent.Text } else { '' })
            InTheTry = [bool](Test-Within $exit $cleanupBody); Condition = ''; ChangesInBlock = @(); ChangesBefore = @(); FirstInOuterBlock = ''; OuterCondition = ''
            KillBefore = $false
        }
        if ($null -ne $if) {
            # Whether PadForge was stopped before the question: before the
            # if (Test-Path ...) that holds the stop, in the block around
            # that if, or before the stop itself where nothing holds it.
            $asked = if ($if.Parent.Parent -is [IfStatementAst]) { $if.Parent.Parent } else { $if }
            $entry.KillBefore = (@($kills | Where-Object { (Test-Within $_ $asked.Parent) -and $_.Extent.EndOffset -le $asked.Extent.StartOffset }).Count -gt 0)
            $clause = @($if.Clauses | Where-Object { Test-Within $exit $_.Item2 })
            $body = if ($clause.Count -eq 1) { $clause[0].Item2 } else { $if.ElseClause }
            $entry.Condition = if ($clause.Count -eq 1) { ($clause[0].Item1.Extent.Text -replace '\s+', ' ') } else { 'else' }
            $entry.ChangesInBlock = @($changes | Where-Object { Test-Within $_ $body } | ForEach-Object { Get-Line $_ })
            $outer = $if.Parent
            $entry.ChangesBefore = @($changes | Where-Object { (Test-Within $_ $outer) -and $_.Extent.EndOffset -le $if.Extent.StartOffset } | ForEach-Object { Get-Line $_ })
            $entry.FirstInOuterBlock = ($outer.Statements[0].Extent.Text -split "`r?`n")[0]
            if ($outer.Parent -is [IfStatementAst]) { $entry.OuterCondition = ($outer.Parent.Clauses[0].Item1.Extent.Text -replace '\s+', ' ') }
        }
        $entry
    })
    # What fills the lines the stop prints, and what is asked of the marker.
    $result.StopLinesFilledUnder = @($ast.FindAll({ param($a) $a -is [AssignmentStatementAst] -and $a.Right.Extent.Text.Contains('Get-BackupStopLines') }, $true) | Where-Object { $null -eq (Get-FunctionOf $_) } | ForEach-Object {
        $if = $_.Parent
        while ($null -ne $if -and -not ($if -is [IfStatementAst])) { $if = $if.Parent }
        $_.Left.Extent.Text + ' under ' + $(if ($null -ne $if) { ($if.Clauses[0].Item1.Extent.Text -replace '\s+', ' ') } else { 'nothing' })
    })
    $result.DoubtAssignments = @($ast.FindAll({ param($a) $a -is [AssignmentStatementAst] -and $a.Right.Extent.Text.StartsWith('Get-BackupDoubt ') }, $true) | Where-Object { $null -eq (Get-FunctionOf $_) } | ForEach-Object { $_.Extent.Text })
    $reuse = $ast.Find({ param($a) $a -is [IfStatementAst] -and $a.Clauses[0].Item2.Extent.Text.Contains('Tail mode: reusing the staged xml') -and -not $a.Clauses[0].Item1.Extent.Text.Contains('SkipToTail') }, $true)
    $result.TailReuseCondition = if ($null -ne $reuse) { ($reuse.Clauses[0].Item1.Extent.Text -replace '\s+', ' ') } else { '' }
    $result.SetBeforeTheTry = @(if ($null -ne $cleanup) { $ast.EndBlock.Statements | Where-Object { $_ -is [AssignmentStatementAst] -and $_.Extent.EndOffset -le $cleanup.Extent.StartOffset } | ForEach-Object { $_.Left.Extent.Text } | Sort-Object -Unique })

    # The single-instance lock of capture_all.ps1: what refuses a second run,
    # and what a run does about a transcript an earlier one left running.
    $refusal = $ast.Find({ param($a) $a -is [IfStatementAst] -and $a.Clauses[0].Item2.Extent.Text.Contains('ALREADY RUNNING') -and -not $a.Clauses[0].Item2.Extent.Text.Contains('$otherPid = ') }, $true)
    $result.SecondRunRefusedUnder = if ($null -ne $refusal) { ($refusal.Clauses[0].Item1.Extent.Text -replace '\s+', ' ') } else { '' }
    $result.EndedHereAssignments = @($ast.FindAll({ param($a) $a -is [AssignmentStatementAst] -and $a.Left.Extent.Text -eq '$endedHere' }, $true) | ForEach-Object { $_.Extent.Text })
    $startTranscript = $ast.Find({ param($a) $a -is [CommandAst] -and $a.GetCommandName() -eq 'Start-Transcript' }, $true)
    $stale = $ast.Find({ param($a) $a -is [IfStatementAst] -and $a.Clauses[0].Item1.Extent.Text -eq '$endedHere' -and $a.Clauses[0].Item2.Extent.Text.Contains('Stop-Transcript') }, $true)
    $result.LeftoverTranscriptStoppedBeforeTheStart = ($null -ne $startTranscript) -and ($null -ne $stale) -and ($stale.Extent.EndOffset -le $startTranscript.Extent.StartOffset)

    # The functions that write the settings file they are handed, and every
    # function that reaches one of them. A call outside the try would write
    # the file before the finally stood behind it.
    $functions = @($ast.FindAll({ param($a) $a -is [FunctionDefinitionAst] }, $true))
    $writers = @($functions | Where-Object { $f = $_; @($writes | Where-Object { $_.Var -eq 'XmlPath' -and [object]::ReferenceEquals((Get-FunctionOf $_.Node), $f) }).Count -gt 0 } | ForEach-Object { $_.Name })
    $result.XmlPathWriters = @($writers | Sort-Object)
    $reach = @($writers)
    do {
        $grew = $false
        foreach ($f in $functions) {
            if ($reach -contains $f.Name) { continue }
            $called = @($f.Body.FindAll({ param($a) $a -is [CommandAst] }, $true) | ForEach-Object { $_.GetCommandName() })
            if (@($called | Where-Object { $reach -contains $_ }).Count -gt 0) { $reach += $f.Name; $grew = $true }
        }
    } while ($grew)
    $result.ReachTheWriters = @($reach | Sort-Object)
    $calls = @($ast.FindAll({ param($a) $a -is [CommandAst] -and ($reach -contains $a.GetCommandName()) }, $true) | Where-Object { $null -eq (Get-FunctionOf $_) })
    $result.WriterCallsInTheTry = @($calls | Where-Object { Test-Within $_ $cleanupBody }).Count
    $result.WriterCallsOutsideTheTry = @($calls | Where-Object { -not (Test-Within $_ $cleanupBody) } | ForEach-Object { Get-Line $_ })
}
elseif ($Mode -eq 'block') {
    # The functions every script under tools shares, read off
    # settings_guard.ps1: the rules inside them that have a number or a
    # comparison to read, and what the file runs when a script loads it.
    $doubt = Get-FunctionDefinition $guardAst 'Get-BackupDoubt'
    $binary = @($doubt.Body.FindAll({ param($a) $a -is [BinaryExpressionAst] }, $true))
    $result.ComparesTheMarkersPath = @($binary | Where-Object { $_.Operator -eq [TokenKind]::Ine -and $_.Extent.Text.Contains('$BackupPath') -and $_.Right.Extent.Text.Contains('$marker.Backup') }).Count
    $result.ComparesTheMarkersAge = @($binary | Where-Object { $_.Operator -eq [TokenKind]::Igt -and $_.Left.Extent.Text.Contains('TotalHours') -and $_.Right.Extent.Text -eq '$script:BackupMarkerMaxAgeHours' }).Count
    $same = $doubt.Body.Find({ param($a) $a -is [CommandAst] -and $a.GetCommandName() -eq 'Test-SameSettings' }, $true)
    $markerRead = $doubt.Body.Find({ param($a) $a -is [CommandAst] -and $a.GetCommandName() -eq 'Get-BackupMarkerPath' }, $true)
    $result.SameBytesAreAskedBeforeTheMarker = ($null -ne $same) -and ($null -ne $markerRead) -and ($same.Extent.EndOffset -lt $markerRead.Extent.StartOffset)
    $result.DoubtReturns = @($doubt.Body.FindAll({ param($a) $a -is [ReturnStatementAst] }, $true) | ForEach-Object { if ($null -ne $_.Pipeline) { $_.Pipeline.Extent.Text } else { '' } })
    $age = @($guardAst.EndBlock.Statements | Where-Object { $_ -is [AssignmentStatementAst] -and $_.Left.Extent.Text -eq '$script:BackupMarkerMaxAgeHours' })
    $result.MaxAgeHours = if ($age.Count -eq 1) { $age[0].Right.Extent.Text } else { '' }
    $functions = @($guardAst.EndBlock.Statements | Where-Object { $_ -is [FunctionDefinitionAst] })
    $result.GuardFunctions = @($functions | ForEach-Object { $_.Name })
    $result.GuardFirstStatements = @($functions | ForEach-Object { $_.Body.EndBlock.Statements[0].Extent.Text })
    $result.GuardLoadStatements = @($guardAst.EndBlock.Statements | Where-Object { -not ($_ -is [FunctionDefinitionAst]) } | ForEach-Object { $_.Extent.Text })
    $result.GuardFunctionsInAll = @($guardAst.FindAll({ param($a) $a -is [FunctionDefinitionAst] }, $true)).Count
    $result.GuardHasParamBlock = ($null -ne $guardAst.ParamBlock)
    $keeper = Get-FunctionDefinition $guardAst 'Save-SettingsCopy'
    $keep = $keeper.Body.Find({ param($a) $a -is [AssignmentStatementAst] -and $a.Left.Extent.Text -eq '$keep' }, $true)
    $result.KeepRule = if ($null -ne $keep) { $keep.Right.Extent.Text } else { '' }
    $result.KeepCounts = @(if ($null -ne $keep) { $keep.Right.FindAll({ param($a) $a -is [ConstantExpressionAst] -and $a.Value -is [int] }, $true) | ForEach-Object { [int]$_.Value } })
    $result.PrunesTheOldestByName = $keeper.Body.Extent.Text.Contains('Sort-Object Name -Descending | Select-Object -Skip $keep')
    $stamp = $keeper.Body.Find({ param($a) $a -is [AssignmentStatementAst] -and $a.Left.Extent.Text -eq '$stamp' }, $true)
    $result.StampText = if ($null -ne $stamp) { $stamp.Right.Extent.Text } else { '' }
    $result.CopyComesBeforeThePrune = $false
    $copy = $keeper.Body.Find({ param($a) $a -is [CommandAst] -and $a.GetCommandName() -eq 'Copy-Item' }, $true)
    $prune = $keeper.Body.Find({ param($a) $a -is [CommandAst] -and $a.GetCommandName() -eq 'Remove-Item' }, $true)
    if (($null -ne $copy) -and ($null -ne $prune)) { $result.CopyComesBeforeThePrune = ($copy.Extent.EndOffset -lt $prune.Extent.StartOffset) }
    $marker = Get-FunctionDefinition $guardAst 'Set-BackupMarker'
    $result.MarkerKeys = @($marker.Body.FindAll({ param($a) $a -is [HashtableAst] }, $true) | ForEach-Object { $_.KeyValuePairs } | ForEach-Object { $_.Item1.Extent.Text })
}
elseif ($Mode -eq 'shared') {
    # How a script takes in settings_guard.ps1: dot-sourced once among its
    # top-level statements, ahead of the first call of one of its functions,
    # with none of those functions defined again and none of its values set.
    $guardNames = @($guardAst.EndBlock.Statements | Where-Object { $_ -is [FunctionDefinitionAst] } | ForEach-Object { $_.Name })
    $guardValues = @($guardAst.EndBlock.Statements | Where-Object { $_ -is [AssignmentStatementAst] } | ForEach-Object { $_.Left.VariablePath.UserPath -replace '^script:', '' })
    $loads = @($ast.FindAll({ param($a) $a -is [CommandAst] -and $a.InvocationOperator -eq [TokenKind]::Dot }, $true))
    $result.DotSources = @($loads | ForEach-Object { $_.Extent.Text })
    $result.DotSourcedAtTheTop = @($loads | Where-Object { [object]::ReferenceEquals((Get-Statement $_).Parent, $ast.EndBlock) }).Count
    $uses = @($ast.FindAll({ param($a) $a -is [CommandAst] -and ($guardNames -contains $a.GetCommandName()) }, $true) | Sort-Object { $_.Extent.StartOffset })
    $result.GuardCalls = $uses.Count
    $result.LoadedBeforeTheFirstCall = ($loads.Count -eq 1) -and ($uses.Count -gt 0) -and ($loads[0].Extent.EndOffset -le $uses[0].Extent.StartOffset)
    $result.GuardFunctionsDefinedHere = @($ast.FindAll({ param($a) $a -is [FunctionDefinitionAst] -and ($guardNames -contains $a.Name) }, $true) | ForEach-Object { Get-Line $_ })
    $result.GuardValuesSetHere = @($ast.FindAll({ param($a) $a -is [AssignmentStatementAst] -and ($a.Left -is [VariableExpressionAst]) -and ($guardValues -contains ($a.Left.VariablePath.UserPath -replace '^(script|global):', '')) }, $true) | ForEach-Object { Get-Line $_ })
}
elseif ($Mode -eq 'names') {
    # Every script beside this one that names the settings file in its
    # code: a string in it holds PadForge.xml. Comments do not count. Each
    # file is parsed, and none is run.
    $named = @()
    foreach ($file in @(Get-ChildItem -LiteralPath (Split-Path $ScriptPath) | Where-Object { $_.Extension -eq '.ps1' } | Sort-Object Name)) {
        $t = $null
        $e = $null
        $tree = [Parser]::ParseFile($file.FullName, [ref]$t, [ref]$e)
        $strings = @($tree.FindAll({ param($a) ($a -is [StringConstantExpressionAst]) -or ($a -is [ExpandableStringExpressionAst]) }, $true))
        if (@($strings | Where-Object { $_.Value -like '*PadForge.xml*' }).Count -gt 0) { $named += $file.Name }
    }
    $result.NameTheSettingsFile = $named
}
elseif ($Mode -eq 'markerpath') {
    # Get-BackupMarkerPath, run alone on backup paths under this test's
    # folder. It builds a path and touches no file, and the folder it puts
    # the markers in is this test's too.
    $fn = Get-FunctionDefinition $guardAst 'Get-BackupMarkerPath'
    $result.Refused = @(Get-Refused @($fn) @('Join-Path'))
    $paths = [ordered]@{}
    if ($result.Refused.Count -eq 0) {
        . ([scriptblock]::Create($fn.Extent.Text))
        $script:CaptureStateDir = Join-Path $WorkDir 'state'
        foreach ($line in [System.IO.File]::ReadAllLines((Join-Path $WorkDir 'backups.txt'))) {
            if ($line) { $paths[$line] = Get-BackupMarkerPath $line }
        }
    }
    $result.MarkerPaths = $paths
}
elseif ($Mode -eq 'ending') {
    # How a run that skipped a shot or failed ends: the flag it keeps, where
    # the flag is set, and the exit codes written out after the last
    # top-level try. Read off the tree: nothing here runs.
    $tries = @($ast.EndBlock.Statements | Where-Object { $_ -is [TryStatementAst] })
    if ($tries.Count -eq 0) { throw 'the script has no top-level try' }
    $main = $tries[$tries.Count - 1]
    $flags = @('$script:missed', '$script:failed')
    $result.FlagSetBeforeTheTry = @($ast.EndBlock.Statements | Where-Object { $_ -is [AssignmentStatementAst] -and ($flags -contains $_.Left.Extent.Text) -and $_.Extent.EndOffset -le $main.Extent.StartOffset } | ForEach-Object { $_.Extent.Text })
    $result.CatchFirst = @($main.CatchClauses | ForEach-Object { $_.Body.Statements[0].Extent.Text })
    $closing = @($ast.EndBlock.Statements | Where-Object { $_.Extent.StartOffset -gt $main.Extent.EndOffset })
    $result.ClosingExits = @($closing | ForEach-Object { $_.FindAll({ param($a) $a -is [ExitStatementAst] }, $true) } | ForEach-Object {
        $if = $_.Parent
        while ($null -ne $if -and -not ($if -is [IfStatementAst])) { $if = $if.Parent }
        $condition = if ($null -ne $if) { ($if.Clauses[0].Item1.Extent.Text -replace '\s+', ' ') } else { 'always' }
        $condition + ' => ' + $(if ($null -ne $_.Pipeline) { $_.Pipeline.Extent.Text } else { '' })
    })
    $top = $ast.EndBlock.Statements
    $result.LastStatement = $top[$top.Count - 1].Extent.Text
    # A way out of a scene, or out of a shot, records the shot as missed in
    # a statement of its own block before it.
    $records = {
        param($node)
        $statement = Get-Statement $node
        $block = $statement.Parent
        $at = $block.Statements.IndexOf($statement)
        @($block.Statements | Select-Object -First $at | Where-Object {
            $_ -is [AssignmentStatementAst] -and $_.Left.Extent.Text -eq '$script:missed' -and $_.Operator -eq [TokenKind]::PlusEquals }).Count -gt 0
    }
    # Each way out of a scene: a continue or break whose nearest loop is the
    # loop over the scenes.
    $sceneLoop = $main.Body.Find({ param($a) $a -is [ForEachStatementAst] -and $a.Variable.VariablePath.UserPath -eq 'sc' }, $true)
    $skips = @()
    if ($null -ne $sceneLoop) {
        $skips = @($sceneLoop.Body.FindAll({ param($a) ($a -is [ContinueStatementAst]) -or ($a -is [BreakStatementAst]) }, $true) | Where-Object {
            $loop = $_.Parent
            while ($null -ne $loop -and -not ($loop -is [LoopStatementAst])) { $loop = $loop.Parent }
            [object]::ReferenceEquals($loop, $sceneLoop) })
    }
    $result.Skips = @($skips | ForEach-Object { Get-Line $_ })
    $result.SkipsNotRecorded = @($skips | Where-Object { -not (& $records $_) } | ForEach-Object { Get-Line $_ })
    $shot = $ast.Find({ param($a) $a -is [FunctionDefinitionAst] -and $a.Name -eq 'Shot' }, $true)
    $returns = @(if ($null -ne $shot) { $shot.Body.FindAll({ param($a) $a -is [ReturnStatementAst] }, $true) })
    $result.ShotReturns = @($returns | ForEach-Object { Get-Line $_ })
    $result.ShotReturnsNotRecorded = @($returns | Where-Object { -not (& $records $_) } | ForEach-Object { Get-Line $_ })
}
elseif ($Mode -eq 'encoding') {
    # How a script reads and writes a settings file. [xml]$x = Get-Content
    # hands PowerShell's XML conversion text that Get-Content has already
    # decoded, as ANSI when the file has no byte order mark.
    $result.XmlCasts = @($ast.FindAll({ param($a) $a -is [ConvertExpressionAst] -and $a.Type.TypeName.Name -eq 'xml' }, $true) | ForEach-Object { Get-Line $_ })
    $result.SettingsReadAsText = @($ast.FindAll({ param($a) $a -is [CommandAst] -and $a.GetCommandName() -eq 'Get-Content' }, $true) | Where-Object {
        @($_.FindAll({ param($v) $v -is [VariableExpressionAst] -and ($settingsVars -contains $v.VariablePath.UserPath) }, $true)).Count -gt 0 } | ForEach-Object { Get-Line $_ })
    # Taken into a variable first: the function hands its list back whole,
    # and piped straight on it would arrive as one object.
    $allWrites = Get-SettingsWrites $ast
    $result.DirectSaves = @($allWrites | Where-Object { $_.Name -eq '.Save' } | ForEach-Object { Get-Line $_.Node })
    $result.SettingsWrittenAsText = @($allWrites | Where-Object { @('Set-Content', 'Add-Content', 'Out-File') -contains $_.Name } | ForEach-Object { Get-Line $_.Node })
    $result.Loads = @($ast.FindAll({ param($a) $a -is [CommandAst] -and $a.GetCommandName() -eq 'Read-SettingsXml' }, $true) | ForEach-Object { $_.Extent.Text })
    $result.Saves = @($ast.FindAll({ param($a) $a -is [CommandAst] -and $a.GetCommandName() -eq 'Save-SettingsXml' }, $true) | ForEach-Object { $_.Extent.Text })
    $loader = Get-FunctionDefinition $guardAst 'Read-SettingsXml'
    $saver = Get-FunctionDefinition $guardAst 'Save-SettingsXml'
    $objects = { param($fn) @($fn.Body.FindAll({ param($a) $a -is [CommandAst] -and $a.GetCommandName() -eq 'New-Object' }, $true) | ForEach-Object { $_.Extent.Text }) }
    $result.LoaderObjects = @(& $objects $loader)
    $result.LoaderShares = @($loader.Body.FindAll({ param($a) ($a -is [MemberExpressionAst]) -and $a.Static -and $a.Expression.Extent.Text -eq '[System.IO.FileShare]' }, $true) | ForEach-Object { $_.Member.Extent.Text })
    $result.LoaderLoads = @($loader.Body.FindAll({ param($a) $a -is [InvokeMemberExpressionAst] -and $a.Member.Extent.Text -eq 'Load' }, $true) | ForEach-Object { $_.Extent.Text })
    $result.LoaderCommands = @($loader.Body.FindAll({ param($a) $a -is [CommandAst] }, $true) | ForEach-Object { $_.GetCommandName() } | Sort-Object -Unique)
    $result.SaverObjects = @(& $objects $saver)
    $result.SaverOpening = @($saver.Body.EndBlock.Statements | Select-Object -First 2 | ForEach-Object { $_.Extent.Text })
    $result.SaverSaves = @($saver.Body.FindAll({ param($a) $a -is [InvokeMemberExpressionAst] -and $a.Member.Extent.Text -eq 'Save' }, $true) | ForEach-Object { $_.Extent.Text })
}
elseif ($Mode -eq 'snapshot') {
    # The settings file a staging run keeps for later targeted runs.
    $toSnapshot = @($ast.FindAll({ param($a) $a -is [CommandAst] -and $a.GetCommandName() -eq 'Copy-Item' }, $true) | Where-Object {
        $d = Get-Argument $_ @('Destination') 1
        ($null -ne $d) -and ($d.Extent.Text -eq '$script:SnapshotXml') })
    $fromSnapshot = @($ast.FindAll({ param($a) $a -is [CommandAst] -and $a.GetCommandName() -eq 'Copy-Item' }, $true) | Where-Object {
        $s = Get-Argument $_ @('LiteralPath', 'Path') 0
        ($null -ne $s) -and ($s.Extent.Text -eq '$script:SnapshotXml') })
    $result.SnapshotSaves = $toSnapshot.Count
    $result.SnapshotLoads = $fromSnapshot.Count
    $result.SavedOnlyWhenTheStagingHeld = $false
    $result.FailedBranchWrites = @()
    $result.FailedBranchSays = ''
    if ($toSnapshot.Count -eq 1) {
        $block = (Get-Statement $toSnapshot[0]).Parent
        $if = $block.Parent
        if (($if -is [IfStatementAst]) -and [object]::ReferenceEquals($if.ElseClause, $block) -and ($if.Clauses.Count -eq 1) -and ($if.Clauses[0].Item1.Extent.Text -eq '$script:StagingFailed')) {
            $result.SavedOnlyWhenTheStagingHeld = $true
            $failed = $if.Clauses[0].Item2
            $result.FailedBranchWrites = @($failed.FindAll({ param($a) $a -is [CommandAst] -and (@('Copy-Item', 'Move-Item', 'Set-Content', 'Out-File', 'Save-SettingsXml') -contains $a.GetCommandName()) }, $true) | ForEach-Object { Get-Line $_ })
            $result.FailedBranchSays = (@($failed.FindAll({ param($a) $a -is [CommandAst] -and $a.GetCommandName() -eq 'Write-Host' }, $true) | ForEach-Object { $_.Extent.Text }) -join ' | ')
        }
    }
}
elseif ($Mode -eq 'same') {
    # Test-SameSettings, run alone on files this test wrote. For two cases the
    # driver holds one of the files open the way another process would.
    $same = Get-FunctionDefinition $guardAst 'Test-SameSettings'
    $result.SameCommands = @($same.Body.FindAll({ param($a) $a -is [CommandAst] }, $true) | ForEach-Object { $_.GetCommandName() } | Sort-Object -Unique)
    $result.SameShares = @($same.Body.FindAll({ param($a) ($a -is [MemberExpressionAst]) -and $a.Static -and $a.Expression.Extent.Text -eq '[System.IO.FileShare]' }, $true) | ForEach-Object { $_.Member.Extent.Text })
    $result.Refused = @(Get-Refused @($same) $sameNeeds)
    $cases = [ordered]@{}
    if ($result.Refused.Count -eq 0) {
        . ([scriptblock]::Create($same.Extent.Text))
        $a = Join-Path $WorkDir 'same-a.xml'
        $b = Join-Path $WorkDir 'same-b.xml'
        $c = Join-Path $WorkDir 'same-c.xml'
        $ask = { param($x, $y) try { [string](Test-SameSettings $x $y) } catch { 'stopped' } }
        $cases['same bytes'] = & $ask $a $b
        $cases['one byte differs'] = & $ask $a $c
        $cases['one file missing'] = & $ask $a (Join-Path $WorkDir 'same-none.xml')
        # As File.WriteAllBytes holds a file while PadForge saves.
        $held = New-Object System.IO.FileStream($b, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Write, [System.IO.FileShare]::Read)
        try { $cases['the other being written'] = & $ask $a $b } finally { $held.Dispose() }
        $held = New-Object System.IO.FileStream($b, [System.IO.FileMode]::Open, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None)
        try { $cases['the other unreadable'] = & $ask $a $b } finally { $held.Dispose() }
    }
    $result.Cases = $cases
}
elseif ($Mode -eq 'wrapper') {
    # A wrapper under tools: the script it calls, and what it does with that
    # script's exit code.
    $top = $ast.EndBlock.Statements
    $result.LastStatement = $top[$top.Count - 1].Extent.Text
    $call = $ast.Find({ param($a) $a -is [CommandAst] -and $a.InvocationOperator -eq [TokenKind]::Ampersand -and $a.Extent.Text -match '\\[A-Za-z_]+\.ps1' }, $true)
    $result.Called = ''
    if (($null -ne $call) -and ($call.Extent.Text -match '\\([A-Za-z_]+\.ps1)')) { $result.Called = $Matches[1] }
    $next = if ($null -ne $call) { Get-NextStatement (Get-Statement $call) } else { $null }
    $result.AfterTheCall = if ($null -ne $next) { $next.Extent.Text } else { '' }
    $result.CodeAssignments = @($ast.FindAll({ param($a) $a -is [AssignmentStatementAst] -and $a.Left.Extent.Text -eq '$code' }, $true) | ForEach-Object { $_.Extent.Text })
}
else { throw "unknown mode $Mode" }

$result.Host = @($script:hostLines)
[System.IO.File]::WriteAllText((Join-Path $WorkDir 'result.json'), (ConvertTo-Json -InputObject $result -Depth 6 -Compress), (New-Object System.Text.UTF8Encoding($false)))
""";
    }
}
