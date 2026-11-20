using System.Buffers.Binary;
using PadForge.Common.Input;
using PadForge.Common.Input.Telemetry;
using PadForge.Common.Telemetry;
using PadForge.Engine;
using PadForge.Engine.RemoteLink;
using PadForge.Engine.Steering;
using Xunit.Abstractions;
using static SDL3.SDL;

namespace PadForge.Tests;

/// <summary>
/// #588: Vehicle mode's centering on its way to a wheel. What each wheel
/// family is sent, byte for byte against the Linux drivers PadForge's
/// writers follow, the tail that carries centering to a wheel on another
/// PC, the effects a wheel routed through SDL holds, and the lists that
/// read a game's road.
/// </summary>
public sealed class WheelCenteringWiringTests(ITestOutputHelper output)
{
    const ushort Logitech = 0x046D, Fanatec = 0x0EB7, Thrustmaster = 0x044F;
    const ushort G29 = 0xC24F, G27 = 0xC29B, CslDd = 0x0020, ClubSportV25 = 0x0004, T300 = 0xB66E;

    static string Path() => "test://wheel/" + Guid.NewGuid().ToString("N");

    static CenteringFrame Frame(short level = 0, int spring = 6667, int springLimit = 2500, int damper = 5333,
        int damperLimit = 6000, int friction = 1400) => new()
    {
        Level = level, SpringCoefficient = spring, SpringSaturation = springLimit,
        DamperCoefficient = damper, DamperSaturation = damperLimit, FrictionSaturation = friction,
    };

    static List<byte[]> Sent(Action act)
    {
        var sent = new List<byte[]>();
        using (RawHidOutput.CaptureForTest((_, report) => { sent.Add(report); return true; })) act();
        return sent;
    }

    static string Hex(byte[] report) => Convert.ToHexString(report);

    // ── Logitech ──

    /// <summary>A Logitech wheel takes the spring in its second slot, the
    /// damper in its third and friction in its fourth, each downloaded
    /// once (new-lg4ff lg4ff_update_slot: the slot's bit shifted by its
    /// number, op 1). A G29 has no friction force, so friction is a
    /// damper at a full coefficient under a low limit.</summary>
    [Fact]
    public void ALogitechWheel_TakesEachEffectInItsOwnSlot()
    {
        string path = Path();
        var sent = Sent(() => WheelCenteringWriter.Apply(path, Logitech, G29, Frame(), 0));
        foreach (var report in sent) output.WriteLine(Hex(report));
        Assert.Equal(3, sent.Count);
        // Spring: 0x0b, the dead band's two edges at center, both coefficients at step 9, the limit a quarter.
        Assert.Equal("00210B8080990" + "03F", Hex(sent[0]));
        // Damper: 0x0c, step 8 each way, the limit at six tenths.
        Assert.Equal("00410C0800080099", Hex(sent[1]));
        // Friction as a damper: full coefficient, the limit at 14 percent.
        Assert.Equal("00810C0F000F0023", Hex(sent[2]));

        // The same frame again writes nothing.
        Assert.Empty(Sent(() => WheelCenteringWriter.Apply(path, Logitech, G29, Frame(), 0)));

        // A changed limit refreshes the slot in place (op 0x0c), and only that slot.
        var refreshed = Sent(() => WheelCenteringWriter.Apply(path, Logitech, G29, Frame(springLimit: 5000), 0));
        Assert.Single(refreshed);
        Assert.Equal("002C0B808099007F", Hex(refreshed[0]));

        // A damper of nothing stops its slot (op 3), and coming back it is downloaded afresh.
        var stopped = Sent(() => WheelCenteringWriter.Apply(path, Logitech, G29, Frame(springLimit: 5000, damper: 0), 0));
        Assert.Single(stopped);
        Assert.Equal("0043000000000000", Hex(stopped[0]));
        var back = Sent(() => WheelCenteringWriter.Apply(path, Logitech, G29, Frame(springLimit: 5000), 0));
        Assert.Single(back);
        Assert.Equal("00410C0800080099", Hex(back[0]));

        // Spring mode's empty frame lets go of all three.
        var released = Sent(() => WheelCenteringWriter.Apply(path, Logitech, G29, default, 0));
        Assert.Equal(new[] { "0023000000000000", "0043000000000000", "0083000000000000" }, released.Select(Hex));
        Assert.Empty(Sent(() => WheelCenteringWriter.Apply(path, Logitech, G29, default, 0)));
        WheelCenteringWriter.ResetDevice(path);
        LogitechRawHidWriter.ResetDevice(path);
    }

    /// <summary>A G27 has a friction force of its own (new-lg4ff
    /// LG4FF_CAP_FRICTION): 0x0e, with eight bits of coefficient. The
    /// first frame a wheel gets writes every slot, so the two this one
    /// leaves empty are stopped: a wheel holds what an earlier run sent
    /// it, and the Linux driver clears each slot when it takes the wheel
    /// (lg4ff_init_slots).</summary>
    [Fact]
    public void ALogitechWheelWithAFrictionForce_TakesFrictionAsFriction()
    {
        string path = Path();
        var sent = Sent(() => WheelCenteringWriter.Apply(path, Logitech, G27, Frame(spring: 0, damper: 0), 0));
        Assert.Equal(new[] { "0023000000000000", "0043000000000000", "00810EFFFF230000" }, sent.Select(Hex));
        // Once written, an empty slot is left alone.
        Assert.Empty(Sent(() => WheelCenteringWriter.Apply(path, Logitech, G27, Frame(spring: 0, damper: 0), 0)));
        WheelCenteringWriter.ResetDevice(path);
        LogitechRawHidWriter.ResetDevice(path);
    }

    /// <summary>The first frame a wheel gets in a run stops every slot or
    /// id centering uses, an empty frame too. A run killed while Vehicle
    /// mode or a Release Test held them left them playing, and the next
    /// run may start in Spring mode, whose frames are empty. A
    /// Thrustmaster's effects play until they are stopped (a play count of
    /// 0, hid-tmt300rs.c t300rs_play_effect). The Linux drivers clear the
    /// slots when they take a wheel (new-lg4ff lg4ff_init_slots,
    /// hid-fanatecff ftecff_init_slots). A Thrustmaster was left out, and
    /// an empty first frame sent nothing to any wheel.</summary>
    [Fact]
    public void TheFirstFrameStopsWhatAnEarlierRunLeft_OnEveryWheel()
    {
        string thrustmaster = Path();
        Assert.Equal(new[] { "6000028900", "6000038900", "6000048900", "6000058900" },
            Sent(() => WheelCenteringWriter.Apply(thrustmaster, Thrustmaster, T300, default, 0)).Select(Hex));
        Assert.Empty(Sent(() => WheelCenteringWriter.Apply(thrustmaster, Thrustmaster, T300, default, 0)));
        WheelCenteringWriter.ResetDevice(thrustmaster);
        ThrustmasterRawHidWriter.ResetDevice(thrustmaster);

        // Vehicle mode with no damper: the damper's id is stopped all the same.
        string vehicle = Path();
        Assert.Contains("6000038900", Sent(() => WheelCenteringWriter.Apply(vehicle, Thrustmaster, T300, Frame(damper: 0), 0)).Select(Hex));
        WheelCenteringWriter.ResetDevice(vehicle);
        ThrustmasterRawHidWriter.ResetDevice(vehicle);

        // A Logitech wheel's three slots, and a Fanatec base's four.
        string logitech = Path();
        Assert.Equal(3, Sent(() => WheelCenteringWriter.Apply(logitech, Logitech, G29, default, 0)).Count);
        Assert.Empty(Sent(() => WheelCenteringWriter.Apply(logitech, Logitech, G29, default, 0)));
        WheelCenteringWriter.ResetDevice(logitech);
        LogitechRawHidWriter.ResetDevice(logitech);
        string fanatec = Path();
        Assert.Equal(4, Sent(() => WheelCenteringWriter.Apply(fanatec, Fanatec, ClubSportV25, default, 0)).Count);
        Assert.Empty(Sent(() => WheelCenteringWriter.Apply(fanatec, Fanatec, ClubSportV25, default, 0)));
        WheelCenteringWriter.ResetDevice(fanatec);
    }

    /// <summary>A Logitech slot that holds an effect of one type is
    /// stopped before an effect of another type is downloaded into it:
    /// new-lg4ff never refreshes a slot into another type, and downloads
    /// a slot's effect afresh after a stop (lg4ff_update_slot, op 3 and
    /// then 1). The first slot switched between a game's constant force
    /// and a game's spring with a refresh (op 0xc). An effect of the same
    /// type is refreshed in place.</summary>
    [Fact]
    public void ALogitechSlotIsStoppedBeforeItTakesAnotherTypeOfEffect()
    {
        string path = Path();
        string[] Ops(Action write) => Sent(write).Select(r => Hex(r)[..4]).ToArray();
        void Spring(int coefficient) => LogitechRawHidWriter.WriteCondition(path, 0, FfbEffectTypes.Spring,
            coefficient, coefficient, 0, 0, 10000, 10000, 100, false);

        Assert.Equal(new[] { "0011" }, Ops(() => LogitechRawHidWriter.WriteConstantForce(path, 0, 8000)));
        Assert.Equal(new[] { "001C" }, Ops(() => LogitechRawHidWriter.WriteConstantForce(path, 0, 9000)));
        Assert.Equal(new[] { "0013", "0011" }, Ops(() => Spring(5000)));
        Assert.Equal(new[] { "001C" }, Ops(() => Spring(6000)));
        Assert.Equal(new[] { "0013", "0011" }, Ops(() => LogitechRawHidWriter.WriteCondition(path, 0, FfbEffectTypes.Damper,
            5000, 5000, 0, 0, 10000, 10000, 100, false)));
        Assert.Equal(new[] { "0013", "0011" }, Ops(() => LogitechRawHidWriter.WriteConstantForce(path, 0, 8000)));
        // A stopped slot takes a download, with no second stop.
        Ops(() => LogitechRawHidWriter.WriteStopEffect(path, 0));
        Assert.Equal(new[] { "0011" }, Ops(() => Spring(5000)));
        LogitechRawHidWriter.ResetDevice(path);
    }

    /// <summary>An empty frame after the first sends nothing, and a write
    /// the wheel refuses is tried again.</summary>
    [Fact]
    public void AnEmptyFrameAfterTheFirstWritesNothing_AndARefusedWriteIsTriedAgain()
    {
        string path = Path();
        Assert.Equal(3, Sent(() => WheelCenteringWriter.Apply(path, Logitech, G29, default, 0)).Count);
        Assert.Empty(Sent(() => WheelCenteringWriter.Apply(path, Logitech, G29, default, 0)));

        int refused = 0;
        using (RawHidOutput.CaptureForTest((_, _) => { refused++; return false; }))
            WheelCenteringWriter.Apply(path, Logitech, G29, Frame(), 0);
        Assert.Equal(3, refused);
        Assert.Equal(3, Sent(() => WheelCenteringWriter.Apply(path, Logitech, G29, Frame(), 0)).Count);
        Assert.Empty(Sent(() => WheelCenteringWriter.Apply(path, Logitech, G29, Frame(), 0)));
        WheelCenteringWriter.ResetDevice(path);
        LogitechRawHidWriter.ResetDevice(path);
    }

    /// <summary>A game's own spring, damper or friction effect plays in a
    /// Logitech wheel's first slot, where the centering force cannot join
    /// the game's as it joins a constant force. While that effect plays,
    /// the force is a constant of its own in the fourth slot, and
    /// friction, whose slot that is, rests. Its level is at every slot's
    /// byte: new-lg4ff puts a slot's constant at byte 2 plus the slot
    /// (lg4ff_update_slot) and runs one in the first slot only, so no
    /// reference shows which byte a wheel reads for the fourth, and it
    /// reads the level whichever that is. The slot is stopped
    /// between the two. When the game's effect ends, friction comes back.
    /// The centering curve used to stop for as long as the game's effect
    /// played.</summary>
    [Fact]
    public void ALogitechWheelsCenteringForceHasTheFourthSlotWhileAGamesEffectHasTheFirst()
    {
        string path = Path();
        Assert.Equal(3, Sent(() => WheelCenteringWriter.Apply(path, Logitech, G29, Frame(level: 8000), 0)).Count);

        // The game's damper starts: friction's slot is stopped and the force is downloaded there (op 1).
        var whileGame = Sent(() => WheelCenteringWriter.Apply(path, Logitech, G29, Frame(level: 8000, friction: 0), FfbEffectTypes.Damper));
        Assert.Equal(new[] { "0083000000000000", "0081009F9F9F9F00" }, whileGame.Select(Hex));
        Assert.Empty(Sent(() => WheelCenteringWriter.Apply(path, Logitech, G29, Frame(level: 8000, friction: 0), FfbEffectTypes.Damper)));
        // A frame that still asks for friction changes nothing: the slot is the force's.
        Assert.Empty(Sent(() => WheelCenteringWriter.Apply(path, Logitech, G29, Frame(level: 8000), FfbEffectTypes.Damper)));

        // The force moves: refreshed in place (op 0x0c). A level that gives the same byte writes nothing.
        var moved = Sent(() => WheelCenteringWriter.Apply(path, Logitech, G29, Frame(level: -8000, friction: 0), FfbEffectTypes.Damper));
        Assert.Equal(new[] { "008C006060606000" }, moved.Select(Hex));
        Assert.Empty(Sent(() => WheelCenteringWriter.Apply(path, Logitech, G29, Frame(level: -7990, friction: 0), FfbEffectTypes.Damper)));

        // The game's effect ends: the force leaves the slot and friction comes back, downloaded afresh.
        var after = Sent(() => WheelCenteringWriter.Apply(path, Logitech, G29, Frame(level: -8000), 0));
        Assert.Equal(new[] { "0083000000000000", "00810C0F000F0023" }, after.Select(Hex));
        Assert.Empty(Sent(() => WheelCenteringWriter.Apply(path, Logitech, G29, Frame(level: -8000), 0)));

        // A stop while the force has the slot lets go of it with the rest.
        Sent(() => WheelCenteringWriter.Apply(path, Logitech, G29, Frame(level: 8000, friction: 0), FfbEffectTypes.Spring));
        var released = Sent(() => WheelCenteringWriter.Apply(path, Logitech, G29, default, 0));
        Assert.Equal(new[] { "0023000000000000", "0043000000000000", "0083000000000000" }, released.Select(Hex));
        WheelCenteringWriter.ResetDevice(path);
        LogitechRawHidWriter.ResetDevice(path);
    }

    /// <summary>The frame is composed for the effects the wheel will run
    /// beside a game's own condition effect. A Fanatec base gives the
    /// game's effect its kind's slot, so centering's effect of that kind
    /// rests, and a game's inertia has a slot centering never uses. A
    /// Logitech wheel gives the centering force friction's slot. A
    /// Thrustmaster holds every effect at once.</summary>
    [Fact]
    public void TheFrameIsComposedForTheEffectsTheWheelRunsBesideAGamesOwn()
    {
        var all = CenteringEffects.All;
        foreach (ushort vendor in new[] { Logitech, Fanatec, Thrustmaster })
            Assert.Equal(all, WheelCenteringWriter.EffectsBeside(vendor, vendor == Logitech ? G29 : vendor == Fanatec ? CslDd : T300, 0));
        foreach (uint kind in new[] { FfbEffectTypes.Spring, FfbEffectTypes.Damper, FfbEffectTypes.Inertia, FfbEffectTypes.Friction })
        {
            Assert.Equal(all & ~CenteringEffects.Friction, WheelCenteringWriter.EffectsBeside(Logitech, G29, kind));
            Assert.Equal(all, WheelCenteringWriter.EffectsBeside(Thrustmaster, T300, kind));
        }
        Assert.Equal(all & ~CenteringEffects.Spring, WheelCenteringWriter.EffectsBeside(Fanatec, CslDd, FfbEffectTypes.Spring));
        Assert.Equal(all & ~CenteringEffects.Damper, WheelCenteringWriter.EffectsBeside(Fanatec, CslDd, FfbEffectTypes.Damper));
        Assert.Equal(all & ~CenteringEffects.Friction, WheelCenteringWriter.EffectsBeside(Fanatec, CslDd, FfbEffectTypes.Friction));
        Assert.Equal(all, WheelCenteringWriter.EffectsBeside(Fanatec, CslDd, FfbEffectTypes.Inertia));

        string pass = ExtrasPageTests.RepoText("PadForge.App", "Common", "Input", "InputManager.Step2.UpdateInputStates.cs");
        Assert.Contains("WheelCenteringWriter.EffectsBeside(ud.VendorId, ud.ProdId, hasCond ? cv.EffectType : 0),", pass);
    }

    // ── Fanatec ──

    /// <summary>A Fanatec base takes each kind in the slot its firmware
    /// keeps for it: spring 1, damper 2, friction 4 (hid-fanatecff
    /// hid-ftecff.c:1023-1036), enabled with op 1 and disabled with op
    /// 3.</summary>
    [Fact]
    public void AFanatecBase_TakesEachEffectInItsKindsSlot()
    {
        string path = Path();
        var sent = Sent(() => WheelCenteringWriter.Apply(path, Fanatec, CslDd, Frame(), 0));
        foreach (var report in sent) output.WriteLine(Hex(report));
        // The first frame writes every slot: the inertia slot, which centering never uses, is disabled, as
        // the Linux driver disables each slot when it takes the base (ftecff_init_slots).
        Assert.Equal(new[] { "01110B8080AA003F", "01210C0800080099", "01410C0F000F0023", "01330C00000000FF" }, sent.Select(Hex));
        Assert.Empty(Sent(() => WheelCenteringWriter.Apply(path, Fanatec, CslDd, Frame(), 0)));

        var released = Sent(() => WheelCenteringWriter.Apply(path, Fanatec, CslDd, default, 0));
        Assert.Equal(new[] { "01130B0000000000", "01230C00000000FF", "01430C00000000FF" }, released.Select(Hex));
        WheelCenteringWriter.ResetDevice(path);
    }

    /// <summary>A game's own spring holds the spring's slot on a Fanatec
    /// base while it plays, and centering's spring comes back when it
    /// ends. A game's damper on a base with no centering at all is
    /// disabled when the game stops it, where it used to stay, and so is
    /// a game's inertia, in the slot centering never uses.</summary>
    [Fact]
    public void AGamesConditionHoldsItsSlotOnAFanatecBase_AndTheSlotIsFreedWhenItEnds()
    {
        string path = Path();
        var whileGame = Sent(() => WheelCenteringWriter.Apply(path, Fanatec, CslDd, Frame(), FfbEffectTypes.Spring));
        Assert.Equal(new[] { "01210C0800080099", "01410C0F000F0023", "01330C00000000FF" }, whileGame.Select(Hex));
        Assert.Empty(Sent(() => WheelCenteringWriter.Apply(path, Fanatec, CslDd, Frame(), FfbEffectTypes.Spring)));
        var after = Sent(() => WheelCenteringWriter.Apply(path, Fanatec, CslDd, Frame(), 0));
        Assert.Equal(new[] { "01110B8080AA003F" }, after.Select(Hex));
        WheelCenteringWriter.ResetDevice(path);

        // No centering: the game's damper is the only thing in its slot, and its end disables the slot. The
        // first frame disables the three slots the game's effect is not in.
        string plain = Path();
        var firstGame = Sent(() => WheelCenteringWriter.Apply(plain, Fanatec, ClubSportV25, default, FfbEffectTypes.Damper));
        Assert.Equal(new[] { "01130B0000000000", "01430C00000000FF", "01330C00000000FF" }, firstGame.Select(Hex));
        Assert.Empty(Sent(() => WheelCenteringWriter.Apply(plain, Fanatec, ClubSportV25, default, FfbEffectTypes.Damper)));
        var ended = Sent(() => WheelCenteringWriter.Apply(plain, Fanatec, ClubSportV25, default, 0));
        Assert.Equal(new[] { "01230C00000000FF" }, ended.Select(Hex));
        Assert.Empty(Sent(() => WheelCenteringWriter.Apply(plain, Fanatec, ClubSportV25, default, 0)));

        Assert.Empty(Sent(() => WheelCenteringWriter.Apply(plain, Fanatec, ClubSportV25, default, FfbEffectTypes.Inertia)));
        var inertiaEnded = Sent(() => WheelCenteringWriter.Apply(plain, Fanatec, ClubSportV25, default, 0));
        Assert.Equal(new[] { "01330C00000000FF" }, inertiaEnded.Select(Hex));
        WheelCenteringWriter.ResetDevice(plain);

        // A Logitech wheel's slots are centering's own: a game's condition plays in the first.
        string logitech = Path();
        Assert.Equal(3, Sent(() => WheelCenteringWriter.Apply(logitech, Logitech, G29, Frame(), FfbEffectTypes.Spring)).Count);
        WheelCenteringWriter.ResetDevice(logitech);
        LogitechRawHidWriter.ResetDevice(logitech);
    }

    /// <summary>A Fanatec base plays every slot at once: hid-fanatecff
    /// sums each slot's effects on every tick, constants into the first
    /// and springs into the second (hid-ftecff.c ftecff_timer). So while a
    /// game's spring plays, the first slot still takes the constant force,
    /// which carries Vehicle mode's centering force (MergedForce). The pass
    /// left the first slot at its last level then, and the centering force
    /// stood still. Each part is written when it changed, so a centering
    /// force that moves does not send the game's spring again.</summary>
    [Fact]
    public void AFanatecBasePlaysTheConstantForceBesideAGamesCondition()
    {
        string path = Path();
        void Write(bool condition, short force, bool forceChanged, int coefficient = 5000)
            => WheelCenteringWriter.WriteFanatecGameForce(path, ClubSportV25, condition, FfbEffectTypes.Spring,
                coefficient, coefficient, 0, 0, 10000, 10000, 100, force, 0, forceChanged);

        var first = Sent(() => Write(true, 8000, true));
        Assert.Equal(new[] { "01110B", "010108" }, first.Select(r => Hex(r)[..6]));
        var moved = Sent(() => Write(true, 9000, true));
        Assert.Equal(new[] { "010108" }, moved.Select(r => Hex(r)[..6]));
        var springChanged = Sent(() => Write(true, 9000, false, coefficient: 6000));
        Assert.Equal(new[] { "01110B" }, springChanged.Select(r => Hex(r)[..6]));
        // The pass wrote the spring slot itself: the game's spring goes back in.
        WheelCenteringWriter.SpringSlotWritten(path);
        Assert.Equal(new[] { "01110B" }, Sent(() => Write(true, 9000, false, coefficient: 6000)).Select(r => Hex(r)[..6]));
        // With no game condition the force goes out alone, as before.
        Assert.Equal(new[] { "010108" }, Sent(() => Write(false, 9000, true)).Select(r => Hex(r)[..6]));
        // The game's spring that ended is written again when it comes back.
        Assert.Equal("01110B", Hex(Sent(() => Write(true, 9000, false, coefficient: 6000))[0])[..6]);
        WheelCenteringWriter.ResetDevice(path);
    }

    /// <summary>The wheel's settings write Spring mode's spring into a
    /// Fanatec base's spring slot when Auto Centering Strength changes,
    /// a game's spring there or not. Both passes put the game's spring
    /// back after it: the pass skips a frame that did not change, so the
    /// game's spring stayed out until its own settings changed.</summary>
    [Fact]
    public void AGamesSpringGoesBackAfterTheSettingsWriteTheSpringSlot_InBothPasses()
    {
        string pass = ExtrasPageTests.RepoText("PadForge.App", "Common", "Input", "InputManager.Step2.UpdateInputStates.cs");
        int settings = pass.IndexOf("applied &= FanatecRawHidWriter.WriteAutocenter(ud.DevicePath, acMag);", StringComparison.Ordinal);
        int back = pass.IndexOf("wheelForce, acMag, forceChanged: false);", StringComparison.Ordinal);
        Assert.True(settings > 0 && back > settings, "the pass leaves a game's spring out after the settings");

        string owner = ExtrasPageTests.RepoText("PadForge.App", "Services", "InputService.cs");
        int ownerSettings = owner.IndexOf("FanatecRawHidWriter.WriteAutocenter(path, acMag); WheelCenteringWriter.SpringSlotWritten(path);", StringComparison.Ordinal);
        int ownerBack = owner.IndexOf("w.CondGain, force, w.Ac, forceChanged: false);", StringComparison.Ordinal);
        Assert.True(ownerSettings > 0 && ownerBack > ownerSettings, "the owner leaves a game's spring out after the settings");
    }

    /// <summary>Spring mode's Auto Centering Strength is a spring in a
    /// Fanatec base's spring slot, the slot Vehicle mode's spring uses.
    /// While it is on, centering writes nothing there: a frame with no
    /// spring leaves the slot on, and a base that never had centering
    /// gets no record, so a stop leaves the spring in place. When the pass
    /// writes the slot itself, Vehicle mode's spring goes back in at the
    /// next frame in which the slot is centering's. A Logitech's and a
    /// Thrustmaster's centering spring is the firmware's own, in no slot
    /// centering uses.</summary>
    [Fact]
    public void SpringModesSpringKeepsAFanatecBasesSlot_AndVehicleModesGoesBackInAfterIt()
    {
        // Vehicle mode's spring is in the slot, and the mode changes to Spring with the slider up.
        string path = Path();
        Sent(() => WheelCenteringWriter.Apply(path, Fanatec, CslDd, Frame(), 0));
        var toSpringMode = Sent(() => WheelCenteringWriter.Apply(path, Fanatec, CslDd, default, 0, autoCenterSpring: true));
        Assert.Equal(new[] { "01230C00000000FF", "01430C00000000FF" }, toSpringMode.Select(Hex));
        Assert.Empty(Sent(() => WheelCenteringWriter.Apply(path, Fanatec, CslDd, default, 0, autoCenterSpring: true)));

        // Back to Vehicle mode: the pass turned the slider's spring off, and centering's goes in.
        var toVehicle = Sent(() => WheelCenteringWriter.Apply(path, Fanatec, CslDd, Frame(), 0));
        Assert.Equal(new[] { "01110B8080AA003F", "01210C0800080099", "01410C0F000F0023" }, toVehicle.Select(Hex));

        // The pass writes the slot while Vehicle mode has it: centering's spring is written again, alone.
        Assert.Empty(Sent(() => WheelCenteringWriter.Apply(path, Fanatec, CslDd, Frame(), 0)));
        WheelCenteringWriter.SpringSlotWritten(path);
        Assert.Equal(new[] { "01110B8080AA003F" }, Sent(() => WheelCenteringWriter.Apply(path, Fanatec, CslDd, Frame(), 0)).Select(Hex));
        WheelCenteringWriter.ResetDevice(path);

        // A base that only ever had the slider's spring: the first frame stops the other three slots and
        // leaves the spring's alone, and the frames after it write nothing while the slider has it. With
        // the slider off, the slot is centering's again, which has no spring there.
        string plain = Path();
        Assert.Equal(new[] { "01230C00000000FF", "01430C00000000FF", "01330C00000000FF" },
            Sent(() => WheelCenteringWriter.Apply(plain, Fanatec, ClubSportV25, default, 0, autoCenterSpring: true)).Select(Hex));
        WheelCenteringWriter.SpringSlotWritten(plain);
        Assert.Empty(Sent(() => WheelCenteringWriter.Apply(plain, Fanatec, ClubSportV25, default, 0, autoCenterSpring: true)));
        Assert.Equal(new[] { "01130B0000000000" }, Sent(() => WheelCenteringWriter.Apply(plain, Fanatec, ClubSportV25, default, 0)).Select(Hex));
        Assert.Empty(Sent(() => WheelCenteringWriter.Apply(plain, Fanatec, ClubSportV25, default, 0)));

        string logitech = Path();
        Sent(() => WheelCenteringWriter.Apply(logitech, Logitech, G29, Frame(), 0));
        Assert.Equal(3, Sent(() => WheelCenteringWriter.Apply(logitech, Logitech, G29, default, 0, autoCenterSpring: true)).Count);
        WheelCenteringWriter.ResetDevice(logitech);
        LogitechRawHidWriter.ResetDevice(logitech);
    }

    /// <summary>Both passes that write a wheel, this PC's and the one that
    /// plays a peer's wheel frame, send centering's effects after the
    /// wheel's settings. The settings' auto-center is a spring in a Fanatec
    /// base's spring slot: written after centering, it turned Vehicle
    /// mode's spring off at every engine start and every change of mode.
    /// Each pass tells the writer when it wrote the slot, and whether the
    /// slider's spring has it.</summary>
    [Fact]
    public void CenteringIsWrittenAfterTheWheelsSettings_InBothPasses()
    {
        string pass = ExtrasPageTests.RepoText("PadForge.App", "Common", "Input", "InputManager.Step2.UpdateInputStates.cs");
        int settings = pass.IndexOf("_appliedWheelSettings[ud.DevicePath] = (desRange, desAc);", StringComparison.Ordinal);
        int centering = pass.IndexOf("WheelCenteringWriter.Apply(ud.DevicePath, ud.VendorId, ud.ProdId, centeringFrame,", StringComparison.Ordinal);
        Assert.True(settings > 0 && centering > settings, "the pass writes centering before the wheel's settings");
        Assert.Contains("hasCond ? cv.EffectType : 0, autoCenterSpring: isFanatecWheel && acMag > 0);", pass);
        Assert.Equal(1, CountOf(pass, "WheelCenteringWriter.SpringSlotWritten(ud.DevicePath);"));
        Assert.Contains("WheelCenteringWriter.WriteFanatecGameForce(ud.DevicePath, ud.ProdId, hasCond, cv.EffectType,", pass);
        Assert.Contains("bool autoCenter = fanatec && _appliedWheelSettings.TryGetValue(path, out var held) && held.ac > 0;", pass);
        Assert.Contains("WheelCenteringWriter.Apply(path, ud.VendorId, ud.ProdId, default, 0, autoCenter);", pass);

        string owner = ExtrasPageTests.RepoText("PadForge.App", "Services", "InputService.cs");
        int ownerSettings = owner.IndexOf("FanatecRawHidWriter.WriteAutocenter(path, acMag); WheelCenteringWriter.SpringSlotWritten(path);", StringComparison.Ordinal);
        int ownerCentering = owner.IndexOf("WheelCenteringWriter.Apply(path, vid, pid, centering, w.HasCond ? w.Effect : 0, autoCenterSpring: isFan && w.Ac > 0);", StringComparison.Ordinal);
        Assert.True(ownerSettings > 0 && ownerCentering > ownerSettings, "the owner writes centering before the wheel's settings");
        Assert.Equal(1, CountOf(owner, "WheelCenteringWriter.SpringSlotWritten(path);"));
        Assert.Contains("WheelCenteringWriter.WriteFanatecGameForce(path, pid, w.HasCond, w.Effect,", owner);
        Assert.Equal(1, CountOf(owner, "WheelCenteringWriter.Apply("));

        static int CountOf(string text, string part)
        {
            int count = 0;
            for (int at = text.IndexOf(part, StringComparison.Ordinal); at >= 0; at = text.IndexOf(part, at + part.Length, StringComparison.Ordinal))
                count++;
            return count;
        }
    }

    /// <summary>A wheel on another PC holds the last frame shipped to it,
    /// Vehicle mode's centering with it, until it is sent a stop frame
    /// (StopNativeWheel). The pass never recorded a shipped peer wheel, so
    /// neither a slot that let it go nor an engine that stopped sent one,
    /// and the vibration lane's stop ships nothing for a wheel.</summary>
    [Fact]
    public void APeerWheelGetsItsStop_WhenItsSlotLetsGoAndWhenTheEngineStops()
    {
        string pass = ExtrasPageTests.RepoText("PadForge.App", "Common", "Input", "InputManager.Step2.UpdateInputStates.cs");
        int ship = pass.IndexOf("centering: PadForge.Engine.RemoteLink.OutputEffectCodec.CenteringTail.From(centeringFrame));", StringComparison.Ordinal);
        int recorded = pass.IndexOf("_appliedWheelFfb[ud.DevicePath] = ffbSig;", ship, StringComparison.Ordinal);
        int peerReturn = pass.IndexOf("return;", ship, StringComparison.Ordinal);
        Assert.True(ship > 0 && recorded > ship && recorded < peerReturn, "the pass does not record a shipped peer wheel");
        Assert.Contains("if (isVendorFfb && !isFanatecPedal && _appliedWheelFfb.ContainsKey(ud.DevicePath)) StopNativeWheel(ud);", pass);
    }

    /// <summary>Vehicle mode's centering runs for a wheel on a slot that is
    /// created and turned on, in the engine's active pass. A wheel left on a
    /// disabled slot was centered every pass, and from the idle pass every
    /// 50 ms, past the 2 to 30 ms delay the centering model is built for
    /// (WheelCentering.Step). Off such a slot the frame is empty, which lets
    /// the wheel go.</summary>
    [Fact]
    public void CenteringRunsOnlyForAWheelOnAnActiveSlot()
    {
        string pass = ExtrasPageTests.RepoText("PadForge.App", "Common", "Input", "InputManager.Step2.UpdateInputStates.cs");
        Assert.Contains("if (centersItself && SettingsManager.SlotCreated[padIndex] && SettingsManager.SlotEnabled[padIndex])", pass);
        Assert.Contains("bool centeringSlot = false;", pass);
        Assert.Equal(2, CountOf(pass, "centeringSlot && !IsIdle,"));
        Assert.Contains("if (!active)", pass);
    }

    /// <summary>A peer's wheel frame sets the wheel's range, auto-center and
    /// LEDs, and so does the local pass. Each puts its own back after the
    /// other wrote them: the local pass forgets what it sent when a peer
    /// writes the wheel, and the owner's record of a peer's settings holds
    /// the count of the local pass's writes it saw. Each skipped a write the
    /// wheel needed, by a record of what it had sent itself.</summary>
    [Fact]
    public void APeerAndTheLocalPassEachPutTheirWheelSettingsBack()
    {
        string pass = ExtrasPageTests.RepoText("PadForge.App", "Common", "Input", "InputManager.Step2.UpdateInputStates.cs");
        int note = pass.IndexOf("public void NoteRemoteWheelWrite(string devicePath)", StringComparison.Ordinal);
        string body = pass.Substring(note, 600);
        Assert.Contains("_appliedWheelSettings.TryRemove(devicePath, out _);", body);
        Assert.Contains("_appliedLeds.TryRemove(devicePath, out _);", body);
        Assert.Equal(2, CountOf(pass, "RemoteLinkOutputRouter.NoteLocalWheelSettings(ud.DevicePath);"));

        string owner = ExtrasPageTests.RepoText("PadForge.App", "Services", "InputService.cs");
        Assert.Contains("int local = RemoteLinkOutputRouter.LocalWheelSettings(path);", owner);
        Assert.Contains("prev.Item1 != range || prev.Item2 != w.Ac || prev.Item5 != local", owner);
        Assert.Contains("prev.Item3 != w.LedMask || prev.Item4 != w.LedValid || prev.Item5 != local", owner);

        string path = "test://wheel/" + Guid.NewGuid().ToString("N");
        int before = RemoteLinkOutputRouter.LocalWheelSettings(path);
        RemoteLinkOutputRouter.NoteLocalWheelSettings(path);
        Assert.Equal(before + 1, RemoteLinkOutputRouter.LocalWheelSettings(path));
    }

    static int CountOf(string text, string part)
    {
        int count = 0;
        for (int at = text.IndexOf(part, StringComparison.Ordinal); at >= 0; at = text.IndexOf(part, at + part.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    // ── Thrustmaster ──

    /// <summary>A Thrustmaster wheel holds sixteen effects by id, and the
    /// header's id byte is the effect's id plus one (hid-tmt300rs.c
    /// T300RS_MAX_EFFECTS, t300rs_fill_header). The game's effect keeps
    /// the first. Centering's spring, damper, friction and force have
    /// the four after it, each uploaded and then played.</summary>
    [Fact]
    public void AThrustmasterWheel_TakesEachEffectUnderItsOwnId()
    {
        string path = Path();
        var sent = Sent(() => WheelCenteringWriter.Apply(path, Thrustmaster, T300, Frame(level: 8000), 0));
        foreach (var report in sent) output.WriteLine(Hex(report));
        Assert.Equal(8, sent.Count);

        // Spring under id 1: header id 2, code 0x64, both coefficients 0x5555, type 6, and its limit of 0x6aa6.
        Assert.Equal(39, sent[0].Length);
        Assert.Equal("60000264" + "55555555" + "00000000" + "A91AA91A" + "FEFFFEFFFEFFFEFF" + "A66AA66A" + "06"
            + "4FFFFF0000000000FFFF", Hex(sent[0]));
        Assert.Equal("60000289410000", Hex(sent[1]));
        // Damper under id 2: header id 3, type 7, its limit of 0x7ffc.
        Assert.Equal("60000364" + "42444244" + "00000000" + "CA4CCA4C" + "FEFFFEFFFEFFFEFF" + "FC7FFC7F" + "07"
            + "4FFFFF0000000000FFFF", Hex(sent[2]));
        Assert.Equal("60000389410000", Hex(sent[3]));
        // Friction under id 3: header id 4, the damper's type at a full coefficient under a low limit.
        Assert.Equal("60000464" + "FF7FFF7F" + "00000000" + "EA11EA11" + "FEFFFEFFFEFFFEFF" + "FC7FFC7F" + "07"
            + "4FFFFF0000000000FFFF", Hex(sent[4]));
        Assert.Equal("60000489410000", Hex(sent[5]));
        // The centering force under id 4: header id 5, half the level, 4000.
        Assert.Equal("6000056A" + "A00F" + "0000000000000000" + "00" + "4FFFFF0000000000FFFF", Hex(sent[6]));
        Assert.Equal("60000589410000", Hex(sent[7]));

        // A level that moves is an update of the playing effect, and one that halves to the same is nothing.
        var moved = Sent(() => WheelCenteringWriter.Apply(path, Thrustmaster, T300, Frame(level: 6000), 0));
        Assert.Single(moved);
        Assert.Equal("6000056A" + "B80B" + "0000000000000000" + "0045" + "FFFF0000", Hex(moved[0]));
        Assert.Empty(Sent(() => WheelCenteringWriter.Apply(path, Thrustmaster, T300, Frame(level: 6001), 0)));

        // The game's own force still goes under the first id, header id 1.
        var game = Sent(() => ThrustmasterRawHidWriter.WriteConstantForce(path, 2000));
        Assert.Equal("6000016A" + "E803", Hex(game[0])[..12]);
        Assert.Equal("60000189410000", Hex(game[1]));

        // Letting go stops each of centering's four, and leaves the game's alone.
        var released = Sent(() => WheelCenteringWriter.Apply(path, Thrustmaster, T300, default, 0));
        Assert.Equal(new[] { "6000028900", "6000038900", "6000048900", "6000058900" }, released.Select(Hex));
        WheelCenteringWriter.ResetDevice(path);
        ThrustmasterRawHidWriter.ResetDevice(path);
    }

    /// <summary>A force the wheel refused is written again at the next
    /// frame, whatever its level. The mark for a write that failed was
    /// -1, which is also the level a steering level of -2 goes on the
    /// wire with, so that one force was never tried again.</summary>
    [Fact]
    public void ARefusedThrustmasterForceIsTriedAgain_WhateverItsLevel()
    {
        string path = Path();
        var force = Frame(level: -2, spring: 0, damper: 0, friction: 0);
        Assert.Equal(-1, ThrustmasterRawHidWriter.ConstantLevel(force.Level));
        int refused = 0;
        using (RawHidOutput.CaptureForTest((_, _) => { refused++; return false; }))
            WheelCenteringWriter.Apply(path, Thrustmaster, T300, force, 0);
        // The force, and the first frame's stops of the other three ids.
        Assert.Equal(4, refused);
        Assert.NotEmpty(Sent(() => WheelCenteringWriter.Apply(path, Thrustmaster, T300, force, 0)));
        Assert.Empty(Sent(() => WheelCenteringWriter.Apply(path, Thrustmaster, T300, force, 0)));
        WheelCenteringWriter.ResetDevice(path);
        ThrustmasterRawHidWriter.ResetDevice(path);
    }

    /// <summary>The builders put the id in the header, and with no id
    /// given they build what they built before centering had any.</summary>
    [Fact]
    public void TheThrustmasterBuildersCarryTheId()
    {
        Assert.Equal(1, ThrustmasterRawHidWriter.BuildUpload(100)[1]);
        Assert.Equal(1, ThrustmasterRawHidWriter.BuildPlay()[1]);
        Assert.Equal(1, ThrustmasterRawHidWriter.BuildUpdate(100)[1]);
        Assert.Equal(1, ThrustmasterRawHidWriter.BuildStop()[1]);
        Assert.Equal(1, ThrustmasterRawHidWriter.BuildConditionUpload(1, 1, 0, 0, 1, 1, 1, 6)[1]);
        for (int id = 0; id < 16; id++)
        {
            Assert.Equal(id + 1, ThrustmasterRawHidWriter.BuildUpload(100, id)[1]);
            Assert.Equal(id + 1, ThrustmasterRawHidWriter.BuildPlay(id)[1]);
            Assert.Equal(id + 1, ThrustmasterRawHidWriter.BuildUpdate(100, id)[1]);
            Assert.Equal(id + 1, ThrustmasterRawHidWriter.BuildStop(id)[1]);
            Assert.Equal(id + 1, ThrustmasterRawHidWriter.BuildConditionUpload(1, 1, 0, 0, 1, 1, 1, 6, id)[1]);
        }
        Assert.Equal(new[] { 0, 1, 2, 3, 4 }, new[]
        {
            ThrustmasterRawHidWriter.GameEffect, ThrustmasterRawHidWriter.CenteringSpring, ThrustmasterRawHidWriter.CenteringDamper,
            ThrustmasterRawHidWriter.CenteringFriction, ThrustmasterRawHidWriter.CenteringForce,
        });
    }

    // ── The force ──

    /// <summary>The centering force joins the game's where the wheel's
    /// constant force has one slot. A Thrustmaster keeps them apart, and
    /// a game's condition effect holds a Logitech's first slot.</summary>
    [Fact]
    public void TheCenteringForceJoinsTheGamesWhereTheWheelHasOneSlot()
    {
        Assert.Equal(7000, WheelCenteringWriter.MergedForce(Logitech, G29, false, 4000, 3000));
        Assert.Equal(-1000, WheelCenteringWriter.MergedForce(Fanatec, CslDd, false, 4000, -5000));
        Assert.Equal(32767, WheelCenteringWriter.MergedForce(Logitech, G29, false, 30000, 30000));
        Assert.Equal(-32767, WheelCenteringWriter.MergedForce(Fanatec, CslDd, false, -30000, -30000));
        Assert.Equal(4000, WheelCenteringWriter.MergedForce(Thrustmaster, T300, false, 4000, 3000));
        Assert.Equal(4000, WheelCenteringWriter.MergedForce(Logitech, G29, true, 4000, 3000));
        // A Fanatec base keeps a game's condition in a slot of its own, so the force still joins.
        Assert.Equal(7000, WheelCenteringWriter.MergedForce(Fanatec, CslDd, true, 4000, 3000));
    }

    /// <summary>The force is compared as the wheel is sent it: a
    /// Logitech and a ClubSport V2.5 keep eight bits, a CSL DD sixteen,
    /// and a Thrustmaster half the level. Nothing but a level of 0 is
    /// the stop.</summary>
    [Fact]
    public void TheForceIsComparedAsItGoesOnTheWire()
    {
        Assert.Equal(WheelCenteringWriter.WireForce(Logitech, G29, 256), WheelCenteringWriter.WireForce(Logitech, G29, 511));
        Assert.NotEqual(WheelCenteringWriter.WireForce(Logitech, G29, 511), WheelCenteringWriter.WireForce(Logitech, G29, 512));
        Assert.Equal(WheelCenteringWriter.WireForce(Fanatec, ClubSportV25, 256), WheelCenteringWriter.WireForce(Fanatec, ClubSportV25, 511));
        Assert.NotEqual(WheelCenteringWriter.WireForce(Fanatec, CslDd, 256), WheelCenteringWriter.WireForce(Fanatec, CslDd, 257));
        Assert.Equal(WheelCenteringWriter.WireForce(Thrustmaster, T300, 1000), WheelCenteringWriter.WireForce(Thrustmaster, T300, 1001));
        Assert.NotEqual(WheelCenteringWriter.WireForce(Thrustmaster, T300, 1000), WheelCenteringWriter.WireForce(Thrustmaster, T300, 1002));
        foreach (var (vendor, product) in new[] { (Logitech, G29), (Fanatec, CslDd), (Fanatec, ClubSportV25), (Thrustmaster, T300) })
            for (int level = -32767; level <= 32767; level += 127)
                if (level != 0)
                    Assert.NotEqual(WheelCenteringWriter.WireForce(vendor, product, 0), WheelCenteringWriter.WireForce(vendor, product, (short)level));
        // A Logitech level that reads 0x80 on the wire, the rest value, is still a force to send.
        Assert.NotEqual(WheelCenteringWriter.WireForce(Logitech, G29, 0), WheelCenteringWriter.WireForce(Logitech, G29, 100));
    }

    /// <summary>The damper's steps by family, and the torque figures the
    /// makers publish.</summary>
    [Fact]
    public void TheDampersStepsAndTheListedTorque()
    {
        Assert.Equal(15, WheelCenteringWriter.DamperSteps(Logitech, G29));
        Assert.Equal(15, WheelCenteringWriter.DamperSteps(Fanatec, CslDd));
        Assert.Equal(100, WheelCenteringWriter.DamperSteps(Thrustmaster, T300));
        Assert.Equal(100, WheelCenteringWriter.DamperSteps(0x1234, 0x5678));

        Assert.Equal(2.3, WheelTorqueList.For(Logitech, G29));
        Assert.Equal(2.3, WheelTorqueList.For(Logitech, 0xC262));
        Assert.Equal(2.3, WheelTorqueList.For(Logitech, 0xC266));
        Assert.Equal(0, WheelTorqueList.For(Logitech, G27));
        Assert.Equal(15.0, WheelTorqueList.For(Fanatec, CslDd));
        Assert.Equal(8.0, WheelTorqueList.For(Fanatec, ClubSportV25));
        Assert.Equal(20.0, WheelTorqueList.For(Fanatec, 0x0006));
        Assert.Equal(25.0, WheelTorqueList.For(Fanatec, 0x0007));
        Assert.Equal(0, WheelTorqueList.For(Thrustmaster, T300));
        // No listed figure is above the one an unlisted wheel starts from.
        Assert.True(CenteringSettings.UnlistedWheelTorque >= 25.0);
    }

    /// <summary>The steering axis is the device's first, which reads 0 to
    /// 65535 with 32768 at center.</summary>
    [Fact]
    public void TheSteeringAxisIsTheFirst()
    {
        static CustomInputState State(int value)
        {
            var state = new CustomInputState();
            state.Axis[0] = value;
            state.Axis[1] = 12345;
            return state;
        }
        Assert.Equal(0.0, InputManager.SteeringAxisOf(State(32768)));
        Assert.Equal(-1.0, InputManager.SteeringAxisOf(State(0)));
        Assert.Equal(1.0, InputManager.SteeringAxisOf(State(65535)));
        Assert.Equal(0.5, InputManager.SteeringAxisOf(State(32768 + 16384)), 3);
        Assert.Equal(0.0, InputManager.SteeringAxisOf(null));
    }

    // ── Remote Link ──

    /// <summary>Centering rides a shared wheel's frame as a tail, and
    /// comes out as it went in. A frame with none decodes as before, and
    /// bytes that are no tail are not read as one.</summary>
    [Fact]
    public void TheWheelFrameCarriesCenteringAsATail()
    {
        var tail = OutputEffectCodec.CenteringTail.From(Frame(level: -12345));
        Assert.Equal(new OutputEffectCodec.CenteringTail(-12345, 6667, 2500, 5333, 6000, 1400), tail);
        byte[] plain = OutputEffectCodec.EncodeWheel(false, true, 1000, 0, 0, FfbEffectTypes.Const, 0, 0, 0, 0, 0, 0, 0, 100, 900, 0, false);
        byte[] with = OutputEffectCodec.EncodeWheel(false, true, 1000, 0, 0, FfbEffectTypes.Const, 0, 0, 0, 0, 0, 0, 0, 100, 900, 0, false,
            centering: tail);
        Assert.Equal(44, plain.Length);
        Assert.Equal(plain.Length + OutputEffectCodec.CenteringTail.Length, with.Length);
        // The frame up to the tail is the frame an owner from before the tail reads.
        Assert.Equal(plain, with[..plain.Length]);
        Assert.Equal(OutputEffectCodec.CenteringTail.Tag, with[plain.Length]);

        Assert.True(OutputEffectCodec.TryDecode(with, out var effect));
        Assert.Equal(OutputEffectCodec.Kind.Wheel, effect.Kind);
        Assert.Equal(1000, effect.Wheel.Force);
        Assert.Equal(900, effect.Wheel.RangeDeg);
        Assert.Equal(tail, effect.Wheel.Centering);

        Assert.True(OutputEffectCodec.TryDecode(plain, out var none));
        Assert.True(none.Wheel.Centering.IsEmpty);
        // An empty tail is not sent.
        Assert.Equal(plain, OutputEffectCodec.EncodeWheel(false, true, 1000, 0, 0, FfbEffectTypes.Const, 0, 0, 0, 0, 0, 0, 0, 100, 900, 0, false,
            centering: default));
        // One byte short, or another tag: no tail, and the frame still reads.
        Assert.True(OutputEffectCodec.TryDecode(with[..^1], out var cut));
        Assert.True(cut.Wheel.Centering.IsEmpty);
        byte[] other = (byte[])with.Clone();
        other[plain.Length] = 0xFA;
        Assert.True(OutputEffectCodec.TryDecode(other, out var foreign));
        Assert.True(foreign.Wheel.Centering.IsEmpty);
        // A coefficient past its 10000 is held to it.
        byte[] wild = (byte[])with.Clone();
        BinaryPrimitives.WriteUInt16LittleEndian(wild.AsSpan(plain.Length + 3), 60000);
        Assert.True(OutputEffectCodec.TryDecode(wild, out var held));
        Assert.Equal(10000, held.Wheel.Centering.SpringCoefficient);
    }

    /// <summary>A wheel frame that asks for nothing is a stop, which
    /// still goes through once the outputs are quiet.</summary>
    [Fact]
    public void AWheelFrameWithNothingInItIsAStop()
    {
        static OutputEffectCodec.OutputEffect Decode(byte[] payload)
        {
            Assert.True(OutputEffectCodec.TryDecode(payload, out var effect));
            return effect;
        }
        byte[] Wheel(short force, bool cond, OutputEffectCodec.CenteringTail tail)
            => OutputEffectCodec.EncodeWheel(cond, false, force, 0, 0, FfbEffectTypes.Spring, 0, 5000, 5000, 0, 0, 10000, 10000, 100, 900, 0,
                false, centering: tail);
        Assert.True(PadForge.Services.InputService.IsRelayedStop(Decode(Wheel(0, false, default))));
        Assert.False(PadForge.Services.InputService.IsRelayedStop(Decode(Wheel(500, false, default))));
        Assert.False(PadForge.Services.InputService.IsRelayedStop(Decode(Wheel(0, true, default))));
        Assert.False(PadForge.Services.InputService.IsRelayedStop(Decode(Wheel(0, false, OutputEffectCodec.CenteringTail.From(Frame())))));
    }

    // ── A wheel routed through SDL ──

    static HapticEffectFixture SdlWheel(uint features = SDL_HAPTIC_CONSTANT | SDL_HAPTIC_SPRING | SDL_HAPTIC_DAMPER | SDL_HAPTIC_FRICTION
        | SDL_HAPTIC_SINE | SDL_HAPTIC_LEFTRIGHT, int axes = 1)
    {
        var h = new HapticEffectFixture();
        h.Set("JoystickType", SDL_JoystickType.SDL_JOYSTICK_TYPE_WHEEL);
        h.Set("HapticFeatures", features);
        h.Set("NumHapticAxes", axes);
        h.Settings.CenteringMode = "1";
        return h;
    }

    static Vibration Centered(in CenteringFrame frame, Vibration game = null)
    {
        game ??= new Vibration();
        game.CenteringActive = true;
        game.Centering = frame;
        return game;
    }

    static CenteringFrame ForceOnly(short level) => Frame(level: level, spring: 0, damper: 0, friction: 0);

    static int Creates(HapticEffectFixture h) => h.Calls.Count(c => c == "create");

    /// <summary>A centering force the driver refuses is not asked for
    /// again until centering lets go, as a refused spring, damper or
    /// friction effect is not. Each poll of Vehicle mode was a CreateEffect
    /// call the driver refused.</summary>
    [Fact]
    public void ARefusedCenteringForceIsNotAskedForAgain()
    {
        using var h = SdlWheel();
        h.FailCreateOfType = (ushort)SDL_HAPTIC_CONSTANT;
        h.Apply(Centered(ForceOnly(5000)));
        int asked = Creates(h);
        Assert.Equal(1, asked);
        h.Apply(Centered(ForceOnly(6000)));
        h.Apply(Centered(ForceOnly(7000)));
        Assert.Equal(asked, Creates(h));

        // Centering lets go and comes back: the force is asked for again.
        h.Apply(new Vibration());
        h.FailCreateOfType = null;
        h.Apply(Centered(ForceOnly(5000)));
        Assert.Contains(h.Writes, w => w.type == SDL_HAPTIC_CONSTANT && w.constant.level == 5000);
    }

    /// <summary>A wheel routed through SDL that holds one effect gives it
    /// to Vehicle mode's centering, and the game's force and rumble wait
    /// until centering lets go, the rule the auto-center spring keeps with
    /// rumble. The game's effect was asked for on every poll, each refused.
    /// A refusal beside centering is tried alone once, to learn which kind
    /// of device it is.</summary>
    [Fact]
    public void AOneEffectWheelGivesItToCentering_AndTheGameWaits()
    {
        using var h = SdlWheel();
        h.Room = 1;
        // The first poll learns the device holds one effect: the game's is tried
        // alone, and let go. The second gives the effect back to centering.
        h.Apply(Centered(ForceOnly(5000), h.Directional()));
        h.Apply(Centered(ForceOnly(5000), h.Directional()));
        int asked = Creates(h);
        h.Apply(Centered(ForceOnly(5000), h.Directional()));
        h.Apply(Centered(ForceOnly(5000), h.Directional()));
        Assert.Equal(asked, Creates(h));
        Assert.True(h.PlayingType(SDL_HAPTIC_CONSTANT));
        Assert.Single(h.Allocated);

        // Rumble waits the same way.
        var rumble = new Vibration { LeftMotorSpeed = 30000, RightMotorSpeed = 30000 };
        h.Apply(Centered(ForceOnly(5000), rumble));
        int beforeRumble = Creates(h);
        h.Apply(Centered(ForceOnly(5000), new Vibration { LeftMotorSpeed = 30000, RightMotorSpeed = 30000 }));
        Assert.Equal(beforeRumble, Creates(h));

        // Centering lets go: the game's force has the effect.
        h.Apply(h.Directional());
        Assert.Contains(h.Writes, w => w.type == SDL_HAPTIC_CONSTANT && w.constant.level != 5000);
        Assert.Single(h.Allocated);
    }

    /// <summary>A game's effect that holds a one-effect wheel when Vehicle
    /// mode starts gives the effect up to centering.</summary>
    [Fact]
    public void CenteringTakesAOneEffectWheelFromTheGame()
    {
        using var h = SdlWheel();
        h.Room = 1;
        h.Apply(h.Directional());
        Assert.Single(h.Allocated);
        h.Apply(Centered(ForceOnly(5000), h.Directional()));
        Assert.Single(h.Allocated);
        Assert.Contains(h.Writes, w => w.type == SDL_HAPTIC_CONSTANT && w.constant.level == 5000);
        int asked = Creates(h);
        h.Apply(Centered(ForceOnly(5000), h.Directional()));
        Assert.Equal(asked, Creates(h));
    }

    /// <summary>A wheel routed through SDL holds centering in four
    /// effects of its own: a constant force on the steering axis, and a
    /// spring, a damper and a friction effect symmetric about center.
    /// Each is written only when it changes, and the force only when it
    /// moves by a step.</summary>
    [Fact]
    public void AnSdlWheelHoldsCenteringInFourEffects()
    {
        using var h = SdlWheel();
        Assert.True(ForceFeedbackState.IsGenericWheelVehicleCapable(h.Device));
        Assert.Equal(CenteringEffects.All, h.State.CenteringEffectsOf(h.Device));

        h.Apply(Centered(Frame(level: 5000)));
        output.WriteLine(string.Join(",", h.Calls));
        Assert.Equal(4, h.Allocated.Count);
        Assert.Equal(4, h.Writes.Count);
        var force = h.Writes.Single(w => w.type == SDL_HAPTIC_CONSTANT);
        Assert.Equal(5000, force.constant.level);
        Assert.Equal(SDL_HAPTIC_STEERING_AXIS, force.constant.direction.type);
        Assert.Equal(SDL_HAPTIC_INFINITY, force.constant.length);
        var spring = h.Writes.Single(w => w.type == SDL_HAPTIC_SPRING);
        Assert.Equal(6667 * 32767 / 10000, spring.condition.right_coeff0);
        Assert.Equal(spring.condition.right_coeff0, spring.condition.left_coeff0);
        Assert.Equal(2500 * 65535 / 10000, spring.condition.right_sat0);
        Assert.Equal(spring.condition.right_sat0, spring.condition.left_sat0);
        Assert.Equal(0, spring.condition.center0);
        Assert.Equal(SDL_HAPTIC_CARTESIAN, spring.condition.direction.type);
        Assert.Equal(1, spring.condition.direction.dir0);
        var damper = h.Writes.Single(w => w.type == SDL_HAPTIC_DAMPER);
        Assert.Equal(5333 * 32767 / 10000, damper.condition.right_coeff0);
        Assert.Equal(6000 * 65535 / 10000, damper.condition.right_sat0);
        var friction = h.Writes.Single(w => w.type == SDL_HAPTIC_FRICTION);
        Assert.Equal(32767, friction.condition.right_coeff0);
        Assert.Equal(1400 * 65535 / 10000, friction.condition.right_sat0);
        Assert.True(h.State.IsActive);
        Assert.Equal(4, h.Playing.Count);

        // The same frame, and a force that moved by less than a step: nothing.
        h.Apply(Centered(Frame(level: 5000)));
        h.Apply(Centered(Frame(level: (short)(5000 + ForceFeedbackState.CenteringLevelStep - 1))));
        Assert.Equal(4, h.Writes.Count);
        // A step: the force alone is updated in place.
        h.Apply(Centered(Frame(level: (short)(5000 + ForceFeedbackState.CenteringLevelStep))));
        Assert.Equal(5, h.Writes.Count);
        Assert.Equal(5008, h.Writes[^1].constant.level);
        Assert.Equal(4, h.Allocated.Count);
        // Back to nothing, however small the move.
        h.Apply(Centered(Frame(level: 3)));
        h.Apply(Centered(Frame(level: 0)));
        Assert.Equal(0, h.Writes[^1].constant.level);
        // A changed damper is an update, and one of nothing lets the effect go.
        int writes = h.Writes.Count;
        h.Apply(Centered(Frame(damper: 2000)));
        Assert.Equal(writes + 1, h.Writes.Count);
        Assert.Equal(2000 * 32767 / 10000, h.Writes[^1].condition.right_coeff0);
        h.Apply(Centered(Frame(damper: 0)));
        Assert.Equal(3, h.Allocated.Count);
        Assert.False(h.PlayingType(SDL_HAPTIC_DAMPER));

        // Vehicle mode off: every effect goes, and nothing is held.
        h.Apply(new Vibration());
        Assert.Empty(h.Allocated);
        Assert.False(h.State.IsActive);
    }

    /// <summary>A wheel with two haptic axes takes the force from the
    /// east, whose share on the steering axis is the level itself, and a
    /// wheel with no friction effect takes friction as a second
    /// damper.</summary>
    [Fact]
    public void AnSdlWheelWithTwoAxesOrNoFrictionEffect()
    {
        using (var two = SdlWheel(axes: 2))
        {
            two.Apply(Centered(Frame(level: -4000)));
            var force = two.Writes.Single(w => w.type == SDL_HAPTIC_CONSTANT);
            Assert.Equal(SDL_HAPTIC_POLAR, force.constant.direction.type);
            Assert.Equal(9000, force.constant.direction.dir0);
            Assert.Equal(-4000, force.constant.level);
        }
        using (var plain = SdlWheel(SDL_HAPTIC_CONSTANT | SDL_HAPTIC_SPRING | SDL_HAPTIC_DAMPER | SDL_HAPTIC_LEFTRIGHT))
        {
            Assert.Equal(CenteringEffects.All, plain.State.CenteringEffectsOf(plain.Device));
            plain.Apply(Centered(Frame(level: 100)));
            var dampers = plain.Writes.Where(w => w.type == SDL_HAPTIC_DAMPER).ToList();
            Assert.Equal(2, dampers.Count);
            Assert.Contains(dampers, d => d.condition.right_coeff0 == 32767 && d.condition.right_sat0 == 1400 * 65535 / 10000);
            Assert.Contains(dampers, d => d.condition.right_coeff0 == 5333 * 32767 / 10000);
            Assert.Equal(4, plain.Allocated.Count);
        }
        using (var bare = SdlWheel(SDL_HAPTIC_CONSTANT | SDL_HAPTIC_LEFTRIGHT))
        {
            // A constant force and nothing else: the force is all the wheel is sent.
            Assert.Equal(CenteringEffects.None, bare.State.CenteringEffectsOf(bare.Device));
            bare.Apply(Centered(Frame(level: 100)));
            Assert.Single(bare.Allocated);
            Assert.Equal(SDL_HAPTIC_CONSTANT, bare.Writes[0].type);
        }
        using (var none = SdlWheel(SDL_HAPTIC_SPRING | SDL_HAPTIC_LEFTRIGHT))
            Assert.False(ForceFeedbackState.IsGenericWheelVehicleCapable(none.Device));
    }

    /// <summary>An effect the driver refuses is left out from then on:
    /// the next frame is composed without it, and it is not asked for
    /// again until centering is let go.</summary>
    [Fact]
    public void AnEffectTheDriverRefusesIsLeftOut()
    {
        using var h = SdlWheel();
        h.FailCreateOfType = (ushort)SDL_HAPTIC_DAMPER;
        h.Apply(Centered(Frame(level: 100)));
        Assert.Equal(3, h.Allocated.Count);
        Assert.Equal(CenteringEffects.Spring | CenteringEffects.Friction, h.State.CenteringEffectsOf(h.Device));
        int creates = h.Calls.Count(c => c == "create");
        h.Apply(Centered(Frame(level: 100)));
        Assert.Equal(creates, h.Calls.Count(c => c == "create"));

        // Let go and taken up again, the damper is asked for once more.
        h.FailCreateOfType = null;
        h.Apply(new Vibration());
        Assert.Equal(CenteringEffects.All, h.State.CenteringEffectsOf(h.Device));
        h.Apply(Centered(Frame(level: 100)));
        Assert.Equal(4, h.Allocated.Count);
    }

    /// <summary>A game's force plays beside centering in the effect it
    /// always had, the slider's spring rests while Vehicle mode centers,
    /// and a stop lets go of everything.</summary>
    [Fact]
    public void AGamesForcePlaysBesideCentering_AndTheSlidersSpringRests()
    {
        using var h = SdlWheel();
        h.Settings.AutoCenterStrength = "60";
        h.Apply(Centered(Frame(level: 100, spring: 0)));
        // No spring from the slider: Vehicle mode's frame asked for none, and it has the wheel.
        Assert.False(h.PlayingType(SDL_HAPTIC_SPRING));
        Assert.Equal(3, h.Allocated.Count);

        h.Apply(Centered(Frame(level: 100, spring: 0), h.Directional()));
        Assert.Equal(4, h.Allocated.Count);
        Assert.Equal(2, h.Types.Values.Count(t => t == SDL_HAPTIC_CONSTANT));

        // Spring mode: centering's effects go and the slider's spring comes back.
        h.Settings.CenteringMode = "0";
        h.Apply(new Vibration());
        Assert.True(h.PlayingType(SDL_HAPTIC_SPRING));
        Assert.Single(h.Allocated);

        h.Settings.CenteringMode = "1";
        h.Apply(Centered(Frame(level: 100)));
        h.State.StopDeviceForces(h.Device);
        Assert.Empty(h.Allocated);
        Assert.False(h.State.IsActive);
    }

    /// <summary>A reopened wheel holds none of the effects made on the
    /// old handle, so each is made again.</summary>
    [Fact]
    public void AReopenedSdlWheelIsSentEverythingAgain()
    {
        using var h = SdlWheel();
        h.Apply(Centered(Frame(level: 100)));
        Assert.Equal(4, h.Calls.Count(c => c == "create"));
        h.Set("Haptic", new IntPtr(2));
        h.Apply(Centered(Frame(level: 100)));
        Assert.Equal(8, h.Calls.Count(c => c == "create"));
    }

    // ── The road ──

    /// <summary>The grip figures come from one table, each over dry
    /// asphalt's 0.85 and kept to two places: wet asphalt 0.60, gravel
    /// 0.60, a dry earth road 0.68 and a wet one 0.55, hard-packed snow
    /// 0.20 and ice 0.10.</summary>
    [Fact]
    public void TheGripFiguresAreTheTablesOverDryAsphalt()
    {
        Assert.Equal(1.0, RoadGrip.Dry);
        Assert.Equal(0.60 / 0.85, RoadGrip.Wet, 2);
        Assert.Equal(0.60 / 0.85, RoadGrip.Gravel, 2);
        Assert.Equal(0.68 / 0.85, RoadGrip.Dirt, 2);
        Assert.Equal(0.55 / 0.85, RoadGrip.Mud, 2);
        Assert.Equal(0.20 / 0.85, RoadGrip.Snow, 2);
        Assert.Equal(0.10 / 0.85, RoadGrip.Ice, 2);
        Assert.Equal(RoadGrip.Mud, RoadGrip.Grass);
        Assert.Equal(RoadGrip.Mud, RoadGrip.Sand);
        Assert.Equal(1.0, RoadGrip.FromWetness(0));
        Assert.Equal((1.0 + RoadGrip.Wet) / 2, RoadGrip.FromWetness(0.5), 9);
        Assert.Equal(RoadGrip.Wet, RoadGrip.FromWetness(1));
        Assert.Equal(RoadGrip.Wet, RoadGrip.FromWetness(7));
        Assert.Equal(1.0, RoadGrip.FromWetness(double.NaN));
        Assert.Equal(0.85, RoadGrip.FrontAxle(1.0, 0.70), 9);
        Assert.Equal(0.70, RoadGrip.FrontAxle(0, 0.70));
        Assert.Equal(1.0, RoadGrip.FrontAxle(1.0, 0));
        Assert.Equal(0, RoadGrip.FrontAxle(0, 0));
        // The presets are the table's too.
        Assert.Equal(RoadGrip.Wet, CenteringSettings.WetGrip);
        Assert.Equal(RoadGrip.Snow, CenteringSettings.SnowGrip);
        Assert.Equal(RoadGrip.Ice, CenteringSettings.IceGrip);
    }

    /// <summary>Each game's own list of surfaces, read as its reference
    /// gives it.</summary>
    [Fact]
    public void EachGamesSurfacesReadByItsOwnList()
    {
        // rFactor: 0 dry, 1 wet, 2 grass, 3 dirt, 4 gravel, 5 rumble strip, 6 special.
        Assert.Equal(1.0, SurfaceGrips.RFactor(0, 0));
        Assert.Equal((1.0 + RoadGrip.Wet) / 2, SurfaceGrips.RFactor(0, 0.5), 9);
        Assert.Equal(RoadGrip.Wet, SurfaceGrips.RFactor(1, 0));
        Assert.Equal(RoadGrip.Wet, SurfaceGrips.RFactor(1, 1));
        Assert.Equal(RoadGrip.Grass, SurfaceGrips.RFactor(2, 0));
        Assert.Equal(RoadGrip.Dirt, SurfaceGrips.RFactor(3, 1));
        Assert.Equal(RoadGrip.Gravel, SurfaceGrips.RFactor(4, 0));
        Assert.Equal(1.0, SurfaceGrips.RFactor(5, 0));
        Assert.Equal(1.0, SurfaceGrips.RFactor(6, 0));
        Assert.Equal(0, SurfaceGrips.RFactor(7, 0));

        // F1: 0 tarmac to 11 ridged, with 4 gravel, 5 mud, 6 sand, 7 grass and 8 water.
        Assert.Equal(new[] { 1.0, 1.0, 1.0, 1.0, RoadGrip.Gravel, RoadGrip.Mud, RoadGrip.Sand, RoadGrip.Grass, RoadGrip.Wet, 1.0, 1.0, 1.0, 0 },
            Enumerable.Range(0, 13).Select(SurfaceGrips.F1));

        // RaceRoom: -1 unavailable, 0 none, 1 tarmac, 2 grass, 3 dirt, 4 gravel, 5 rumble, 6 concrete.
        Assert.Equal(new[] { 0, 0, 1.0, RoadGrip.Grass, RoadGrip.Dirt, RoadGrip.Gravel, 1.0, 1.0, 0 },
            Enumerable.Range(-1, 9).Select(SurfaceGrips.RaceRoom));

        // Madness: by the header's order. Road 0, grass 7, gravel 8, sand 15, dirt 17, snow 33 and 34, ice road 45.
        Assert.Equal(1.0, SurfaceGrips.Madness(0, 0));
        Assert.Equal(RoadGrip.Wet, SurfaceGrips.Madness(0, 1));
        Assert.Equal(1.0, SurfaceGrips.Madness(10, 0));
        Assert.Equal(RoadGrip.Grass, SurfaceGrips.Madness(7, 0));
        Assert.Equal(RoadGrip.Gravel, SurfaceGrips.Madness(8, 1));
        Assert.Equal(RoadGrip.Sand, SurfaceGrips.Madness(15, 0));
        Assert.Equal(RoadGrip.Dirt, SurfaceGrips.Madness(17, 0));
        Assert.Equal(RoadGrip.Snow, SurfaceGrips.Madness(33, 0));
        Assert.Equal(RoadGrip.Snow, SurfaceGrips.Madness(34, 0));
        Assert.Equal(RoadGrip.Ice, SurfaceGrips.Madness(45, 0));
        Assert.Equal(1.0, SurfaceGrips.Madness(50, 0));
        Assert.Equal(0, SurfaceGrips.Madness(51, 0));
        Assert.Equal(0, SurfaceGrips.Madness(-1, 0));

        // The SCS trucks name their substances, in bytes that end at a zero.
        static byte[] Name(string text)
        {
            var bytes = new byte[64];
            System.Text.Encoding.ASCII.GetBytes(text).CopyTo(bytes, 0);
            return bytes;
        }
        Assert.Equal(1.0, SurfaceGrips.Scs(Name("road")));
        Assert.Equal(RoadGrip.Dirt, SurfaceGrips.Scs(Name("road_dirt")));
        Assert.Equal(RoadGrip.Snow, SurfaceGrips.Scs(Name("Road_Snow")));
        Assert.Equal(RoadGrip.Ice, SurfaceGrips.Scs(Name("ice")));
        Assert.Equal(RoadGrip.Grass, SurfaceGrips.Scs(Name("grass")));
        Assert.Equal(RoadGrip.Gravel, SurfaceGrips.Scs(Name("gravel")));
        Assert.Equal(RoadGrip.Mud, SurfaceGrips.Scs(Name("mud")));
        Assert.Equal(0, SurfaceGrips.Scs(Name("")));

        // iRacing: 1 dry through 7 extremely wet, 0 unknown.
        Assert.Equal(0, SurfaceGrips.IRacing(0));
        Assert.Equal(1.0, SurfaceGrips.IRacing(1));
        Assert.Equal((1.0 + RoadGrip.Wet) / 2, SurfaceGrips.IRacing(4), 9);
        Assert.Equal(RoadGrip.Wet, SurfaceGrips.IRacing(7), 9);
        Assert.Equal(0, SurfaceGrips.IRacing(8));

        // Forza's puddle depth, and Assetto Corsa's own figure.
        Assert.Equal(1.0, SurfaceGrips.Forza(0));
        Assert.Equal(RoadGrip.Wet, SurfaceGrips.Forza(1));
        Assert.Equal(0, SurfaceGrips.Forza(float.NaN));
        Assert.Equal(0.97, SurfaceGrips.AssettoCorsa(0.97f), 6);
        Assert.Equal(0, SurfaceGrips.AssettoCorsa(0));
        Assert.Equal(0, SurfaceGrips.AssettoCorsa(250f));
        Assert.Equal(0, SurfaceGrips.AssettoCorsa(float.NaN));
    }

    static void F32(byte[] p, int at, float v) => BinaryPrimitives.WriteSingleLittleEndian(p.AsSpan(at), v);

    static TelemetryFrame NewFrame()
    {
        var f = new TelemetryFrame();
        f.Reset(0);
        return f;
    }

    /// <summary>The packet decoders read the road with the speed: Forza's
    /// puddle depths at 132 and 136, F1's front surfaces at 58 and 59 of
    /// the car's entry, Project CARS 2's terrain at 140 with the rain
    /// from its game state at 17, and Assetto Corsa's surface grip at
    /// 280, or 1240 in Competizione.</summary>
    [Fact]
    public void ThePacketDecodersReadTheRoad()
    {
        var forza = GameTelemetryTests.Forza(311);
        F32(forza, 132, 0.5f);
        F32(forza, 136, 1.0f);
        var f = NewFrame();
        Assert.True(TelemetryDecoders.TryForza(forza, f));
        // One wheel half in a puddle and one fully in: the mean of the two.
        Assert.Equal(((1.0 + RoadGrip.Wet) / 2 + RoadGrip.Wet) / 2, f.Grip, 4);
        Assert.Equal(30f, f.SpeedMs);
        // A sled packet, which has no dash, still has the puddles.
        var sled = new byte[232];
        F32(sled, 8, 8000f);
        F32(sled, 32, 3f);
        F32(sled, 40, 4f);
        f = NewFrame();
        Assert.True(TelemetryDecoders.TryForza(sled, f));
        Assert.Equal(5f, f.SpeedMs, 5);
        Assert.Equal(1.0, f.Grip, 6);

        foreach (int year in new[] { 2022, 2024 })
        {
            var f1 = GameTelemetryTests.F1Telemetry(year, 3);
            int entry = (year == 2022 ? 24 : 29) + 3 * 60;
            f1[entry + 56] = 7;   // rear left on grass, which the front axle does not read
            f1[entry + 58] = 7;   // front left on grass
            f1[entry + 59] = 0;   // front right on tarmac
            f = NewFrame();
            Assert.Equal(TelemetryDecode.Frame, TelemetryDecoders.TryF1(f1, new TelemetryDecoders.F1State(), f));
            Assert.Equal((RoadGrip.Grass + 1.0) / 2, f.Grip, 6);
            Assert.Equal(50f, f.SpeedMs, 4);
        }

        var state = new TelemetryDecoders.ProjectCars2State();
        var gameState = GameTelemetryTests.ProjectCars2GameState(2);
        gameState[17] = 255;
        Assert.Equal(TelemetryDecode.Claimed, TelemetryDecoders.TryProjectCars2(gameState, state, NewFrame()));
        Assert.Equal(1f, state.Rain);
        var pcars = GameTelemetryTests.ProjectCars2Telemetry();
        pcars[140] = 0;    // road, wet
        pcars[141] = 34;   // snow
        f = NewFrame();
        Assert.Equal(TelemetryDecode.Frame, TelemetryDecoders.TryProjectCars2(pcars, state, f));
        Assert.Equal((RoadGrip.Wet + RoadGrip.Snow) / 2, f.Grip, 6);

        var (physics, statics, graphics) = GameTelemetryTests.Kunos("1.7", 2);
        F32(graphics, AssettoCorsaTelemetrySource.SurfaceGripOffset, 0.97f);
        f = NewFrame();
        Assert.True(TelemetryDecoders.TryKunos(physics, statics, graphics, f));
        Assert.Equal(0.97f, f.Grip);
        // Competizione's page has sixty cars where Assetto Corsa's has its grip, so that float is not read as one.
        (physics, statics, graphics) = GameTelemetryTests.Kunos("1.9", 2);
        F32(graphics, AssettoCorsaTelemetrySource.SurfaceGripOffset, 0.5f);
        f = NewFrame();
        Assert.True(TelemetryDecoders.TryKunos(physics, statics, graphics, f));
        Assert.Equal(0f, f.Grip);
        var page = new byte[TelemetryDecoders.KunosGraphicsBytes];
        graphics.CopyTo(page, 0);
        F32(page, AssettoCorsaTelemetrySource.CompetizioneSurfaceGripOffset, 0.93f);
        f = NewFrame();
        Assert.True(TelemetryDecoders.TryKunos(physics, statics, page, f));
        Assert.Equal(0.93f, f.Grip);
        Assert.Equal(280, AssettoCorsaTelemetrySource.SurfaceGripOffset);
        Assert.Equal(1240, AssettoCorsaTelemetrySource.CompetizioneSurfaceGripOffset);
        Assert.True(AssettoCorsaTelemetrySource.IsCompetizione("1.8"));
        Assert.False(AssettoCorsaTelemetrySource.IsCompetizione("1.7"));
        Assert.False(AssettoCorsaTelemetrySource.IsCompetizione(""));

        // A frame's grip is held to 0 through 1, and rests with the frame.
        f.Grip = 4f;
        f.Clamp();
        Assert.Equal(1f, f.Grip);
        var copy = NewFrame();
        f.CopyTo(copy);
        Assert.Equal(1f, copy.Grip);
        f.Reset(0);
        Assert.Equal(0f, f.Grip);
    }

    /// <summary>The lights' own listeners read the same fields: Forza's
    /// velocity and puddles, and F1's speed and front surfaces from the
    /// packet that gave the RPM.</summary>
    [Fact]
    public void TheHubsListenersReadSpeedAndRoad()
    {
        var forza = new byte[311];
        F32(forza, 32, 6f);
        F32(forza, 36, 0f);
        F32(forza, 40, 8f);
        F32(forza, 132, 1f);
        F32(forza, 136, 1f);
        ForzaUdpTelemetrySource.ReadMotion(forza, out bool hasSpeed, out float speed, out float grip);
        Assert.True(hasSpeed);
        Assert.Equal(10f, speed, 5);
        Assert.Equal(RoadGrip.Wet, grip, 6);
        // The 20 bytes the RPM needs hold neither.
        ForzaUdpTelemetrySource.ReadMotion(new byte[20], out hasSpeed, out speed, out grip);
        Assert.False(hasSpeed);
        Assert.Equal(0f, grip);

        foreach (int year in new[] { 2022, 2025 })
        {
            var f1 = GameTelemetryTests.F1Telemetry(year, 5);
            int entry = (year == 2022 ? 24 : 29) + 5 * 60;
            f1[entry + 58] = 4;
            f1[entry + 59] = 4;
            Assert.Equal(CodemastersUdpTelemetrySource.F1Read.Rpm,
                CodemastersUdpTelemetrySource.ReadF1(f1, out _, out float rpm, out _, out _));
            Assert.Equal(11000f, rpm);
            CodemastersUdpTelemetrySource.ReadF1Motion(f1, out float f1Speed, out float f1Grip);
            Assert.Equal(50f, f1Speed, 4);
            Assert.Equal(RoadGrip.Gravel, f1Grip, 6);
        }

        // The Madness sequence number sits past the seven weather floats that follow the rain at 7300.
        Assert.Equal(7300 + 5 * 4, MadnessTelemetrySource.OffSequence);

        var snap = new GameTelemetrySnapshot();
        Assert.False(snap.HasSpeed);
        snap.SetSpeed(float.NaN);
        Assert.False(snap.HasSpeed);
        snap.SetSpeed(-12f);
        Assert.True(snap.HasSpeed);
        Assert.Equal(12f, snap.Speed);
    }

    // ── The runtime ──

    static CenteringSettings Vehicle(double torque = 2.2) => new(true, SteeringVehicle.Sedan, 100, 100, 100, 100, 0.30, false,
        60 * 0.44704, 1, torque, 900);

    /// <summary>The runtime gives Vehicle mode's frame in Vehicle mode,
    /// nothing in Spring mode, and the card what it last sent.</summary>
    [Fact]
    public void TheRuntimeStepsVehicleMode_AndTellsTheCard()
    {
        var runtime = new WheelCenteringRuntime();
        Assert.False(runtime.IsPolled(1000));
        Assert.False(runtime.Live(1000).Active);
        Assert.Null(runtime.Test(1000));

        CenteringFrame frame = default;
        for (int ms = 0; ms <= 1200; ms++) frame = runtime.Step(Vehicle(), "", 0.1, -1, 0, 5000 + ms, out bool testing);
        Assert.True(frame.Level > 0);
        var live = runtime.Live(6200);
        Assert.True(live.Active);
        Assert.Equal(45.0, live.AngleDegrees, 6);
        Assert.True(live.TorqueNm > 0);
        Assert.Equal(60 * 0.44704, live.Speed, 0);
        Assert.False(live.SpeedFromGame);
        Assert.True(runtime.IsPolled(6200));
        // A second without a step: the card takes centering to be off.
        Assert.False(runtime.Live(6200 + WheelCenteringRuntime.StaleMs + 1).Active);
        Assert.False(runtime.IsPolled(6200 + WheelCenteringRuntime.StaleMs + 1));

        // A game's speed and grip, where the settings ask for them.
        var fromGame = new CenteringSettings(true, SteeringVehicle.Sedan, 100, 100, 100, 100, 0.30, true, 20, 0, 2.2, 900);
        runtime.Step(fromGame, "", 0.1, 33.0, 0.7, 6300, out _);
        live = runtime.Live(6300);
        Assert.True(live.SpeedFromGame);
        Assert.True(live.GripFromGame);
        Assert.Equal(33.0, live.Speed, 0);
        Assert.Equal(0.7, live.Grip, 1);
        runtime.Step(fromGame, "", 0.1, -1, 0, 6301, out _);
        live = runtime.Live(6301);
        Assert.False(live.SpeedFromGame);
        Assert.False(live.GripFromGame);
        Assert.Equal(20.0, live.Speed, 0);

        // Spring mode: an empty frame, and the card reads centering as off.
        var spring = new CenteringSettings(false, SteeringVehicle.Sedan, 100, 100, 100, 100, 0.30, false, 20, 1, 2.2, 900);
        frame = runtime.Step(spring, "", 0.1, -1, 0, 6302, out bool none);
        Assert.False(none);
        Assert.Equal(0, frame.Level);
        Assert.Equal(0, frame.DamperCoefficient);
        Assert.False(runtime.Live(6302).Active);
        Assert.True(runtime.IsPolled(6302));

        // Back in Vehicle mode, centering comes in softly again.
        frame = runtime.Step(Vehicle(), "", 0.1, -1, 0, 6303, out _);
        Assert.Equal(0, frame.Level);
    }

    /// <summary>A Release Test owns the wheel while it runs, its result
    /// is the window's to read, and a wheel that stops being polled
    /// reads as stalled.</summary>
    [Fact]
    public void TheRuntimeRunsAReleaseTest()
    {
        var plant = new WheelPlant { Inertia = 0.015, Friction = 0.15, Delay = 0.008, WheelTorque = 2.2, DamperFull = 0.30, SpringFull = 12.0 };
        // The search for the return ratio runs in place, so the simulated clock waits for it.
        var runtime = new WheelCenteringRuntime { TestSchedule = work => work() };
        var settings = Vehicle();
        long tick = 10_000;
        Assert.True(runtime.StartTest(settings, "", tick));
        Assert.False(runtime.StartTest(settings, "", tick));
        bool sawTesting = false;
        ReleaseTestStatus status = runtime.Test(tick);
        Assert.Equal(ReleaseTestOutcome.Running, status.Outcome);
        Assert.False(status.Stalled);
        for (int ms = 0; ms < 40_000 && status.Outcome == ReleaseTestOutcome.Running; ms++)
        {
            double axis = Math.Round(plant.Axis * 32767.0) / 32767.0;
            var frame = runtime.Step(settings, "", axis, -1, 0, tick + ms, out bool testing);
            sawTesting |= testing;
            plant.Command(frame);
            plant.Advance(0.001);
            if (ms % 250 == 0 || !testing) status = runtime.Test(tick + ms);
            // Vehicle mode's centering is off the card while the test has the wheel.
            if (testing) Assert.False(runtime.Live(tick + ms).Active);
        }
        Assert.True(sawTesting);
        Assert.Equal(ReleaseTestOutcome.Done, status.Outcome);
        Assert.Equal(1.0, status.Progress);
        Assert.NotNull(status.Result);
        Assert.Equal(ReleaseTest.ReleaseMs, status.Before.Length);
        Assert.Equal(ReleaseTest.ReleaseMs, status.After.Length);
        Assert.InRange(status.Result.Inertia, 0.015 * 0.85, 0.015 * 1.15);
        // The saved form reads back, and a second test may start once the first is over.
        Assert.NotNull(WheelCalibration.Parse(status.Result.Format()));
        Assert.True(runtime.StartTest(settings, status.Result.Format(), tick + 50_000));
        // No poll for more than a second: stalled, and another may take its place.
        Assert.True(runtime.Test(tick + 50_000 + WheelCenteringRuntime.StaleMs + 1).Stalled);
        Assert.True(runtime.StartTest(settings, "", tick + 60_000));
        runtime.CancelTest();
        Assert.Equal(ReleaseTestOutcome.Canceled, runtime.Test(tick + 60_001).Outcome);
        var after = runtime.Step(settings, "", 0.2, -1, 0, tick + 60_002, out bool still);
        Assert.False(still);
        runtime.ClearTest();
        Assert.Null(runtime.Test(tick + 60_003));
    }

    /// <summary>A saved measurement is read once for its text, and a
    /// text that is no measurement reads as none.</summary>
    [Fact]
    public void TheRuntimeReadsTheSavedMeasurement()
    {
        var measured = new WheelCalibration
        {
            Inertia = 0.02, Friction = 0.03, Delay = 0.004, WheelTorque = 8.0, DamperMid = 0.43, DamperFull = 0.8,
            SpringThird = 133, SpringTwoThirds = 267, SpringFull = 400, ForceSign = -1,
        };
        var settings = new CenteringSettings(true, SteeringVehicle.StanfordX1, 100, 100, 100, 100, 0.30, false, 60 * 0.44704, 1, 8.0, 900);
        var with = new WheelCenteringRuntime();
        var without = new WheelCenteringRuntime();
        CenteringFrame a = default, b = default;
        for (int ms = 0; ms <= 1200; ms++)
        {
            a = with.Step(settings, measured.Format(), 0.01, -1, 0, 1000 + ms, out _);
            b = without.Step(settings, "not a measurement", 0.01, -1, 0, 1000 + ms, out _);
        }
        // The measured wheel answers the other way and has a spring worth using.
        Assert.True(a.Level < 0);
        Assert.True(b.Level > 0);
        Assert.Equal(WheelCalibration.SpringCoefficients[0], a.SpringCoefficient);
        Assert.Equal(0, b.SpringCoefficient);
    }
}

/// <summary>#588: the Game Telemetry row's speed and road, which a
/// wheel's centering reads beside the lights' RPM.</summary>
[Collection("SettingsManagerStatics")]
public sealed class WheelCenteringTelemetryRowTests : IDisposable
{
    public WheelCenteringTelemetryRowTests() => GameTelemetryRuntime.ResetForTest();
    public void Dispose() => GameTelemetryRuntime.ResetForTest();

    [Fact]
    public void TheRowGivesItsSpeedAndRoad_WithOrWithoutAMaxRpm()
    {
        Assert.False(GameTelemetryRuntime.TryGetMotion(1000, out _, out _, out _));

        var frame = new TelemetryFrame();
        frame.Reset(0);
        frame.SpeedMs = 42f;
        frame.SpeedReported = true;
        frame.Grip = 0.7f;
        frame.Rpm = 3000f;
        GameTelemetryRuntime.Publish(frame, Array.Empty<TelemetryVariable>(), true, 1000);
        // No max RPM: nothing for the lights, and the speed and the road all the same.
        Assert.False(GameTelemetryRuntime.TryGetEngine(1000, out _, out _, out _));
        Assert.True(GameTelemetryRuntime.TryGetMotion(1000, out float speed, out bool hasSpeed, out float grip));
        Assert.True(hasSpeed);
        Assert.Equal(42f, speed);
        Assert.Equal(0.7f, grip);
        // Stale after a second.
        Assert.False(GameTelemetryRuntime.TryGetMotion(1000 + GameTelemetryRuntime.EngineStaleMs + 1, out _, out _, out _));

        // The lights' source carries both when it answers.
        frame.MaxRpm = 8000f;
        GameTelemetryRuntime.Publish(frame, Array.Empty<TelemetryVariable>(), true, Environment.TickCount64);
        var source = new GameTelemetryRowSource(learnedMax: false);
        Assert.True(source.TryGetSnapshot(out var snap));
        Assert.True(snap.HasSpeed);
        Assert.Equal(42f, snap.Speed);
        Assert.Equal(0.7f, snap.Grip);

        // A game in its menus gives neither.
        frame.DrivingReported = true;
        frame.Driving = false;
        GameTelemetryRuntime.Publish(frame, Array.Empty<TelemetryVariable>(), true, 5000);
        Assert.False(GameTelemetryRuntime.TryGetMotion(5000, out _, out _, out _));
        GameTelemetryRuntime.ClearValues();
        Assert.False(GameTelemetryRuntime.TryGetMotion(5000, out _, out _, out _));
    }

    /// <summary>A custom format with no Speed field sends no speed. Its
    /// frames' speed rests at 0, which is no reading, so the row gives a
    /// wheel's centering no speed and the wheel keeps its fixed one.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ACustomFormatWithNoSpeedField_GivesNoSpeed(bool withSpeed)
    {
        var format = new CustomTelemetryFormat
        {
            Name = "Rpm only",
            Kind = CustomTelemetryKind.Json,
            Key = "rpm",
            Fields =
            {
                new CustomTelemetryField { Target = CustomTelemetryTarget.Rpm, Key = "rpm" },
                new CustomTelemetryField { Target = CustomTelemetryTarget.MaxRpm, Key = "max" },
            },
        };
        if (withSpeed)
            format.Fields.Add(new CustomTelemetryField { Target = CustomTelemetryTarget.Speed, Key = "kmh" });
        var compiled = new CompiledTelemetryFormat(format, GameTelemetryRuntime.BuildVariables(new[] { format }));
        var frame = new TelemetryFrame();
        frame.Reset(0);
        Assert.True(compiled.TryRead(System.Text.Encoding.UTF8.GetBytes("{\"rpm\":3000,\"max\":8000,\"kmh\":72}"), frame, pinned: true));
        long now = Environment.TickCount64;
        GameTelemetryRuntime.Publish(frame, Array.Empty<TelemetryVariable>(), true, now);
        // The hub's fallback, for a row with no max RPM yet, reads this.
        Assert.True(GameTelemetryRuntime.TryGetMotion(now, out _, out bool hasSpeed, out _));
        Assert.Equal(withSpeed, hasSpeed);

        var source = new GameTelemetryRowSource(learnedMax: false);
        Assert.True(source.TryGetSnapshot(out var snap));
        Assert.Equal(withSpeed, snap.HasSpeed);
        if (withSpeed) Assert.Equal(20f, snap.Speed, 3);
    }
}
