using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using PadForge.Common.Input.Touchpads;
using PadForge.Engine;
using PadForge.Engine.Common;
using PadForge.Engine.Data;
using PadForge.Engine.Tablets;
using PadForge.Engine.Touchpads;
using PadForge.Resources.Strings;
using PadForge.Services;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>Use Only in PadForge for a precision touchpad: the touchpad
    /// row's switch, which HidHide carries out while PadForge reads the
    /// touchpad's collection itself, and the pointer its mouse row reads
    /// meanwhile.</summary>
    public class TakenTouchpadPointerTests
    {
        /// <summary>The Goodix touchpad of the owner's laptop, measured: X 0
        /// to 3751 over 4.69 inches, Y 0 to 2327 over 2.91 inches, unit 0x13
        /// (inches), unit exponent 0xE. Linux reads that as 31 units per
        /// millimeter (hid-input.c hidinput_calc_abs_res).</summary>
        [Fact]
        public void TheLaptopsTouchpad_Measures31UnitsPerMillimeter()
        {
            Assert.Equal(31, TouchpadPointer.UnitsPerMm(0, 3751, 0, 469, 0x13, 0xE));
            Assert.Equal(31, TouchpadPointer.UnitsPerMm(0, 2327, 0, 291, 0x13, 0xE));
        }

        [Fact]
        public void Centimeters_RaiseTheExponent()
        {
            // 1000 units over 10 cm.
            Assert.Equal(10, TouchpadPointer.UnitsPerMm(0, 1000, 0, 10, 0x11, 0));
            // 4000 units over 100 mm, written as 100 x 10^-1 cm.
            Assert.Equal(40, TouchpadPointer.UnitsPerMm(0, 4000, 0, 100, 0x11, 0xF));
        }

        [Fact]
        public void ADescriptorWithNoSizeOrAnotherUnit_GivesNoResolution()
        {
            Assert.Equal(0, TouchpadPointer.UnitsPerMm(0, 3751, 0, 0, 0x13, 0xE));
            Assert.Equal(0, TouchpadPointer.UnitsPerMm(0, 3751, 0, 469, 0x14, 0xE));
            Assert.Equal(0, TouchpadPointer.UnitsPerMm(0, 0, 0, 469, 0x13, 0xE));
        }

        [Fact]
        public void TheUnitExponent_ReadsAsLinuxReadsIt()
        {
            Assert.Equal(-2, TouchpadPointer.Exponent(0xE));
            Assert.Equal(7, TouchpadPointer.Exponent(0x7));
            Assert.Equal(-1, TouchpadPointer.Exponent(0xF));
            Assert.Equal(-2, TouchpadPointer.Exponent(0xFE));
            Assert.Equal(-2, TouchpadPointer.Exponent(0xFFFFFFFE));
        }

        /// <summary>libinput's base speed: a 1000-count-per-inch mouse
        /// (libinput-util.h:60, evdev-mt-touchpad.c:2987-2988), and its
        /// assumed 69 mm width for a touchpad that gives no size
        /// (evdev-mt-touchpad.c:3591-3609).</summary>
        [Fact]
        public void TheMotion_ScalesToA1000CountPerInchMouse()
        {
            Assert.Equal(1000.0 / 25.4 / 31, TouchpadPointer.CountsPerUnit(31, 0, 3751, TouchpadPointer.AssumedWidthMm), 9);
            Assert.Equal(1000.0 / 25.4 / (3752 / 69), TouchpadPointer.CountsPerUnit(0, 0, 3751, TouchpadPointer.AssumedWidthMm), 9);
            Assert.Equal(1000.0 / 25.4, TouchpadPointer.CountsPerUnit(0, 0, 9, TouchpadPointer.AssumedWidthMm), 9);
        }

        /// <summary>One finger moves the pointer, a finger that just landed
        /// has not moved, two fingers move nothing unless the pad is pressed,
        /// and then every moving finger counts (evdev-mt-touchpad-gestures.c:179-190).</summary>
        [Fact]
        public void OneFinger_MovesThePointer_TwoOnlyWhilePressed()
        {
            Assert.Equal((3.0, -2.0), TouchpadPointer.FrameMotion(new[] { (3.0, -2.0, true) }, pressed: false));
            Assert.Equal((0.0, 0.0), TouchpadPointer.FrameMotion(new[] { (3.0, -2.0, false) }, pressed: false));
            Assert.Equal((0.0, 0.0), TouchpadPointer.FrameMotion(new[] { (3.0, -2.0, true), (1.0, 1.0, true) }, pressed: false));
            Assert.Equal((4.0, -1.0), TouchpadPointer.FrameMotion(new[] { (3.0, -2.0, true), (1.0, 1.0, true) }, pressed: true));
            Assert.Equal((3.0, -2.0), TouchpadPointer.FrameMotion(new[] { (3.0, -2.0, true), (5.0, 5.0, false) }, pressed: true));
            Assert.Equal((0.0, 0.0), TouchpadPointer.FrameMotion(ReadOnlySpan<(double, double, bool)>.Empty, pressed: true));
        }

        /// <summary>Buttons 2 and 3 are a pad's external primary and secondary
        /// buttons (Microsoft, Windows Precision Touchpad Collection, input
        /// report usages). Button 1, a click-pad's own, clicks by where and
        /// with how many fingers it is pressed (TouchpadMouseTests).</summary>
        [Fact]
        public void TheTouchpadsExternalButtons_ClickLeftAndRight()
        {
            Assert.Equal(0, TouchpadPointer.MouseButtons(0));
            Assert.Equal(0, TouchpadPointer.MouseButtons(TouchpadPointer.IntegratedButton));
            Assert.Equal(1, TouchpadPointer.MouseButtons(TouchpadPointer.PrimaryButton));
            Assert.Equal(2, TouchpadPointer.MouseButtons(TouchpadPointer.SecondaryButton));
            Assert.Equal(2, TouchpadPointer.MouseButtons(TouchpadPointer.IntegratedButton | TouchpadPointer.SecondaryButton));
        }

        private static void Frame(PrecisionTouchpadReader.PtpDeviceState ds, params (float X, float Y, int Id)[] touches)
        {
            ds.FrameSeen = touches.Length;
            ds.FrameExpected = touches.Length;
            for (int i = 0; i < touches.Length; i++)
            {
                ds.FrameBufX[i] = touches[i].X;
                ds.FrameBufY[i] = touches[i].Y;
                ds.FrameBufId[i] = touches[i].Id;
            }
            PrecisionTouchpadReader.CommitFrame(ds);
        }

        /// <summary>A captured touchpad's frames leave its mouse row the
        /// motion of its finger, in mouse counts. A finger that lands moves
        /// nothing, a second finger stops the motion, and a press makes both
        /// move it. A touchpad Windows reads leaves nothing.</summary>
        [Fact]
        public void ACapturedTouchpadsFrames_MoveItsMouseRow()
        {
            var ds = new PrecisionTouchpadReader.PtpDeviceState
            {
                FeedsPointer = true,
                CountsPerUnitX = 2,
                CountsPerUnitY = 3,
                ExtentX = 1000,
                ExtentY = 500,
            };
            Frame(ds, (0.5f, 0.5f, 7));
            Assert.Equal(0, ds.PointerX, 3);
            Assert.True(ds.Down[0]);

            Frame(ds, (0.6f, 0.4f, 7));
            Assert.Equal(200, ds.PointerX, 2);
            Assert.Equal(-150, ds.PointerY, 2);

            Frame(ds, (0.7f, 0.4f, 7), (0.2f, 0.2f, 8));
            Assert.Equal(200, ds.PointerX, 2);

            ds.PointerButtons = TouchpadPointer.IntegratedButton;
            Frame(ds, (0.8f, 0.4f, 7), (0.3f, 0.2f, 8));
            Assert.Equal(600, ds.PointerX, 2);
            Assert.Equal(-150, ds.PointerY, 2);

            var shared = new PrecisionTouchpadReader.PtpDeviceState { CountsPerUnitX = 2, ExtentX = 1000 };
            Frame(shared, (0.5f, 0.5f, 1));
            Frame(shared, (0.9f, 0.5f, 1));
            Assert.Equal(0, shared.PointerX, 3);
            Assert.Equal(0.9f, shared.X[0]);
        }
    }

    /// <summary>The clicks and the wheel of a touchpad PadForge reads alone,
    /// on the owner's laptop touchpad's size (119 by 74 mm).</summary>
    public class TouchpadMouseTests
    {
        private const double Width = 119, Height = 74;

        private static TouchpadMouse Mouse(TouchpadSettings settings = null)
            => new(settings ?? TouchpadSettings.Default, Width, Height);

        private static void Touch(TouchpadMouse m, long ms, bool pressed = false, params (int Id, double X, double Y)[] contacts)
            => m.Frame(contacts, pressed, ms);

        /// <summary>A one-finger tap clicks left at the lift and holds it as
        /// long as Windows held it on the owner's touchpad with tap-and-drag
        /// on. A touch that lasted past libinput's tap timeout, or moved past
        /// its threshold, clicks nothing.</summary>
        [Fact]
        public void AOneFingerTap_ClicksLeft()
        {
            var m = Mouse();
            Touch(m, 0, false, (1, 50, 30));
            Touch(m, 60, false, (1, 50.5, 30));
            Assert.Equal(0, m.Buttons(60));
            Touch(m, 100);
            Assert.Equal(TouchpadMouse.Left, m.Buttons(100));
            Assert.Equal(TouchpadMouse.Left, m.Buttons(100 + TouchpadMouse.TapDragHoldMs - 1));
            Assert.Equal(0, m.Buttons(100 + TouchpadMouse.TapDragHoldMs));

            var slow = Mouse();
            Touch(slow, 0, false, (1, 50, 30));
            Touch(slow, TouchpadMouse.TapTimeoutMs + 1);
            Assert.Equal(0, slow.Buttons(TouchpadMouse.TapTimeoutMs + 1));

            var moved = Mouse();
            Touch(moved, 0, false, (1, 50, 30));
            Touch(moved, 40, false, (1, 51.5, 30));
            Touch(moved, 80);
            Assert.Equal(0, moved.Buttons(80));
        }

        /// <summary>A two-finger tap clicks right, held as Windows held it.</summary>
        [Fact]
        public void ATwoFingerTap_ClicksRight()
        {
            var m = Mouse();
            Touch(m, 0, false, (1, 40, 30), (2, 60, 30));
            Touch(m, 100);
            Assert.Equal(TouchpadMouse.Right, m.Buttons(100));
            Assert.Equal(0, m.Buttons(100 + TouchpadMouse.ClickHoldMs));
        }

        /// <summary>The Windows settings turn each behavior off.</summary>
        [Fact]
        public void TheWindowsSettings_TurnTheirBehaviorsOff()
        {
            var noTaps = Mouse(TouchpadSettings.Default with { Taps = false, TwoFingerTap = false });
            Touch(noTaps, 0, false, (1, 50, 30));
            Touch(noTaps, 50);
            Touch(noTaps, 400, false, (1, 40, 30), (2, 60, 30));
            Touch(noTaps, 450);
            Assert.Equal(0, noTaps.Buttons(450));

            var noZone = Mouse(TouchpadSettings.Default with { RightClickZone = false });
            Touch(noZone, 0, true, (1, 100, 70));
            Assert.Equal(TouchpadMouse.Left, noZone.Buttons(0));

            var noScroll = Mouse(TouchpadSettings.Default with { Scroll = false });
            Touch(noScroll, 0, false, (1, 40, 30), (2, 60, 30));
            Touch(noScroll, 10, false, (1, 40, 40), (2, 60, 40));
            Assert.Equal(0, noScroll.TakeWheel());

            // Without tap-and-drag a tap's click is a short one.
            var noDrag = Mouse(TouchpadSettings.Default with { TapAndDrag = false });
            Touch(noDrag, 0, false, (1, 50, 30));
            Touch(noDrag, 50);
            Assert.Equal(TouchpadMouse.Left, noDrag.Buttons(50));
            Assert.Equal(0, noDrag.Buttons(50 + TouchpadMouse.ClickHoldMs));
        }

        /// <summary>A press clicks right in the bottom right area (libinput's
        /// software button: the bottom 10 mm of a 74 mm pad, right of the
        /// middle) or with two fingers together, middle with three, and left
        /// otherwise. The button holds until the press lets go.</summary>
        [Fact]
        public void APress_ClicksByWhereAndWithHowManyFingers()
        {
            var m = Mouse();
            Assert.True(m.InRightButtonArea(100, 70));
            Assert.False(m.InRightButtonArea(100, 60));
            Assert.False(m.InRightButtonArea(40, 70));

            Touch(m, 0, true, (1, 100, 70));
            Assert.Equal(TouchpadMouse.Right, m.Buttons(0));
            Touch(m, 50, false, (1, 100, 70));
            Assert.Equal(0, m.Buttons(50));
            Touch(m, 300);

            Touch(m, 1000, true, (1, 30, 30));
            Assert.Equal(TouchpadMouse.Left, m.Buttons(1000));
            Touch(m, 1100);
            Assert.Equal(0, m.Buttons(1100));

            Touch(m, 2000, true, (1, 40, 30), (2, 60, 32));
            Assert.Equal(TouchpadMouse.Right, m.Buttons(2000));
            Touch(m, 2100);

            Touch(m, 3000, true, (1, 30, 30), (2, 45, 30), (3, 60, 30));
            Assert.Equal(TouchpadMouse.Middle, m.Buttons(3000));
            Touch(m, 3100);

            // Two fingers 50 mm apart are a finger and a resting one.
            Touch(m, 4000, true, (1, 20, 30), (2, 70, 30));
            Assert.Equal(TouchpadMouse.Left, m.Buttons(4000));
            Touch(m, 4100);

            // A thumb resting in the bottom 20 mm with a finger above it.
            Assert.False(m.Together((1, 40, 30), (2, 50, 58)));
            Assert.True(m.Together((1, 40, 50), (2, 50, 58)));
        }

        /// <summary>Two fingers moving scroll by their average motion, 120
        /// wheel units to 10 mm. Down is wheel up under Windows' standard
        /// direction and wheel down under reverse.</summary>
        [Fact]
        public void TwoFingers_Scroll()
        {
            var m = Mouse();
            Touch(m, 0, false, (1, 40, 30), (2, 60, 30));
            Touch(m, 10, false, (1, 40, 36), (2, 60, 34));
            Assert.Equal(60, m.TakeWheel());
            Touch(m, 20, false, (1, 40, 41), (2, 60, 39));
            Assert.Equal(60, m.TakeWheel());
            Assert.Equal(0, m.TakeWheel());
            Touch(m, 30);
            Assert.Equal(0, m.Buttons(30));

            var reverse = Mouse(TouchpadSettings.Default with { ScrollReversed = true });
            Touch(reverse, 0, false, (1, 40, 30), (2, 60, 30));
            Touch(reverse, 10, false, (1, 40, 40), (2, 60, 40));
            Assert.Equal(-120, reverse.TakeWheel());

            // One finger moves the pointer, never the wheel.
            var one = Mouse();
            Touch(one, 0, false, (1, 40, 30));
            Touch(one, 10, false, (1, 40, 40));
            Assert.Equal(0, one.TakeWheel());
        }

        /// <summary>A touch that begins while a tap's left click is held drags
        /// with the button down until it lifts. A second tap there is a
        /// double click.</summary>
        [Fact]
        public void TapAndDrag_HoldsTheButtonThroughTheDrag()
        {
            var m = Mouse();
            Touch(m, 0, false, (1, 50, 30));
            Touch(m, 80);
            Touch(m, 150, false, (2, 50, 30));
            Touch(m, 400, false, (2, 70, 40));
            Assert.Equal(TouchpadMouse.Left, m.Buttons(400));
            Touch(m, 500);
            Assert.Equal(0, m.Buttons(500));

            var twice = Mouse();
            Touch(twice, 0, false, (1, 50, 30));
            Touch(twice, 80);
            Touch(twice, 150, false, (2, 50, 30));
            Touch(twice, 200);
            Assert.Equal(TouchpadMouse.Left, twice.Buttons(200));
            Assert.Equal(0, twice.Buttons(200 + TouchpadMouse.ClickHoldMs));
        }

        /// <summary>A touch the touchpad stopped reporting ends at its last
        /// report, so a quick one still taps.</summary>
        [Fact]
        public void ATouchThatGoesSilent_EndsAtItsLastReport()
        {
            var m = Mouse();
            Touch(m, 0, false, (1, 50, 30));
            m.Silence(90);
            Assert.Equal(TouchpadMouse.Left, m.Buttons(90));
        }
    }

    public class TouchpadCaptureTests
    {
        private sealed class Input : Stream
        {
            internal readonly Channel<byte[]> Reports = Channel.CreateUnbounded<byte[]>();
            internal readonly TaskCompletionSource Reading = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal int DisposeCount;
            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                Reading.TrySetResult();
                var report = await Reports.Reader.ReadAsync(cancellationToken);
                report.CopyTo(buffer);
                return report.Length;
            }
            protected override void Dispose(bool disposing)
            {
                Interlocked.Increment(ref DisposeCount);
                Reports.Writer.TryComplete();
                base.Dispose(disposing);
            }
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }

        private sealed class Layout : ITouchpadLayout
        {
            internal int Disposed;
            public int ReportLength => 8;
            public void Dispose() => Interlocked.Increment(ref Disposed);
        }

        /// <summary>A reader that files nothing: it counts what the capture
        /// asks of it, and takes a report whose first byte is 4 as the
        /// touchpad's.</summary>
        private sealed class Host : ITouchpadCaptureHost
        {
            internal readonly Layout Shape = new();
            internal int Published, Withdrawn, Fed;
            internal Exception DescribeFails;
            public ITouchpadLayout Describe(TouchpadCapture capture)
            {
                if (DescribeFails != null) throw DescribeFails;
                return Shape;
            }
            public IntPtr Publish(TouchpadCapture capture, ITouchpadLayout layout)
            {
                Interlocked.Increment(ref Published);
                return new IntPtr(-0x10001);
            }
            public bool Feed(IntPtr key, ITouchpadLayout layout, IntPtr report, int length)
            {
                Interlocked.Increment(ref Fed);
                return System.Runtime.InteropServices.Marshal.ReadByte(report) == 4;
            }
            public void Withdraw(IntPtr key) => Interlocked.Increment(ref Withdrawn);
        }

        private static async Task Until(Func<bool> condition)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!condition()) await Task.Delay(5, timeout.Token);
        }

        private static string Instance() => @"HID\TEST&Col02\" + Guid.NewGuid().ToString("N");

        /// <summary>The tablet's capture for a touchpad: the touchpad is
        /// filed for reading once PadForge holds it, its report marks it
        /// captured, the unhide closes PadForge's handle once, and the
        /// give-back restarts the collection once, with that handle already
        /// gone, then takes the touchpad's state off the list.</summary>
        [Fact]
        public async Task ACapture_ReadsTheTouchpad_AndGivesItBackWithOneRestart()
        {
            var host = new Host();
            var input = new Input();
            int restarts = 0;
            using var capture = new TouchpadCapture(host, @"\\?\HID#TEST&Col02#1#{4d1e55b2-f16f-11cf-88cb-001111000030}",
                Instance(), "Touchpad", 1, 2, null, _ => input, _ =>
                {
                    Assert.Equal(1, input.DisposeCount);
                    Interlocked.Increment(ref restarts);
                });
            capture.SetCapture(true);
            await input.Reading.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(TabletCaptureState.WaitingForInput, capture.CaptureState);
            Assert.True(capture.IsReading);
            Assert.Equal(1, host.Published);

            input.Reports.Writer.TryWrite(new byte[] { 9, 0 });
            await Until(() => Volatile.Read(ref host.Fed) == 1);
            Assert.Equal(TabletCaptureState.WaitingForInput, capture.CaptureState);
            input.Reports.Writer.TryWrite(new byte[] { 4, 0 });
            await Until(() => capture.CaptureState == TabletCaptureState.Captured);

            capture.PrepareForUnhide();
            Assert.Equal(TabletCaptureState.Switching, capture.CaptureState);
            Assert.Equal(1, input.DisposeCount);
            Assert.False(capture.IsReading);
            capture.SetCapture(false);
            await Until(() => capture.CaptureState == TabletCaptureState.Shared);
            Assert.True(await Task.Run(() => capture.WaitForTransition(TimeSpan.FromSeconds(5))));
            Assert.Equal(1, restarts);
            Assert.Equal(1, host.Withdrawn);
            Assert.Equal(1, host.Shape.Disposed);
        }

        /// <summary>While Windows' own reader holds the collection, PadForge's
        /// unshared open fails. The capture restarts the collection, which
        /// closes Windows' handle under HidHide's hide, and opens it.</summary>
        [Fact]
        public async Task AnOpenWindowsStillHolds_RestartsTheCollection_ThenOpens()
        {
            var host = new Host();
            var input = new Input();
            int opens = 0, restarts = 0;
            using var capture = new TouchpadCapture(host, "path", Instance(), "Touchpad", 1, 2, null,
                _ => Interlocked.Increment(ref opens) == 1 ? throw new IOException("in use") : input,
                _ => Interlocked.Increment(ref restarts),
                () => true);
            capture.SetCapture(true);
            await input.Reading.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, restarts);
            Assert.Equal(2, opens);
            Assert.True(capture.IsReading);
        }

        /// <summary>A capture whose hide was just written and that fails
        /// before any report asks for that hide back, so the touchpad is
        /// never left hidden with nothing reading it.</summary>
        [Fact]
        public async Task ACaptureThatFailsBeforeAReport_AsksForItsHideBack()
        {
            var host = new Host { DescribeFails = new IOException("gone") };
            var events = new ConcurrentQueue<(TabletCaptureState State, bool Rollback)>();
            using var capture = new TouchpadCapture(host, "path", Instance(), "Touchpad", 1, 2,
                (c, _, rollback, _) => events.Enqueue((c.CaptureState, rollback)),
                _ => throw new InvalidOperationException("no open expected"), _ => { }, () => true);
            capture.SetCapture(true, rollbackNewHide: true);
            await Until(() => capture.CaptureState == TabletCaptureState.Failed);
            Assert.Contains(events, e => e.State == TabletCaptureState.Failed && e.Rollback);
            Assert.Equal(0, host.Published);
            Assert.False(capture.IsReading);
        }

        /// <summary>A give-back with no capture to make it restarts the
        /// collection for Windows. The test host restarts nothing, which the
        /// helper logs and survives.</summary>
        [Fact]
        public async Task ARestartForWindows_OnTheTestHost_RestartsNothingAndReturns()
        {
            await TouchpadCapture.RestartForWindows(Instance()).WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    public class TouchpadSwitchTests
    {
        private const string Parent = @"ACPI\GXTP5100\3&62d7e73&0";
        private const string MouseId = @"HID\GXTP5100&Col01\4&c834cd3&0&0000";
        private const string TouchpadId = @"HID\GXTP5100&Col02\4&c834cd3&0&0001";
        private const string ConfigId = @"HID\GXTP5100&Col03\4&c834cd3&0&0002";
        private const string MousePath = @"\\?\HID#GXTP5100&Col01#4&c834cd3&0&0000#{378de44c-56ef-11d1-bc8c-00a0c91405dd}";
        private const string TouchpadPath = @"\\?\HID#GXTP5100&Col02#4&c834cd3&0&0001#{4d1e55b2-f16f-11cf-88cb-001111000030}";

        /// <summary>The laptop's touchpad device as Windows lists it,
        /// measured: the mouse, touchpad and configuration collections under
        /// one ACPI node, each named by its usage in its hardware IDs.</summary>
        private static (string Parent, IReadOnlyList<(string Id, IReadOnlyList<string> HardwareIds)> Children)? Laptop(string id)
        {
            if (id != MouseId && id != TouchpadId && id != ConfigId) return null;
            return (Parent, new List<(string, IReadOnlyList<string>)>
            {
                (MouseId, new[] { @"HID\VID_27C6&UP:0001_U:0002", "HID_DEVICE_SYSTEM_MOUSE", TouchpadNodes.MouseHardwareId, "HID_DEVICE" }),
                (TouchpadId, new[] { @"HID\VID_27C6&UP:000D_U:0005", TouchpadNodes.TouchpadHardwareId, "HID_DEVICE" }),
                (ConfigId, new[] { @"HID\VID_27C6&UP:000D_U:000E", "HID_DEVICE_UP:000D_U:000E", "HID_DEVICE" }),
            });
        }

        [Fact]
        public void ATouchpadsMouseCollection_LeadsToItsTouchpadCollection()
        {
            Assert.Equal(TouchpadId, TouchpadNodes.TouchpadOf(TouchpadId, Laptop));
            Assert.Equal(TouchpadId, TouchpadNodes.TouchpadOf(MouseId, Laptop));
            Assert.Null(TouchpadNodes.TouchpadOf(ConfigId, Laptop));
            Assert.Null(TouchpadNodes.TouchpadOf(@"HID\VID_046D&PID_C54D&MI_00\8&1&0&0000", Laptop));
            Assert.Equal(new[] { MouseId }, TouchpadNodes.MiceBeside(TouchpadId, Laptop));

            // A plain mouse has no touchpad beside it, and a device with two
            // touchpad collections names none for its mouse.
            var plain = new List<(string, IReadOnlyList<string>)> { ("HID\\M", new[] { TouchpadNodes.MouseHardwareId }) };
            Assert.Null(TouchpadNodes.TouchpadOf("HID\\M", _ => ("USB\\P", plain)));
            var twin = new List<(string, IReadOnlyList<string>)>
            {
                ("HID\\M", new[] { TouchpadNodes.MouseHardwareId }),
                ("HID\\T1", new[] { TouchpadNodes.TouchpadHardwareId }),
                ("HID\\T2", new[] { TouchpadNodes.TouchpadHardwareId }),
            };
            Assert.Null(TouchpadNodes.TouchpadOf("HID\\M", _ => ("USB\\P", twin)));
        }

        /// <summary>The touchpad's own row and its mouse row both offer Use
        /// Only in PadForge in the Input Hiding section, acting on the
        /// touchpad row's switch, never a WinUSB bind of the whole device.
        /// Hide from Games leaves both rows, and the give-back changes no
        /// driver.</summary>
        [Fact]
        public void TheTouchpadsRows_OfferUseOnlyInPadForge()
        {
            var vm = new DevicesViewModel
            {
                TouchpadOfCollection = id => TouchpadNodes.TouchpadOf(id, Laptop),
            };
            var touchpad = new DeviceRowViewModel { DeviceTypeKey = "Touchpad", DevicePath = TouchpadPath, InstanceGuid = Guid.NewGuid(), DeviceName = "HIDI2C Device" };
            var mouse = new DeviceRowViewModel { DeviceTypeKey = "Mouse", DevicePath = MousePath, InstanceGuid = Guid.NewGuid(), DeviceName = "HIDI2C Device" };
            vm.Devices.Add(touchpad);
            vm.Devices.Add(mouse);

            Assert.True(touchpad.IsTouchpadRow);
            Assert.False(touchpad.ShowHidHideToggle);
            Assert.True(mouse.ShowHidHideToggle);

            vm.SelectedDevice = mouse;
            Assert.True(mouse.IsTouchpadMouse);
            Assert.False(mouse.ShowHidHideToggle);
            Assert.True(mouse.ShowConsumeToggle);
            Assert.Equal(touchpad.InstanceGuid, vm.TouchpadSwitch.TouchpadRow);
            Assert.Null(vm.DriverOffer);
            Assert.True(vm.ShowHidingDriverBind);
            Assert.False(vm.ShowHidingDriverRestore);
            Assert.False(vm.ShowDriverBind);
            Assert.Equal(Strings.Instance.Devices_DriverBind_OnlyInPadForge, vm.DriverBindText);
            Assert.True(mouse.ShowsHidingDriverSwitch && mouse.ShowInputHidingSection);

            touchpad.TouchpadOnlyInPadForge = true;
            vm.RefreshDriverOffer();
            Assert.True(vm.ShowHidingDriverRestore);
            Assert.False(vm.ShowHidingDriverBind);
            Assert.Equal(Strings.Instance.Devices_TouchpadGiveBack, vm.DriverRestoreText);

            vm.DriverBusy = true;
            Assert.False(vm.ShowHidingDriverRestore || vm.ShowHidingDriverBind);
            vm.DriverBusy = false;

            vm.SelectedDevice = touchpad;
            Assert.False(mouse.IsTouchpadMouse);
            Assert.True(mouse.ShowHidHideToggle);
            Assert.Equal(touchpad.InstanceGuid, vm.TouchpadSwitch.TouchpadRow);
            Assert.True(vm.ShowHidingDriverRestore);
            Assert.True(touchpad.ShowTouchpadCaptureStatus);
            Assert.True(touchpad.ShowInputHidingSection);

            // A mouse with no touchpad beside it keeps its own switch.
            var plain = new DeviceRowViewModel { DeviceTypeKey = "Mouse", DevicePath = @"\\?\HID#VID_046D&PID_C54D&MI_00#8&1&0&0000#{378de44c-56ef-11d1-bc8c-00a0c91405dd}" };
            vm.Devices.Add(plain);
            vm.SelectedDevice = plain;
            Assert.Null(vm.TouchpadSwitch);
            Assert.False(plain.IsTouchpadMouse);
            Assert.Equal(Strings.Instance.Devices_DriverRestore, vm.DriverRestoreText);

            // The give-back button reads the page's text, and the capture's
            // line stands with it.
            string page = AuditDelta20261002Tests.RepoText("PadForge.App", "Views", "DevicesPage.xaml");
            Assert.Contains("<Button Content=\"{Binding DriverRestoreText}\"", page);
            Assert.Contains("{Binding SelectedDevice.ShowTouchpadCaptureStatus,", page);
        }

        /// <summary>Before PadForge has read the touchpad, its own row does not
        /// exist yet. Its mouse row is still the touchpad's: no WinUSB bind
        /// and no Hide from Games, and no switch until the touchpad's row
        /// is there.</summary>
        [Fact]
        public void ATouchpadsMouseRow_WithNoTouchpadRowYet_OffersNothingElse()
        {
            var vm = new DevicesViewModel { TouchpadOfCollection = id => TouchpadNodes.TouchpadOf(id, Laptop) };
            var mouse = new DeviceRowViewModel { DeviceTypeKey = "Mouse", DevicePath = MousePath, InstanceGuid = Guid.NewGuid() };
            vm.Devices.Add(mouse);
            vm.SelectedDevice = mouse;
            Assert.True(mouse.IsTouchpadMouse);
            Assert.False(mouse.ShowHidHideToggle);
            Assert.Null(vm.TouchpadSwitch);
            Assert.Null(vm.DriverOffer);
            Assert.False(vm.ShowHidingDriverBind || vm.ShowHidingDriverRestore || vm.ShowDriverBind || vm.ShowDriverRestore);

            // Only a HID collection is looked up: an ID that merely starts
            // with the letters is not one.
            var asked = new List<string>();
            vm.TouchpadOfCollection = id => { asked.Add(id); return null; };
            var other = new DeviceRowViewModel { DeviceTypeKey = "Mouse", DevicePath = @"\\?\HIDX#1#2#{378de44c-56ef-11d1-bc8c-00a0c91405dd}" };
            vm.Devices.Add(other);
            vm.SelectedDevice = other;
            Assert.Empty(asked);
            Assert.False(other.IsTouchpadMouse);
        }

        private static UserDevice Row(int type, string path, bool hide = false, bool only = false) => new()
        {
            InstanceGuid = Guid.NewGuid(),
            CapType = type,
            DevicePath = path,
            HidHideEnabled = hide,
            TouchpadOnlyInPadForge = only,
            IsOnline = true,
        };

        /// <summary>Hide from Games never reaches HidHide from a touchpad's
        /// row: hidden with nothing reading it, the touchpad was dead in
        /// Windows and in PadForge. Its switch does that, through its own
        /// capture.</summary>
        [Fact]
        public void HideFromGames_NeverHidesATouchpad()
        {
            Assert.False(InputService.AppliesHidHideFlag(Row(InputDeviceType.Touchpad, TouchpadPath, hide: true)));
            Assert.True(InputService.AppliesHidHideFlag(Row(InputDeviceType.Gamepad, @"\\?\HID#VID_054C&PID_0CE6#1#{4d1e55b2-f16f-11cf-88cb-001111000030}", hide: true)));
            Assert.False(InputService.AppliesHidHideFlag(Row(InputDeviceType.Gamepad, @"\\?\HID#VID_054C&PID_0CE6#1#{4d1e55b2-f16f-11cf-88cb-001111000030}")));

            Assert.True(InputService.IsTouchpadCollectionRow(Row(InputDeviceType.Touchpad, TouchpadPath)));
            Assert.False(InputService.IsTouchpadCollectionRow(Row(InputDeviceType.Touchpad, "aggregate://touchpads")));
            Assert.False(InputService.IsTouchpadCollectionRow(Row(InputDeviceType.Mouse, MousePath)));
        }

        /// <summary>A touchpad PadForge reads alone is hidden, not a row left
        /// visible, so its own node never keeps its own hide out (#400's
        /// keep-out). With the switch off, the row keeps every other row's
        /// hide off the touchpad, as before.</summary>
        [Fact]
        public void ATakenTouchpad_DoesNotKeepItsOwnHideOut()
        {
            IReadOnlyList<string> Chain(string id) => new[] { id };
            var taken = InputService.BuildHidHideKeepOut(new[] { Row(InputDeviceType.Touchpad, TouchpadPath, only: true) }, Chain);
            Assert.DoesNotContain(TouchpadId, taken);
            var shared = InputService.BuildHidHideKeepOut(new[] { Row(InputDeviceType.Touchpad, TouchpadPath) }, Chain);
            Assert.Contains(TouchpadId, shared);
        }

        /// <summary>The switch's flag round-trips through the settings file,
        /// and a file from before it reads as off.</summary>
        [Fact]
        public void TheSwitch_RoundTripsThroughTheSettingsFile()
        {
            var serializer = new System.Xml.Serialization.XmlSerializer(typeof(UserDevice));
            var writer = new StringWriter();
            serializer.Serialize(writer, new UserDevice { InstanceGuid = Guid.NewGuid(), TouchpadOnlyInPadForge = true });
            Assert.Contains("<TouchpadOnlyInPadForge>true</TouchpadOnlyInPadForge>", writer.ToString());
            var back = (UserDevice)serializer.Deserialize(new StringReader(writer.ToString()));
            Assert.True(back.TouchpadOnlyInPadForge);
            var old = (UserDevice)serializer.Deserialize(new StringReader(writer.ToString()
                .Replace("<TouchpadOnlyInPadForge>true</TouchpadOnlyInPadForge>", "")));
            Assert.False(old.TouchpadOnlyInPadForge);
        }

        /// <summary>The confirm states what the switch does, Hide from Games'
        /// warning about the only pointer and the undo that follows, and the
        /// Keep prompt is worded for a touchpad.</summary>
        [Fact]
        public void TheConfirmAndTheKeepPrompt_AreTheTouchpads()
        {
            string cost = PadForge.MainWindow.TouchpadCost();
            Assert.StartsWith(Strings.Instance.Devices_TouchpadConfirm, cost);
            Assert.Contains(string.Format(Strings.Instance.Devices_WarnHide_Format, Strings.Instance.Devices_DriverKind_MouseOrTouchpad), cost);
            Assert.EndsWith(string.Format(Strings.Instance.Devices_TouchpadKeepNote_Format, PadForge.MainWindow.KeepDriverSeconds), cost);

            string dialog = AuditDelta20261002Tests.RepoText("PadForge.App", "Views", "KeepDriverDialog.xaml.cs");
            Assert.Contains("Title = touchpad ? Strings.Instance.Devices_TouchpadKeepTitle", dialog);
            Assert.Contains("? Strings.Instance.Devices_TouchpadKeepMessage_Format", dialog);

            // The switch shows the Keep prompt for a touchpad, gives the
            // touchpad back when nobody keeps it, and gives back at once a
            // take PadForge could not read.
            string window = AuditDelta20261002Tests.RepoText("PadForge.App", "MainWindow.xaml.cs");
            int start = window.IndexOf("private async System.Threading.Tasks.Task SwitchTouchpadAsync(bool take)", StringComparison.Ordinal);
            int end = window.IndexOf("private async System.Threading.Tasks.Task<bool> SetTouchpadOnlyInPadForgeAsync", start, StringComparison.Ordinal);
            Assert.True(start > 0 && end > start, $"{start} {end}");
            string flow = window.Substring(start, end - start);
            Assert.Contains("Views.KeepDriverDialog.Show(this, name, KeepDriverSeconds, touchpad: true)", flow);
            Assert.Equal(2, flow.Split("await SetTouchpadOnlyInPadForgeAsync(ud, false);").Length - 1);
            Assert.Contains("bool back = await SetTouchpadOnlyInPadForgeAsync(ud, false);", flow);
            Assert.Contains("if (devices.TouchpadSwitch != null)", window);
        }
    }
}
