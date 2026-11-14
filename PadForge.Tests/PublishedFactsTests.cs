using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using HIDMaestro;
using PadForge.Common.Input;
using PadForge.Services;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Facts the README, BUILD.md, LICENSE and the About page publish, each
    /// held in one of three ways.
    ///
    /// <para>Read from its source in this repository: the profile counts
    /// from the bundled HIDMaestro catalog, the driver versions from the
    /// bundled files, the package versions from the project files, the
    /// 500 ms in the Handheld Companion credit from the constant PadForge
    /// runs on, and the order of the About page's credits from the page and
    /// its strings.</para>
    ///
    /// <para>Held to recorded wording: another project's license grant or
    /// the technique PadForge took from it, whose source is a clone a test
    /// run does not have, the license each About credit's chip names, and
    /// two README sentences that were wrong once, on the Quest's runtime and
    /// on MIDI without Windows MIDI Services. Each
    /// summary names what was read. These tests catch a surface that drifts
    /// from the others, or back to the old text. They do not read the source
    /// again.</para>
    ///
    /// <para>Held by hash: the README's "New in" blocks, which describe
    /// releases that are out.</para>
    /// </summary>
    public class PublishedFactsTests
    {
        private static readonly string[] Locales = PadForge.Tests.LocaleFiles.Complete;

        // The license each About credit's chip names (#574), by chip, as the
        // credits' keys less "About_". A credit of one project shows that
        // project's license, a credit of projects under different licenses
        // shows Several licenses, and Raw Input, a Windows API, shows none.
        private static readonly (string Chip, string[] Credits)[] AboutChips =
        {
            ("About_License_Mit", new[]
            {
                "AudioEndPointLibrary", "AutoEq", "BouncyCastle", "Bs2b", "Colore", "Ds4Audio", "Ds4Drv", "Ds5Dongle", "DuaLib",
                "DsBtHaptics", "DsTester", "DualSenseWindows", "EdRefCard2", "Fusion", "GamepadBatteryMonitor",
                "GamepadAssetPack", "GamepadMotionHelpers", "HelixToolkit", "HidHide", "HIDMaestro", "Hitboxer",
                "Jc2Mouse", "JoyShockMapper", "Libinput", "MidiServices", "MvvmToolkit", "NAudio", "NefariusDevMgmt",
                "DotNet10", "Nipplejs", "QrCodeGen", "TriggerEffectGenerator", "TwitchChatRefs", "ViGEmClient",
                "WiimoteLib", "ModernWpf", "X1nput", "Xbledctl", "MouseArt", "ZstdSharp",
            }),
            ("About_License_MitSdkLoader", new[] { "GameInput" }),
            ("About_License_Apache2", new[] { "Bluepad32", "ProtobufNet", "Vosk" }),
            ("About_License_Bsd3", new[]
            {
                "BthPS3", "Concentus", "DsHidMini", "LogitechGKeyRefs", "OpenVR", "QDollar", "SipSorcery",
                "SteamControllerSinger", "SteamHapticsSinger",
            }),
            ("About_License_Bsd2", new[] { "PcscSharp", "UsbipWin2" }),
            ("About_License_BsdStyle", new[] { "Hidapi" }),
            ("About_License_Bsl1", new[] { "Monado", "OpenHmd" }),
            ("About_License_Isc", new[] { "ForgePlay", "Opentrack" }),
            ("About_License_Mpl2", new[] { "SAxense" }),
            ("About_License_Unlicense", new[] { "CemuhookProtocol" }),
            ("About_License_Zlib", new[] { "SDL3", "GameControllerDb" }),
            ("About_License_Gpl2", new[] { "Ds4Mac", "GestureSign" }),
            ("About_License_Gpl2OrLater", new[]
            {
                "AutoHotkey", "Dolphin", "Kodi", "LinuxKernel", "ObsBrowser", "OpenRgb", "Qmk", "XoneXow",
            }),
            ("About_License_Gpl3", new[] { "Ds4MapperTest", "Ds4WindowsHbashton", "LegionToolkit", "Touchmote" }),
            ("About_License_Gpl3OrLater", new[]
            {
                "AntiMicroX", "Ds4Windows", "Eden", "InputPlumber", "SpecialK", "ViiperHbashton",
            }),
            ("About_License_Agpl3", new[] { "ChiakiNg", "Ds5Bridge" }),
            ("About_License_Lgpl21OrLater", new[] { "FFmpeg", "Libusb" }),
            ("About_License_Lgpl3", new[] { "OpenTabletDriver" }),
            ("About_License_Lgpl3OrLater", new[] { "Libwdi" }),
            ("About_License_CcByNcSa4", new[] { "HandheldCompanion", "ValveCad" }),
            ("About_License_CcBy3", new[] { "ZackslyIcons" }),
            ("About_License_CgTrader", new[] { "3doverstock", "Hado" }),
            ("About_License_WyvrnEula", new[] { "Interhaptics" }),
            ("About_License_VisualStudio", new[] { "VcRuntime" }),
            ("About_License_CorsairSdk", new[] { "IcueSdk" }),
            ("About_License_TrademarkOnly", new[] { "OpenXInput" }),
            ("About_License_NoTermsNamed", new[] { "MysticLightSdk" }),
            ("About_License_NonePublished", new[]
            {
                "DualSenseSupport", "DualSenseY", "JoyconSinger", "SwitchResearch", "WiiBrew",
            }),
            ("About_License_Several", new[]
            {
                "ArcadeIoRefs", "BlissBoxRefs", "BleControllerRefs", "RfcommIcadeRefs", "DjiRemoteRefs", "GameModRefs",
                "Ds4HeadsetRefs", "GunConRefs", "AnalogKeyboardRefs", "HidDeviceRefs", "IForceRefs", "IconSources",
                "InstrumentRefs", "IrohFlexInput", "Kaldi", "KeyboardScreenRefs", "LightingRefs", "Linuxmotehook",
                "LogitechLedRefs", "MenuRefs", "MingwRuntime", "Nefcon", "FalconRefs", "OpenXrRefs",
                "PeripheralOutputRefs", "PortMidiRtMidi", "ProfileImportRefs", "Psmoveapi", "TelemetryRefs",
                "RingConRefs", "Ds3Research", "ScreenColorRefs", "SerialControllerRefs", "SpaceMouseRefs",
                "SpecialtyUsbRefs", "SteamControllerRefs", "SteamKit2", "ThumbstickDeadzones", "TrainControllerRefs",
                "TritonLib", "WheelRefs", "XboxControllerRefs",
            }),
        };

        private const string NewInBlock = @"<details>\s*<summary><b>New in ([0-9.]+):</b>.*?</details>";

        /// <summary>
        /// Every profile count README.md and BUILD.md give matches the
        /// HIDMaestro.Core that ships: the catalog size, the profiles
        /// PadForge offers (a captured descriptor, nothing withheld, Custom
        /// not counted), and the profiles that lack a captured descriptor.
        /// Three README sites and BUILD.md still said 231 and 133 after two
        /// HIDMaestro bumps moved the rest. The missing captures are counted
        /// on their own: a withheld profile has a capture, so the catalog
        /// less the offered profiles overstated them once HIDMaestro 2.0.0
        /// gave the withheld Wii U GameCube adapter its descriptor. The
        /// released "New in" blocks keep the counts of their release.
        /// </summary>
        [Fact]
        public void EveryProfileCountMatchesTheBundledCatalog()
        {
            int total, offered, missing;
            using (var ctx = new HMContext())
            {
                ctx.LoadDefaultProfiles();
                total = ctx.AllProfiles.Count;
                offered = ctx.AllProfiles.Count(p => p.IsDeployable && !HMaestroProfileCatalog.IsWithheldProfile(p.Id));
                missing = ctx.AllProfiles.Count(p => !p.IsDeployable);
            }
            Assert.True(total > offered && offered > 0, $"catalog {total}, offered {offered}");
            Assert.True(missing > 0 && missing < total, $"catalog {total}, missing captures {missing}");

            var texts = new[] { ("README.md", ReadmeWithoutNewInBlocks()), ("BUILD.md", Text("BUILD.md")) };

            var counts = new List<(string Where, int Said, int Is)>();
            foreach (var (name, text) in texts)
            {
                foreach (string pattern in new[]
                         {
                             @"<b>(\d+)</b> device profiles", @"(\d+) device profiles",
                             @"HIDMaestro['’]s (\d+) profiles", @"ships (\d+) profiles",
                         })
                    foreach (Match m in Regex.Matches(text, pattern))
                        counts.Add(($"{name}: {m.Value}", int.Parse(m.Groups[1].Value), total));
                foreach (string pattern in new[] { @"(\d+) of HIDMaestro['’]s", @"offers the (\d+)" })
                    foreach (Match m in Regex.Matches(text, pattern))
                        counts.Add(($"{name}: {m.Value}", int.Parse(m.Groups[1].Value), offered));
                foreach (Match m in Regex.Matches(text, @"(\d+) are missing their captures"))
                    counts.Add(($"{name}: {m.Value}", int.Parse(m.Groups[1].Value), missing));
            }

            // Positive control: the patterns still find the sentences.
            Assert.True(counts.Count >= 9, $"found only {counts.Count} counts:\n" + string.Join("\n", counts.Select(c => c.Where)));
            var wrong = counts.Where(c => c.Said != c.Is).ToList();
            Assert.True(wrong.Count == 0, "counts that differ from the bundled catalog:\n"
                + string.Join("\n", wrong.Select(c => $"{c.Where} (should be {c.Is})")));
        }

        /// <summary>
        /// BUILD.md names the driver versions PadForge bundles, and they are
        /// the bundled ones: HIDMaestro's is the product version of
        /// Resources/HIDMaestro/HIDMaestro.Core.dll, and BthPS3's is the
        /// DriverVer of its INF. The sentence said HIDMaestro 1.10.0 through
        /// two bumps of the bundle, to 1.10.1 and to 1.11.0. The HidHide
        /// ARM64 driver's version is the DriverVer of the INF inside
        /// Resources/HidHideArm64/HidHide_ARM64.zip, and nefcon's, which the
        /// README's license list gives too, is the product version of
        /// nefconc.exe beside it.
        /// </summary>
        [Fact]
        public void BuildMdNamesTheBundledDriverVersions()
        {
            string core = FileVersionInfo.GetVersionInfo(AuditDelta20260823Tests.FindRepoFile(
                Path.Combine("PadForge.App", "Resources", "HIDMaestro", "HIDMaestro.Core.dll"))).ProductVersion ?? string.Empty;
            var hidMaestro = Regex.Match(core, @"^\d+\.\d+\.\d+");
            Assert.True(hidMaestro.Success, $"HIDMaestro.Core.dll has product version '{core}'");

            var bthPs3 = Regex.Match(Text("PadForge.App", "Resources", "BthPS3", "BthPS3", "BthPS3.inf"),
                @"DriverVer\s*=\s*[\d/]+\s*,\s*(\d+\.\d+\.\d+)\.\d+");
            Assert.True(bthPs3.Success, "BthPS3.inf has no DriverVer");

            var sentence = Regex.Matches(Text("BUILD.md"), @"HIDMaestro (\d+\.\d+\.\d+) and BthPS3 (\d+\.\d+\.\d+) each carry");
            // Positive control: the sentence is still there to be checked.
            Assert.True(sentence.Count == 1, $"BUILD.md names the bundled driver versions {sentence.Count} times, not once");
            string saidHidMaestro = sentence[0].Groups[1].Value, saidBthPs3 = sentence[0].Groups[2].Value;
            Assert.True(saidHidMaestro == hidMaestro.Value,
                $"BUILD.md says HIDMaestro {saidHidMaestro}, and the bundled HIDMaestro.Core.dll is {hidMaestro.Value}");
            Assert.True(saidBthPs3 == bthPs3.Groups[1].Value,
                $"BUILD.md says BthPS3 {saidBthPs3}, and the bundled BthPS3.inf is {bthPs3.Groups[1].Value}");

            string hidHideInf;
            using (var zip = ZipFile.OpenRead(AuditDelta20260823Tests.FindRepoFile(
                       Path.Combine("PadForge.App", "Resources", "HidHideArm64", "HidHide_ARM64.zip"))))
            using (var reader = new StreamReader(
                       zip.Entries.Single(e => e.Name.Equals("HidHide.inf", StringComparison.OrdinalIgnoreCase)).Open()))
                hidHideInf = reader.ReadToEnd();
            var hidHide = Regex.Match(hidHideInf, @"DriverVer\s*=\s*[\d/]+\s*,\s*(\d+\.\d+\.\d+\.\d+)");
            Assert.True(hidHide.Success, "the HidHide ARM64 INF has no DriverVer");
            string nefcon = FileVersionInfo.GetVersionInfo(AuditDelta20260823Tests.FindRepoFile(
                Path.Combine("PadForge.App", "Resources", "HidHideArm64", "nefconc.exe"))).ProductVersion ?? string.Empty;
            Assert.True(Regex.IsMatch(nefcon, @"^\d+\.\d+\.\d+$"), $"nefconc.exe has product version '{nefcon}'");

            var arm64 = Regex.Matches(Text("BUILD.md"),
                @"driver (\d+\.\d+\.\d+\.\d+)\), installed with HidHide's tool \(`nefconc\.exe`, nefcon (\d+\.\d+\.\d+),");
            // Positive control: the sentence is still there to be checked.
            Assert.True(arm64.Count == 1, $"BUILD.md names the HidHide ARM64 driver and nefcon versions {arm64.Count} times, not once");
            Assert.True(arm64[0].Groups[1].Value == hidHide.Groups[1].Value,
                $"BUILD.md says HidHide ARM64 driver {arm64[0].Groups[1].Value}, and the bundled INF is {hidHide.Groups[1].Value}");
            Assert.True(arm64[0].Groups[2].Value == nefcon,
                $"BUILD.md says nefcon {arm64[0].Groups[2].Value}, and the bundled nefconc.exe is {nefcon}");

            var bullet = Regex.Matches(Text("README.md"), @"The ARM64 console build of nefcon (\d+\.\d+\.\d+) ships");
            Assert.True(bullet.Count == 1, $"README's license list names the bundled nefcon version {bullet.Count} times, not once");
            Assert.True(bullet[0].Groups[1].Value == nefcon,
                $"README says nefcon {bullet[0].Groups[1].Value}, and the bundled nefconc.exe is {nefcon}");
        }

        /// <summary>
        /// The project file's note on the bundled HIDMaestro build names the
        /// build that ships. It said v1.9.2 through three bumps of the
        /// bundle, to 1.10.0, 1.10.1 and 1.11.0, with no line on what any of
        /// them brought.
        /// </summary>
        [Fact]
        public void TheProjectFileNamesTheBundledHidMaestroBuild()
        {
            string core = FileVersionInfo.GetVersionInfo(AuditDelta20260823Tests.FindRepoFile(
                Path.Combine("PadForge.App", "Resources", "HIDMaestro", "HIDMaestro.Core.dll"))).ProductVersion ?? string.Empty;
            var hidMaestro = Regex.Match(core, @"^\d+\.\d+\.\d+");
            Assert.True(hidMaestro.Success, $"HIDMaestro.Core.dll has product version '{core}'");

            var note = Regex.Matches(Text("PadForge.App", "PadForge.App.csproj"), @"Current: v(\d+\.\d+\.\d+),");
            // Positive control: the note is still there to be checked.
            Assert.True(note.Count == 1, $"PadForge.App.csproj names the current HIDMaestro build {note.Count} times, not once");
            Assert.True(note[0].Groups[1].Value == hidMaestro.Value,
                $"PadForge.App.csproj says HIDMaestro v{note[0].Groups[1].Value}, and the bundled HIDMaestro.Core.dll is {hidMaestro.Value}");
        }

        /// <summary>
        /// BUILD.md lists each project's NuGet packages at the versions the
        /// project references, no more and no fewer. It still gave
        /// Nefarius.Utilities.DeviceManagement as 5.2.0 after the project
        /// moved to 6.1.0.
        /// </summary>
        [Fact]
        public void BuildMdListsEveryPackageAtItsReferencedVersion()
        {
            var blocks = Regex.Matches(Text("BUILD.md"),
                @"\*\*(?<project>PadForge[\w.]*)\.csproj:\*\*\s*```\r?\n(?<body>.*?)```", RegexOptions.Singleline);
            // Positive control: the three project lists are still found.
            Assert.Equal(3, blocks.Count);

            var wrong = new List<string>();
            foreach (Match block in blocks)
            {
                string project = block.Groups["project"].Value;
                var referenced = Regex.Matches(Text(project, project + ".csproj"),
                        @"<PackageReference\s+Include=""([^""]+)""\s+Version=""([^""]+)""")
                    .ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value, StringComparer.Ordinal);
                var listed = block.Groups["body"].Value.Split('\n')
                    .Select(line => Regex.Match(line, @"^(\S+)\s+(\S+)\s"))
                    .Where(m => m.Success)
                    .ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value, StringComparer.Ordinal);
                Assert.True(referenced.Count > 0, $"{project}.csproj references no package");

                foreach (var (name, version) in referenced)
                {
                    if (!listed.TryGetValue(name, out string said))
                        wrong.Add($"{project}: {name} {version} is referenced and not listed");
                    else if (said != version)
                        wrong.Add($"{project}: listed as {name} {said}, referenced at {version}");
                }
                foreach (string name in listed.Keys.Where(n => !referenced.ContainsKey(n)))
                    wrong.Add($"{project}: {name} is listed and not referenced");
            }
            Assert.True(wrong.Count == 0, "BUILD.md's NuGet lists differ from the project files:\n" + string.Join("\n", wrong));
        }

        /// <summary>
        /// A released "New in" block says what that release did, so its text
        /// stays as it shipped: new behavior goes into the next release's
        /// block. Two commits after 5.0.0 rewrote the 5.0.0 and 4.4.0 blocks
        /// to describe lighting and rumble work that was not released. Each
        /// block is recorded here by the SHA-256 of its text, line endings
        /// as LF, the next release's included once it is written. An edit
        /// fails until the recorded value is changed on purpose, which is
        /// right for a correction of a false statement, such as the 4.5.0
        /// headset bullet, and for a block whose release is not out yet.
        /// </summary>
        [Fact]
        public void EveryNewInBlockKeepsItsRecordedText()
        {
            var blocks = Regex.Matches(Text("README.md").Replace("\r\n", "\n"), NewInBlock, RegexOptions.Singleline)
                .ToDictionary(m => m.Groups[1].Value,
                              m => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(m.Value))).ToLowerInvariant());
            // Positive control: the pattern still finds the blocks.
            Assert.True(blocks.Count >= 1, "the README has no New in block");

            var recorded = RecordedNewInBlocks.ToDictionary(b => b.Version, b => b.Sha256);
            var wrong = new List<string>();
            foreach (var (version, hash) in blocks)
            {
                if (!recorded.TryGetValue(version, out string was))
                    wrong.Add($"New in {version} is not recorded. Add (\"{version}\", \"{hash}\").");
                else if (was != hash)
                    wrong.Add($"New in {version} changed. If it is released and the edit adds or rewords behavior, put that in the next release's block instead. Otherwise record \"{hash}\".");
            }
            foreach (string version in recorded.Keys.Where(v => !blocks.ContainsKey(v)))
                wrong.Add($"New in {version} is recorded and the README no longer has it. Drop its entry.");
            Assert.True(wrong.Count == 0, string.Join("\n", wrong));
        }

        private static readonly (string Version, string Sha256)[] RecordedNewInBlocks =
        {
            ("5.3.0", "d91993bdf93bbaaca8ed7e012a5255d7b4f9ebed189a1bfa160ad3c6b0be8ef3"),
            ("5.2.0", "9c5cd026ac4106c58c7ca644d7c0303e160f95e4f3cfd0dd456bc3db86334b00"),
            ("5.1.0", "31f173ae7831da8e37b738510723fbae62f05992a05e89ab3510b2c4ddcd3213"),
            ("5.0.0", "648fbf07c0d29caa18d9fa6e49c33fe8d227e7a9bd20747d4e7ec5bcd5b34714"),
            ("4.5.3", "1daec61e80076738a933bc6f7d22deb7280095ebcc6b27d7c2cbf29f4ffd923d"),
            ("4.5.2", "e9e77cc8ad05c55996dd277045cc28ed4170be4dcdcddae78ce9ea7ea66f8d77"),
            ("4.5.0", "e952fcdfec04bcde11e888c088342fa91c220487ae6fea908054ac1a1acabc51"),
            ("4.4.0", "9191d9b2a8e7c3fc0ce1eee6645f78341847b32ea6727374439f746a3f0ac8d2"),
            ("4.3.2", "6e41aaf66c1fed39064eab76916d935636d2c8363622abca952f040cf302b410"),
            ("4.3.1", "9e74b6aa88e48443758c118ae92dc12d416110455d0d462ca7b507e3a973ee7d"),
            ("4.3.0", "9d32da330b4397d2f56ff0dd37a927c0e7172061016cdd873945c5fe79dd85a9"),
            ("4.2.0", "6623832e612706c4010aaf427d2c91e8a6ee0ed0ff5a310ea4ed40535a26535b"),
            ("4.1.0", "399a031c9a8275408cfd4705d16a74fbb8ecd809ac16c516446a14cbe018020c"),
        };

        /// <summary>
        /// The README sends a Quest through SteamVR's runtime only on Link
        /// or Air Link, where Meta's runtime is the alternative and offers no
        /// headless session. It said every Quest has to, and Virtual
        /// Desktop's runtime offers the session PadForge asks for
        /// (XR_MND_headless in VirtualDesktop-OpenXR's instance.cpp, absent
        /// from OpenXR-Inventory's meta_pc.json).
        /// </summary>
        [Fact]
        public void TheQuestSentenceNamesLink()
        {
            var sentences = Regex.Split(Text("README.md"), @"(?<=[.!?])\s+")
                .Where(s => Regex.IsMatch(s, @"\bQuest\b.*\bhas to go through SteamVR"))
                .ToList();
            // Positive control: the README still has the sentence.
            Assert.True(sentences.Count >= 1, "no README sentence says a Quest has to go through SteamVR any more");
            Assert.All(sentences, s => Assert.Contains("Link", s));
        }

        /// <summary>
        /// The README's Known limits say what MIDI does without Windows MIDI
        /// Services. It said neither MIDI input nor the MIDI virtual
        /// controller is available there, after PadForge had gained the
        /// legacy WinMM backend (MidiBackendLegacy), which runs on every
        /// Windows.
        /// </summary>
        [Fact]
        public void TheKnownLimitsNameTheLegacyMidiFallback()
        {
            string readme = Text("README.md");
            Assert.DoesNotContain("neither is available", readme);

            int start = readme.IndexOf("## Known limits", StringComparison.Ordinal);
            Assert.True(start >= 0, "the README has no Known limits section");
            int end = readme.IndexOf("\n---", start, StringComparison.Ordinal);
            Assert.True(end > start, "the Known limits section has no end");
            var midi = readme.Substring(start, end - start).Split('\n')
                .Where(line => line.StartsWith("- ", StringComparison.Ordinal) && line.Contains("MIDI", StringComparison.Ordinal))
                .ToList();
            // Positive control: the section still has a MIDI bullet.
            Assert.True(midi.Count >= 1, "Known limits has no MIDI bullet");
            Assert.True(midi.Any(line => line.Contains("legacy MIDI API", StringComparison.Ordinal)),
                "no Known limits bullet names the legacy MIDI API:\n" + string.Join("\n", midi));
        }

        /// <summary>
        /// Solaar, OpenRazer and OpenRGB are credited GPL-2.0-or-later on
        /// every surface. Every file PadForge read from them grants "either
        /// version 2 of the License, or (at your option) any later version":
        /// Solaar's base.py, hidpp20.py, hidpp20_constants.py and
        /// settings_templates.py headers, OpenRazer's 76 SPDX tags and its
        /// LICENSES/GPL-2.0-or-later.txt, and the SPDX tags in OpenRGB's
        /// SteelSeries, Logitech and Sony gamepad controller folders.
        ///
        /// <para>Since the direct paths of #494 the peripheral output
        /// credit names eight sources under that tag: these three, and
        /// OpenRGB-Fixed (its SPDX tags), openrazer-win and razer-life
        /// (each README's license section), the Linux iFeel driver (the
        /// header of ifeel.c) and the Linux kernel's hid-core.c (its SPDX
        /// tag). It names one as GPL-2.0-only, acer-predator-helios-rgb,
        /// which is what that project's SPDX tags say. That tag is no bare
        /// GPL-2.0.</para>
        ///
        /// <para>The About page names OpenRGB's license in its chip, and its
        /// description names none (#574).</para>
        /// </summary>
        [Fact]
        public void SolaarOpenRazerAndOpenRgbAreCreditedGplTwoOrLater()
        {
            string license = Text("LICENSE");
            const string licenseProjects = @"(openrazer/openrazer|pwr-Solaar/Solaar|CalcProgrammer1/OpenRGB),\s*GPL-2\.0";
            Assert.Empty(Regex.Matches(license, licenseProjects + "(?!-or-later)"));
            Assert.Equal(5, Regex.Matches(license, licenseProjects + "-or-later").Count);

            string readme = Text("README.md");
            const string readmeLinks = @"(openrazer/openrazer|pwr-Solaar/Solaar|CalcProgrammer1/OpenRGB)\) \(GPL-2\.0";
            Assert.Empty(Regex.Matches(readme, readmeLinks + "(?!-or-later)"));
            Assert.Equal(4, Regex.Matches(readme, readmeLinks + "-or-later").Count);
            Assert.Empty(Regex.Matches(readme, @"GPL-2\.0(?!-or-later) \((Solaar|OpenRazer|OpenRGB)"));
            Assert.Equal(2, Regex.Matches(readme, @"GPL-2\.0-or-later \((Solaar|OpenRazer)").Count);
            Assert.Single(Regex.Matches(readme, @"\*\*OpenRGB\*\* \(GPL-2\.0-or-later\)"));
            string row = readme.Split('\n').Single(l => l.StartsWith("| [OpenRGB](", StringComparison.Ordinal));
            Assert.EndsWith("| GPL-2.0-or-later |", row.TrimEnd('\r'));
            Assert.Equal("About_License_Gpl2OrLater", AboutChip("About_OpenRgb"));

            foreach (string locale in Locales)
            {
                string text = Text("PadForge.App", "Resources", "Strings", locale);
                foreach (var (key, count) in new[]
                         {
                             ("About_PeripheralOutputRefsDesc", 8),
                             ("About_AnalogKeyboardRefsDesc", 1),
                         })
                {
                    string value = ResxValue(text, key);
                    Assert.True(Regex.Matches(value, @"GPL-2\.0(?!-or-later|-only)").Count == 0,
                                $"{locale} {key} keeps a bare GPL-2.0: {value}");
                    Assert.True(Regex.Matches(value, @"GPL-2\.0-or-later").Count == count,
                                $"{locale} {key} should name GPL-2.0-or-later {count} times: {value}");
                }
                string openRgb = ResxValue(text, "About_OpenRgbDesc");
                Assert.True(!openRgb.Contains("GPL-2.0", StringComparison.Ordinal),
                            $"{locale} About_OpenRgbDesc names GPL-2.0, which its chip names: {openRgb}");
            }
        }

        /// <summary>
        /// Dolphin and new-lg4ff are credited GPL-2.0-or-later on every
        /// surface. LICENSE tagged each of them both ways, and README and
        /// the About page had the bare GPL-2.0 too. Dolphin's COPYING says
        /// most of its original source is GPLv2+ and sends the reader to each
        /// file's SPDX tag, and the files PadForge read are tagged
        /// GPL-2.0-or-later: IOWin.cpp, Speaker.cpp, WiimoteEmu.cpp and
        /// StickGate.cpp among them. new-lg4ff's hid-lg4ff.c, where its
        /// force feedback and LED reports are, carries the same tag. Its
        /// LICENSE file is the version 2 text, and only two headers of
        /// declarations are tagged GPL-2.0.
        ///
        /// <para>The About page names Dolphin's license in its chip, and its
        /// description names none (#574).</para>
        /// </summary>
        [Fact]
        public void DolphinAndNewLg4ffAreCreditedGplTwoOrLater()
        {
            string license = Text("LICENSE");
            const string licenseProjects = @"(dolphin-emu/dolphin|berarma/new-lg4ff),\s*GPL-2\.0";
            Assert.Empty(Regex.Matches(license, licenseProjects + "(?!-or-later)"));
            Assert.Equal(5, Regex.Matches(license, licenseProjects + "-or-later").Count);

            string readme = Text("README.md");
            const string readmeLinks = @"(dolphin-emu/dolphin|berarma/new-lg4ff)\) \(GPL-2\.0";
            Assert.Empty(Regex.Matches(readme, readmeLinks + "(?!-or-later)"));
            Assert.Equal(4, Regex.Matches(readme, readmeLinks + "-or-later").Count);
            Assert.Empty(Regex.Matches(readme, @"new-lg4ff \(GPL-2\.0(?!-or-later)"));
            Assert.Single(Regex.Matches(readme, @"new-lg4ff \(GPL-2\.0-or-later\)"));
            string row = readme.Split('\n').Single(l => l.StartsWith("| [Dolphin](", StringComparison.Ordinal));
            Assert.EndsWith("| GPL-2.0-or-later |", row.TrimEnd('\r'));

            foreach (string locale in Locales)
            {
                string text = Text("PadForge.App", "Resources", "Strings", locale);
                // The reference lists name each project with its tag after
                // it, with or without a space, in every locale.
                Assert.True(Regex.Matches(text, @"(Dolphin|new-lg4ff)\s?\(GPL-2\.0(?!-or-later)").Count == 0,
                            $"{locale} tags Dolphin or new-lg4ff with a bare GPL-2.0");
                Assert.True(Regex.Matches(text, @"Dolphin\s?\(GPL-2\.0-or-later\)").Count == 2,
                            $"{locale} should tag Dolphin GPL-2.0-or-later in two reference lists");
                Assert.True(Regex.Matches(text, @"new-lg4ff\s?\(GPL-2\.0-or-later\)").Count == 2,
                            $"{locale} should tag new-lg4ff GPL-2.0-or-later in two reference lists");

                string dolphin = ResxValue(text, "About_DolphinDesc");
                Assert.True(!dolphin.Contains("GPL-2.0", StringComparison.Ordinal),
                            $"{locale} About_DolphinDesc names GPL-2.0, which its chip names: {dolphin}");
            }
            Assert.Equal("About_License_Gpl2OrLater", AboutChip("About_Dolphin"));
        }

        /// <summary>
        /// The Handheld Companion credit names the technique PadForge took
        /// from it to release a hidden USB controller: cycle the hub port,
        /// 500 ms between cycles (HandheldCompanion IController.Hide runs
        /// HideHID then CyclePort, and ControllerManager sleeps 500 ms
        /// between cycles against re-enumeration collisions). The project
        /// credits every source it follows on README, LICENSE and the About
        /// page. The digits survive translation, so "500" anchors the check
        /// in every locale.
        /// </summary>
        [Fact]
        public void TheHandheldCompanionCreditNamesTheHubPortCycle()
        {
            // The credit quotes the interval PadForge runs on. A change to it
            // reddens this until the credit is rewritten on every surface.
            Assert.Equal(500, HiddenControllerRelease.PauseBetweenCyclesMs);

            foreach (string locale in Locales)
            {
                string value = ResxValue(Text("PadForge.App", "Resources", "Strings", locale), "About_HandheldCompanionDesc");
                Assert.True(value.Contains("500", StringComparison.Ordinal),
                            $"{locale}: About_HandheldCompanionDesc does not credit the hub port cycle: {value}");
            }

            var readme = Text("README.md").Split('\n');
            foreach (string start in new[] { "| [Handheld Companion](", "- **3D controller models**" })
            {
                string line = readme.Single(l => l.StartsWith(start, StringComparison.Ordinal));
                Assert.True(line.Contains("hub port cycle, 500 ms apart", StringComparison.Ordinal),
                            $"README '{start}' does not credit the hub port cycle");
            }

            string license = Regex.Replace(Text("LICENSE"), @"\s+", " ");
            int block = license.IndexOf("3D Controller Models Adapted from Handheld Companion", StringComparison.Ordinal);
            Assert.True(block >= 0, "LICENSE has no Handheld Companion block");
            int end = license.IndexOf("Licensed under CC BY-NC-SA 4.0", block, StringComparison.Ordinal);
            Assert.True(end > block, "the Handheld Companion block has no license line");
            Assert.Contains("hub port cycle, 500 ms apart", license.Substring(block, end - block));
        }

        /// <summary>
        /// The direct paths of #494 follow what other projects worked out
        /// about each device's reports, and say so beside the code. The
        /// project credits every source it follows on README, LICENSE and
        /// the About page, so each project those source files cite is on
        /// all three: in the README's table row and its license list, in
        /// the LICENSE entry, and in the About text of every locale.
        /// </summary>
        [Fact]
        public void TheDirectPathReferencesAreCredited()
        {
            // What the source cites a project as, and a name its credit
            // carries in every locale.
            var cited = new (string InSource, string Credit)[]
            {
                ("keyleds", "keyleds"), ("g810-led", "g810-led"), ("g810-led-resources", "g810-led-resources"),
                ("rigdeck", "rigdeck"), ("OpenGHub", "OpenGHub"), ("g915x-heatmap", "g915x-heatmap"),
                ("OpenRGB-Fixed", "OpenRGB-Fixed"), ("HaloDaemon-plugins", "HaloDaemon-plugins"),
                ("uncoil", "uncoil"), ("OpenSynapse", "OpenSynapse"), ("openrazer-win", "openrazer-win"),
                ("sanshoku", "sanshoku"), ("razer_test", "razer_test"), ("razer-life", "razer-life"),
                ("LibreHardwareMonitor", "LibreHardwareMonitor"), ("Aurora", "Aurora"),
                ("signal-plugins", "SignalRGB"), ("Aerox_5-LennardKittner", "Aerox_5"), ("apex-3-tkl", "apex-3-tkl"),
                ("ApexCtl", "ApexCtl"), ("SteelSeriesControl", "SteelSeriesControl"), ("rival310-re", "rival310-re"),
                ("Cricetus-Imperator", "Cricetus-Imperator"), ("wine", "Wine"),
                ("ArduinoHidForWindows", "ArduinoHidForWindows"), ("RP2040MacropadHidSample", "RP2040MacropadHidSample"),
                ("lampctl", "lampctl"), ("hid-rgb-ctl", "hid-rgb-ctl"), ("keybackcon", "keybackcon"),
                ("mistel-lights", "mistel-lights"), ("acer-predator-helios-rgb", "acer-predator-helios-rgb"),
                ("CaYaLight", "CaYaLight"), ("crosrgbkblight", "crosrgbkblight"), ("hp-omen-35l-rgb", "hp-omen-35l-rgb"),
                ("mac-dynamic-lighting", "mac-dynamic-lighting"), ("aura-loq", "aura-loq"),
                ("Apex3TklDynamicLighting", "Apex3TklDynamicLighting"),
                ("tactile-ifeel", "iFeel"), ("Logitech-iFeel-Vibration-POC", "Logitech-iFeel-Vibration-POC"),
                ("linux-input", "Linux"), ("LMouFrc", "MouseWare"),
            };

            // The claim the credits answer for is still in the source.
            string source = string.Concat(new[]
            {
                "HidppLightPlan.cs", "HidppUnits.cs", "RazerDirectProtocol.cs", "RazerDirectBackend.cs", "RazerGuard.cs",
                "SteelSeriesDirectProtocol.cs", "SteelSeriesDirectBackend.cs", "LampArrayProtocol.cs", "LampArrayBackend.cs",
                "TouchSenseProtocol.cs", "TouchSenseBackend.cs", "PeripheralLinker.cs", "PeripheralProductIds.cs",
            }.Select(file => Text("PadForge.App", "Common", "Input", "Peripherals", file)));
            foreach (var (inSource, _) in cited)
                Assert.True(source.Contains(inSource, StringComparison.Ordinal),
                    $"no direct-path source file cites {inSource} any more");

            var readme = Text("README.md").Split('\n').Select(l => l.TrimEnd('\r')).ToList();
            string row = readme.Single(l => l.StartsWith("| Peripheral output references |", StringComparison.Ordinal));
            string bullet = readme.Single(l => l.StartsWith("- **Peripheral output references**:", StringComparison.Ordinal));
            Assert.Contains("Documentation only, no code ships |", row);
            Assert.EndsWith("No code from them ships.", bullet);

            string license = Regex.Replace(Text("LICENSE"), @"\s+", " ");
            int start = license.IndexOf("Peripheral output references:", StringComparison.Ordinal);
            Assert.True(start >= 0, "LICENSE has no peripheral output references entry");
            int end = license.IndexOf("PortMidi (https://github.com/PortMidi/portmidi", start, StringComparison.Ordinal);
            Assert.True(end > start, "the peripheral output references entry is no longer followed by PortMidi's");
            string entry = license.Substring(start, end - start);

            var about = Locales.ToDictionary(locale => locale,
                locale => ResxValue(Text("PadForge.App", "Resources", "Strings", locale), "About_PeripheralOutputRefsDesc"));
            foreach (var (_, credit) in cited)
            {
                Assert.True(row.Contains(credit, StringComparison.Ordinal), $"README's table row does not credit {credit}");
                Assert.True(bullet.Contains(credit, StringComparison.Ordinal), $"README's license list does not credit {credit}");
                Assert.True(entry.Contains(credit, StringComparison.Ordinal), $"LICENSE does not credit {credit}");
                foreach (string locale in Locales)
                    Assert.True(about[locale].Contains(credit, StringComparison.Ordinal),
                        $"{locale}: the About text does not credit {credit}");
            }
        }

        /// <summary>
        /// The features of 5.3.0 follow other projects and say so beside the
        /// code: the reWASD and DS4Windows import, the On-Screen Keyboard and
        /// radial menus, Screen Color, the speaker of a wired DualShock 4, the
        /// battery on a DualSense's player lights, the Media Remote starter,
        /// the OBS address and the lighting families #557 added. Each project
        /// a source file cites is credited in the README's credit table, in
        /// its license list, in LICENSE and in the About text of every
        /// complete locale. Most of them had no credit on any surface before
        /// the 5.3.0 prep.
        /// </summary>
        [Fact]
        public void TheFiveThreeReferencesAreCredited()
        {
            // The source file, what it cites the project as, the name every
            // credit carries, and the About key that carries it.
            var cited = new (string[] File, string InSource, string Credit, string About)[]
            {
                (new[] { "Common", "Input", "OnScreenKeyboard", "OskModel.cs" }, "daisywheeljs", "daisywheeljs", "About_MenuRefsDesc"),
                (new[] { "Common", "Input", "InputManager.MenuRuntime.cs" }, "sc-controller", "sc-controller", "About_MenuRefsDesc"),
                (new[] { "Common", "ConfigImport", "RewasdTranslator.cs" }, "shunt's enum dump", "shunt", "About_ProfileImportRefsDesc"),
                (new[] { "Common", "ConfigImport", "RewasdTranslator.cs" }, "reWASD-Powertools", "reWASD-Powertools", "About_ProfileImportRefsDesc"),
                (new[] { "Common", "ConfigImport", "Ds4WindowsTranslator.cs" }, "Schmaldeo's fork", "Schmaldeo", "About_ProfileImportRefsDesc"),
                (new[] { "Common", "Input", "ScreenColor", "ScreenCaptureWorker.cs" }, "Prismatik", "Prismatik", "About_ScreenColorRefsDesc"),
                (new[] { "Common", "Input", "ScreenColor", "WindowsCaptureGrabber.cs" }, "Sunshine", "foundation-sunshine", "About_ScreenColorRefsDesc"),
                (new[] { "Common", "Input", "ScreenColor", "ScreenDxgi.cs" }, "ScreenCapture.NET", "ScreenCapture.NET", "About_ScreenColorRefsDesc"),
                (new[] { "Common", "Input", "ScreenColor", "ScreenColorMath.cs" }, "RazerAmbilight", "RazerAmbilight", "About_ScreenColorRefsDesc"),
                (new[] { "Common", "Input", "ScreenColor", "ScreenColorMath.cs" }, "OpenRGBEffectsPlugin", "OpenRGBEffectsPlugin", "About_ScreenColorRefsDesc"),
                (new[] { "Common", "Input", "ScreenColor", "ScreenCaptureWorker.cs" }, "Aurora's", "Aurora", "About_ScreenColorRefsDesc"),
                (new[] { "Common", "Input", "ScreenColor", "WindowsCaptureGrabber.cs" }, "AlienFX Tools", "alienfx-tools", "About_ScreenColorRefsDesc"),
                (new[] { "Common", "Input", "Ds4UsbAudio.cs" }, "NFSU2-SplitScreen", "NFSU2-SplitScreen", "About_Ds4HeadsetRefsDesc"),
                (new[] { "Common", "Input", "Ds4UsbAudio.cs" }, "Controllers Wiki", "Controllers Wiki", "About_Ds4HeadsetRefsDesc"),
                (new[] { "Common", "Input", "Ds5EffectSynthesizer.cs" }, "DualSense-Windows", "DualSense-Windows", "About_DualSenseWindows"),
                (new[] { "Services", "StarterProfileCatalog.cs" }, "Kodi", "Kodi", "About_Kodi"),
                (new[] { "Services", "WebControllerServer.cs" }, "obs-browser", "obs-browser", "About_ObsBrowser"),
                (new[] { "Common", "Input", "Peripherals", "HidFamilies", "QmkLighting.cs" }, "vial-qmk", "vial-qmk", "About_LightingRefsDesc"),
                (new[] { "Common", "Input", "Peripherals", "HidFamilies", "BlinkyTapeLighting.cs" }, "BlinkyTape_Arduino", "BlinkyTape_Arduino", "About_LightingRefsDesc"),
                (new[] { "Common", "Input", "Peripherals", "HidFamilies", "ThingMBlinkLighting.cs" }, "blink1-tool", "blink1-tool", "About_LightingRefsDesc"),
                (new[] { "Common", "Input", "Peripherals", "VendorPrograms.cs" }, "Blink1Control2", "Blink1Control2", "About_LightingRefsDesc"),
                (new[] { "Common", "Input", "Peripherals", "VendorPrograms.cs" }, "LuxaforOnAir", "LuxaforOnAir", "About_LightingRefsDesc"),
                (new[] { "Common", "Input", "Peripherals", "VendorPrograms.cs" }, "elgatosf/cli", "elgatosf/cli", "About_LightingRefsDesc"),
                (new[] { "Common", "Input", "Peripherals", "VendorPrograms.cs" }, "LenovoLegionToolkit-Team", "LenovoLegionToolkit-Team", "About_LightingRefsDesc"),
                (new[] { "Common", "Input", "Peripherals", "OpenRgbClient.cs" }, "OpenRGB.NET", "OpenRGB.NET", "About_LightingRefsDesc"),
                (new[] { "Common", "Input", "Peripherals", "PeripheralProductIds.cs" }, "CSDK_ChromaModSample", "CSDK_ChromaModSample", "About_LightingRefsDesc"),
                (new[] { "Common", "Input", "Peripherals", "ChromaLayouts.cs" }, "WyvrnChroma", "WyvrnChroma", "About_LightingRefsDesc"),
                (new[] { "Common", "Input", "Peripherals", "HidFamilies", "AnnePro2Lighting.cs" }, "Annemone", "Annemone", "About_LightingRefsDesc"),
                (new[] { "Common", "Input", "Peripherals", "HidFamilies", "PluginDevices.cs" }, "Nexus-Manager", "Nexus-Manager", "About_LightingRefsDesc"),
                (new[] { "Common", "Input", "Peripherals", "LenovoLegionLighting.cs" }, "legion-spectrum-control", "legion-spectrum-control", "About_LightingRefsDesc"),
            };

            string readme = Text("README.md").Replace("\r\n", "\n");
            int tableStart = readme.IndexOf("## Built on the work of these projects", StringComparison.Ordinal);
            int tableEnd = readme.IndexOf("## Donations", tableStart + 1, StringComparison.Ordinal);
            int listStart = readme.IndexOf("## License", StringComparison.Ordinal);
            int listEnd = readme.IndexOf("## Trademark", listStart + 1, StringComparison.Ordinal);
            // Positive control: the sections are still where they were.
            Assert.True(tableStart >= 0 && tableEnd > tableStart, "the README has no credit table section");
            Assert.True(listStart > tableEnd && listEnd > listStart, "the README has no license list section");
            string table = readme.Substring(tableStart, tableEnd - tableStart);
            string list = readme.Substring(listStart, listEnd - listStart);
            string license = Regex.Replace(Text("LICENSE"), @"\s+", " ");

            foreach (var (file, inSource, credit, about) in cited)
            {
                string source = Text(new[] { "PadForge.App" }.Concat(file).ToArray());
                Assert.True(source.Contains(inSource, StringComparison.Ordinal), $"{file[^1]} no longer cites {inSource}");
                Assert.True(table.Contains(credit, StringComparison.Ordinal), $"README's credit table does not credit {credit}");
                Assert.True(list.Contains(credit, StringComparison.Ordinal), $"README's license list does not credit {credit}");
                Assert.True(license.Contains(credit, StringComparison.Ordinal), $"LICENSE does not credit {credit}");
                foreach (string locale in Locales)
                {
                    string value = ResxValue(Text("PadForge.App", "Resources", "Strings", locale), about);
                    Assert.True(value.Contains(credit, StringComparison.Ordinal), $"{locale}: {about} does not credit {credit}");
                }
            }
        }

        /// <summary>
        /// The legacy MIDI backend says whose WinMM sequences it follows:
        /// RtMidi, PortMidi and NAudio (the MidiBackendLegacy summary). The
        /// project credits every source it follows on README, LICENSE and the
        /// About page. RtMidi and PortMidi were on none of them, and NAudio's
        /// credit named WASAPI capture alone.
        ///
        /// <para>The sequences were read in RtMidi.cpp (MidiInWinMM and
        /// MidiOutWinMM), PortMidi's pm_win/pmwinmm.c, and NAudio.Midi's
        /// MidiInterop.cs, MidiIn.cs and MidiOut.cs. PadForge's backend is
        /// its own C# and ports none of them. PortMidi's license.txt is the
        /// MIT text, copyright Ross Bencina and Phil Burk, and Roger B.
        /// Dannenberg. RtMidi's LICENSE is the MIT text with a request, not
        /// binding, to send changes upstream, copyright Gary P. Scavone.
        /// PadForge ships neither, and of NAudio it ships NAudio.Core and
        /// NAudio.Wasapi, not NAudio.Midi.</para>
        /// </summary>
        [Fact]
        public void TheLegacyMidiReferencesAreCredited()
        {
            // The claim the credits answer for is still in the source.
            string backend = Text("PadForge.App", "Common", "Input", "MidiBackendLegacy.cs");
            foreach (string followed in new[] { "RtMidi", "PortMidi", "NAudio" })
                Assert.Contains(followed, backend);

            var readme = Text("README.md").Split('\n').Select(l => l.TrimEnd('\r')).ToList();
            int row = readme.FindIndex(l => l.StartsWith(
                "| [PortMidi](https://github.com/PortMidi/portmidi) and [RtMidi](https://github.com/thestk/rtmidi) |", StringComparison.Ordinal));
            Assert.True(row >= 0, "README's table has no PortMidi and RtMidi row");
            Assert.Contains("legacy MIDI API", readme[row]);
            Assert.Contains("Documentation only, no code ships", readme[row]);
            Assert.EndsWith("| MIT (PortMidi), MIT-style RtMidi license |", readme[row]);
            Assert.StartsWith("| Peripheral output references |", readme[row - 1]);
            Assert.StartsWith("| Profile import references |", readme[row + 1]);
            Assert.StartsWith("| [protobuf-net](", readme[row + 2]);

            int bullet = readme.FindIndex(l => l.StartsWith("- **PortMidi**", StringComparison.Ordinal));
            Assert.True(bullet >= 0, "README's license list has no PortMidi and RtMidi entry");
            foreach (string part in new[]
                     {
                         "**RtMidi**", "Ross Bencina and Phil Burk", "Roger B. Dannenberg", "Gary P. Scavone",
                         "legacy MIDI API", "No code from them ships.",
                     })
                Assert.Contains(part, readme[bullet]);
            Assert.StartsWith("- **Peripheral output references**", readme[bullet - 1]);
            Assert.StartsWith("- **Profile import references**", readme[bullet + 1]);
            Assert.StartsWith("- **protobuf-net**", readme[bullet + 2]);

            foreach (string start in new[] { "| [NAudio](", "- **NAudio**" })
            {
                string line = readme.Single(l => l.StartsWith(start, StringComparison.Ordinal));
                Assert.True(line.Contains("NAudio.Midi", StringComparison.Ordinal) && line.Contains("legacy MIDI API", StringComparison.Ordinal),
                            $"README '{start}' does not credit NAudio.Midi for the legacy MIDI API");
            }

            string license = Regex.Replace(Text("LICENSE"), @"\s+", " ");
            int item = license.IndexOf("PortMidi (https://github.com/PortMidi/portmidi, MIT,", StringComparison.Ordinal);
            Assert.True(item >= 0, "LICENSE has no PortMidi reference");
            int next = license.IndexOf("Racing telemetry references:", item, StringComparison.Ordinal);
            Assert.True(next > item, "the PortMidi reference is not ahead of Racing telemetry references");
            Assert.True(license.LastIndexOf("Peripheral output references:", item, StringComparison.Ordinal) >= 0,
                        "the PortMidi reference is not after Peripheral output references");
            string reference = license.Substring(item, next - item);
            foreach (string part in new[]
                     {
                         "Ross Bencina", "Phil Burk", "Roger B. Dannenberg",
                         "RtMidi (https://github.com/thestk/rtmidi, MIT-style RtMidi license,", "Gary P. Scavone",
                         "legacy MIDI API",
                     })
                Assert.Contains(part, reference);

            int naudio = license.IndexOf("NAudio https://github.com/naudio/NAudio", StringComparison.Ordinal);
            Assert.True(naudio >= 0, "LICENSE has no NAudio block");
            int naudioEnd = license.IndexOf("Licensed under MIT License", naudio, StringComparison.Ordinal);
            string naudioBlock = license.Substring(naudio, naudioEnd - naudio);
            Assert.Contains("NAudio.Midi", naudioBlock);
            Assert.Contains("legacy MIDI API", naudioBlock);

            foreach (string locale in Locales)
            {
                string text = Text("PadForge.App", "Resources", "Strings", locale);
                Assert.Equal("PortMidi / RtMidi", ResxValue(text, "About_PortMidiRtMidi"));
                string credit = ResxValue(text, "About_PortMidiRtMidiDesc");
                foreach (string part in new[]
                         {
                             "WinMM", "MIDI", "PortMidi", "RtMidi", "MIT",
                             "Ross Bencina", "Phil Burk", "Roger B. Dannenberg", "Gary P. Scavone",
                         })
                    Assert.True(credit.Contains(part, StringComparison.Ordinal), $"{locale}: About_PortMidiRtMidiDesc lacks '{part}': {credit}");
                string wasapi = ResxValue(text, "About_NAudioDesc");
                foreach (string part in new[] { "WASAPI", "NAudio.Midi", "WinMM", "MIDI", "Mark Heath" })
                    Assert.True(wasapi.Contains(part, StringComparison.Ordinal), $"{locale}: About_NAudioDesc lacks '{part}': {wasapi}");
            }
            // NAudio's license is in its chip on the About page (#574).
            Assert.Equal("About_License_Mit", AboutChip("About_NAudio"));
        }

        /// <summary>
        /// The OpenLogi credit names the repository the code cites, with the
        /// license of each file read. HidppHaptics and HidppBackend cite
        /// OpenLogi's haptic_feedback.rs and transport.rs, and the three
        /// surfaces credited its docs page alone, with no license. Read in
        /// the clone: crates/openlogi-hidpp/Cargo.toml says 0BSD and that
        /// crate's LICENSE is copyright Lukas Schulte Pelkum, the workspace
        /// Cargo.toml says MIT OR Apache-2.0, and LICENSE-MIT and
        /// LICENSE-APACHE are copyright AprilNEA. The docs page was the first
        /// source the haptic work cited, it publishes no license, and it
        /// stays as a second source.
        /// </summary>
        [Fact]
        public void TheOpenLogiCreditNamesTheRepositoryAndItsLicenses()
        {
            // The citations the credit answers for are still in the source.
            Assert.Contains("OpenLogi (haptic_feedback.rs", Text("PadForge.App", "Common", "Input", "HidppHaptics.cs"));
            Assert.Contains("OpenLogi openlogi-hid transport.rs",
                Text("PadForge.App", "Common", "Input", "Peripherals", "HidppBackend.cs"));

            const string repository = "https://github.com/AprilNEA/OpenLogi";
            const string page = "https://openlogi.org/hidpp/features/x19b0-haptic-feedback";
            const string grant = "0BSD for its openlogi-hidpp crate, Lukas Schulte Pelkum, and MIT OR Apache-2.0 for the rest, AprilNEA";

            string license = Regex.Replace(Text("LICENSE"), @"\s+", " ");
            int item = license.IndexOf("Peripheral output references:", StringComparison.Ordinal);
            Assert.True(item >= 0, "LICENSE has no Peripheral output references item");
            int next = license.IndexOf("PortMidi (", item, StringComparison.Ordinal);
            Assert.True(next > item, "the Peripheral output references item has no end");
            string reference = license.Substring(item, next - item);
            Assert.Contains($"OpenLogi ({repository}, {grant})", reference);
            Assert.Contains($"the OpenLogi docs ({page}, no license published)", reference);

            var readme = ReadmeLines();
            string row = readme.Single(l => l.StartsWith("| Peripheral output references |", StringComparison.Ordinal));
            Assert.Contains($"[OpenLogi]({repository})", row);
            Assert.Contains($"[OpenLogi docs]({page})", row);
            Assert.Contains("0BSD and MIT OR Apache-2.0 (OpenLogi)", row);
            var unlicensed = Regex.Match(row, @"none published \(([^)]*)\)");
            Assert.True(unlicensed.Success, "the row has no none published list");
            var unlicensedNames = unlicensed.Groups[1].Value.Split(", ");
            Assert.Contains("OpenLogi docs", unlicensedNames);
            Assert.DoesNotContain("OpenLogi", unlicensedNames);

            string bullet = readme.Single(l => l.StartsWith("- **Peripheral output references**", StringComparison.Ordinal));
            Assert.Contains($"[OpenLogi]({repository}) ({grant})", bullet);
            Assert.Contains($"[OpenLogi docs]({page}) (no license published)", bullet);

            foreach (string locale in Locales)
            {
                string value = ResxValue(Text("PadForge.App", "Resources", "Strings", locale), "About_PeripheralOutputRefsDesc");
                Assert.True(Regex.IsMatch(value, @"OpenLogi\s?\(0BSD[^)]*MIT OR Apache-2\.0\)"),
                            $"{locale}: the About credit does not tag the OpenLogi repository: {value}");
                Assert.True(Regex.Matches(value, "OpenLogi").Count == 2,
                            $"{locale}: the About credit should name OpenLogi and its docs: {value}");
            }
        }

        /// <summary>
        /// A project whose files grant a later version of its license is
        /// tagged or-later on every surface. The tag is the grant every file
        /// PadForge read shares, so it says or-later where each of them does,
        /// or where the project does. Each grant was read in the clone
        /// beside the repository:
        ///
        /// <para>GPL-2.0-or-later: xone (21 SPDX tags) and xow (19 file
        /// headers), AutoHotkey (source/hook.cpp and keyboard_mouse.cpp), and
        /// the fortius_1942 driver posted to linux-usb (line 4 of the patch),
        /// which the credit had called the Linux kernel. GPL-3.0-or-later:
        /// spacenavd (README and 36 of 40 files), Eden (README, udp_client,
        /// udp_protocol, ringcon), libhmk and hmkconf (file headers),
        /// AntiMicroX (joygradientbutton.cpp), zeemoted (zeemoted.c),
        /// dji-firmware-tools (comm_mkdupc.py, comm_dat2pcap.py), DS4Windows
        /// by Ryochan7 (DS4Device.cs, DualSenseDevice.cs, Mapping.cs),
        /// InputPlumber (Cargo.toml, README), chmfp (13 file headers), the
        /// VIIPER fork (README) and Special K (bluetooth.cpp,
        /// xinput_core.cpp, hid.cpp). LGPL-2.1-or-later: android-bluez-ime
        /// (its reader headers, no license file). LGPL-3.0-or-later: x360ce
        /// (x360ce/COPYING.txt) and NintendoExtensionCtrl (README and 48
        /// headers).</para>
        ///
        /// <para>The Linux kernel is tagged by the files each credit read.
        /// hid-sony.c, hid-playstation.c, hid-steam.c, hid-pidff.c and wmi.c
        /// behind its own entry, hid-icade.c and hid-ids.h behind the iCade
        /// driver, xpad.c behind the Xbox drivers and hid-core.c behind the
        /// peripheral output references are GPL-2.0-or-later,
        /// as the I-Force and psxpad-spi files already credited are. Three
        /// credits keep GPL-2.0, each for one file that grants no later
        /// version: pxrc.c among the HID devices, hid-udraw-ps3.c among the
        /// instruments and fsia6b.c among the serial drivers.</para>
        ///
        /// <para>Two grants were read on the project's own pages, where no
        /// clone is on disk. The GCC runtime libraries the Vosk package
        /// brings are GPL-3.0-or-later with the GCC Runtime Library
        /// Exception 3.1: libgcc/libgcc2.c and libstdc++-v3/libsupc++/
        /// eh_throw.cc say so in their headers, and the copy of GCC's
        /// license files that Git for Windows installs says the same.
        /// Thumbstick Deadzones is two works: its article, which PadForge
        /// cites for the deadzone shapes, is under the GNU Free
        /// Documentation License 1.3 or later (README.md, the License
        /// section), and its code is GPL-3.0-or-later (main.py), both
        /// copyright Carlos Pérez Ramil.</para>
        /// </summary>
        [Fact]
        public void TheLaterVersionGrantsAreCreditedOnEverySurface()
        {
            string license = Text("LICENSE");
            foreach (var (url, bare, count) in new[]
                     {
                         ("AntiMicroX/antimicrox", "GPL-3.0", 1), ("AutoHotkey/AutoHotkey", "GPL-2.0", 1),
                         ("peppapighs/libhmk", "GPL-3.0", 1), ("peppapighs/hmkconf", "GPL-3.0", 1),
                         ("kenkendk/android-bluez-ime", "LGPL-2.1", 1), ("dafugg/zeemoted", "GPL-3.0", 1),
                         ("o-gs/dji-firmware-tools", "GPL-3.0", 1), ("eden-emu/eden", "GPL-3.0", 2),
                         ("ShadowBlip/InputPlumber", "GPL-3.0", 1), ("dmadison/NintendoExtensionCtrl", "LGPL-3.0", 1),
                         ("FreeSpacenav/spacenavd", "GPL-3.0", 2), ("SpecialKO/SpecialK", "GPL-3.0", 1),
                         ("sourceforge.net/projects/chmfp/", "GPL-3.0", 1), ("hbashton/VIIPER", "GPL-3.0", 1),
                         ("medusalix/xow", "GPL-2.0", 1), ("linux-usb/msg15908.html", "GPL-2.0", 1),
                         ("torvalds/linux", "GPL-2.0", 6),
                     })
            {
                string tagged = Regex.Escape(url) + @",\s*" + Regex.Escape(bare);
                int kept = url == "torvalds/linux" ? 3 : 0;
                Assert.True(Regex.Matches(license, tagged + "(?!-or-later)").Count == kept,
                            $"LICENSE should tag {url} with a bare {bare} {kept} times");
                Assert.True(Regex.Matches(license, tagged + "-or-later").Count == count,
                            $"LICENSE should tag {url} {bare}-or-later {count} times");
            }
            Assert.Single(Regex.Matches(license, @"DS4Windows by Ryochan7 \(GPL-3\.0-or-later,"));
            Assert.Single(Regex.Matches(license, @"GNU Lesser General Public License v3\.0 or later\s+\(full text below\)"));
            string flatLicense = Regex.Replace(license, @"\s+", " ");
            Assert.Contains("GNU General Public License v3 or later with the GCC Runtime Library Exception 3.1, both published by "
                + "the Free Software Foundation at https://www.gnu.org/licenses/.", flatLicense);
            Assert.Contains("Minimuino/thumbstick-deadzones, GFDL-1.3-or-later for its article and GPL-3.0-or-later for its code, "
                + "Carlos Pérez Ramil)", flatLicense);

            var readme = ReadmeLines();
            string readmeText = string.Join("\n", readme);
            foreach (var (url, bare, count, kept) in new[]
                     {
                         ("peppapighs/hmkconf", "GPL-3.0", 1, 0), ("kenkendk/android-bluez-ime", "LGPL-2.1", 1, 0),
                         ("dafugg/zeemoted", "GPL-3.0", 1, 0), ("o-gs/dji-firmware-tools", "GPL-3.0", 1, 0),
                         ("eden-emu/eden", "GPL-3.0", 1, 0), ("dmadison/NintendoExtensionCtrl", "LGPL-3.0", 1, 0),
                         ("FreeSpacenav/spacenavd", "GPL-3.0", 2, 0), ("sourceforge.net/projects/chmfp/", "GPL-3.0", 1, 0),
                         ("medusalix/xow", "GPL-2.0", 1, 0), ("Ryochan7/DS4Windows", "GPL-3.0", 1, 0),
                         ("torvalds/linux", "GPL-2.0", 4, 3),
                     })
            {
                string tagged = Regex.Escape(url) + @"\) \(" + Regex.Escape(bare);
                Assert.True(Regex.Matches(readmeText, tagged + "(?!-or-later)").Count == kept,
                            $"README should tag {url} with a bare {bare} {kept} times");
                Assert.True(Regex.Matches(readmeText, tagged + "-or-later").Count == count,
                            $"README should tag {url} {bare}-or-later {count} times");
            }
            foreach (string phrase in new[]
                     {
                         "**AntiMicroX** (GPL-3.0-or-later)", "**AutoHotkey** (GPL-2.0-or-later)", "**Eden** (GPL-3.0-or-later)",
                         "**InputPlumber** (GPL-3.0-or-later)", "**Linux kernel** drivers (GPL-2.0-or-later)",
                         "**Special K** (GPL-3.0-or-later)", "**VIIPER (hbashton fork)** is licensed under the GPL-3.0-or-later.",
                         "[x360ce](https://github.com/x360ce/x360ce) (LGPL-3.0-or-later)",
                         "linux-usb/msg15908.html), a Linux driver posted to the linux-usb list (GPL-2.0-or-later)",
                         "(GPL-3.0-or-later with the GCC Runtime Library Exception 3.1, Copyright (C) Free Software Foundation, Inc.,",
                         "**Thumbstick Deadzones** (GFDL-1.3-or-later for its article and GPL-3.0-or-later for its code, Copyright (C) 2017 Carlos Pérez Ramil,",
                     })
                Assert.True(readmeText.Contains(phrase, StringComparison.Ordinal), $"README lacks '{phrase}'");
            foreach (var (start, cell) in new[]
                     {
                         ("| [AntiMicroX](", "| GPL-3.0-or-later |"), ("| [AutoHotkey](", "| GPL-2.0-or-later |"),
                         ("| [DS4Windows](", "| GPL-3.0-or-later |"), ("| [Eden](", "| GPL-3.0-or-later |"),
                         ("| [InputPlumber](", "| GPL-3.0-or-later |"), ("| [Linux kernel](", "| GPL-2.0-or-later |"),
                         ("| [Special K](", "| GPL-3.0-or-later |"), ("| [VIIPER (hbashton fork)](", "| GPL-3.0-or-later |"),
                         ("| [x360ce](", "| LGPL-3.0-or-later |"), ("| [xone](", "| GPL-2.0-or-later |"),
                         ("| Analog keyboard references |", "| MIT, MPL-2.0, GPL-3.0-or-later (libhmk, hmkconf), GPL-2.0-or-later (OpenRazer), none published (razer-analog-keyboard, Sainan's capture gists) |"),
                         ("| Bluetooth RFCOMM and iCade references |", "| GPL-2.0-or-later, GPL-3.0-or-later, LGPL-2.1-or-later, MIT, none published (flatlib.jp, zeemouse) |"),
                         ("| SpaceMouse references |", "| CC BY-NC-SA 4.0, GPL-3.0-or-later, MIT |"),
                         ("| Specialty USB device references |", "| Apache-2.0, GPL-2.0, GPL-2.0-or-later, GPL-3.0, GPL-3.0-or-later, MIT, none published (ttyT1941 wiki) |"),
                         ("| [Thumbstick Deadzones](", "| GFDL-1.3-or-later (article), GPL-3.0-or-later (code) |"),
                     })
            {
                string row = readme.Single(l => l.StartsWith(start, StringComparison.Ordinal));
                Assert.True(row.EndsWith(cell, StringComparison.Ordinal), $"README '{start}' should end with '{cell}'");
            }
            foreach (var (start, part) in new[]
                     {
                         ("| DJI remote references |", "| Apache-2.0, GPL-3.0, GPL-3.0-or-later, LGPL-2.1, MIT, none published ("),
                         ("| Instrument and Wii extension references |", ", GPL-3.0, LGPL-3.0-or-later, MIT, none published ("),
                         ("| Xbox controller references |", "| Apache-2.0, GPL-2.0-or-later, GPL-3.0, GPL-3.0-or-later, LGPL-2.0-or-later, LGPL-3.0, MIT, none published ("),
                         ("| [MinGW-w64 runtime](", "| GPL-3.0-or-later with the GCC Runtime Library Exception 3.1, MIT and BSD 3-Clause (winpthreads),"),
                     })
            {
                string row = readme.Single(l => l.StartsWith(start, StringComparison.Ordinal));
                Assert.True(row.Contains(part, StringComparison.Ordinal), $"README '{start}' should hold '{part}'");
            }

            // A credit of one project states its tag once, in its chip on
            // the About page (#574), and its description names none.
            var single = new[]
            {
                ("About_AntiMicroX", "GPL-3.0"), ("About_AutoHotkey", "GPL-2.0"), ("About_Ds4Windows", "GPL-3.0"),
                ("About_Eden", "GPL-3.0"), ("About_InputPlumber", "GPL-3.0"), ("About_LinuxKernel", "GPL-2.0"),
                ("About_SpecialK", "GPL-3.0"), ("About_ViiperHbashton", "GPL-3.0"), ("About_XoneXow", "GPL-2.0"),
            };
            foreach (var (name, bare) in single)
                Assert.Equal(bare == "GPL-3.0" ? "About_License_Gpl3OrLater" : "About_License_Gpl2OrLater", AboutChip(name));

            foreach (string locale in Locales)
            {
                string text = Text("PadForge.App", "Resources", "Strings", locale);
                foreach (var (name, bare) in single)
                {
                    string value = ResxValue(text, name + "Desc");
                    Assert.True(!value.Contains(bare, StringComparison.Ordinal),
                                $"{locale} {name}Desc names {bare}, which its chip names: {value}");
                }
                // A credit of projects under different licenses states each
                // tag in its description, once.
                foreach (var (key, bare) in new[]
                         {
                             ("About_MingwRuntimeDesc", "GPL-3.0"), ("About_ThumbstickDeadzonesDesc", "GPL-3.0"),
                         })
                {
                    string value = ResxValue(text, key);
                    Assert.True(Regex.Matches(value, Regex.Escape(bare) + "(?!-or-later)").Count == 0,
                                $"{locale} {key} keeps a bare {bare}: {value}");
                    Assert.True(Regex.Matches(value, Regex.Escape(bare) + "-or-later").Count == 1,
                                $"{locale} {key} should name {bare}-or-later once: {value}");
                }
                // A reference list names each project with its tag after it,
                // with or without a space, in every locale.
                foreach (var (key, name, bare) in new[]
                         {
                             ("About_AnalogKeyboardRefsDesc", "hmkconf", "GPL-3.0"),
                             ("About_RfcommIcadeRefsDesc", "android-bluez-ime", "LGPL-2.1"), ("About_RfcommIcadeRefsDesc", "Linux", "GPL-2.0"),
                             ("About_RfcommIcadeRefsDesc", "zeemoted", "GPL-3.0"), ("About_DjiRemoteRefsDesc", "dji-firmware-tools", "GPL-3.0"),
                             ("About_InstrumentRefsDesc", "NintendoExtensionCtrl", "LGPL-3.0"), ("About_SpaceMouseRefsDesc", "spacenavd", "GPL-3.0"),
                             ("About_SpecialtyUsbRefsDesc", "chmfp", "GPL-3.0"), ("About_SpecialtyUsbRefsDesc", "fortius_1942", "GPL-2.0"),
                             ("About_XboxControllerRefsDesc", "Linux", "GPL-2.0"), ("About_LicenseText", "x360ce", "LGPL-3.0"),
                         })
                {
                    string value = ResxValue(text, key);
                    string tagged = Regex.Escape(name) + @"\s?[(（]" + Regex.Escape(bare);
                    Assert.True(Regex.Matches(value, tagged + "(?!-or-later)").Count == 0,
                                $"{locale} {key} tags {name} with a bare {bare}: {value}");
                    Assert.True(Regex.Matches(value, tagged + "-or-later").Count == 1,
                                $"{locale} {key} should tag {name} {bare}-or-later: {value}");
                }
                string specialty = ResxValue(text, "About_SpecialtyUsbRefsDesc");
                Assert.True(Regex.Matches(specialty, @"Linux\s?\(").Count == 0,
                            $"{locale} About_SpecialtyUsbRefsDesc still credits the Linux kernel: {specialty}");
                string deadzones = ResxValue(text, "About_ThumbstickDeadzonesDesc");
                Assert.True(deadzones.Contains("GFDL-1.3-or-later", StringComparison.Ordinal),
                            $"{locale} About_ThumbstickDeadzonesDesc does not name the article's license: {deadzones}");
            }
        }

        /// <summary>
        /// ttyT1941 is credited beside the other sources of the SDL fork's
        /// Tacx driver, which ships in SDL3.dll. The driver's stop frame is
        /// the one its script sends (ttyT1941.py, the Stop command), and
        /// its steering value for no steering unit and the head units'
        /// processor come from its wiki, as the fork's
        /// SDL_hidapi_tacx_proto.h and SDL_hidapi_vendorusb.c say. Read on
        /// the project's page, with no clone on disk: the repository is
        /// totalreverse's, its LICENSE is the GPL version 3 text with no
        /// later-version statement, and the wiki states no license.
        /// </summary>
        [Fact]
        public void TtyT1941IsCreditedWithTheOtherTacxSources()
        {
            const string location = "https://github.com/totalreverse/ttyT1941";
            string license = Regex.Replace(Text("LICENSE"), @"\s+", " ");
            int item = license.IndexOf("Specialty USB device references:", StringComparison.Ordinal);
            Assert.True(item >= 0, "LICENSE has no Specialty USB device references item");
            int next = license.IndexOf("Steam Controller protocol references:", item, StringComparison.Ordinal);
            Assert.True(next > item, "the Specialty USB device references item has no end");
            string reference = license.Substring(item, next - item);
            Assert.Contains($"ttyT1941 by totalreverse ({location}, GPL-3.0, with a wiki that publishes no license)", reference);
            // The neighbors it is credited beside are still there.
            foreach (string neighbor in new[] { "fortius_1942", "FortiusANT", "antifier" })
                Assert.Contains(neighbor, reference);

            var readme = ReadmeLines();
            string row = readme.Single(l => l.StartsWith("| Specialty USB device references |", StringComparison.Ordinal));
            Assert.Contains($"[ttyT1941]({location}) by totalreverse", row);
            Assert.Contains("none published (ttyT1941 wiki)", row);
            string bullet = readme.Single(l => l.StartsWith("- **Specialty USB device references**", StringComparison.Ordinal));
            Assert.Contains($"[ttyT1941]({location}) by totalreverse (GPL-3.0, with a wiki that publishes no license)", bullet);

            foreach (string locale in Locales)
            {
                string value = ResxValue(Text("PadForge.App", "Resources", "Strings", locale), "About_SpecialtyUsbRefsDesc");
                Assert.True(Regex.Matches(value, @"t19xx_usb\s?\(GPL-3\.0\)(, |、|، )ttyT1941\s?\(GPL-3\.0\)").Count == 1,
                            $"{locale}: the About credit does not name ttyT1941 after t19xx_usb: {value}");
            }
        }

        /// <summary>
        /// chiaki-ng is credited on README and the About page as it is in
        /// LICENSE, which alone named it: the PS5 haptic PCM format, signed
        /// 16-bit at 3 kHz. Its gui/src/streamsession.cpp, where that stream
        /// is played, is tagged LicenseRef-AGPL-3.0-only-OpenSSL, so the tag
        /// stays AGPL-3.0, which the About page shows in the credit's chip
        /// (#574). The digits survive translation and anchor the check in
        /// every locale.
        /// </summary>
        [Fact]
        public void ChiakiNgIsCreditedOnEverySurface()
        {
            Assert.Contains("chiaki-ng (https://github.com/streetpea/chiaki-ng, AGPL-3.0): the PS5 haptic PCM format",
                Regex.Replace(Text("LICENSE"), @"\s+", " "));

            var readme = ReadmeLines();
            int row = readme.FindIndex(l => l.StartsWith("| [chiaki-ng](https://github.com/streetpea/chiaki-ng) |", StringComparison.Ordinal));
            Assert.True(row >= 0, "README's table has no chiaki-ng row");
            Assert.Contains("3 kHz", readme[row]);
            Assert.EndsWith("| AGPL-3.0 |", readme[row]);
            Assert.StartsWith("| [cemuhook-protocol](", readme[row - 1]);
            Assert.StartsWith("| [Colore](", readme[row + 1]);

            int bullet = readme.FindIndex(l => l.StartsWith("- **chiaki-ng** (AGPL-3.0)", StringComparison.Ordinal));
            Assert.True(bullet >= 0, "README's license list has no chiaki-ng entry");
            Assert.Contains("3 kHz", readme[bullet]);
            Assert.StartsWith("- **cemuhook-protocol**", readme[bullet - 1]);
            Assert.StartsWith("- **CommunityToolkit.Mvvm**", readme[bullet + 1]);

            foreach (string locale in Locales)
            {
                string text = Text("PadForge.App", "Resources", "Strings", locale);
                Assert.Equal("chiaki-ng", ResxValue(text, "About_ChiakiNg"));
                string credit = ResxValue(text, "About_ChiakiNgDesc");
                foreach (string part in new[] { "PS5", "PCM", "16", "3 kHz", "8" })
                    Assert.True(credit.Contains(part, StringComparison.Ordinal), $"{locale}: About_ChiakiNgDesc lacks '{part}': {credit}");
            }
            Assert.Equal("About_License_Agpl3", AboutChip("About_ChiakiNg"));
        }

        /// <summary>
        /// LICENSE carries the notices the shipped libraries' licenses call
        /// for, and README and the About page name what LICENSE names.
        ///
        /// <para>Concentus (its package LICENSE) has nine named holders and
        /// a third condition that names the Internet Society, IETF and IETF
        /// Trust, so its notice stands apart from the BSD block. The MIT
        /// notice is reproduced once, in a block that carries the copyright
        /// line of each MIT library that ships, as the BSD block does. The
        /// entries in the body stay the short entries they were and hold no
        /// license text: NAudio (license.txt), Nefarius (LICENSE), the
        /// Windows MIDI Services projection (LICENSE), WPF UI (LICENSE.md)
        /// and xbledctl (LICENSE) had no line anywhere. Wpf.Ui.dll carries
        /// the components its ThirdPartyNotices.txt lists, which have an
        /// entry of their own. nefconc.exe links Argh! (BSD 3-Clause),
        /// colorwin, easyloggingpp, Microsoft Detours, neflib and the
        /// Windows Implementation Libraries (MIT) and scope_guard
        /// (Unlicense), by its sources at tag v1.20.0, each license read on
        /// the library's own page. Its README credits WinReg too, which
        /// those sources do not include. OpenBLAS 0.3.20 is built on
        /// GotoBLAS2, whose notice (GotoBLAS_00License.txt) stands in
        /// memory.c, a file both libvosk.dll builds hold. winpthreads'
        /// COPYING has a second notice, of Lockless Inc. WinRT.Runtime.dll
        /// (C#/WinRT, MIT) and Microsoft.Windows.SDK.NET.dll (the Windows
        /// SDK license terms its nuspec points to) ship beside the .NET
        /// runtime.</para>
        ///
        /// <para>Held to what was read: the holders of SDL3 (the fork's
        /// LICENSE.txt), SDL_GameControllerDB (LICENSE) and libusb (AUTHORS),
        /// DsHidMini's years (LICENSE), the Interhaptics files' own version
        /// information, which names Go Touch VR SAS, and SteamKit2, whose
        /// license.txt grants version 2.1 or later while its package
        /// metadata says LGPL-2.1-only. OpenXInput's upstream LICENSE file
        /// is a trademark disclaimer, and its devobj stub, which PadForge
        /// does not ship, is GPL-3.0-or-later.</para>
        /// </summary>
        [Fact]
        public void TheShippedLibrariesCarryTheirNotices()
        {
            string raw = Text("LICENSE");
            string Flat(string s) => Regex.Replace(s, @"\s+", " ");

            string concentus = Flat(LicenseBlock(raw, "Concentus notice ("));
            foreach (string holder in new[]
                     {
                         "Skype Limited", "Xiph.Org Foundation", "CSIRO", "Microsoft Corporation", "Jean-Marc Valin",
                         "Gregory Maxwell", "Mark Borgerding", "Timothy B. Terriberry", "Logan Stromberg",
                     })
                Assert.Contains(holder, concentus);
            Assert.Contains("Neither the name of Internet Society, IETF or IETF Trust", concentus);
            Assert.Contains("Redistributions in binary form must reproduce the above copyright notice", concentus);

            string mit = LicenseBlock(raw, "MIT License (");
            Assert.Contains("Permission is hereby granted, free of charge, to any person obtaining a copy", Flat(mit));
            Assert.Contains("The above copyright notice and this permission notice shall be included in all", Flat(mit));
            var mitLines = mit.Split('\n').Select(l => l.TrimEnd('\r'))
                .Where(l => l.StartsWith("Copyright ", StringComparison.Ordinal)).ToList();
            // Positive control: the block lists its works.
            Assert.True(mitLines.Count >= 15, $"the MIT block lists only {mitLines.Count} copyright lines");
            foreach (string line in new[]
                     {
                         "Copyright 2020 Mark Heath", "Copyright (c) 2022 Benjamin Höglinger-Stelzer", "Copyright (c) Microsoft Corporation.",
                         "Copyright (c) 2021-2025 Leszek Pomianowski and WPF UI Contributors.", "Copyright (c) 2019 S. Bäumlisberger",
                         "Copyright (c) 2020 Microsoft Corporation", "Copyright (c) 2011 mingw-w64 project",
                         "Copyright (c) 2024 Benjamin Höglinger-Stelzer", "Copyright (c) 2021 Oleg Stepanischev",
                         "Copyright (c) 2016 Jeff Rebacz", "Copyright (c) 2012-present @abumq (Majid Q.)",
                         "Copyright (c) 2026 Leclowndu93150",
                     })
                Assert.Contains(line, mitLines);
            // The block says whose line each one is.
            foreach (string work in new[]
                     {
                         "(Bouncy Castle)", "(HelixToolkit)", "(HidHide)", "(HIDMaestro)", "(NAudio)", "(Nefarius.Utilities.DeviceManagement)",
                         "(nefcon)", "(winpthreads)", "(WPF UI)", "(ZstdSharp)", "colorwin, inside nefconc.exe", "easyloggingpp, inside nefconc.exe",
                         "neflib, inside nefconc.exe", "Detours, inside nefconc.exe", "VirtualizingWrapPanel, inside Wpf.Ui.dll",
                         "fluentui-system-icons, inside Wpf.Ui.dll", "(xbledctl,",
                     })
                Assert.Contains(work, Flat(mit));

            // The MIT text stands at the end, three times in all: the block,
            // and the two notices that were there before it. No entry of the
            // body holds it, and none points at it with words it did not have.
            int listStart = raw.IndexOf("Third-Party Licenses and Attributions", StringComparison.Ordinal);
            int listEnd = raw.IndexOf("Special Thanks", StringComparison.Ordinal);
            Assert.True(listStart >= 0 && listEnd > listStart, "LICENSE has no attribution list");
            string list = raw.Substring(listStart, listEnd - listStart);
            Assert.DoesNotContain("Permission is hereby granted", list);
            Assert.DoesNotContain("MIT License (full text", list);
            Assert.Equal(3, Regex.Matches(Flat(raw), "Permission is hereby granted, free of charge, to any person obtaining a copy").Count);
            var entries = Regex.Split(list.Replace("\r", string.Empty), @"\n\n+").Where(e => e.Length > 0 && !char.IsWhiteSpace(e[0])).ToList();
            // Positive control: the split found the entries.
            Assert.True(entries.Count > 60, $"found only {entries.Count} entries");
            // The entries the block was written for are the entries they were.
            Assert.Contains("WPF UI\n  https://github.com/lepoco/wpfui\n  Licensed under MIT License", entries);
            Assert.Contains("Windows MIDI Services\n  https://github.com/microsoft/MIDI\n  Licensed under MIT License", entries);

            string Entry(string name) => Flat(entries.Single(e => e.Split('\n')[0] == name));
            foreach (string part in new[] { "VirtualizingWrapPanel", "fluentui-system-icons", "microsoft-ui-xaml", "Segoe Fluent Icons", "ThirdPartyNotices.txt" })
                Assert.Contains(part, Entry("WPF UI third-party components"));
            foreach (string part in new[]
                     {
                         "WinRT.Runtime.dll", "Microsoft.Windows.SDK.NET.dll", "https://aka.ms/WinSDKLicenseURL",
                         "Microsoft Software License Terms for the Windows Software Development Kit (SDK) for Windows 10",
                     })
                Assert.Contains(part, Entry(".NET"));
            string nefcon = Entry("nefcon");
            Assert.Contains("It links Argh!, colorwin, easyloggingpp, Microsoft Detours, scope_guard, the Windows Implementation "
                + "Libraries and neflib, and its README credits WinReg too.", nefcon);
            string openXInput = Entry("OpenXInput");
            Assert.DoesNotContain("grants no OSS license", openXInput);
            foreach (string part in new[] { "trademark disclaimer", "devobj", "GPL-3.0-or-later", "does not ship" })
                Assert.Contains(part, openXInput);
            Assert.Contains("Copyright (C) 1997-2026 Sam Lantinga", Entry("SDL3"));
            Assert.Contains("Copyright (C) 1997-2025 Sam Lantinga", Entry("SDL_GameControllerDB"));
            foreach (string part in new[] { "Johannes Erdfelt", "Daniel Drake", "Chris Dickens", "AUTHORS" })
                Assert.Contains(part, Entry("libusb"));
            Assert.Contains("Go Touch VR SAS", Entry("Interhaptics engine and Razer provider"));
            foreach (string part in new[] { "license.txt", "version 2.1 or any later version", "LGPL-2.1-only" })
                Assert.Contains(part, Entry("SteamKit2"));
            Assert.Contains("The University of Texas at Austin", Entry("Kaldi, OpenFst, OpenBLAS and CLAPACK"));
            Assert.Contains("Lockless Inc.", Entry("MinGW-w64 runtime"));
            Assert.Equal(2, Regex.Matches(raw, @"Copyright \(c\) 2020-2026, Benjamin Höglinger-Stelzer").Count);
            Assert.Empty(Regex.Matches(raw, @"2020-2025, Benjamin"));

            string goto2 = Flat(LicenseBlock(raw, "GotoBLAS2 notice ("));
            Assert.Contains("Copyright 2009, 2010 The University of Texas at Austin.", goto2);
            Assert.Contains("THIS SOFTWARE IS PROVIDED BY THE UNIVERSITY OF TEXAS AT AUSTIN", goto2);
            string bsd = Flat(LicenseBlock(raw, "BSD 3-Clause License ("));
            Assert.Contains("(C) 2010 Lockless Inc.", bsd);
            Assert.Contains("the third condition names Lockless Inc.", bsd);
            Assert.Contains("Copyright (c) 2016, Adi Shavit (Argh!)", bsd);
            Assert.Contains("You may use the Segoe and icon fonts", Flat(LicenseBlock(raw, "Segoe Fluent Icons font terms (")));

            var readme = ReadmeLines();
            string Line(string start) => readme.Single(l => l.StartsWith(start, StringComparison.Ordinal));
            Assert.EndsWith("| MIT |", Line("| [Bouncy Castle]("));
            Assert.EndsWith("| LGPL-2.1-or-later (license.txt), LGPL-2.1-only (package metadata) |", Line("| [SteamKit2]("));
            Assert.Contains("(Unlicense)", Line("- **Bliss-Box references**"));
            Assert.DoesNotContain("(public domain)", Line("- **Bliss-Box references**"));
            foreach (var (start, part) in new[]
                     {
                         ("| [WPF UI](", "VirtualizingWrapPanel"), ("| [Kaldi](", "GotoBLAS2"), ("| [MinGW-w64 runtime](", "Lockless Inc."),
                         ("- **Concentus**", "CSIRO"), ("- **DsHidMini**", "2020-2026"), ("- **Interhaptics**", "Go Touch VR SAS"),
                         ("- **Kaldi**", "The University of Texas at Austin"), ("- **libusb**", "Johannes Erdfelt"),
                         ("- **MinGW-w64 runtime**", "Lockless Inc."), ("- **NAudio**", "Copyright 2020 Mark Heath"),
                         ("- **Nefarius.Utilities.DeviceManagement**", "Copyright (c) 2022 Benjamin Höglinger-Stelzer"),
                         ("- **OpenXInput**", "devobj"), ("- **SDL3**", "1997-2026 Sam Lantinga"),
                         ("- **SDL_GameControllerDB**", "1997-2025 Sam Lantinga"), ("- **SteamKit2**", "license.txt"),
                         ("- **Windows MIDI Services**", "Copyright (c) Microsoft Corporation."), ("- **WPF UI**", "VirtualizingWrapPanel"),
                         ("- **xbledctl**", "Copyright (c) 2026 Leclowndu93150"),
                         ("| [nefcon](", "It links Argh!, colorwin, easyloggingpp, Microsoft Detours, scope_guard, the Windows Implementation Libraries and neflib | MIT, BSD 3-Clause (Argh!), Unlicense (scope_guard) |"),
                         ("- **nefcon**", "Argh! (BSD 3-Clause, Copyright (c) 2016, Adi Shavit)"), ("- **nefcon**", "scope_guard (Unlicense)"),
                     })
                Assert.True(Line(start).Contains(part, StringComparison.Ordinal), $"README '{start}' lacks '{part}'");

            foreach (string locale in Locales)
            {
                string text = Text("PadForge.App", "Resources", "Strings", locale);
                foreach (var (key, parts) in new[]
                         {
                             ("About_ModernWpfDesc", new[] { "VirtualizingWrapPanel", "fluentui-system-icons", "microsoft-ui-xaml" }),
                             ("About_KaldiDesc", new[] { "GotoBLAS2", "f2c", "BSD 3-Clause" }),
                             ("About_MingwRuntimeDesc", new[] { "Lockless Inc.", "MIT", "BSD 3-Clause" }),
                             ("About_SteamKit2Desc", new[] { "LGPL-2.1-or-later", "license.txt", "LGPL-2.1-only", "NuGet" }),
                             ("About_NefconDesc", new[] { "Argh!", "colorwin", "easyloggingpp", "Microsoft Detours", "scope_guard", "Windows Implementation Libraries", "neflib", "MIT" }),
                         })
                {
                    string value = ResxValue(text, key);
                    foreach (string part in parts)
                        Assert.True(value.Contains(part, StringComparison.Ordinal), $"{locale}: {key} lacks '{part}': {value}");
                }
            }
            // WPF UI's license is in its chip on the About page (#574).
            Assert.Equal("About_License_Mit", AboutChip("About_ModernWpf"));
        }

        /// <summary>
        /// The About page's credits are one row each in CreditsList: the
        /// name bound to the credit's key, the description bound to the key
        /// plus "Desc", and the chip that names the credit's license, sorted
        /// by the English name without regard to case or a leading symbol. A
        /// row goes in at its sorted place. Every name in Strings.resx that
        /// has its description has its row, and every chip is the license
        /// AboutChips holds for the credit (#574).
        /// </summary>
        [Fact]
        public void TheAboutCreditsAreOneRowEachInAlphabeticalOrder()
        {
            var rows = AboutCreditRows();
            // Positive control: the pattern still finds the rows.
            Assert.True(rows.Count > 100, $"found only {rows.Count} credit rows");
            Assert.Equal(rows.Count, rows.Select(r => r.Name).Distinct().Count());

            string english = Text("PadForge.App", "Resources", "Strings", "Strings.resx");
            var keys = Regex.Matches(english, "<data name=\"(About_\\w+)\"").Select(m => m.Groups[1].Value).ToHashSet();
            Assert.Equal(keys.Count(k => k.EndsWith("Desc", StringComparison.Ordinal) && keys.Contains(k[..^4])), rows.Count);

            var chips = AboutChips.SelectMany(c => c.Credits.Select(n => ("About_" + n, c.Chip))).ToDictionary(p => p.Item1, p => p.Item2);
            Assert.Empty(chips.Keys.Except(rows.Select(r => r.Name)));
            string previous = null;
            foreach (var (name, description, chip) in rows)
            {
                Assert.Equal(name + "Desc", description);
                string license = chips.TryGetValue(name, out string held) ? held : null;
                Assert.True(license == chip, $"{name} shows {chip ?? "no chip"}, and its license is {license ?? "none"}");

                string sortName = Regex.Replace(ResxValue(english, name), @"^[^A-Za-z0-9]+", string.Empty).ToLowerInvariant();
                Assert.True(previous == null || string.CompareOrdinal(previous, sortName) <= 0,
                            $"'{sortName}' ({name}) comes after '{previous}' and sorts before it");
                previous = sortName;
            }
            Assert.Equal(new[] { "About_RawInput" }, rows.Where(r => r.Chip == null).Select(r => r.Name));
        }

        /// <summary>The About page's credit rows in order, as the keys their
        /// name, description and chip bind, with no chip for a row that has
        /// none. Each row is a CreditRow Border of CreditsList.</summary>
        private static List<(string Name, string Description, string Chip)> AboutCreditRows()
        {
            string xaml = Text("PadForge.App", "Views", "AboutPage.xaml");
            int list = xaml.IndexOf("<StackPanel x:Name=\"CreditsList\">", StringComparison.Ordinal);
            int end = xaml.IndexOf("x:Name=\"NoCreditMatch\"", StringComparison.Ordinal);
            Assert.True(list >= 0 && end > list, "the About page has no CreditsList ahead of NoCreditMatch");
            const string Key = @"common:BidiText\.Text=""\{Binding (About_\w+), Source=";
            var rows = new List<(string Name, string Description, string Chip)>();
            foreach (Match row in Regex.Matches(xaml, @"<Border Style=""\{StaticResource CreditRow\}"">\s*<DockPanel>(.*?)</DockPanel>", RegexOptions.Singleline))
            {
                Assert.True(row.Index > list && row.Index < end, "a credit row stands outside CreditsList");
                string body = row.Groups[1].Value;
                var name = Regex.Match(body, @"Style=""\{StaticResource CreditName\}"" " + Key);
                var description = Regex.Match(body, @"Style=""\{StaticResource CreditDesc\}"" " + Key);
                var chip = Regex.Match(body, @"Style=""\{StaticResource CreditChip\}"">\s*<TextBlock " + Key);
                Assert.True(name.Success && description.Success, "a credit row lacks its name or its description: " + body.Trim());
                Assert.True(Regex.Matches(body, Key).Count == (chip.Success ? 3 : 2),
                            "a credit row binds more than its name, description and chip: " + body.Trim());
                rows.Add((name.Groups[1].Value, description.Groups[1].Value, chip.Success ? chip.Groups[1].Value : null));
            }
            return rows;
        }

        /// <summary>The key the chip of a credit's row binds on the About
        /// page, or null for a row with no chip.</summary>
        private static string AboutChip(string name) => AboutCreditRows().Single(r => r.Name == name).Chip;

        private static string ReadmeWithoutNewInBlocks() =>
            Regex.Replace(Text("README.md"), NewInBlock, string.Empty, RegexOptions.Singleline);

        private static List<string> ReadmeLines() =>
            Text("README.md").Split('\n').Select(l => l.TrimEnd('\r')).ToList();

        /// <summary>One of the license texts at the end of LICENSE: from the
        /// heading that starts with the given words to the next rule of
        /// dashes, or to the end of the file.</summary>
        private static string LicenseBlock(string license, string heading)
        {
            int start = license.IndexOf("\n" + heading, StringComparison.Ordinal);
            Assert.True(start >= 0, $"LICENSE has no '{heading}' text");
            int end = license.IndexOf("\n----------------------------------------", start, StringComparison.Ordinal);
            return end > start ? license.Substring(start, end - start) : license.Substring(start);
        }

        private static string ResxValue(string resx, string key)
        {
            var m = Regex.Match(resx, "<data name=\"" + Regex.Escape(key) + "\"[^>]*>\\s*<value>(.*?)</value>", RegexOptions.Singleline);
            Assert.True(m.Success, key + " is missing");
            return m.Groups[1].Value;
        }

        private static string Text(params string[] parts) =>
            File.ReadAllText(AuditDelta20260823Tests.FindRepoFile(Path.Combine(parts)));
    }
}
