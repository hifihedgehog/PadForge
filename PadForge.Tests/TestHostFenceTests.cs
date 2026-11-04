using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using PadForge.Common;
using PadForge.Common.Input;
using PadForge.Common.Input.Peripherals;
using PadForge.Engine.Common;
using PadForge.Engine.Common.AnalogKeyboard;
using PadForge.Engine.Common.Logitech;
using PadForge.Engine.RemoteLink.Dht;
using PadForge.Engine.Tablets;
using PadForge.Services;
using PadForge.SteamWorkshop.Api;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>What the fence tests read a guard out of the source
    /// with.</summary>
    internal static class FenceSource
    {
        internal static readonly string[] Projects = { "PadForge.App", "PadForge.Engine", "PadForge.SteamWorkshop" };

        /// <summary>Where a member's signature starts: at a line's start,
        /// once in the file. Answers the indent with it. A signature that
        /// ends in a name ends there, so a longer name is another member.</summary>
        internal static (int At, string Indent) Start(string source, string signature)
        {
            string end = signature.Length > 0 && (char.IsLetterOrDigit(signature[^1]) || signature[^1] == '_') ? @"\b" : "";
            var found = Regex.Matches(source, @"(?m)^([ \t]*)" + Regex.Escape(signature) + end);
            Assert.True(found.Count == 1, signature + " is in the file " + found.Count + " times");
            return (found[0].Index, found[0].Groups[1].Value);
        }

        /// <summary>The first statement of a member: what follows the
        /// arrow of an expression body, or the first line of a block that
        /// is no comment.</summary>
        internal static string FirstStatement(string source, string signature)
        {
            int at = Start(source, signature).At;
            int brace = source.IndexOf('{', at);
            int arrow = source.IndexOf("=>", at, StringComparison.Ordinal);
            if (arrow >= 0 && (brace < 0 || arrow < brace))
                return source[(arrow + 2)..(source.IndexOf(';', arrow) + 1)].Trim();
            Assert.True(brace > at, "no body: " + signature);
            foreach (string line in source[(brace + 1)..].Split('\n'))
            {
                string text = line.Trim();
                if (text.Length > 0 && !text.StartsWith("//", StringComparison.Ordinal)) return text;
            }
            throw new InvalidOperationException("an empty body: " + signature);
        }

        /// <summary>The text of a member, or of a type, from its signature
        /// to the brace that closes it at its own indent.</summary>
        internal static string Body(string source, string signature)
        {
            var (at, indent) = Start(source, signature);
            int close = source.IndexOf("\n" + indent + "}", at, StringComparison.Ordinal);
            Assert.True(close > at, "never closed: " + signature);
            return source[at..close];
        }

        /// <summary>A member asks its fence before it does anything
        /// else.</summary>
        internal static void AsksFirst(string file, string signature, string guard)
            => Assert.StartsWith(guard, FirstStatement(OffLimitsSource.Read(file), signature), StringComparison.Ordinal);

        /// <summary>A member that does other work first asks its fence
        /// before the first call the fence covers.</summary>
        internal static void AsksBefore(string file, string signature, string guard, string call)
        {
            string body = Body(OffLimitsSource.Read(file), signature);
            int asked = body.IndexOf(guard, StringComparison.Ordinal);
            int made = body.IndexOf(call, StringComparison.Ordinal);
            Assert.True(asked >= 0, "no fence in " + signature);
            Assert.True(made >= 0, "no call in " + signature);
            Assert.True(asked < made, "the call comes before its fence in " + signature);
        }

        /// <summary>The lines of the app, the engine and the workshop
        /// library that are code, each with its file's name. Comment lines
        /// are left out.</summary>
        internal static IEnumerable<(string File, string Line)> Code()
        {
            string root = OffLimitsSource.RepoRoot();
            foreach (string project in Projects)
                foreach (string path in OffLimitsSource.Sources(project))
                    foreach (string line in File.ReadAllLines(path))
                    {
                        string text = line.Trim();
                        if (text.StartsWith("//", StringComparison.Ordinal) || text.StartsWith("*", StringComparison.Ordinal)) continue;
                        yield return (Path.GetRelativePath(root, path).Replace('\\', '/'), text);
                    }
        }

        /// <summary>How often a pattern is in each file's code. An import's
        /// own declaration is left out on request, so only calls count.</summary>
        internal static SortedDictionary<string, int> Census(string pattern, bool callsOnly = false)
        {
            var regex = new Regex(pattern, RegexOptions.Compiled);
            var found = new SortedDictionary<string, int>(StringComparer.Ordinal);
            foreach (var (file, line) in Code())
            {
                if (callsOnly && line.Contains(" extern ", StringComparison.Ordinal)) continue;
                int n = regex.Matches(line).Count;
                if (n > 0) found[file] = found.GetValueOrDefault(file) + n;
            }
            return found;
        }
    }

    /// <summary>
    /// The test process leaves the PC it runs on as it found it: its desktop,
    /// its clipboard, its devices, its radio, its drivers, its certificate
    /// stores, its logon task, its sound, the programs other vendors run on
    /// it, and the network beyond it. Eighteen fences hold that
    /// (TestHostFences.cs), beside the ones MachineOffLimitsTests,
    /// HidHideOffLimitsTests and their neighbors pin. The listener gate's
    /// tests are in TestHostListenerTests.cs, and the workshop library's own
    /// test project sets and tests the Steam flag for its process. One more
    /// flag is HIDMaestro's own, set so its context leaves the GameInput
    /// service alone.
    ///
    /// <para>Three things are pinned for each fence. The flag is set here
    /// and nothing in the app sets it, so production runs as before. Each
    /// call the fence covers asks it before the call is made, read from the
    /// source. And every call of a kind that can reach the machine is on a
    /// list, so a new one fails here until it is given a fence.</para>
    ///
    /// <para>A call is made here only where one that got through would do no
    /// harm: a program that does not exist, a file that does not exist, an
    /// address nothing answers on. The others are read and never run. Reads
    /// of the PC are not fenced, and neither are the app's own files in its
    /// own folders.</para>
    /// </summary>
    public class TestHostFenceTests
    {
        private const string Input = "PadForge.App/Common/Input/";
        private const string Services = "PadForge.App/Services/";
        private const string DeviceAccess = Input + "DirectDeviceAccess.cs";
        private const string Shell = "PadForge.App/Common/ShellLaunch.cs";
        private const string Gate = "PadForge.Engine/Common/NetworkListenerGate.cs";
        private const string Bluetooth = Input + "BluetoothLinkHelper.cs";
        private const string VirtualController = Input + "HMaestroVirtualController.cs";
        private const string HeadTracker = Input + "HeadTrackerDevice.cs";
        private const string Hooks = "PadForge.Engine/Common/InputHookManager.cs";
        private const string Startup = "PadForge.App/Common/StartupHelper.cs";
        private const string Ds3Driver = Services + "Ds3DriverInstaller.cs";
        private const string WebTls = Services + "WebControllerTls.cs";
        private const string Mutex = "PadForge.Engine/Common/AnalogKeyboard/AnalogKeyboardNamedMutex.cs";
        private const string Tablet = "PadForge.Engine/Common/Tablets/TabletNative.cs";
        private const string AudioAccess = Input + "AudioEndpointAccess.cs";
        private const string VendorGate = "PadForge.Engine/Common/VendorProgramGate.cs";
        private const string Outbound = "PadForge.Engine/Common/OutboundNetworkGate.cs";
        private const string Macros = Input + "InputManager.Step4b.EvaluateMacros.cs";
        private const string Kbm = Input + "KeyboardMouseVirtualController.cs";
        private const string Cursor = Services + "CursorControlService.cs";
        private const string WiiPairing = Services + "WiiPairingService.cs";
        private const string Passthrough = Input + "AudioPassthroughService.cs";
        private const string RumbleAudio = Input + "RumbleAudioService.cs";
        private const string SoundMacro = Input + "SoundMacroService.cs";
        private const string Chroma = Input + "Peripherals/ChromaBackend.cs";
        private const string GameSense = Services + "GameSenseClient.cs";
        private const string LogiLed = Services + "LogiLedEngineNative.cs";
        private const string Sensa = Services + "SensaHapticsService.cs";
        private const string GKeys = "PadForge.Engine/Common/Logitech/LogitechGKeySource.cs";
        private const string LgsLcd = "PadForge.Engine/Common/Logitech/LogitechLcdLibrary.cs";
        private const string LgsArx = "PadForge.Engine/Common/Logitech/LogitechArxLibrary.cs";
        private const string Strike7 = "PadForge.Engine/Common/MadCatz/Strike7Library.cs";
        private const string ForceDimension = "PadForge.Engine/Common/ForceDimension/ForceDimensionLibrary.cs";
        private const string G19Screen = Input + "KeyboardScreens/G19UsbScreen.cs";
        private const string LinkServerFile = "PadForge.Engine/RemoteLink/LinkServer.cs";
        private const string Krpc = "PadForge.Engine/RemoteLink/Dht/UdpKrpcTransport.cs";
        private const string Stun = "PadForge.Engine/RemoteLink/StunClient.cs";
        private const string Relay = "PadForge.Engine/RemoteLink/IrohRelayClient.cs";
        private const string TwitchChat = Input + "Twitch/TwitchChatClient.cs";
        private const string SteamHttpFile = "PadForge.SteamWorkshop/Api/SteamHttp.cs";
        private const string SteamClientFile = "PadForge.SteamWorkshop/Api/SteamWorkshopClient.cs";
        private const string WorkshopDialog = "PadForge.App/Views/WorkshopBrowseDialog.xaml.cs";
        private const string ControlPipe = Services + "ExternalControlService.cs";
        private const string Dsu = Services + "DsuMotionServer.cs";
        private const string GameMods = Services + "GameModServer.cs";
        private const string GameModFiles = Services + "GameModPortFiles.cs";
        private const string Telemetry = Input + "Telemetry/GameTelemetryDevice.cs";
        private const string Kunos = Input + "Telemetry/KunosSharedMemory.cs";
        private const string OwnLighting = Input + "Peripherals/OwnLightingChannels.cs";
        private const string AuraSdk = Input + "Peripherals/AuraSdk.cs";
        private const string MysticLight = Input + "Peripherals/MysticLightSdk.cs";
        private const string AuraCore = Input + "Peripherals/HidFamilies/AsusAuraCoreLighting.cs";
        private const string OpenRgb = Input + "Peripherals/OpenRgbClient.cs";
        private const string OpenRgbPort = Input + "Peripherals/OpenRgbServerPort.cs";

        private const string RefusesHost = "if (PadForge.Engine.Common.OutboundNetworkGate.RefusesHost(host))";
        private const string VendorRefuses = "if (PadForge.Engine.Common.VendorProgramGate.Refuses";

        /// <summary>The fences, each with the file that declares its flag
        /// and the class it belongs to. Every flag has the one name.</summary>
        private static readonly (string File, string Class)[] Fences =
        {
            (DeviceAccess, "DirectDeviceAccess"),
            (Shell, "ShellLaunch"),
            (Gate, "NetworkListenerGate"),
            (Bluetooth, "BluetoothLinkHelper"),
            (VirtualController, "HMaestroVirtualController"),
            (HeadTracker, "FreeTrackReader"),
            (Hooks, "InputHookManager"),
            (Startup, "StartupHelper"),
            (Ds3Driver, "Ds3DriverInstaller"),
            (WebTls, "WebControllerTls"),
            (Mutex, "AnalogKeyboardNamedMutex"),
            (Tablet, "TabletNative"),
            (AudioAccess, "AudioEndpointAccess"),
            (VendorGate, "VendorProgramGate"),
            (Outbound, "OutboundNetworkGate"),
            (SteamHttpFile, "SteamHttp"),
            (ControlPipe, "ExternalControlService"),
            (Dsu, "DsuMotionServer"),
            (GameMods, "GameModServer"),
            (GameModFiles, "GameModPortFiles"),
            (Telemetry, "GameTelemetryDevice"),
            (Kunos, "KunosSharedMemory"),
            (OwnLighting, "OwnLightingChannels"),
            (AuraCore, "AsusAuraCoreLighting"),
            (OpenRgb, "OpenRgbTcpLink"),
            (OpenRgbPort, "OpenRgbServerPort"),
        };

        private const string Flag = "OffLimitsForTest";
        private const string Assigns = @"\s*(=(?!=)|\|=|&=|\^=)";

        private static readonly Regex SetsAFenceByName = new(
            @"\b(" + string.Join("|", Fences.Select(f => f.Class)) + @")\." + Flag + Assigns, RegexOptions.Compiled);

        // ── The flags ──

        [Fact]
        public void EveryFence_IsSetInTheTestHost()
        {
            Assert.True(DirectDeviceAccess.OffLimitsForTest);
            Assert.True(ShellLaunch.OffLimitsForTest);
            Assert.True(NetworkListenerGate.OffLimitsForTest);
            Assert.True(BluetoothLinkHelper.OffLimitsForTest);
            Assert.True(HMaestroVirtualController.OffLimitsForTest);
            Assert.True(FreeTrackReader.OffLimitsForTest);
            Assert.True(InputHookManager.OffLimitsForTest);
            Assert.True(InputHookManager.DesktopInputOffLimits);
            Assert.True(StartupHelper.OffLimitsForTest);
            Assert.True(Ds3DriverInstaller.OffLimitsForTest);
            Assert.True(WebControllerTls.OffLimitsForTest);
            Assert.True(AnalogKeyboardNamedMutex.OffLimitsForTest);
            Assert.True(TabletNative.OffLimitsForTest);
            Assert.True(AudioEndpointAccess.OffLimitsForTest);
            Assert.True(AudioEndpointAccess.Refuses);
            Assert.True(VendorProgramGate.OffLimitsForTest);
            Assert.True(VendorProgramGate.Refuses);
            Assert.True(OutboundNetworkGate.OffLimitsForTest);
            Assert.True(SteamHttp.OffLimitsForTest);
            Assert.True(ExternalControlService.OffLimitsForTest);
            Assert.True(DsuMotionServer.OffLimitsForTest);
            Assert.True(GameModServer.OffLimitsForTest);
            Assert.True(GameModPortFiles.OffLimitsForTest);
            Assert.True(PadForge.Common.Input.Telemetry.GameTelemetryDevice.OffLimitsForTest);
            Assert.True(PadForge.Common.Input.Telemetry.KunosSharedMemory.OffLimitsForTest);
            Assert.True(OwnLightingChannels.OffLimitsForTest);
            Assert.True(PadForge.Common.Input.Peripherals.AsusAuraCoreLighting.OffLimitsForTest);
            Assert.True(OpenRgbTcpLink.OffLimitsForTest);
            Assert.True(OpenRgbServerPort.OffLimitsForTest);
        }

        /// <summary>Each flag is a plain field that starts false, and no
        /// line of the app, the engine or the workshop library writes one,
        /// in its own file or through its class's name.</summary>
        [Fact]
        public void NothingInTheApplication_SetsAFence()
        {
            var writes = new List<string>();
            foreach (var (file, _) in Fences)
            {
                string source = OffLimitsSource.Read(file);
                Assert.Single(Regex.Matches(source, @"^\s*internal static bool " + Flag + @";$", RegexOptions.Multiline));
                foreach (Match m in Regex.Matches(source, @"\b" + Flag + Assigns))
                    writes.Add(Path.GetFileName(file) + ": " + m.Value);
            }

            int read = 0;
            foreach (string project in FenceSource.Projects)
            {
                foreach (string path in OffLimitsSource.Sources(project))
                {
                    read++;
                    foreach (Match m in SetsAFenceByName.Matches(File.ReadAllText(path)))
                        writes.Add(Path.GetFileName(path) + ": " + m.Value);
                }
            }
            Assert.True(read > 200, "the application's sources were not found");
            Assert.Empty(writes);
        }

        /// <summary>In the tests, only the module initializers write a flag,
        /// one each, and only to set it, so no test lifts a fence for a
        /// while.</summary>
        [Fact]
        public void OnlyTheModuleInitializers_SetAFence_AndNoTestClearsOne()
        {
            var writes = new List<string>();
            foreach (string path in OffLimitsSource.Sources("PadForge.Tests"))
            {
                string name = Path.GetFileName(path);
                if (name == "TestHostFenceTests.cs") continue;
                foreach (string line in File.ReadAllLines(path))
                    if (SetsAFenceByName.IsMatch(line)) writes.Add(name + ": " + line.Trim());
            }
            Assert.Equal(Fences.Length, writes.Count);
            Assert.All(writes, w => Assert.StartsWith("TestHostFences.cs: internal static void Set() => ", w, StringComparison.Ordinal));
            Assert.All(writes, w => Assert.EndsWith("." + Flag + " = true;", w, StringComparison.Ordinal));
            Assert.Equal(Fences.Length, writes.Distinct().Count());
        }

        // ── Each covered call asks its fence first ──

        public static IEnumerable<object[]> AskedFirst() => new[]
        {
            // A device opened by a writer that makes its own handle.
            new object[] { Input + "AnalogKeyboardHid.cs", "private static SafeFileHandle CreateOverlapped(string path, uint access, uint share)", "if (DirectDeviceAccess.Refuses(path))" },
            new object[] { Passthrough, "public static IntPtr OpenHidSync(string path)", "if (DirectDeviceAccess.Refuses(path)) return new IntPtr(-1);" },
            new object[] { Passthrough, "public static IntPtr OpenHid(string path)", "if (DirectDeviceAccess.Refuses(path)) return new IntPtr(-1);" },
            new object[] { Input + "Ds3DirectService.cs", "private bool OpenBluetooth()", "if (DirectDeviceAccess.RefusesAll) return false;" },
            new object[] { Input + "Ds3DirectService.cs", "private bool OpenUsb()", "if (DirectDeviceAccess.RefusesAll) return false;" },
            new object[] { Input + "PsMoveDirectService.cs", "private bool OpenBluetooth()", "if (DirectDeviceAccess.RefusesAll) return false;" },
            new object[] { Input + "PsMoveDirectService.cs", "private bool OpenUsb()", "if (DirectDeviceAccess.RefusesAll) return false;" },
            new object[] { Input + "HapticToneService.cs", "private static IntPtr OpenPairSecondChild(Sink s, string path)", "if (DirectDeviceAccess.Refuses(path)) return IntPtr.Zero;" },
            new object[] { Input + "PlayStationEffectWriter.cs", "private static bool WriteRaw(string devicePath, byte[] buf)", "if (DirectDeviceAccess.Refuses(devicePath)) return false;" },
            new object[] { Input + "WinScard.cs", "public static string ReadUid(string reader)", "if (DirectDeviceAccess.RefusesAll) return null;" },
            new object[] { Input + "XboxGipGuideLedWriter.cs", "private bool OpenInterface()", "if (DirectDeviceAccess.Refuses(" },
            new object[] { Input + "XboxImpulseHidWriter.cs", "private static SafeFileHandle CreateFileSafe(", "if (DirectDeviceAccess.Refuses(path)) return new SafeFileHandle(new IntPtr(-1), true);" },
            new object[] { G19Screen, "public static IG19Link TryOpen(string path)", "if (DirectDeviceAccess.Refuses(path)) return null;" },
            new object[] { DeviceAccess, "internal static bool Refuses(string path)", "OffLimitsForTest && IsDevicePath(path);" },
            new object[] { DeviceAccess, "internal static bool RefusesAll", "OffLimitsForTest;" },

            // The shell and the clipboard.
            new object[] { Shell, "internal static Process Start(ProcessStartInfo psi)", "OffLimitsForTest ? null : Process.Start(psi);" },
            new object[] { Shell, "internal static Process Start(string fileName, string arguments)", "OffLimitsForTest ? null : Process.Start(fileName, arguments);" },
            new object[] { Shell, "internal static void SetClipboardText(string text)", "if (OffLimitsForTest) return;" },

            // The radio and what is paired to it.
            new object[] { Bluetooth, "private static bool TryCycleBthDevNodes(", "if (OffLimitsForTest) return false;" },
            new object[] { Bluetooth, "private static void ReEnableDevNode(uint devInst)", "if (OffLimitsForTest) return;" },
            new object[] { Bluetooth, "public static void ReEnablePendingDevNodes()", "if (OffLimitsForTest) return;" },
            new object[] { Bluetooth, "private static bool TrySwitch2EffectPassthrough(IntPtr gamepadHandle)", "if (OffLimitsForTest) return false;" },
            new object[] { Bluetooth, "private static async System.Threading.Tasks.Task<bool> TrySwitch2PowerOffAsync(string serial)", "if (OffLimitsForTest) return false;" },
            new object[] { Bluetooth, "private static bool TrySteamPowerOff(string devicePath)", "if (OffLimitsForTest) return false;" },
            new object[] { Bluetooth, "private static bool TryXInputPowerOff(ushort vendorId, ushort productId)", "if (OffLimitsForTest) return false;" },
            new object[] { Input + "BluetoothFirmwareService.cs", "private static async Task<(string Text, bool Final)> ReadRevisionAsync(ulong address)", "if (BluetoothLinkHelper.OffLimitsForTest) return (null, true);" },
            new object[] { Services + "HeadsetTrackerRepair.cs", "internal static Outcome RequestHidServiceByAddress(ulong address, Action<string> log)", "if (PadForge.Common.Input.BluetoothLinkHelper.OffLimitsForTest) return Outcome.Failed;" },
            new object[] { Services + "HeadsetTrackerRepair.cs", "internal static bool RebindNode(string hardwareId, Action<string> log)", "if (PadForge.Common.Input.BluetoothLinkHelper.OffLimitsForTest) return false;" },

            // Virtual controller devices.
            new object[] { VirtualController, "internal static void SweepVirtualControllers()", "if (OffLimitsForTest) return;" },
            new object[] { VirtualController, "internal static HMUninstallReport RemoveHIDMaestro(HMUninstallOptions options)", "if (OffLimitsForTest) return null;" },

            // Keys, buttons, the cursor and hooks.
            new object[] { Hooks, "public static bool DesktopInputOffLimits", "OffLimitsForTest;" },
            new object[] { Hooks, "private static IntPtr SetWindowsHookExW(int idHook, LowLevelHookProc lpfn, IntPtr hMod, uint dwThreadId)", "OffLimitsForTest ? IntPtr.Zero : SetWindowsHookExNative(idHook, lpfn, hMod, dwThreadId);" },
            new object[] { Hooks, "private static uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize)", "OffLimitsForTest ? 0 : SendInputNative(nInputs, pInputs, cbSize);" },
            new object[] { Macros, "private static uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize)", "PadForge.Engine.Common.InputHookManager.DesktopInputOffLimits ? 0 : SendInputNative(nInputs, pInputs, cbSize);" },
            new object[] { Kbm, "private static uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize)", "PadForge.Engine.Common.InputHookManager.DesktopInputOffLimits ? 0 : SendInputNative(nInputs, pInputs, cbSize);" },
            new object[] { Kbm, "private static bool SetCursorPos(int x, int y)", "!PadForge.Engine.Common.InputHookManager.DesktopInputOffLimits && SetCursorPosNative(x, y);" },
            new object[] { Cursor, "private static bool SetCursorPos(int X, int Y)", "!PadForge.Engine.Common.InputHookManager.DesktopInputOffLimits && SetCursorPosNative(X, Y);" },

            // The logon task.
            new object[] { Startup, "private static void DeleteLegacyRunEntry()", "if (OffLimitsForTest) return;" },
            new object[] { Startup, "private static int RunSchtasks(string arguments)", "if (OffLimitsForTest) return -1;" },

            // The DualShock 3 drivers, the radio and the pairing records.
            new object[] { Ds3Driver, "public static bool EnsureInstalled(Action<string> log)", "if (OffLimitsForTest) return false;" },
            new object[] { Ds3Driver, "private static void RepairPsmFilter(Action<string> log)", "if (OffLimitsForTest) return;" },
            new object[] { Ds3Driver, "internal static bool SignDriverPackage(string dir, string catalogName, Action<string> log)", "if (OffLimitsForTest)" },
            new object[] { Ds3Driver, "private static (int Code, string Output) RunTool(string exe, string args, string workingDir, int timeoutMs = 120000)", "if (OffLimitsForTest)" },
            new object[] { Ds3Driver, "public static bool EnsureWinUsbBound(\n", "if (OffLimitsForTest) return false;" },
            new object[] { Ds3Driver, "internal static int RemoveStaleBthPs3Children(Action<string> log)", "if (OffLimitsForTest) return 0;" },
            new object[] { Ds3Driver, "public static void CycleBluetoothRadio(Action<string> log)", "if (OffLimitsForTest) return;" },
            new object[] { Ds3Driver, "public static bool WriteRememberedDeviceRecord(byte[] radioMacBigEndian, string deviceMacHex,\n", "if (OffLimitsForTest) return false;" },
            new object[] { Ds3Driver, "public static bool EnsureMoveFamilySupportEnabled(Action<string> log)", "if (OffLimitsForTest) return false;" },
            new object[] { Ds3Driver, "private static bool WriteDeviceNameRecord(string deviceMacHex, string remoteName, int pid, Action<string> log)", "if (OffLimitsForTest) return false;" },
            new object[] { Ds3Driver, "private static bool SetDeviceRecordOwnerToSystem(string deviceMacHex, Action<string> log)", "if (OffLimitsForTest) return false;" },
            new object[] { Ds3Driver, "private static void GrantAdministratorsFullControl(string subKey, Action<string> log)", "if (OffLimitsForTest) return;" },
            new object[] { Ds3Driver, "public static void DeleteRememberedDeviceRecord(byte[] radioMacBigEndian, string deviceMacHex, Action<string> log)", "if (OffLimitsForTest) return;" },
            new object[] { Ds3Driver, "private static IntPtr OpenKeysBackupRestore(byte[] radioMacBigEndian, Action<string> log)", "if (OffLimitsForTest) return IntPtr.Zero;" },
            new object[] { Ds3Driver, "private static void InstallInf(string infPath, Action<string> log)", "if (OffLimitsForTest) throw new InvalidOperationException(" },
            new object[] { Ds3Driver, "private static bool EnsureConsumerParams()", "if (OffLimitsForTest) return false;" },
            new object[] { Ds3Driver, "public static void RestoreBthPs3AutoArm()", "if (OffLimitsForTest) return;" },
            new object[] { Ds3Driver, "public static void EnsurePadForgeOwnsPsmPatch()", "if (OffLimitsForTest) return;" },
            new object[] { Ds3Driver, "private static int SetPsmPatchingCore(bool enable, Action<string> log, int waitForFilterMs)", "if (OffLimitsForTest) return 0;" },
            new object[] { Ds3Driver, "private static bool EnableBthPs3Service(Action<string> log)", "if (OffLimitsForTest) return false;" },
            // Removing BthPS3, its WinUSB package and the certificate.
            new object[] { Ds3Driver, "internal static int UninstallBthPs3Setup(string productCode, Action<string> log)", "if (OffLimitsForTest) return -1;" },
            new object[] { Ds3Driver, "internal static void RemoveBthPs3ClassFilter(Action<string> log)", "if (OffLimitsForTest) return;" },
            new object[] { Ds3Driver, "internal static bool DisableBthPs3Service(Action<string> log)", "if (OffLimitsForTest) return false;" },
            new object[] { Ds3Driver, "internal static bool SetRadioOn(bool on, Action<string> log)", "if (OffLimitsForTest) return false;" },
            new object[] { Ds3Driver, "internal static void RestartOffRadio(Action<string> log)", "if (OffLimitsForTest) return;" },
            new object[] { Ds3Driver, "internal static void RemoveBthPs3Package(DriverPackage package, Action<string> log)", "if (OffLimitsForTest) return;" },
            new object[] { Ds3Driver, "internal static void DeletePadForgePackage(DriverPackage package, Action<string> log)", "if (OffLimitsForTest) return;" },
            new object[] { Ds3Driver, "internal static bool DeleteDriverService(string name, Action<string> log)", "if (OffLimitsForTest) return false;" },
            new object[] { Ds3Driver, "internal static bool DeleteDriverFile(string path, Action<string> log)", "if (OffLimitsForTest) return false;" },
            new object[] { Ds3Driver, "internal static void RemoveSigningCertificate(Action<string> log)", "if (OffLimitsForTest) return;" },
            new object[] { Services + "Ds3PairingService.cs", "private static IntPtr CreateFile(string n, uint a, uint s, IntPtr sa, uint d, uint f, IntPtr t)", "Ds3DriverInstaller.OffLimitsForTest ? INVALID_HANDLE : CreateFileNative(n, a, s, sa, d, f, t);" },

            // Force Dimension's Falcon driver (#521), which stocks and forces
            // its package through VendorUsbDriverInstaller's fenced helpers.
            new object[] { "PadForge.App/Common/DriverInstaller.cs", "public static void InstallFalconDriver(string inf)", "if (OffLimitsForTest)" },

            // The web controller's certificate, binding and firewall rules.
            new object[] { WebTls, "private static string FindOrCreateCertThumbprint()", "if (OffLimitsForTest) return null;" },
            new object[] { WebTls, "private static string RunNetsh(string arguments)", "if (OffLimitsForTest)" },
            new object[] { Services + "WebControllerServer.cs", "private static string RunNetsh(string arguments)", "if (WebControllerTls.OffLimitsForTest)" },

            // Names and collections other programs share.
            new object[] { Mutex, "private static AnalogKeyboardNamedMutex Create(string name)", "if (OffLimitsForTest && IsShared(name)) return null;" },
            new object[] { Tablet, "internal static Stream OpenCapture(string path, int reportLength)", "if (OffLimitsForTest) throw new IOException(" },
            new object[] { Tablet, "internal static void RestartCollection(string instance, CancellationToken stop)", "if (OffLimitsForTest) throw new InvalidOperationException(" },

            // Sound, and the PC's sound settings.
            new object[] { RumbleAudio, "private static EndpointPlayer BuildPlayer(string endpointId, string name,", "if (AudioEndpointAccess.Refuses) return null;" },
            new object[] { Passthrough, "public static bool TryEnableEndpoint(string endpointId)", "if (AudioEndpointAccess.Refuses) return false;" },
            new object[] { Passthrough, "public static bool TrySetDefaultEndpoint(string endpointId)", "if (AudioEndpointAccess.Refuses) return false;" },
            new object[] { Macros, "private void SetSystemVolume(float volume, bool showOsd = true)", "if (AudioEndpointAccess.Refuses) return;" },
            new object[] { Macros, "internal static bool TrySetSessionVolume(IntPtr pSession, float volume)", "if (AudioEndpointAccess.Refuses) return false;" },
            new object[] { AudioAccess, "internal static bool Refuses", "OffLimitsForTest;" },

            // The programs other vendors run on the PC.
            new object[] { Chroma, "private async Task<string> InitAsync(IReadOnlyList<string> categories, CancellationToken ct)", VendorRefuses + " && IsRazersServer(_endpoint)) return null;" },
            new object[] { GameSense, "public bool TryConnect(long now)", VendorRefuses + " && IsGgsOwnFile(_corePropsPath)) return false;" },
            new object[] { LogiLed, "public bool TryLoad(out string detail)", VendorRefuses + ")" },
            // The ASUS Aura SDK's COM server and MSI's Mystic Light library (#557).
            new object[] { AuraSdk, "public bool Open(out string detail)", VendorRefuses + ")" },
            new object[] { MysticLight, "public bool TryLoad(out string detail)", VendorRefuses + ")" },
            new object[] { VendorGate, "public static bool Refuses", "OffLimitsForTest;" },

            // What is sent beyond this PC.
            new object[] { Stun, "public static async Task<IPEndPoint> QueryAsync(", RefusesHost + " return null;" },
            new object[] { LinkServerFile, "private async Task<IPEndPoint> OneStunProbeAsync(Socket sock, IPEndPoint server, CancellationToken ct)", "if (PadForge.Engine.Common.OutboundNetworkGate.Refuses(server?.Address)) return null;" },
            new object[] { LinkServerFile, "public async Task<bool> ConnectAsync(string host, int port, IReadOnlyList<RemotePeerDeviceInfo> exposeLocal, CancellationToken ct = default)", RefusesHost + " return false;" },
            new object[] { LinkServerFile, "public async Task SendToAsync(byte[] dg, IPEndPoint dest, CancellationToken ct)", "if (PadForge.Engine.Common.OutboundNetworkGate.Refuses(dest?.Address)) return;" },
            new object[] { Relay, "private async Task<bool> ConnectOneAsync(string host, CancellationToken ct)", RefusesHost + " return false;" },
            // The Twitch Chat row's connection to Twitch's chat (#555).
            new object[] { TwitchChat, "private async Task<bool> SessionAsync(CancellationToken ct)", "if (PadForge.Engine.Common.OutboundNetworkGate.RefusesHost(Host))" },
            new object[] { Outbound, "public static bool Refuses(IPAddress address)", "OffLimitsForTest && (address == null || !IPAddress.IsLoopback(address));" },
            new object[] { Outbound, "public static bool RefusesHost(string host)", "OffLimitsForTest && !IsLoopbackHost(host);" },
            new object[] { Outbound, "public static HttpClient NewHttpClient()", "OffLimitsForTest ? new HttpClient(new LoopbackOnly()) : new HttpClient();" },

            // The pipe a running PadForge serves.
            new object[] { ControlPipe, "public void Start()", "if (OffLimitsForTest && ServesTheAppsOwnPipe) return;" },

            // A laptop's lighting through its firmware, and a keyboard's
            // serial port (#557).
            new object[] { OwnLighting, "internal static IAtkAcpiChannel OpenAtkAcpi()", "if (OffLimitsForTest) return null;" },
            new object[] { OwnLighting, "internal static IHpBiosChannel OpenHpBios()", "if (OffLimitsForTest) return null;" },
            new object[] { OwnLighting, "public byte[] Call(int command, int commandType, byte[] input)", "if (OffLimitsForTest) return null;" },
            new object[] { OwnLighting, "internal static bool HpBiosCallOnce(int command, int commandType, byte[] input, int timeoutMs)", "if (OffLimitsForTest) return false;" },
            new object[] { OwnLighting, "internal static ISerialLineChannel OpenSerial(string path, int baud)", "if (OffLimitsForTest) return null;" },
            new object[] { OwnLighting, "internal static bool SerialWriteOnce(string path, int baud, byte[] bytes, int timeoutMs)", "if (OffLimitsForTest) return false;" },
            new object[] { OwnLighting, "internal static bool SetLenovoLightControlOwner(int owner, int timeoutMs)", "if (OffLimitsForTest) return false;" },
            new object[] { AuraCore, "internal static string MainboardName()", "if (OffLimitsForTest) return null;" },
            new object[] { OpenRgb, "internal static IOpenRgbLink Connect(int port, int timeoutMs)", "if (OffLimitsForTest) return null;" },
            new object[] { OpenRgbPort, "internal static int Find()", "if (OffLimitsForTest) return OpenRgbProtocol.DefaultPort;" },
            new object[] { OpenRgbPort, "internal static string ReadSettings(string path)", "if (OffLimitsForTest) return null;" },
            new object[] { OpenRgbPort, "internal static string CommandLineOf(uint processId)", "if (OffLimitsForTest) return null;" },

            // Steam.
            new object[] { SteamHttpFile, "private static HttpClient CreateClient()", "var http = OffLimitsForTest ? new HttpClient(new SendsNothing()) : new HttpClient();" },
            new object[] { SteamClientFile, "public async Task EnsureLoggedOnAsync(CancellationToken ct = default)", "if (SteamHttp.OffLimitsForTest)" },
        };

        [Theory]
        [MemberData(nameof(AskedFirst))]
        public void ACoveredMember_AsksItsFenceBeforeAnythingElse(string file, string signature, string guard)
            => FenceSource.AsksFirst(file, signature, guard);

        public static IEnumerable<object[]> AskedBeforeTheCall() => new[]
        {
            new object[] { Bluetooth, "public static bool TryDisconnect(string serial)", "if (OffLimitsForTest) return false;", "BluetoothFindFirstRadio(" },
            new object[] { VirtualController, "public void Connect()", "if (seam == null && OffLimitsForTest)", "_ctx.CreateController(_profile, _identityKey)" },
            new object[] { Kbm, "private IKbmHidSink CreateSink(HMProfile profile, bool keyboard)", "if (HMaestroVirtualController.OffLimitsForTest)", "_ctx.CreateController(profile, key)" },
            new object[] { Input + "HapticToneService.cs", "private static bool BuildSink(Sink s, Func<Action, bool> publish = null)", "if (DirectDeviceAccess.Refuses(path)) return false;", "h = CreateFileW(path, GENERIC_WRITE | GENERIC_READ, SHARE_RW," },
            new object[] { HeadTracker, "internal sealed class FreeTrackReader : IDisposable", "if (OffLimitsForTest && (_heapName == HeapName || _mutexName == MutexName))", "MemoryMappedFile.CreateOrOpen(" },
            new object[] { Input + "InputManager.Step5.VirtualDevices.cs", "private void EnsureHMaestroContext()", "if (HMaestroVirtualController.OffLimitsForTest) return;", "lock (_hmaestroContextLock)" },
            new object[] { Input + "InputManager.cs", "private bool InitializeSdl()", "if (DirectDeviceAccess.RefusesAll)", "SDL_Init(" },
            new object[] { Input + "SonyHeadsetMotionDevice.cs", "public bool Open()", "if (DirectDeviceAccess.Refuses(DevicePath)) return false;", "SonyHeadsetHid.CreateFile(DevicePath," },
            new object[] { Input + "WiiSpeakerService.cs", "private static bool BuildSink(Sink s)", "if (DirectDeviceAccess.Refuses(s.HidPath)) return false;", "h = CreateFileW(s.HidPath," },
            new object[] { WiiPairing, "internal PairPassResult RunPairingPass(bool temporary, CancellationToken ct,", "if (PadForge.Common.Input.BluetoothLinkHelper.OffLimitsForTest)", "BluetoothFindFirstRadio(" },
            new object[] { Ds3Driver, "internal static string EnsureSigningCertificate()", "if (OffLimitsForTest)", "RSA.Create(" },
            new object[] { "PadForge.App/Common/HidHideArm64Installer.cs", "int? Run(string arguments)", "if (HidHideController.SeamsOnlyForTest)", "Process.Start(psi)" },

            // Sound.
            new object[] { SoundMacro, "private static SlotOutput EnsureDefaultOutput_NoLock(int slot)", "if (AudioEndpointAccess.Refuses) return o;", "new WasapiOut(" },
            new object[] { Passthrough, "private static void BuildTransportOnWorker(Sink s, Thread worker)", "if (AudioEndpointAccess.Refuses) return;", "new WasapiOut(" },

            // The programs other vendors run on the PC.
            new object[] { GKeys, "public bool Start()", VendorRefuses + ")", "NativeLibrary.TryLoad(" },
            new object[] { LgsLcd, "public static LogitechLcdLibrary TryLoad(out string reason)", VendorRefuses + ")", "NativeLibrary.TryLoad(" },
            new object[] { LgsArx, "public static LogitechArxLibrary TryLoad(out string reason)", VendorRefuses + ")", "NativeLibrary.TryLoad(" },
            new object[] { Strike7, "public static Strike7Library TryLoad(string path, out string reason)", VendorRefuses + ")", "NativeLibrary.TryLoad(" },
            new object[] { ForceDimension, "public static ForceDimensionLibrary TryLoad(string picked, out string reason)", VendorRefuses + ")", "NativeLibrary.TryLoad(" },
            // A Falcon is opened by Force Dimension's library, by index (#521).
            new object[] { Input + "InputManager.Falcon.cs", "private void FalconSweep()", "if (DirectDeviceAccess.RefusesAll && !FalconRuntime.ScriptedForTest) break;", "link.OpenIndex(index)" },

            // What is sent beyond this PC.
            new object[] { Stun, "public static async Task<IPEndPoint> QueryAsync(", RefusesHost + " return null;", "Dns.GetHostAddressesAsync(" },
            new object[] { LinkServerFile, "private async Task NatKeepaliveTickAsync()", RefusesHost + " continue;", "Dns.GetHostAddressesAsync(" },
            new object[] { LinkServerFile, "public async Task<StunResult> ProbePublicEndpointAsync(CancellationToken ct = default)", RefusesHost + " continue;", "Dns.GetHostAddressesAsync(" },
            new object[] { Krpc, "public static async Task<System.Collections.Generic.List<IPEndPoint>> ResolveBootstrapAsync(CancellationToken ct)", RefusesHost + " continue;", "Dns.GetHostAddressesAsync(" },
            new object[] { Krpc, "public async Task SendAsync(byte[] datagram, IPEndPoint node, CancellationToken ct)", "if (PadForge.Engine.Common.OutboundNetworkGate.Refuses(node?.Address)) return;", "_socket.SendToAsync(" },
            new object[] { LinkServerFile, "public async Task SendAsync(byte[] dg, CancellationToken ct)", "if (PadForge.Engine.Common.OutboundNetworkGate.Refuses(ep.Address)) return;", "_sock.SendToAsync(" },
            new object[] { LinkServerFile, "private bool SendSealed(LinkPeerConnection c, byte[] dg)", "if (ep != null && PadForge.Engine.Common.OutboundNetworkGate.Refuses(ep.Address)) return false;", "sock.SendTo(dg, ep)" },
            new object[] { SteamClientFile, "public async Task EnsureLoggedOnAsync(CancellationToken ct = default)", "if (SteamHttp.OffLimitsForTest)", "_client.Connect()" },

            // The port every DSU client polls.
            new object[] { Dsu, "public bool Start(int port = DefaultPort)", "if (OffLimitsForTest && port == DefaultPort) return false;", "new Socket(" },
            // The port every DSX game mod sends to, and the files they read
            // it from (#529).
            new object[] { GameMods, "public bool Start(int port, bool standIn)", "if (OffLimitsForTest && port == DefaultPort)", "new Socket(" },
            new object[] { GameModFiles, "internal static void Write(int port)", "if (OffLimitsForTest) return;", "WriteTo(" },
            // The ports games send their telemetry to, and the memory
            // Assetto Corsa writes (#531).
            new object[] { Telemetry, "private void BindUdp()", "if (OffLimitsForTest && IsSharedPort(_port))", "new Socket(" },
            new object[] { Kunos, "private bool TryOpen()", "if (OffLimitsForTest && (_physicsName == PhysicsName || _staticName == StaticName || _graphicsName == GraphicsName))", "MemoryMappedFile.OpenExisting(" },
        };

        /// <summary>Where a member does other work first, its fence is
        /// asked before the first call it covers.</summary>
        [Theory]
        [MemberData(nameof(AskedBeforeTheCall))]
        public void AFence_IsAskedBeforeTheCallItCovers(string file, string signature, string guard, string call)
            => FenceSource.AsksBefore(file, signature, guard, call);

        // ── Every call of a kind that reaches the machine is on a list ──

        /// <summary>A creation of one of the types: by its name, with or
        /// without its namespace written out and with or without arguments,
        /// or by a bare new() that takes its type from the declaration.</summary>
        private static string Creates(string types)
            => @"\bnew\s+(?:[A-Za-z_][\w.]*\.)?(?:" + types + @")\b(?!\s*[<\[])|\b(?:" + types + @")\s+\w+\s*=\s*new\s*\(";

        private static SortedDictionary<string, int> Map(params (string File, int Count)[] entries)
        {
            var map = new SortedDictionary<string, int>(StringComparer.Ordinal);
            foreach (var (file, count) in entries) map[file] = count;
            return map;
        }

        /// <summary>A program is started in eight places, each behind a
        /// fence: the shell's two, schtasks, the installers' tools and netsh
        /// here, and the driver installer's and the updater's under
        /// MachineOffLimitsTests and UpdateServiceOffLimitsTests.</summary>
        [Fact]
        public void EveryProcessStart_IsOnTheList()
        {
            Assert.Equal(Map(
                ("PadForge.App/Common/DriverInstaller.cs", 1),
                ("PadForge.App/Common/HidHideArm64Installer.cs", 1),
                (Shell, 2),
                (Startup, 1),
                (Ds3Driver, 1),
                (Services + "UpdateService.cs", 1),
                (Services + "WebControllerServer.cs", 1),
                (WebTls, 1)), FenceSource.Census(@"\bProcess\.Start\("));
            // And none is made to be started by its own Start().
            Assert.Empty(FenceSource.Census(Creates("Process")));

            // The four that start a tool do it inside the member that asks.
            Assert.Contains("Process.Start(psi)", FenceSource.Body(OffLimitsSource.Read(Startup), "private static int RunSchtasks(string arguments)"), StringComparison.Ordinal);
            Assert.Contains("Process.Start(psi)", FenceSource.Body(OffLimitsSource.Read(Ds3Driver), "private static (int Code, string Output) RunTool(string exe, string args, string workingDir, int timeoutMs = 120000)"), StringComparison.Ordinal);
            Assert.Contains("Process.Start(psi)", FenceSource.Body(OffLimitsSource.Read(WebTls), "private static string RunNetsh(string arguments)"), StringComparison.Ordinal);
            Assert.Contains("Process.Start(psi)", FenceSource.Body(OffLimitsSource.Read(Services + "WebControllerServer.cs"), "private static string RunNetsh(string arguments)"), StringComparison.Ordinal);
        }

        [Fact]
        public void TheClipboard_IsWrittenInOnePlace()
            => Assert.Equal(Map((Shell, 1)), FenceSource.Census(@"\bClipboard\.(Set[A-Za-z]*|Clear|Flush)\("));

        /// <summary>Each import that injects input, moves the cursor or
        /// installs a hook is named Native and called once, by the wrapper
        /// that asks first.</summary>
        [Fact]
        public void EveryInjectionAndHook_GoesThroughItsWrapper()
        {
            var imports = Map((Hooks, 2), (Macros, 1), (Kbm, 2), (Cursor, 1));
            Assert.Equal(imports, FenceSource.Census(@"\bextern\b.*\b(SendInput|SetCursorPos|SetWindowsHookEx|keybd_event|mouse_event)[A-Za-z]*\("));
            Assert.Equal(imports, FenceSource.Census(@"\bextern\b.*\b(SendInputNative|SetCursorPosNative|SetWindowsHookExNative)\("));
            Assert.Equal(imports, FenceSource.Census(@"(=>|\?|:|&&)[^;]*\b(SendInputNative|SetCursorPosNative|SetWindowsHookExNative)\("));
            // An import and its one call: two mentions a file for each.
            Assert.Equal(imports.ToDictionary(p => p.Key, p => p.Value * 2),
                FenceSource.Census(@"\b(SendInputNative|SetCursorPosNative|SetWindowsHookExNative)\("));
        }

        /// <summary>A pipe is served in one place and connected to in two.
        /// The app's own startup forwards a command to the instance that
        /// already runs, which no test runs. The control service asks its
        /// fence as it starts, and both its server and the nudge its Stop
        /// sends exist only once it has started.</summary>
        [Fact]
        public void EveryPipe_IsOnTheList()
        {
            Assert.Equal(Map((ControlPipe, 1)), FenceSource.Census(@"NamedPipeServerStreamAcl\.Create\(|" + Creates("NamedPipeServerStream")));
            Assert.Equal(Map(("PadForge.App/App.xaml.cs", 1), (ControlPipe, 1)), FenceSource.Census(Creates("NamedPipeClientStream")));

            string service = OffLimitsSource.Read(ControlPipe);
            Assert.StartsWith("if (_cts == null) return;", FenceSource.FirstStatement(service, "public void Stop()"), StringComparison.Ordinal);
            Assert.Single(Regex.Matches(service, @"\b_cts = new CancellationTokenSource\(\);"));
            Assert.Contains("_cts = new CancellationTokenSource();", FenceSource.Body(service, "public void Start()"), StringComparison.Ordinal);
            Assert.Single(OffLimitsSource.Calls(service, "CreateServer(_pipeName)"));
            Assert.Contains("CreateServer(_pipeName)", FenceSource.Body(service, "private async Task AcceptLoop(CancellationToken token)"), StringComparison.Ordinal);
            Assert.Single(OffLimitsSource.Calls(service, "AcceptLoop(token)"));
            Assert.Contains("AcceptLoop(token)", FenceSource.Body(service, "public void Start()"), StringComparison.Ordinal);
        }

        /// <summary>The files that import CreateFile. A new one opens
        /// something of the PC's and fails here until its opens are read:
        /// a writer gets a fence, and an open that asks for no access, or
        /// only reads, is left as it is.</summary>
        [Fact]
        public void EveryFileThatImportsCreateFile_IsOnTheList()
        {
            var expected = new[]
            {
                "PadForge.App/Common/HidHideController.cs",
                "PadForge.App/Common/Input/AudioPassthroughService.cs",
                "PadForge.App/Common/Input/BluetoothLinkHelper.cs",
                "PadForge.App/Common/Input/Ds3DirectService.cs",
                "PadForge.App/Common/Input/HapticToneService.cs",
                "PadForge.App/Common/Input/KeyboardScreens/G19UsbScreen.cs",
                "PadForge.App/Common/Input/Peripherals/OwnLightingChannels.cs",
                "PadForge.App/Common/Input/Peripherals/TouchSenseWinUsb.cs",
                "PadForge.App/Common/Input/PlayStationEffectWriter.cs",
                "PadForge.App/Common/Input/PsMoveDirectService.cs",
                "PadForge.App/Common/Input/RawHidOutput.cs",
                "PadForge.App/Common/Input/SonyHeadsetHid.cs",
                "PadForge.App/Common/Input/WiiSpeakerService.cs",
                "PadForge.App/Common/Input/XboxGipGuideLedWriter.cs",
                "PadForge.App/Common/Input/XboxImpulseHidWriter.cs",
                "PadForge.App/Services/Ds3DriverInstaller.cs",
                "PadForge.App/Services/Ds3PairingService.cs",
                "PadForge.Engine/Common/RawInputListener.cs",
                "PadForge.Engine/Common/SdlDeviceWrapper.cs",
                "PadForge.Engine/Common/Tablets/TabletNative.cs",
            };
            Assert.Equal(expected, FenceSource.Census(@"\bextern\b.*\bCreateFile[A-Za-z]*\(").Keys.ToArray());
        }

        /// <summary>Two writes sit behind a fence asked one call up. A
        /// pairing record's key comes from OpenKeysBackupRestore alone, which
        /// answers no key here. And a Wii controller is paired by one
        /// member, called from the pass that asks at its start.</summary>
        [Fact]
        public void TheWritesBehindAnotherMembersFence_HaveOneWayIn()
        {
            string installer = OffLimitsSource.Read(Ds3Driver);
            foreach (string member in new[]
            {
                "public static bool WriteLinkKeyAnchor(byte[] radioMacBigEndian, string deviceMacHex, Action<string> log)",
                "public static void DeleteLinkKeyAnchor(byte[] radioMacBigEndian, string deviceMacHex, Action<string> log)",
            })
            {
                string body = FenceSource.Body(installer, member);
                int key = body.IndexOf("IntPtr hk = OpenKeysBackupRestore(radioMacBigEndian, log);", StringComparison.Ordinal);
                int write = Math.Max(body.IndexOf("RegSetValueEx(hk,", StringComparison.Ordinal), body.IndexOf("RegDeleteValue(hk,", StringComparison.Ordinal));
                Assert.True(key > 0 && write > key, member);
                Assert.Contains("if (hk == IntPtr.Zero) return", body, StringComparison.Ordinal);
            }

            string pairing = OffLimitsSource.Read(WiiPairing);
            string pass = FenceSource.Body(pairing, "internal PairPassResult RunPairingPass(bool temporary, CancellationToken ct,");
            string pair = FenceSource.Body(pairing, "private bool TryPairDevice(");
            Assert.Contains("if (TryPairDevice(", pass, StringComparison.Ordinal);
            Assert.Single(Regex.Matches(pairing, @"(?<!private bool )\bTryPairDevice\("));
            Assert.Contains("BluetoothAuthenticateDevice(", pair, StringComparison.Ordinal);
            Assert.Contains("BluetoothSetServiceState(", pair, StringComparison.Ordinal);
            Assert.Contains("BluetoothRemoveDevice(", pass, StringComparison.Ordinal);
        }

        /// <summary>Sound is played on an endpoint in three places and a
        /// sound setting changed in four, each in a member that asks
        /// first.</summary>
        [Fact]
        public void EverySoundOutputAndSoundSetting_IsOnTheList()
        {
            Assert.Equal(Map((Passthrough, 3), (Macros, 3), (RumbleAudio, 1), (SoundMacro, 1)), FenceSource.Census(
                Creates("WasapiOut|WaveOutEvent|DirectSoundOut|AsioOut")
                + @"|\.(SetEndpointVisibility|SetDefaultEndpoint|SetMasterVolumeLevelScalar|SetMasterVolumeLevel|SetMute)\("
                + @"|GetDelegateForFunctionPointer<SetMasterVolumeFn>"));

            string passthrough = OffLimitsSource.Read(Passthrough);
            Assert.Contains("pc.SetEndpointVisibility(endpointId, 1)",
                FenceSource.Body(passthrough, "public static bool TryEnableEndpoint(string endpointId)"), StringComparison.Ordinal);
            Assert.Contains("pc.SetDefaultEndpoint(endpointId, role)",
                FenceSource.Body(passthrough, "public static bool TrySetDefaultEndpoint(string endpointId)"), StringComparison.Ordinal);
            string macros = OffLimitsSource.Read(Macros);
            Assert.Equal(2, Regex.Matches(FenceSource.Body(macros, "private void SetSystemVolume(float volume, bool showOsd = true)"),
                Regex.Escape("_audioEndpointVolume.SetMasterVolumeLevelScalar(volume, ref emptyGuid);")).Count);
            Assert.Contains("GetDelegateForFunctionPointer<SetMasterVolumeFn>",
                FenceSource.Body(macros, "internal static bool TrySetSessionVolume(IntPtr pSession, float volume)"), StringComparison.Ordinal);
        }

        /// <summary>A library of another vendor's is loaded by its path in
        /// nine places. Logitech's four, Mad Catz's one (#519), Force
        /// Dimension's one (#521) and MSI's Mystic Light library (#557) ask
        /// the vendor gate. The two VR runtimes ask theirs
        /// (VrRuntimeOffLimitsTests).</summary>
        [Fact]
        public void EveryLibraryLoadedByPath_IsOnTheList()
            => Assert.Equal(Map(
                ("PadForge.App/Common/Input/OpenVrConsumerService.cs", 1),
                (LogiLed, 1),
                (GKeys, 1),
                (LgsLcd, 1),
                (LgsArx, 1),
                (Strike7, 1),
                (ForceDimension, 1),
                (MysticLight, 1),
                ("PadForge.Engine/Common/OpenXr/OpenXrSession.cs", 1)),
                FenceSource.Census(@"NativeLibrary\.(Try)?Load\(|\bLoadLibrary(Ex)?[AW]?\(", callsOnly: true));

        /// <summary>What can carry a request off this PC is made in the
        /// places listed: a name looked up, a stream client, an HTTP client
        /// and a datagram sent. Each asks the gate, or its own fence, or
        /// cannot leave loopback.</summary>
        [Fact]
        public void EveryClientThatLeavesThisPc_IsOnTheList()
        {
            // A name looked up: each behind the gate (AskedBeforeTheCall).
            Assert.Equal(Map((Krpc, 1), (LinkServerFile, 2), (Stun, 1)), FenceSource.Census(@"\bDns\.[A-Z][A-Za-z]*\("));

            // A stream client: the relay's, the link's, Steam's and the
            // Twitch Chat row's (#555), whose session asks the gate first,
            // and the OpenRGB route's, which connects to loopback alone and
            // asks its own fence first (#557).
            Assert.Equal(Map((Relay, 1), (LinkServerFile, 1), (SteamClientFile, 1), (TwitchChat, 1), (OpenRgb, 1)),
                FenceSource.Census(Creates("TcpClient|ClientWebSocket|SteamClient")));

            // An HTTP client. The driver installer's, the updater's and
            // Steam's refuse under their own flags, and the two vendor
            // clients under the vendor gate. The workshop browser's is made
            // by the gate.
            Assert.Equal(Map(
                ("PadForge.App/Common/DriverInstaller.cs", 2),
                (Chroma, 1),
                (GameSense, 1),
                (Services + "UpdateService.cs", 1),
                (Outbound, 2),
                (SteamHttpFile, 2)), FenceSource.Census(Creates("HttpClient")));
            Assert.Contains("var http = PadForge.Engine.Common.OutboundNetworkGate.NewHttpClient();",
                OffLimitsSource.Read(WorkshopDialog), StringComparison.Ordinal);

            // A datagram sent. The DSU server's and the game mod server's
            // sockets are bound to loopback and the discovery socket is bound
            // behind the listener gate.
            // The hole puncher sends through the link's adapter. The others
            // ask the gate (AskedFirst and AskedBeforeTheCall).
            Assert.Equal(Map(
                (Services + "DsuMotionServer.cs", 3),
                (GameMods, 1),
                // The telemetry row's forward socket is bound to loopback and
                // sends to 127.0.0.1 only (#531).
                (Telemetry, 1),
                (Krpc, 1),
                ("PadForge.Engine/RemoteLink/HolePuncher.cs", 2),
                ("PadForge.Engine/RemoteLink/LinkDiscovery.cs", 1),
                (LinkServerFile, 4),
                (Stun, 1)), FenceSource.Census(@"\.SendTo(Async)?\("));
            Assert.Equal(2, Regex.Matches(OffLimitsSource.Read("PadForge.Engine/RemoteLink/HolePuncher.cs"), @"_transport\.SendToAsync\(").Count);
        }

        // ── What a fence answers ──

        [Theory]
        [InlineData(@"\\?\hid#vid_046d&pid_c539&mi_02#7&1a2b3c&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}", true)]
        [InlineData(@"\\?\usb#vid_054c&pid_0268#5&2a3b4c&0&1#{b35924d6-3e16-4a9e-9782-5524a4b79bac}", true)]
        [InlineData(@"\\.\XboxGIP", true)]
        [InlineData(@"\\.\BthPS3PSMControl", true)]
        [InlineData(@"\\.\pipe\padforge-test", false)]
        [InlineData(@"\\?\C:\Temp\file.bin", false)]
        [InlineData(@"\\?\UNC\server\share\file.bin", false)]
        [InlineData(@"C:\Temp\file.bin", false)]
        [InlineData(@"\\?\", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void ADevicePath_IsToldFromAFileAndAPipe(string path, bool device)
        {
            Assert.Equal(device, DirectDeviceAccess.IsDevicePath(path));
            Assert.Equal(device, DirectDeviceAccess.Refuses(path));
            Assert.True(DirectDeviceAccess.RefusesAll);
        }

        /// <summary>The shell is handed nothing. The program named exists
        /// on no PC, so a call that got through would throw instead of
        /// answering no process.</summary>
        [Fact]
        public void TheShell_StartsNothing()
        {
            FenceSource.AsksFirst(Shell, "internal static Process Start(ProcessStartInfo psi)", "OffLimitsForTest ? null : Process.Start(psi);");
            FenceSource.AsksFirst(Shell, "internal static Process Start(string fileName, string arguments)", "OffLimitsForTest ? null : Process.Start(fileName, arguments);");
            const string nothing = "padforge-no-such-program-4f2a9c.exe";
            Assert.Null(ShellLaunch.Start(new ProcessStartInfo(nothing) { UseShellExecute = false }));
            Assert.Null(ShellLaunch.Start(nothing, "--none"));
        }

        /// <summary>A tablet capture opens no collection and restarts none.
        /// The path and the instance name no device, and the refusal is told
        /// from the failure a call that got through would meet by its own
        /// words.</summary>
        [Fact]
        public void ATabletCapture_OpensAndRestartsNothing()
        {
            FenceSource.AsksFirst(Tablet, "internal static Stream OpenCapture(string path, int reportLength)", "if (OffLimitsForTest) throw new IOException(");
            FenceSource.AsksFirst(Tablet, "internal static void RestartCollection(string instance, CancellationToken stop)", "if (OffLimitsForTest) throw new InvalidOperationException(");

            var open = Assert.Throws<IOException>(() => TabletNative.OpenCapture(@"C:\padforge-no-such-tablet-4f2a9c.bin", 8));
            Assert.Equal("The test host opens no tablet collection.", open.Message);
            var restart = Assert.Throws<InvalidOperationException>(
                () => TabletNative.RestartCollection(@"HID\VID_0000&PID_0000\padforge-none", CancellationToken.None));
            Assert.Equal("The test host restarts no HID collection.", restart.Message);
        }

        /// <summary>HIDMaestro is never removed from the PC (#518): the
        /// removal answers null, and the Settings flow's own path reaches it
        /// with no seam. The source is read before the call, since the
        /// call is the removal.</summary>
        [Fact]
        public void HIDMaestro_IsNotRemoved()
        {
            FenceSource.AsksFirst(VirtualController, "internal static HMUninstallReport RemoveHIDMaestro(HMUninstallOptions options)", "if (OffLimitsForTest) return null;");
            Assert.Contains("RunSeam != null ? RunSeam(options) : HMaestroVirtualController.RemoveHIDMaestro(options);",
                OffLimitsSource.Read(Services + "HIDMaestroRemoval.cs"), StringComparison.Ordinal);
            EveryFence_IsSetInTheTestHost();
            Assert.Null(HMaestroVirtualController.RemoveHIDMaestro(new HIDMaestro.HMUninstallOptions { IncludeUsbip = true }));
            Assert.Null(HIDMaestroRemoval.Run(includeUsbip: false, stopEngine: null));
        }

        /// <summary>The memory and the mutex every FreeTrack tracker and
        /// game shares are not opened. Opening the mapping makes it where no
        /// tracker has, so the source is read before the call.</summary>
        [Fact]
        public void TheSharedFreeTrackMemory_IsNotOpened()
        {
            FenceSource.AsksBefore(HeadTracker, "internal sealed class FreeTrackReader : IDisposable",
                "if (OffLimitsForTest && (_heapName == HeapName || _mutexName == MutexName))", "MemoryMappedFile.CreateOrOpen(");
            EveryFence_IsSetInTheTestHost();
            using var reader = new FreeTrackReader();
            Assert.False(reader.Open());
        }

        /// <summary>None of the three mutexes other analog keyboard
        /// programs share is made or opened, and a name of a test's own
        /// still is.</summary>
        [Fact]
        public void TheSharedMutexes_AreNotTaken()
        {
            FenceSource.AsksFirst(Mutex, "private static AnalogKeyboardNamedMutex Create(string name)", "if (OffLimitsForTest && IsShared(name)) return null;");
            Assert.Contains("=> name == DrunkDeer || name == Keychron || name == Madlions;", OffLimitsSource.Read(Mutex), StringComparison.Ordinal);
            EveryFence_IsSetInTheTestHost();
            Assert.Null(AnalogKeyboardNamedMutex.Get(AnalogKeyboardNamedMutex.DrunkDeer));
            Assert.Null(AnalogKeyboardNamedMutex.Get(AnalogKeyboardNamedMutex.Keychron));
            Assert.Null(AnalogKeyboardNamedMutex.Get(AnalogKeyboardNamedMutex.Madlions));
            Assert.NotNull(AnalogKeyboardNamedMutex.Get("PadForge.Tests.Fence." + Guid.NewGuid().ToString("N")));
        }

        /// <summary>schtasks is never started: the question about a task
        /// that exists on no PC is answered as a tool that did not run,
        /// where the tool itself would answer 1.</summary>
        [Fact]
        public void TheLogonTask_IsNeitherReadNorWritten()
        {
            FenceSource.AsksFirst(Startup, "private static int RunSchtasks(string arguments)", "if (OffLimitsForTest) return -1;");
            var run = typeof(StartupHelper).GetMethod("RunSchtasks", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.NotNull(run);
            Assert.Equal(-1, run.Invoke(null, new object[] { "/Query /TN \"PadForge-no-such-task-4f2a9c\"" }));
        }

        /// <summary>A program's volume in the mixer is not set. The session
        /// handed over is no session, so a call that got through would throw
        /// on it instead of answering false.</summary>
        [Fact]
        public void AProgramsVolume_IsNotSet()
        {
            FenceSource.AsksFirst(Macros, "internal static bool TrySetSessionVolume(IntPtr pSession, float volume)", "if (AudioEndpointAccess.Refuses) return false;");
            Assert.False(AudioSessionHelper.TrySetSessionVolume(IntPtr.Zero, 0.5f));
        }

        /// <summary>HIDMaestro's context neither asks about the GameInput
        /// service nor starts it. The SDK's step opens by reading its own
        /// flag and returns when the flag is set, which the test host has
        /// done before any context is built. Read from the SDK's compiled
        /// step, since its source is not PadForge's: a volatile read of the
        /// flag, a branch on it, and a return.</summary>
        [Fact]
        public void TheGameInputService_IsNeitherAskedAboutNorStarted()
        {
            FieldInfo flag = GameInputServiceOffLimits.Flag();
            Assert.True(flag != null, "this HIDMaestro has no " + GameInputServiceOffLimits.Owner + "." + GameInputServiceOffLimits.Name);
            Assert.Equal(typeof(bool), flag.FieldType);
            Assert.True((bool)flag.GetValue(null));

            MethodInfo step = flag.DeclaringType.GetMethod(GameInputServiceOffLimits.Step, BindingFlags.Static | BindingFlags.NonPublic);
            Assert.True(step != null, "this HIDMaestro has no " + GameInputServiceOffLimits.Step);
            byte[] il = step.GetMethodBody().GetILAsByteArray();
            // volatile. (FE 13), ldsfld (7E) and the flag's token, then a
            // short branch (2C brfalse.s or 2D brtrue.s) past a ret (2A).
            Assert.True(il.Length > 10);
            Assert.Equal(new byte[] { 0xFE, 0x13, 0x7E }, il[..3]);
            Assert.Equal(flag, step.Module.ResolveField(BitConverter.ToInt32(il, 3)));
            Assert.Contains(il[7], new byte[] { 0x2C, 0x2D });
            int ret = il[7] == 0x2C ? 9 : 9 + il[8];
            Assert.Equal(0x2A, il[ret]);
        }

        /// <summary>A control service left on the app's own pipe name starts
        /// nothing, so the test host neither answers a command meant for a
        /// running PadForge nor writes to its pipe. One on a name of a
        /// test's own still runs.</summary>
        [Fact]
        public void TheAppsOwnControlPipe_IsNeitherServedNorWrittenTo()
        {
            FenceSource.AsksFirst(ControlPipe, "public void Start()", "if (OffLimitsForTest && ServesTheAppsOwnPipe) return;");
            Assert.Contains("private bool ServesTheAppsOwnPipe => string.Equals(_pipeName, PipeName, StringComparison.OrdinalIgnoreCase);",
                OffLimitsSource.Read(ControlPipe), StringComparison.Ordinal);
            Assert.Equal("PadForge.Control", ExternalControlService.PipeName);
            EveryFence_IsSetInTheTestHost();

            foreach (string name in new[] { null, "", ExternalControlService.PipeName, "padforge.control" })
            {
                using var theApps = new ExternalControlService(_ => "ok", name);
                theApps.Start();
                Assert.False(theApps.IsRunning);
                theApps.Stop();
            }

            using var aTests = new ExternalControlService(_ => "ok", "PadForge.Tests.Fence." + Guid.NewGuid().ToString("N"));
            aTests.Start();
            Assert.True(aTests.IsRunning);
            aTests.Stop();
            Assert.False(aTests.IsRunning);
        }

        /// <summary>A DSU server left on the default port binds nothing, so
        /// no emulator on the PC finds the test host where it looks for a
        /// running PadForge. One on a port of a test's own still starts, on
        /// loopback as in the app.</summary>
        [Fact]
        public void TheDsuPort_IsNotBound()
        {
            FenceSource.AsksBefore(Dsu, "public bool Start(int port = DefaultPort)", "if (OffLimitsForTest && port == DefaultPort) return false;", "new Socket(");
            Assert.Equal(26760, DsuMotionServer.DefaultPort);
            Assert.Contains("_socket.Bind(new IPEndPoint(IPAddress.Loopback, port));", OffLimitsSource.Read(Dsu), StringComparison.Ordinal);
            EveryFence_IsSetInTheTestHost();

            using var theDefault = new DsuMotionServer();
            Assert.False(theDefault.Start());
            Assert.False(theDefault.Start(DsuMotionServer.DefaultPort));

            using var aTests = new DsuMotionServer();
            Assert.True(aTests.Start(0));
            aTests.Stop();
        }

        // ── The programs other vendors run on the PC ──

        [Theory]
        [InlineData("http://localhost:54235", true)]
        [InlineData("http://localhost:54235/", true)]
        [InlineData("HTTP://LOCALHOST:54235", true)]
        [InlineData("http://127.0.0.1:50123", false)]
        [InlineData("http://localhost:50123", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void RazersServer_IsToldFromATestsOwn(string endpoint, bool razers)
        {
            Assert.Equal("http://localhost:54235", ChromaBackend.DefaultEndpoint);
            Assert.Equal(razers, ChromaBackend.IsRazersServer(endpoint));
        }

        /// <summary>A backend built as the app builds it opens no session
        /// with Razer's server. The source is read first, so the request
        /// behind a guard that was removed is never made.</summary>
        [Fact]
        public async Task RazersServer_IsNotCalled()
        {
            const string signature = "private async Task<string> InitAsync(IReadOnlyList<string> categories, CancellationToken ct)";
            FenceSource.AsksFirst(Chroma, signature, VendorRefuses + " && IsRazersServer(_endpoint)) return null;");
            // The init is the one request sent to the endpoint itself. Every
            // other one goes to the session the init answers with.
            string source = OffLimitsSource.Read(Chroma);
            Assert.Single(Regex.Matches(source, @"\b_endpoint\s*\+"));
            Assert.Contains("_endpoint + \"/razer/chromasdk\"", FenceSource.Body(source, signature), StringComparison.Ordinal);
            Assert.Contains("_endpoint = string.IsNullOrEmpty(endpoint) ? DefaultEndpoint : endpoint.TrimEnd('/');", source, StringComparison.Ordinal);
            EveryFence_IsSetInTheTestHost();
            Assert.True(ChromaBackend.IsRazersServer(ChromaBackend.DefaultEndpoint));

            var init = typeof(ChromaBackend).GetMethod("InitAsync", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(init);
            using var backend = new ChromaBackend();
            var answer = (Task<string>)init.Invoke(backend, new object[] { new[] { "keyboard" }, CancellationToken.None });
            Assert.Null(await answer.WaitAsync(TimeSpan.FromSeconds(10)));
        }

        /// <summary>A client built as the app builds it reads no address of
        /// SteelSeries GG's and registers no game with it.</summary>
        [Fact]
        public void SteelSeriesGg_IsNotCalled()
        {
            FenceSource.AsksFirst(GameSense, "public bool TryConnect(long now)", VendorRefuses + " && IsGgsOwnFile(_corePropsPath)) return false;");
            Assert.Contains("_corePropsPath = corePropsPath ?? DefaultCorePropsPath;", OffLimitsSource.Read(GameSense), StringComparison.Ordinal);
            Assert.True(GameSenseClient.IsGgsOwnFile(GameSenseClient.DefaultCorePropsPath));
            Assert.True(GameSenseClient.IsGgsOwnFile(GameSenseClient.DefaultCorePropsPath.ToUpperInvariant()));
            Assert.False(GameSenseClient.IsGgsOwnFile(Path.Combine(Path.GetTempPath(), "coreProps.json")));
            Assert.False(GameSenseClient.IsGgsOwnFile(null));
            EveryFence_IsSetInTheTestHost();

            using var client = new GameSenseClient();
            Assert.False(client.TryConnect(0));
            Assert.False(client.Connected);
        }

        /// <summary>Logitech's LED engine is not loaded into the test host,
        /// on a PC that has G HUB as on one that does not.</summary>
        [Fact]
        public void LogitechsLedEngine_IsNotLoaded()
        {
            FenceSource.AsksFirst(LogiLed, "public bool TryLoad(out string detail)", VendorRefuses + ")");
            Assert.Contains("LoadLibraryExW(path, IntPtr.Zero, LoadWithAlteredSearchPath)",
                FenceSource.Body(OffLimitsSource.Read(LogiLed), "public bool TryLoad(out string detail)"), StringComparison.Ordinal);
            EveryFence_IsSetInTheTestHost();

            Assert.False(new LogiLedEngineNative().TryLoad(out string detail));
            Assert.Equal("the test host loads no vendor engine", detail);
        }

        /// <summary>Logitech's G-key library is not looked for or loaded, so
        /// no session is opened with G HUB.</summary>
        [Fact]
        public void LogitechsGKeyLibrary_IsNotLoaded()
        {
            FenceSource.AsksBefore(GKeys, "public bool Start()", VendorRefuses + ")", "LogitechGKeyCatalog.Candidates(");
            FenceSource.AsksBefore(GKeys, "public bool Start()", VendorRefuses + ")", "NativeLibrary.TryLoad(");
            EveryFence_IsSetInTheTestHost();

            var said = new List<string>();
            using var source = new LogitechGKeySource(_ => { }, said.Add);
            Assert.False(source.Start());
            Assert.Equal(LogitechGKeyState.NoSdk, source.State);
            Assert.Equal(new[] { "G-Keys: the test host loads no G-key library" }, said);
        }

        /// <summary>The Sensa service runs its engine in the test host and
        /// never the provider, which reaches Razer's runtime on the PC and
        /// renders on a Sensa device. Read, never run past: the provider is
        /// started in one place, and rendered and cleaned up only where it
        /// started.</summary>
        [Fact]
        public void TheSensaProvider_IsNeverStarted()
        {
            string source = OffLimitsSource.Read(Sensa);
            Assert.Single(OffLimitsSource.Calls(source, "Provider.ProviderInit()"));
            Assert.Contains("try { providerUp = !PadForge.Engine.Common.VendorProgramGate.Refuses && Provider.ProviderInit(); }", source, StringComparison.Ordinal);
            // Declared false, and set in that one place.
            Assert.Contains("bool providerUp = false;", source, StringComparison.Ordinal);
            Assert.Equal(2, Regex.Matches(source, @"\bproviderUp = ").Count);
            Assert.Single(OffLimitsSource.Calls(source, "Provider.ProviderRenderHaptics()"));
            Assert.Contains("if (providerUp && Provider.ProviderIsPresent())\n                        Provider.ProviderRenderHaptics();", source, StringComparison.Ordinal);
            Assert.Single(OffLimitsSource.Calls(source, "Provider.ProviderClean()"));
            Assert.Contains("if (providerUp)\n                    {\n                        try { Provider.ProviderClean(); } catch { }", source, StringComparison.Ordinal);
        }

        // ── What is sent beyond this PC ──

        public static IEnumerable<object[]> Addresses() => new[]
        {
            new object[] { IPAddress.Loopback, false },
            new object[] { IPAddress.IPv6Loopback, false },
            new object[] { IPAddress.Parse("127.0.0.2"), false },
            new object[] { IPAddress.Parse("192.168.1.20"), true },
            new object[] { IPAddress.Parse("203.0.113.7"), true },
            new object[] { IPAddress.Broadcast, true },
            new object[] { null, true },
        };

        [Theory]
        [MemberData(nameof(Addresses))]
        public void TheOutboundGate_PassesALoopbackAddressAlone(IPAddress address, bool refused)
            => Assert.Equal(refused, OutboundNetworkGate.Refuses(address));

        [Theory]
        [InlineData("localhost", false)]
        [InlineData("LOCALHOST", false)]
        [InlineData("127.0.0.1", false)]
        [InlineData("::1", false)]
        [InlineData("[::1]", false)]
        [InlineData("stun.l.google.com", true)]
        [InlineData("localhost.example.com", true)]
        [InlineData("192.168.1.20", true)]
        [InlineData("203.0.113.7", true)]
        [InlineData("", true)]
        [InlineData(null, true)]
        public void TheOutboundGate_PassesALoopbackHostAlone(string host, bool refused)
        {
            Assert.Equal(!refused, OutboundNetworkGate.IsLoopbackHost(host));
            Assert.Equal(refused, OutboundNetworkGate.RefusesHost(host));
        }

        /// <summary>A request to a server on the network fails before it is
        /// sent. The address is one set aside for documents, which no host
        /// has, so a request that got through would reach nothing.</summary>
        [Fact]
        public async Task ARequestBeyondThisPc_IsNotSent()
        {
            FenceSource.AsksFirst(Outbound, "public static HttpClient NewHttpClient()", "OffLimitsForTest ? new HttpClient(new LoopbackOnly()) : new HttpClient();");
            string gate = OffLimitsSource.Read(Outbound);
            string handler = FenceSource.Body(gate, "private sealed class LoopbackOnly : DelegatingHandler");
            Assert.Equal(2, Regex.Matches(handler, Regex.Escape("ThrowIfBeyondLoopback(request);\n                return base.Send")).Count);
            Assert.Contains("if (uri != null && uri.IsAbsoluteUri && IsLoopbackHost(uri.IdnHost)) return;\n"
                + "                throw new HttpRequestException(", handler, StringComparison.Ordinal);
            Assert.Contains("if (string.IsNullOrEmpty(host)) return false;\n"
                + "            if (string.Equals(host, \"localhost\", StringComparison.OrdinalIgnoreCase)) return true;\n"
                + "            return IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address);\n", gate, StringComparison.Ordinal);
            EveryFence_IsSetInTheTestHost();
            Assert.True(OutboundNetworkGate.RefusesHost("192.0.2.1"));

            using var http = OutboundNetworkGate.NewHttpClient();
            http.Timeout = TimeSpan.FromSeconds(5);
            var refused = await Assert.ThrowsAsync<HttpRequestException>(() => http.GetAsync("http://192.0.2.1/padforge-off-limits-check"));
            Assert.Equal("The test host sends no request beyond this PC: 192.0.2.1", refused.Message);
            refused = Assert.Throws<HttpRequestException>(() => http.Send(new HttpRequestMessage(HttpMethod.Get, "https://padforge-off-limits-check.invalid/")));
            Assert.Equal("The test host sends no request beyond this PC: padforge-off-limits-check.invalid", refused.Message);
        }

        /// <summary>A request to a server of a test's own on this PC still
        /// goes.</summary>
        [Fact]
        public async Task ARequestToThisPc_StillGoes()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                var served = Task.Run(async () =>
                {
                    using var peer = await listener.AcceptTcpClientAsync();
                    var stream = peer.GetStream();
                    var head = new byte[4096];
                    int read = 0;
                    // To the blank line that ends the request's head.
                    while (read < head.Length && !Encoding.ASCII.GetString(head, 0, read).Contains("\r\n\r\n", StringComparison.Ordinal))
                    {
                        int n = await stream.ReadAsync(head.AsMemory(read));
                        if (n == 0) break;
                        read += n;
                    }
                    await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 204 No Content\r\nConnection: close\r\n\r\n"));
                });

                using var http = OutboundNetworkGate.NewHttpClient();
                using var response = await http.GetAsync("http://127.0.0.1:" + port + "/").WaitAsync(TimeSpan.FromSeconds(15));
                Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
                await served.WaitAsync(TimeSpan.FromSeconds(15));
            }
            finally { listener.Stop(); }
        }

        /// <summary>The DHT's bootstrap routers are not looked up, so the
        /// lookup that finds them on any PC with a network finds none
        /// here.</summary>
        [Fact]
        public async Task TheDhtsBootstrapNames_AreNotLookedUp()
        {
            FenceSource.AsksBefore(Krpc, "public static async Task<System.Collections.Generic.List<IPEndPoint>> ResolveBootstrapAsync(CancellationToken ct)",
                RefusesHost + " continue;", "Dns.GetHostAddressesAsync(");
            Assert.NotEmpty(DhtPresenceStore.DefaultBootstrap);
            EveryFence_IsSetInTheTestHost();
            Assert.All(DhtPresenceStore.DefaultBootstrap, router => Assert.True(OutboundNetworkGate.RefusesHost(router.Host)));
            Assert.Empty(await UdpKrpcTransport.ResolveBootstrapAsync(CancellationToken.None));
        }
    }
}
