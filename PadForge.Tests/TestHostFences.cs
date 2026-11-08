using System.Reflection;
using System.Runtime.CompilerServices;
using PadForge.Common;
using PadForge.Common.Input;
using PadForge.Common.Input.Peripherals;
using PadForge.Common.Input.Telemetry;
using PadForge.Engine.Common;
using PadForge.Engine.Common.AnalogKeyboard;
using PadForge.Engine.Tablets;
using PadForge.Services;
using PadForge.SteamWorkshop.Api;

// Each class below keeps the test process off one part of the PC it runs on,
// for its whole life. A test hands the code under test a fake, a seam or a
// path of its own. A mutation run builds the code with a guard taken out, and
// a guard is often what stood between a test and the real call. With these
// set the real calls refuse, so such a build fails its test and leaves the
// desktop, the devices, the radio, the drivers, the sound, the other vendors'
// programs and the network of the bench as they were. Nothing in the app sets
// any of them (TestHostFenceTests). The last class sets a flag of the
// HIDMaestro SDK's and not one of PadForge's.
namespace PadForge.Tests
{
#pragma warning disable CA2255 // Only the test host loads this assembly.

    /// <summary>No device is opened by the writers that make their own
    /// handles, and SDL's device layer and the smart card service are not
    /// started.</summary>
    internal static class DeviceOutputOffLimits
    {
        [ModuleInitializer]
        internal static void Set() => DirectDeviceAccess.OffLimitsForTest = true;
    }

    /// <summary>Nothing is handed to the shell: no page, folder or program
    /// is opened, and the clipboard stays as it is.</summary>
    internal static class ShellOffLimits
    {
        [ModuleInitializer]
        internal static void Set() => ShellLaunch.OffLimitsForTest = true;
    }

    /// <summary>The screen is not read: no capture of a display starts
    /// without a test's own grabber (#559).</summary>
    internal static class ScreenCaptureOffLimits
    {
        [ModuleInitializer]
        internal static void Set() => PadForge.Common.Input.ScreenColor.ScreenColorSource.OffLimitsForTest = true;
    }

    /// <summary>No socket or listener is bound beyond loopback.</summary>
    internal static class ListenerOffLimits
    {
        [ModuleInitializer]
        internal static void Set() => NetworkListenerGate.OffLimitsForTest = true;
    }

    /// <summary>No Bluetooth link is dropped, no controller powered off, no
    /// device paired, unpaired or rebound.</summary>
    internal static class BluetoothOffLimits
    {
        [ModuleInitializer]
        internal static void Set() => BluetoothLinkHelper.OffLimitsForTest = true;
    }

    /// <summary>No virtual controller device is created, no HIDMaestro
    /// context set up, and no sweep of the PC's virtual controllers
    /// run.</summary>
    internal static class VirtualControllerOffLimits
    {
        [ModuleInitializer]
        internal static void Set() => HMaestroVirtualController.OffLimitsForTest = true;
    }

    /// <summary>The FreeTrack memory and mutex every tracker and game
    /// shares are not opened.</summary>
    internal static class FreeTrackOffLimits
    {
        [ModuleInitializer]
        internal static void Set() => FreeTrackReader.OffLimitsForTest = true;
    }

    /// <summary>No key, button, wheel turn or cursor move is injected, and
    /// no low-level hook installed.</summary>
    internal static class DesktopInputOffLimits
    {
        [ModuleInitializer]
        internal static void Set() => InputHookManager.OffLimitsForTest = true;
    }

    /// <summary>The logon task and the legacy Run value are left
    /// alone.</summary>
    internal static class StartupOffLimits
    {
        [ModuleInitializer]
        internal static void Set() => StartupHelper.OffLimitsForTest = true;
    }

    /// <summary>No give-back task is registered or deleted, so nothing runs
    /// at the next boot of the PC that ran the tests.</summary>
    internal static class GiveBackTaskOffLimits
    {
        [ModuleInitializer]
        internal static void Set() => GiveBackTask.OffLimitsForTest = true;
    }

    /// <summary>No driver is installed, removed, signed or forced onto a
    /// device, no certificate added or removed, no radio cycled or
    /// switched, no setup's uninstaller run, and no pairing record
    /// written.</summary>
    internal static class Ds3DriverOffLimits
    {
        [ModuleInitializer]
        internal static void Set() => Ds3DriverInstaller.OffLimitsForTest = true;
    }

    /// <summary>The web controller's certificate, its http.sys binding and
    /// its firewall rules are not made.</summary>
    internal static class WebControllerSetupOffLimits
    {
        [ModuleInitializer]
        internal static void Set() => WebControllerTls.OffLimitsForTest = true;
    }

    /// <summary>The mutexes other analog keyboard programs share are not
    /// taken.</summary>
    internal static class AnalogKeyboardMutexOffLimits
    {
        [ModuleInitializer]
        internal static void Set() => AnalogKeyboardNamedMutex.OffLimitsForTest = true;
    }

    /// <summary>No tablet collection is captured or restarted.</summary>
    internal static class TabletOffLimits
    {
        [ModuleInitializer]
        internal static void Set() => TabletNative.OffLimitsForTest = true;
    }

    /// <summary>No sound is played on an audio endpoint of the PC, and its
    /// volume, its mixer, its endpoints and its default output are left as
    /// they are.</summary>
    internal static class AudioEndpointOffLimits
    {
        [ModuleInitializer]
        internal static void Set() => AudioEndpointAccess.OffLimitsForTest = true;
    }

    /// <summary>The programs other vendors run on the PC are not called:
    /// Razer's Chroma server and Sensa runtime, SteelSeries GG, and
    /// Logitech's LED engine and G-key library.</summary>
    internal static class VendorProgramsOffLimits
    {
        [ModuleInitializer]
        internal static void Set() => VendorProgramGate.OffLimitsForTest = true;
    }

    /// <summary>Nothing is sent beyond loopback: no call to a peer, a relay
    /// or a STUN server, and no name looked up for one.</summary>
    internal static class OutboundOffLimits
    {
        [ModuleInitializer]
        internal static void Set() => OutboundNetworkGate.OffLimitsForTest = true;
    }

    /// <summary>No request is sent to Steam, and the Steam client connects
    /// to no server.</summary>
    internal static class SteamOffLimits
    {
        [ModuleInitializer]
        internal static void Set() => SteamHttp.OffLimitsForTest = true;
    }

    /// <summary>No pipe is served or written to under the name a running
    /// PadForge serves.</summary>
    internal static class ControlPipeOffLimits
    {
        [ModuleInitializer]
        internal static void Set() => ExternalControlService.OffLimitsForTest = true;
    }

    /// <summary>No DSU server is started on the port every DSU client on
    /// the PC polls.</summary>
    internal static class DsuPortOffLimits
    {
        [ModuleInitializer]
        internal static void Set() => DsuMotionServer.OffLimitsForTest = true;
    }

    /// <summary>No game mod server is started on the port every DSX game
    /// mod on the PC sends to (#529).</summary>
    internal static class GameModPortOffLimits
    {
        [ModuleInitializer]
        internal static void Set() => GameModServer.OffLimitsForTest = true;
    }

    /// <summary>No telemetry row binds a port the PC's games send to
    /// (#531).</summary>
    internal static class GameTelemetryPortOffLimits
    {
        [ModuleInitializer]
        internal static void Set() => GameTelemetryDevice.OffLimitsForTest = true;
    }

    /// <summary>The shared memory Assetto Corsa and ACC write is not
    /// opened (#531).</summary>
    internal static class KunosOffLimits
    {
        [ModuleInitializer]
        internal static void Set() => KunosSharedMemory.OffLimitsForTest = true;
    }

    /// <summary>The port files every DSX game mod on the PC reads are left
    /// as they are (#529).</summary>
    internal static class GameModPortFilesOffLimits
    {
        [ModuleInitializer]
        internal static void Set() => GameModPortFiles.OffLimitsForTest = true;
    }

    /// <summary>No laptop's lighting is set through its firmware's ACPI
    /// device, BIOS WMI method or Lenovo GameZone WMI method, and no serial
    /// port is opened (#557).</summary>
    internal static class OwnLightingOffLimits
    {
        [ModuleInitializer]
        internal static void Set() => OwnLightingChannels.OffLimitsForTest = true;
    }

    /// <summary>The ASUS Aura Core family does not read the PC's mainboard
    /// name through WMI (#557).</summary>
    internal static class AsusAuraCoreOffLimits
    {
        [ModuleInitializer]
        internal static void Set() => PadForge.Common.Input.Peripherals.AsusAuraCoreLighting.OffLimitsForTest = true;
    }

    /// <summary>No connection is made to an OpenRGB SDK server, the one
    /// OpenRGB serves on the PC included (#557).</summary>
    internal static class OpenRgbServerOffLimits
    {
        [ModuleInitializer]
        internal static void Set() => OpenRgbTcpLink.OffLimitsForTest = true;
    }

    /// <summary>OpenRGB's process is not opened for its command line, and
    /// its settings file is not read (#557).</summary>
    internal static class OpenRgbPortOffLimits
    {
        [ModuleInitializer]
        internal static void Set() => OpenRgbServerPort.OffLimitsForTest = true;
    }

    /// <summary>The iCUE SDK's library is not loaded, so nothing reaches
    /// Corsair iCUE on the PC (#557).</summary>
    internal static class IcueSdkOffLimits
    {
        [ModuleInitializer]
        internal static void Set() => IcueSdkNative.OffLimitsForTest = true;
    }

    /// <summary>The GameInput service is left as it is. HIDMaestro's context
    /// asks sc.exe about the service as it is built, and where the service
    /// is stopped it sets it to start with Windows and starts it. The
    /// profile catalog builds a context in nearly every test run. The SDK
    /// keeps one private flag that says the service is known to run, and
    /// with the flag set it asks nothing and starts nothing. The flag is
    /// the SDK's and has no name PadForge can reach, so it is set here
    /// through reflection, and TestHostFenceTests fails when an SDK no
    /// longer has it or no longer reads it first.</summary>
    internal static class GameInputServiceOffLimits
    {
        internal const string Owner = "HIDMaestro.Internal.DeviceOrchestrator";
        internal const string Name = "s_gameInputSvcRunning";
        internal const string Step = "EnsureGameInputService";

        internal static FieldInfo Flag()
            => typeof(HIDMaestro.HMContext).Assembly.GetType(Owner)?.GetField(Name, BindingFlags.Static | BindingFlags.NonPublic);

        [ModuleInitializer]
        internal static void Set() => Flag()?.SetValue(null, true);
    }

    /// <summary>The PC's keyboard layout is not read: a reWASD import in a
    /// test reads every key through the US table, whatever layout the test
    /// machine has.</summary>
    internal static class KeyboardLayoutOffLimits
    {
        [ModuleInitializer]
        internal static void Set() => PadForge.Common.ConfigImport.DirectInputKeys.OffLimitsForTest = true;
    }

#pragma warning restore CA2255
}
