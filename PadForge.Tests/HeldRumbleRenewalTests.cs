using System.IO;
using PadForge.Engine;
using PadForge.Engine.Data;
using Xunit.Abstractions;

namespace PadForge.Tests;

/// <summary>
/// #572: SDL ends a rumble 0xFFFF ms after the call that set it
/// (SDL_MAX_RUMBLE_DURATION_MS, SDL_sysjoystick.h:292, stopped by the update
/// loop at SDL_joystick.c:1645-1648), while PadForge holds a level until it
/// changes. The SDL wrapper asks again for a held level before then. A call
/// that repeats the level SDL holds only moves the end time
/// (SDL_joystick.c:3203-3206), so the device itself gets nothing new.
/// </summary>
public sealed class HeldRumbleRenewalTests(ITestOutputHelper output)
{
    sealed class Rig : IDisposable
    {
        internal readonly SdlDeviceWrapper Device = new();
        internal readonly List<(ushort A, ushort B, uint Ms)> Main = [];
        internal readonly List<(ushort A, ushort B, uint Ms)> Triggers = [];
        internal long Now;
        internal bool Refuse;

        internal Rig()
        {
            Set("Joystick", new IntPtr(1));
            Set("GameController", new IntPtr(2));
            Set("HasRumble", true);
            Device.RumbleClock = () => Now;
            Device.RumbleJoystickCall = (_, a, b, ms) =>
            {
                if (Refuse) return false;
                Main.Add((a, b, ms));
                return true;
            };
            Device.RumbleTriggersCall = (_, a, b, ms) =>
            {
                if (Refuse) return false;
                Triggers.Add((a, b, ms));
                return true;
            };
        }

        internal void Set(string name, object value) => typeof(SdlDeviceWrapper).GetProperty(name)!.SetValue(Device, value);

        // No SDL handle is real here, so none may reach SDL's close calls.
        public void Dispose()
        {
            Set("GameController", IntPtr.Zero);
            Set("Joystick", IntPtr.Zero);
            Device.Dispose();
        }
    }

    const long Half = SdlDeviceWrapper.RumbleRenewAfterMs;

    [Fact]
    public void AHeldLevel_IsAskedForAgain_BeforeSdlsCap()
    {
        Assert.True(Half < SdlDeviceWrapper.SdlRumbleCapMs);
        using var r = new Rig();
        Assert.True(r.Device.SetRumble(1000, 2000));
        r.Now = Half - 1;
        r.Device.RenewHeldRumble();
        Assert.Single(r.Main);

        r.Now = Half;
        r.Device.RenewHeldRumble();
        output.WriteLine(string.Join(", ", r.Main));
        Assert.Equal(2, r.Main.Count);
        Assert.Equal(((ushort)1000, (ushort)2000, uint.MaxValue), r.Main[^1]);
        r.Device.RenewHeldRumble();
        Assert.Equal(2, r.Main.Count);

        r.Now = 2 * Half;
        r.Device.RenewHeldRumble();
        Assert.Equal(3, r.Main.Count);
    }

    [Theory]
    [InlineData("stop")]
    [InlineData("zero")]
    [InlineData("timed")]
    public void OnlyAnOpenEndedLevel_IsHeld(string how)
    {
        using var r = new Rig();
        r.Device.SetRumble(1000, 0);
        switch (how)
        {
            case "stop": r.Device.StopRumble(); break;
            case "zero": r.Device.SetRumble(0, 0); break;
            case "timed": r.Device.SetRumble(1000, 0, 500); break;
        }
        int calls = r.Main.Count;
        r.Now = 10 * Half;
        r.Device.RenewHeldRumble();
        Assert.Equal(calls, r.Main.Count);
    }

    [Fact]
    public void ARefusedCall_KeepsTheLevelSdlStillHolds()
    {
        using var r = new Rig();
        r.Device.SetRumble(1000, 0);
        r.Refuse = true;
        Assert.False(r.Device.SetRumble(3000, 0));
        r.Refuse = false;
        r.Now = Half;
        r.Device.RenewHeldRumble();
        Assert.Equal(((ushort)1000, (ushort)0, uint.MaxValue), r.Main[^1]);
    }

    [Fact]
    public void ARefusedRenewal_IsTriedAgainAHalfCapLater_NotEveryPoll()
    {
        using var r = new Rig();
        r.Device.SetRumble(1000, 0);
        r.Now = Half;
        r.Refuse = true;
        r.Device.RenewHeldRumble();
        r.Refuse = false;
        r.Now = Half + 1;
        r.Device.RenewHeldRumble();
        Assert.Single(r.Main);
        r.Now = 2 * Half;
        r.Device.RenewHeldRumble();
        Assert.Equal(2, r.Main.Count);
    }

    [Fact]
    public void ASteamDeckLevel_IsRenewedAsSdlTookIt()
    {
        using var r = new Rig();
        r.Set("VendorId", (ushort)0x28DE);
        r.Set("ProductId", (ushort)0x1205);
        r.Device.SetRumble(65535, 0);
        r.Now = Half;
        r.Device.RenewHeldRumble();
        Assert.Equal(2, r.Main.Count);
        Assert.Equal(r.Main[0].A, r.Main[1].A);
        Assert.True(r.Main[1].A < 65535);
    }

    [Fact]
    public void TriggerRumble_IsHeldAndRenewed_ApartFromTheMainMotors()
    {
        using var r = new Rig();
        Assert.True(r.Device.SetTriggerRumble(500, 600));
        r.Now = Half;
        r.Device.RenewHeldRumble();
        Assert.Equal(2, r.Triggers.Count);
        Assert.Equal(((ushort)500, (ushort)600, uint.MaxValue), r.Triggers[^1]);
        Assert.Empty(r.Main);

        r.Device.SetTriggerRumble(0, 0, 0);
        r.Now = 3 * Half;
        r.Device.RenewHeldRumble();
        Assert.Equal(3, r.Triggers.Count);
    }

    [Fact]
    public void AClosedJoystick_HoldsNothing()
    {
        var r = new Rig();
        r.Device.SetRumble(1000, 0);
        r.Device.SetTriggerRumble(500, 0);
        r.Dispose();
        r.Set("Joystick", new IntPtr(1));
        r.Set("GameController", new IntPtr(2));
        r.Now = 10 * Half;
        r.Device.RenewHeldRumble();
        Assert.Single(r.Main);
        Assert.Single(r.Triggers);
        r.Set("GameController", IntPtr.Zero);
        r.Set("Joystick", IntPtr.Zero);
    }

    /// <summary>A pad with trigger motors on SDL's path takes its trigger
    /// level through the device, so the wrapper can hold it.</summary>
    sealed class TriggerPad : WebControllerDevice, ISdlInputDevice
    {
        public TriggerPad() : base(Guid.NewGuid().ToString(), "Trigger Pad") { }
        public new bool HasRumble => true;
        public new bool HasRumbleTriggers => true;
        public new IntPtr GamepadHandle => new(7);
        internal readonly List<(ushort A, ushort B, uint Ms)> TriggerCalls = [];
        public bool SetTriggerRumble(ushort left, ushort right, uint durationMs = uint.MaxValue)
        {
            TriggerCalls.Add((left, right, durationMs));
            return true;
        }
    }

    [Fact]
    public void ForceFeedback_SetsAndStopsTriggerRumble_ThroughTheDevice()
    {
        var pad = new TriggerPad();
        var state = new ForceFeedbackState();
        var ps = new PadSetting { ForceOverall = "100" };
        state.SetDeviceForces(null, pad, ps, new Vibration { LeftTriggerMotorSpeed = 500, RightTriggerMotorSpeed = 600 });
        Assert.Equal(((ushort)500, (ushort)600, uint.MaxValue), pad.TriggerCalls.Single());
        state.StopDeviceForces(pad);
        Assert.Equal(((ushort)0, (ushort)0, 0u), pad.TriggerCalls[^1]);
    }

    [Fact]
    public void Step2_RenewsEveryPolledDevice_PastTheQuiesce_AheadOfTheSlotLookup()
    {
        string step2 = RepoFile("PadForge.App", "Common", "Input", "InputManager.Step2.UpdateInputStates.cs").Replace("\r\n", "\n");
        int quiesce = step2.IndexOf("if (OutputsQuiesced)\n                return;", StringComparison.Ordinal);
        int renew = step2.IndexOf("ud.Device?.RenewHeldRumble();", StringComparison.Ordinal);
        int slots = step2.IndexOf("int slotCount = settings.FindByInstanceGuid(ud.InstanceGuid, _instanceGuidBuffer);", StringComparison.Ordinal);
        Assert.True(quiesce > 0 && renew > quiesce && slots > renew, $"quiesce={quiesce} renew={renew} slots={slots}");
    }

    private static string RepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PadForge.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray()));
    }
}
