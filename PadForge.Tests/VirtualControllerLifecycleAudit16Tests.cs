using System;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HIDMaestro;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Data;
using Xunit;

namespace PadForge.Tests;

/// <summary>
/// Step 5's virtual controller lifecycle: what a reorder moves with a reused
/// controller, and what a submit that throws on every pass raises.
/// </summary>
[Collection("SettingsManagerStatics")]
public sealed class VirtualControllerLifecycleAudit16Tests : IDisposable
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly Guid DevGuid = new("5a1e0016-0000-4000-8000-00000000f00d");

    private readonly SettingsCollection _settings = SettingsManager.UserSettings;
    private readonly DeviceCollection _devices = SettingsManager.UserDevices;
    private readonly bool[] _created = (bool[])SettingsManager.SlotCreated.Clone();
    private readonly bool[] _enabled = (bool[])SettingsManager.SlotEnabled.Clone();
    private readonly System.Collections.Generic.List<InputManager> _managers = new();

    public void Dispose()
    {
        foreach (var im in _managers)
            typeof(InputManager).GetMethod("DestroyAllVirtualControllers", Private)!.Invoke(im, null);
        SettingsManager.UserSettings = _settings;
        SettingsManager.UserDevices = _devices;
        Array.Copy(_created, SettingsManager.SlotCreated, _created.Length);
        Array.Copy(_enabled, SettingsManager.SlotEnabled, _enabled.Length);
    }

    private static T Field<T>(InputManager im, string name)
        => (T)typeof(InputManager).GetField(name, Private)!.GetValue(im)!;

    private static HMaestroVirtualController Hm(string profile, int pad)
    {
        // Managed state only. Connect is never called.
        var vc = (HMaestroVirtualController)RuntimeHelpers.GetUninitializedObject(typeof(HMaestroVirtualController));
        typeof(HMaestroVirtualController).GetField("_dispatcherLock", Private)!.SetValue(vc, new object());
        typeof(HMaestroVirtualController).GetField("_profile", Private)!.SetValue(vc,
            new HMProfileBuilder().Id(profile).Vid(0xCAFE).Pid(1).Build());
        typeof(HMaestroVirtualController).GetField("_type", Private)!.SetValue(vc, VirtualControllerType.Extended);
        typeof(HMaestroVirtualController).GetProperty(nameof(HMaestroVirtualController.IsConnected))!.SetValue(vc, true);
        vc.FeedbackPadIndex = pad;
        return vc;
    }

    /// <summary>Seeds a pad as PublishExtendedApplied leaves it: every
    /// applied value is the one the slot asked for.</summary>
    private static void Publish(InputManager im, int pad, IVirtualController vc)
    {
        Field<IVirtualController[]>(im, "_virtualControllers")[pad] = vc;
        Field<string[]>(im, "_extendedAppliedProductString")[pad] = im.SlotOemOverrideLabel[pad];
        Field<CustomControllerLayout[]>(im, "_extendedAppliedLayout")[pad] = im.SlotCustomLayouts[pad];
        Field<bool[]>(im, "_extendedAppliedFfbEnabled")[pad] = im.SlotExtendedFfbEnabled[pad];
        Field<bool[]>(im, "_extendedAppliedCustomize")[pad] = im.SlotExtendedCustomize[pad];
        Field<int[]>(im, "_extendedAppliedVendorId")[pad] = im.SlotExtendedVendorId[pad];
        Field<int[]>(im, "_extendedAppliedProductId")[pad] = im.SlotExtendedProductId[pad];
    }

    private static bool Changed(InputManager im, int pad)
        => (bool)typeof(InputManager).GetMethod("ExtendedConfigurationChanged", Private)!.Invoke(im, new object[] { pad })!;

    /// <summary>A same-profile reorder reuses a controller, and every value
    /// it was built with goes with it, the Customize flag among them. Pads A
    /// and B on profile P with Customize on, C on profile Q: A, B, C to B, C,
    /// A reuses A's controller for B and destroys B's and C's, which resets
    /// B's applied Customize. A's controller then landed on B with every
    /// applied value but Customize, and the next pass rebuilt it for a change
    /// no one made.</summary>
    [Fact]
    public void ASameProfileReorderMovesTheAppliedCustomizeWithTheController()
    {
        const int a = 9, b = 10, c = 11;
        var im = new InputManager();
        _managers.Add(im);
        foreach (int pad in new[] { a, b, c })
        {
            im.SlotControllerTypes[pad] = VirtualControllerType.Extended;
            im.SlotExtendedCustomize[pad] = true;
            im.SlotOemOverrideLabel[pad] = "Probe pad";
        }
        im.SlotProfileIds[a] = im.SlotProfileIds[b] = "reorder-probe-p";
        im.SlotProfileIds[c] = "reorder-probe-q";
        var vcA = Hm("reorder-probe-p", a);
        Publish(im, a, vcA);
        Publish(im, b, Hm("reorder-probe-p", b));
        Publish(im, c, Hm("reorder-probe-q", c));
        Assert.False(Changed(im, a));
        Assert.False(Changed(im, b));

        im.RerouteVirtualControllersForReorder(VirtualControllerType.Extended,
            new[] { a, b, c }, new[] { b, c, a });

        Assert.Same(vcA, Field<IVirtualController[]>(im, "_virtualControllers")[b]);
        Assert.True(Field<bool[]>(im, "_extendedAppliedCustomize")[b]);
        Assert.False(Changed(im, b));
        Assert.False(Field<bool[]>(im, "_extendedAppliedCustomize")[a]);
    }

    private sealed class ThrowingPad : IVirtualController
    {
        public bool Throws = true;
        public int Submits;
        public VirtualControllerType Type => VirtualControllerType.Xbox;
        public bool IsConnected => true;
        public int FeedbackPadIndex { get; set; }
        public void Connect() { }
        public void Disconnect() { }
        public void Dispose() { }
        public void SubmitGamepadState(Gamepad state)
        {
            Submits++;
            if (Throws) throw new NotSupportedException("The driver refused the frame.");
        }
        public void RegisterFeedbackCallback(int padIndex, Vibration[] vibrationStates) { }
    }

    private static UserDevice ArrangeActiveSlot(int pad)
    {
        SettingsManager.UserDevices = new DeviceCollection();
        SettingsManager.UserSettings = new SettingsCollection();
        Array.Clear(SettingsManager.SlotCreated, 0, SettingsManager.SlotCreated.Length);
        for (int i = 0; i < SettingsManager.SlotEnabled.Length; i++) SettingsManager.SlotEnabled[i] = true;
        SettingsManager.SlotCreated[pad] = true;
        var ud = new UserDevice
        {
            InstanceGuid = DevGuid,
            ProductName = "Refused Pad",
            IsOnline = true,
            InputState = new CustomInputState(),
        };
        lock (SettingsManager.UserDevices.SyncRoot) SettingsManager.UserDevices.Items.Add(ud);
        lock (SettingsManager.UserSettings.SyncRoot)
            SettingsManager.UserSettings.Items.Add(new UserSetting { InstanceGuid = DevGuid, MapTo = pad });
        return ud;
    }

    private static void RunStep5(InputManager im)
        => typeof(InputManager).GetMethod("UpdateVirtualDevices", Private)!.Invoke(im, null);

    /// <summary>A submit the controller refuses on every pass raises its
    /// error once, on the poll thread, and not a formatted line, a stack
    /// trace into the log and a dispatcher post on every pass. A pass whose
    /// submit goes through arms it again, and a pass with no submit does
    /// not.</summary>
    [Fact]
    public void ASubmitThatThrowsOnEveryPassRaisesItsErrorOnce()
    {
        const int pad = 3;
        var device = ArrangeActiveSlot(pad);
        var im = new InputManager();
        _managers.Add(im);
        im.SlotControllerTypes[pad] = VirtualControllerType.Xbox;
        var vc = new ThrowingPad { FeedbackPadIndex = pad };
        Field<IVirtualController[]>(im, "_virtualControllers")[pad] = vc;
        int raised = 0;
        im.ErrorOccurred += (_, _) => raised++;

        for (int i = 0; i < 5; i++) RunStep5(im);
        Assert.Equal(5, vc.Submits);
        Assert.Equal(1, raised);

        // The device drops: the first inactive pass submits the neutral
        // frame, the next ones submit nothing, and the device comes back.
        device.IsOnline = false;
        for (int i = 0; i < 3; i++) RunStep5(im);
        device.IsOnline = true;
        for (int i = 0; i < 2; i++) RunStep5(im);
        Assert.Equal(8, vc.Submits);
        Assert.Equal(1, raised);

        vc.Throws = false;
        RunStep5(im);
        vc.Throws = true;
        for (int i = 0; i < 3; i++) RunStep5(im);
        Assert.Equal(12, vc.Submits);
        Assert.Equal(2, raised);
    }
}

/// <summary>What a shutdown does with HIDMaestro's context, and a create
/// HIDMaestro's attach gate turned away.</summary>
[Collection("SettingsManagerStatics")]
public sealed class VirtualControllerShutdownAudit16Tests : IDisposable
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly Guid DevGuid = new("5a1e0016-0000-4000-8000-00000000ca7e");

    // HIDMaestro v2.4.0 UsbipDriverInstaller.HubFilter.cs:293-294, HoldHubRestartGate.
    private const string GateMessage =
        "A USB/IP root hub restart or another program's attach held HIDMaestro's attach gate for 30 s, so the device was not plugged in.";

    private readonly SettingsCollection _settings = SettingsManager.UserSettings;
    private readonly DeviceCollection _devices = SettingsManager.UserDevices;
    private readonly bool[] _created = (bool[])SettingsManager.SlotCreated.Clone();
    private readonly bool[] _enabled = (bool[])SettingsManager.SlotEnabled.Clone();

    public void Dispose()
    {
        SettingsManager.UserSettings = _settings;
        SettingsManager.UserDevices = _devices;
        Array.Copy(_created, SettingsManager.SlotCreated, _created.Length);
        Array.Copy(_enabled, SettingsManager.SlotEnabled, _enabled.Length);
    }

    private static T Field<T>(InputManager im, string name)
        => (T)typeof(InputManager).GetField(name, Private)!.GetValue(im)!;

    /// <summary>A USB/IP create can wait 55 s inside HIDMaestro 2.4.0, past
    /// the 30 s Stop waited. The context leaves the field at Stop, so the
    /// next start sets up its own, and its dispose waits for a create still
    /// running: disposed under it, the create's own teardown ran on a
    /// disposed context after the shutdown overlay had gone.</summary>
    [Fact]
    public async System.Threading.Tasks.Task StopKeepsTheContextWhileACreateStillRuns()
    {
        var im = new InputManager();
        var pending = new System.Threading.Tasks.TaskCompletionSource<bool>(System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
        Field<System.Threading.Tasks.Task[]>(im, "_pendingConnectTask")[5] = pending.Task;
        var ctxField = typeof(InputManager).GetField("_hmaestroContext", BindingFlags.Static | BindingFlags.NonPublic)!;
        var ctx = (HMContext)RuntimeHelpers.GetUninitializedObject(typeof(HMContext));
        try
        {
            var unfinished = (System.Collections.Generic.List<System.Threading.Tasks.Task>)typeof(InputManager)
                .GetMethod("AwaitPendingLifecycleTasks", Private)!.Invoke(im, new object[] { TimeSpan.FromMilliseconds(50) })!;
            Assert.Same(pending.Task, Assert.Single(unfinished));
            ctxField.SetValue(null, ctx);
            var disposed = typeof(HMContext).GetField("_disposed", Private)!;
            var dispose = (System.Threading.Tasks.Task)typeof(InputManager)
                .GetMethod("DisposeHMaestroContextOnShutdown", Private)!.Invoke(im, new object[] { unfinished })!;
            Assert.Null(ctxField.GetValue(null));
            Assert.False((bool)disposed.GetValue(ctx)!);
            Assert.False(dispose.IsCompleted);
            pending.TrySetResult(true);
            await dispose.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True((bool)disposed.GetValue(ctx)!);
        }
        finally
        {
            ctxField.SetValue(null, null);
            pending.TrySetResult(true);
        }
        // 5 s for the root hub, 15 s for its restart, 5 s for the hub again
        // and 30 s for the attach gate (UsbipDriverInstaller.HubFilter.cs at
        // v2.4.0, :236, :396, :427, :279).
        Assert.True(InputManager.LifecycleWaitBound >= TimeSpan.FromSeconds(55));
    }

    private sealed class GateRefusedPad : IVirtualController
    {
        public VirtualControllerType Type => VirtualControllerType.Xbox;
        public bool IsConnected => false;
        public int FeedbackPadIndex { get; set; }
        public void Connect() => throw new InvalidOperationException(GateMessage);
        public void Disconnect() { }
        public void Dispose() { }
        public void SubmitGamepadState(Gamepad state) { }
        public void RegisterFeedbackCallback(int padIndex, Vibration[] vibrationStates) { }
    }

    private static void Step(InputManager im, int pad)
    {
        typeof(InputManager).GetMethod("UpdateVirtualDevices", Private)!.Invoke(im, null);
        Field<System.Threading.Tasks.Task[]>(im, "_pendingConnectTask")[pad]?.Wait(5000);
    }

    /// <summary>A create the attach gate turned away failed for a wait on
    /// another party that ends by itself. It tries once more after the hold
    /// with nothing about the slot changed, and a second refusal stays
    /// failed until something does.</summary>
    [Fact]
    public void ACreateTheAttachGateTurnedAwayRetriesOnceAfterTheHold()
    {
        const int pad = 4;
        SettingsManager.UserDevices = new DeviceCollection();
        SettingsManager.UserSettings = new SettingsCollection();
        Array.Clear(SettingsManager.SlotCreated, 0, SettingsManager.SlotCreated.Length);
        for (int i = 0; i < SettingsManager.SlotEnabled.Length; i++) SettingsManager.SlotEnabled[i] = true;
        SettingsManager.SlotCreated[pad] = true;
        var ud = new UserDevice { InstanceGuid = DevGuid, ProductName = "Gate Pad", IsOnline = true, InputState = new CustomInputState() };
        lock (SettingsManager.UserDevices.SyncRoot) SettingsManager.UserDevices.Items.Add(ud);
        lock (SettingsManager.UserSettings.SyncRoot)
            SettingsManager.UserSettings.Items.Add(new UserSetting { InstanceGuid = DevGuid, MapTo = pad });

        var im = new InputManager();
        im.SlotControllerTypes[pad] = VirtualControllerType.Xbox;
        int creates = 0;
        im.HMaestroCreateForTest = (_, _) => { System.Threading.Interlocked.Increment(ref creates); return new GateRefusedPad(); };
        im.CreateGateRetryHoldMs = long.MaxValue / 4;

        for (int i = 0; i < 10; i++) Step(im, pad);
        Assert.Equal(1, creates);
        Assert.True(im.IsVirtualControllerCreateFailed(pad));

        im.CreateGateRetryHoldMs = 0;
        for (int i = 0; i < 10; i++) Step(im, pad);
        Assert.Equal(2, creates);
        Assert.True(im.IsVirtualControllerCreateFailed(pad));
    }
}

/// <summary>The precision touchpad reader leaves HIDMaestro's own devices
/// out, as the Raw Input listener and the tablet reader do.</summary>
public sealed class PrecisionTouchpadOwnDeviceTests
{
    [Fact]
    public void AHidMaestroTouchpadIsNeverListed()
    {
        const string own = @"\\?\HID#HIDMAESTRO&VID_045E&PID_0B13#2&1a2b3c4d&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}";
        const string real = @"\\?\HID#VID_06CB&PID_CE26&Col02#5&2f8d3a1&0&0001#{4d1e55b2-f16f-11cf-88cb-001111000030}";
        using var reader = new PrecisionTouchpadReader(path => path.Contains("HIDMAESTRO", StringComparison.OrdinalIgnoreCase));
        Assert.False(reader.AdmitDevice((IntPtr)0x1601, new PrecisionTouchpadReader.PtpDeviceState { DevicePath = own }));
        Assert.True(reader.AdmitDevice((IntPtr)0x1602, new PrecisionTouchpadReader.PtpDeviceState { DevicePath = real }));
        Assert.Equal(new[] { real }, reader.GetDevices().Select(d => d.path));
    }
}
