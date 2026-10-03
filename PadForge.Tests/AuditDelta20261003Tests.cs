using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using PadForge.Common;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Common;
using PadForge.Engine.Common.Mapping;
using PadForge.Engine.Data;
using PadForge.Engine.RemoteLink;
using PadForge.Engine.Touchpad;
using PadForge.Resources.Strings;
using PadForge.Services;
using PadForge.ViewModels;
using PadForge.Views;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>Delta-audit 2026-10-03 contracts that run the engine's shared
    /// statics: the IR pointer's held aim and its cursor lane, the hooks'
    /// and the Raw Input reader's handling of PadForge's own output, and
    /// Numpad Enter as a Keyboard + Mouse output.</summary>
    [Collection("SettingsManagerStatics")]
    public class AuditDelta20261003EngineTests
    {
        internal static string RepoText(string relative)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PadForge.sln")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return File.ReadAllText(Path.Combine(dir.FullName, relative)).Replace("\r\n", "\n");
        }

        // ── The IR pointer's held aim (#485) ──

        private static float ReadIr(CustomInputState state, MappingSource src, int slot)
            => SourceCoercion.EvaluateForBipolarAxisTarget(state, src, slotIndex: slot);

        private static CustomInputState IrAim(float x)
        {
            var s = new CustomInputState();
            s.Ir.X = x;
            s.Ir.Detected = true;
            return s;
        }

        private static CustomInputState IrLost() => new();

        /// <summary>A profile switch drops every held IR aim, and replacing
        /// one slot's rows drops that slot's alone, the touch-momentum tiers.
        /// The held aim carried the old rows' bar offset and smoothing, and a
        /// remote that could not see the bar served it to the new rows.</summary>
        [Fact]
        public void TheHeldIrAim_FollowsTheMomentumResetTiers()
        {
            const int slotA = 12, slotB = 13;
            var prev = SourceCoercion.IrTuningProvider;
            try
            {
                SourceCoercion.IrTuningProvider = null;
                var src = new MappingSource { Descriptor = "IR Pointer X", DeviceGuid = "tier-a" };
                // A GunCon 2 feeds the same read.
                var gunSrc = new MappingSource { Descriptor = "IR Pointer X", DeviceGuid = "tier-gun" };
                var gun = new CustomInputState();
                SdlDeviceWrapper.ApplyGunCon2(gun, 600, 130, GunCon2Calibration.Default);
                SourceCoercion.BeginPollFrame();
                Assert.Equal(0.45f, ReadIr(IrAim(0.25f), src, slotA), precision: 5);
                Assert.Equal(0.45f, ReadIr(IrAim(0.25f), src, slotB), precision: 5);
                float gunAim = ReadIr(gun, gunSrc, slotA);
                Assert.NotEqual(0f, gunAim);

                InputManager.ResetSourceKindRuntimeForSlot(slotA);
                SourceCoercion.BeginPollFrame();
                Assert.Equal(0f, ReadIr(IrLost(), src, slotA), precision: 5);
                Assert.Equal(0f, ReadIr(IrLost(), gunSrc, slotA), precision: 5);
                Assert.Equal(0.45f, ReadIr(IrLost(), src, slotB), precision: 5);

                InputManager.ClearSourceKindRuntime();
                SourceCoercion.BeginPollFrame();
                Assert.Equal(0f, ReadIr(IrLost(), src, slotB), precision: 5);
            }
            finally
            {
                SourceCoercion.IrTuningProvider = prev;
                SourceCoercion.ForgetIrPointerForDevice("tier-a");
                SourceCoercion.ForgetIrPointerForDevice("tier-gun");
            }
        }

        /// <summary>Replacing a slot's rows drops its IR smoothing too, so the
        /// next aim snaps rather than sliding in from the old rows' last aim,
        /// while another slot keeps smoothing. A profile switch restarts every
        /// slot's. No unseen read comes between, since one drops the smoothing
        /// by itself.</summary>
        [Fact]
        public void ReplacedRows_StartTheIrSmoothingOver()
        {
            const int slotA = 12, slotB = 13;
            var prev = SourceCoercion.IrTuningProvider;
            try
            {
                SourceCoercion.IrTuningProvider = (dev, s) => (0f, 0.5f);
                var src = new MappingSource { Descriptor = "IR Pointer X", DeviceGuid = "tier-ema" };
                SourceCoercion.BeginPollFrame();
                Assert.Equal(0.45f, ReadIr(IrAim(0.25f), src, slotA), precision: 5);
                Assert.Equal(0.45f, ReadIr(IrAim(0.25f), src, slotB), precision: 5);
                // Tracked to tracked, the smoothing goes half way.
                SourceCoercion.BeginPollFrame();
                Assert.Equal(0f, ReadIr(IrAim(-0.25f), src, slotA), precision: 5);
                Assert.Equal(0f, ReadIr(IrAim(-0.25f), src, slotB), precision: 5);

                InputManager.ResetSourceKindRuntimeForSlot(slotA);
                SourceCoercion.BeginPollFrame();
                Assert.Equal(-0.45f, ReadIr(IrAim(-0.25f), src, slotA), precision: 5);
                Assert.Equal(-0.225f, ReadIr(IrAim(-0.25f), src, slotB), precision: 5);

                InputManager.ClearSourceKindRuntime();
                SourceCoercion.BeginPollFrame();
                Assert.Equal(0.45f, ReadIr(IrAim(0.25f), src, slotB), precision: 5);
            }
            finally
            {
                SourceCoercion.IrTuningProvider = prev;
                SourceCoercion.ForgetIrPointerForDevice("tier-ema");
            }
        }

        private static void OnAnotherThread(Action action)
        {
            var thread = new System.Threading.Thread(() => action());
            thread.Start();
            thread.Join();
        }

        /// <summary>A forget asked for on another thread takes effect at the
        /// poll thread's next frame boundary. A paste or a Remote Link handover
        /// can land while the poll thread is between a remote's X and Y reads.
        /// Cleared there and then, the read finishing after it wrote its axis
        /// of the old aim back: the new rows or the new connection served X
        /// from center and Y from before. A forget the poll thread asks for
        /// itself takes effect at once, so the aim a new connection reads after
        /// it in the same frame stays.</summary>
        [Fact]
        public void AForgetFromAnotherThread_OutlastsTheReadsInFlight()
        {
            const int slot = 12;
            var prev = SourceCoercion.IrTuningProvider;
            var x = new MappingSource { Descriptor = "IR Pointer X", DeviceGuid = "flight-a" };
            var y = new MappingSource { Descriptor = "IR Pointer Y", DeviceGuid = "flight-a" };
            static CustomInputState Aim2(float ax, float ay)
            {
                var s = IrAim(ax);
                s.Ir.Y = ay;
                return s;
            }
            try
            {
                SourceCoercion.IrTuningProvider = null;
                foreach (Action forget in new Action[]
                {
                    () => InputManager.ResetSourceKindRuntimeForSlot(slot),
                    () => InputManager.ClearSourceKindRuntime(),
                    () => SourceCoercion.ForgetIrPointerForDevice("flight-a"),
                })
                {
                    SourceCoercion.BeginPollFrame();
                    var aim = Aim2(0.25f, 0.2f);
                    ReadIr(aim, x, slot);
                    OnAnotherThread(forget);
                    // Mid-frame the forget has not happened: the aim this
                    // frame read still holds.
                    Assert.Equal(0.45f, ReadIr(IrLost(), x, slot), precision: 5);
                    ReadIr(aim, y, slot);

                    SourceCoercion.BeginPollFrame();
                    Assert.Equal(0f, ReadIr(IrLost(), x, slot), precision: 5);
                    Assert.Equal(0f, ReadIr(IrLost(), y, slot), precision: 5);
                }

                SourceCoercion.BeginPollFrame();
                SourceCoercion.ForgetIrPointerForDevice("flight-a");
                ReadIr(Aim2(0.25f, 0.2f), y, slot);
                SourceCoercion.BeginPollFrame();
                Assert.Equal(0.4f, ReadIr(IrLost(), y, slot), precision: 5);
            }
            finally
            {
                SourceCoercion.IrTuningProvider = prev;
                SourceCoercion.ForgetIrPointerForDevice("flight-a");
                SourceCoercion.BeginPollFrame();
            }
        }

        /// <summary>A poll loop Stop retired, still running after a stalled
        /// join, changes none of the frame bookkeeping once a newer run has
        /// registered: no frame advance, no poll thread claim and no drain of
        /// the IR forgets. The run is checked under the lock the registration
        /// takes, so the check cannot go stale before the change. Drained by
        /// the retired loop, a forget landed between the current run's X and
        /// Y reads and left a split aim.</summary>
        [Fact]
        public void ARetiredPollRun_ChangesNoFrameBookkeeping()
        {
            const int slot = 12;
            var prev = SourceCoercion.IrTuningProvider;
            var x = new MappingSource { Descriptor = "IR Pointer X", DeviceGuid = "retired-a" };
            var y = new MappingSource { Descriptor = "IR Pointer Y", DeviceGuid = "retired-a" };
            var aim = IrAim(0.25f);
            aim.Ir.Y = 0.2f;
            var seq = typeof(SourceCoercion).GetField("_pollFrameSeq", BindingFlags.NonPublic | BindingFlags.Static);
            var owner = typeof(SourceCoercion).GetField("_pollThreadId", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(seq);
            Assert.NotNull(owner);
            int retired = 0;
            OnAnotherThread(() => retired = SourceCoercion.BeginPollRun());
            int current = SourceCoercion.BeginPollRun();
            try
            {
                SourceCoercion.IrTuningProvider = null;
                SourceCoercion.BeginPollFrame(current);
                ReadIr(aim, x, slot);
                OnAnotherThread(() => SourceCoercion.ForgetIrPointerForDevice("retired-a"));
                object seqBefore = seq.GetValue(null), ownerBefore = owner.GetValue(null);
                OnAnotherThread(() => SourceCoercion.BeginPollFrame(retired));
                Assert.Equal(seqBefore, seq.GetValue(null));
                Assert.Equal(ownerBefore, owner.GetValue(null));
                ReadIr(aim, y, slot);
                SourceCoercion.BeginPollFrame(current);
                Assert.Equal(0f, ReadIr(IrLost(), x, slot), precision: 5);
                Assert.Equal(0f, ReadIr(IrLost(), y, slot), precision: 5);
            }
            finally
            {
                SourceCoercion.IrTuningProvider = prev;
                SourceCoercion.ForgetIrPointerForDevice("retired-a");
                SourceCoercion.BeginPollFrame();
            }
        }

        /// <summary>Start registers each poll run before it starts the run's
        /// thread and hands the loop the token. A loop that registered itself
        /// could do it late: one Stop retired before it reached its first
        /// frame registered after its replacement and locked it out of the
        /// frame bookkeeping. The registration also ends the old loop's claim
        /// to be the poll thread, so a forget that loop asks for while still
        /// mid-frame waits for the new run's first frame boundary. A forget
        /// makes its poll-thread check and its clear under the registration's
        /// lock: one the old loop began just before the registration cleared
        /// between the new run's X and Y reads.</summary>
        [Fact]
        public void StartRegistersTheRun_AndEndsTheOldLoopsClaim()
        {
            const int slot = 12;
            var prev = SourceCoercion.IrTuningProvider;
            var x = new MappingSource { Descriptor = "IR Pointer X", DeviceGuid = "claim-a" };
            try
            {
                SourceCoercion.IrTuningProvider = null;
                // This thread plays the old loop: it began a frame and read.
                SourceCoercion.BeginPollFrame();
                ReadIr(IrAim(0.25f), x, slot);
                int run = SourceCoercion.BeginPollRun();
                // Still mid-frame, the old loop asks for a forget. It waits.
                SourceCoercion.ForgetIrPointerForDevice("claim-a");
                Assert.Equal(0.45f, ReadIr(IrLost(), x, slot), precision: 5);
                SourceCoercion.BeginPollFrame(run);
                Assert.Equal(0f, ReadIr(IrLost(), x, slot), precision: 5);
            }
            finally
            {
                SourceCoercion.IrTuningProvider = prev;
                SourceCoercion.ForgetIrPointerForDevice("claim-a");
                SourceCoercion.BeginPollFrame();
            }
            string im = RepoText("PadForge.App/Common/Input/InputManager.cs");
            Assert.Contains("int pollRun = Engine.Common.Mapping.SourceCoercion.BeginPollRun();\n"
                + "            _pollingThread = new Thread(() => PollingLoop(generation, pollRun))", im);
            int start = im.IndexOf("private void PollingLoop(int generation, int pollRun)", StringComparison.Ordinal);
            Assert.True(start > 0);
            string loop = im.Substring(start, im.IndexOf("\n        }\n", start, StringComparison.Ordinal) - start);
            Assert.Contains("Engine.Common.Mapping.SourceCoercion.BeginPollFrame(pollRun);", loop);
            Assert.DoesNotContain("BeginPollRun", loop);
            string sc = RepoText("PadForge.Engine/Common/Mapping/SourceCoercion.cs");
            Assert.Contains("            lock (_pollRunLock)\n            {\n"
                + "                if (Environment.CurrentManagedThreadId == System.Threading.Volatile.Read(ref _pollThreadId))\n"
                + "                    ApplyIrForget(kind, dev, slot);", sc);
        }

        /// <summary>The Triggers tab's live preview reads an IR source on the
        /// UI thread. The read writes neither the held aim nor the smoothing,
        /// which the poll thread owns: a preview read between two poll reads
        /// stepped the smoothing in their place and left its own aim
        /// held.</summary>
        [Fact]
        public void TheTriggerPreview_LeavesTheIrStateAlone()
        {
            const int slot = 12;
            var prev = SourceCoercion.IrTuningProvider;
            try
            {
                SourceCoercion.IrTuningProvider = (dev, s) => (0f, 0.5f);
                var src = new MappingSource { Descriptor = "IR Pointer X", DeviceGuid = "preview-a" };
                var ms = new MappingSet();
                ms.Rows.Add(Row("LeftTrigger", "Base", src));
                SourceCoercion.BeginPollFrame();
                Assert.Equal(0.45f, ReadIr(IrAim(0.25f), src, slot), precision: 5);

                // A preview read in a frame: the poll read smooths from its
                // own last aim, which is the aim it reads again.
                SourceCoercion.BeginPollFrame();
                InputManager.EvaluatePerDeviceTriggerPreview(IrAim(-0.25f), ms, "preview-a", "LeftTrigger", slot);
                Assert.Equal(0.45f, ReadIr(IrAim(0.25f), src, slot), precision: 5);

                // A frame only the preview reads: the hold stays the poll's.
                SourceCoercion.BeginPollFrame();
                InputManager.EvaluatePerDeviceTriggerPreview(IrAim(-0.25f), ms, "preview-a", "LeftTrigger", slot);
                SourceCoercion.BeginPollFrame();
                Assert.Equal(0.45f, ReadIr(IrLost(), src, slot), precision: 5);

                // A preview of a remote that cannot see the bar drops no
                // smoothing: the poll's next aim still smooths half way.
                SourceCoercion.BeginPollFrame();
                Assert.Equal(0.45f, ReadIr(IrAim(0.25f), src, slot), precision: 5);
                SourceCoercion.BeginPollFrame();
                InputManager.EvaluatePerDeviceTriggerPreview(IrLost(), ms, "preview-a", "LeftTrigger", slot);
                Assert.Equal(0f, ReadIr(IrAim(-0.25f), src, slot), precision: 5);

                // A preview of a found bar seeds no smoothing: after sight
                // loss the poll's next aim snaps to itself.
                SourceCoercion.BeginPollFrame();
                ReadIr(IrLost(), src, slot);
                SourceCoercion.BeginPollFrame();
                InputManager.EvaluatePerDeviceTriggerPreview(IrAim(-0.25f), ms, "preview-a", "LeftTrigger", slot);
                SourceCoercion.BeginPollFrame();
                Assert.Equal(0.45f, ReadIr(IrAim(0.25f), src, slot), precision: 5);

                // The read-only scope ends with the preview: the poll read
                // after it writes its hold again.
                SourceCoercion.IrTuningProvider = null;
                SourceCoercion.BeginPollFrame();
                InputManager.EvaluatePerDeviceTriggerPreview(IrAim(-0.25f), ms, "preview-a", "LeftTrigger", slot);
                ReadIr(IrAim(0.1f), src, slot);
                SourceCoercion.BeginPollFrame();
                Assert.Equal(0.18f, ReadIr(IrLost(), src, slot), precision: 5);
            }
            finally
            {
                SourceCoercion.IrTuningProvider = prev;
                SourceCoercion.ForgetIrPointerForDevice("preview-a");
                SourceCoercion.BeginPollFrame();
            }
        }

        private static IDictionary StickCoast()
            => (IDictionary)typeof(SourceCoercion)
                .GetField("_stickCoast", BindingFlags.NonPublic | BindingFlags.Static)
                .GetValue(null);

        private static void AddStickBall(int slot, string device)
        {
            var ball = typeof(SourceCoercion).GetNestedType("StickBall", BindingFlags.NonPublic);
            StickCoast()[(slot, device)] = Activator.CreateInstance(ball, nonPublic: true);
        }

        private static bool HasStickBall(int slot, string device) => StickCoast().Contains((slot, device));

        /// <summary>A new connection's IR pointer reads center until the remote
        /// sees the bar, and its trackballs start at rest, by the rule that
        /// gives it a fresh motor state. A reload of the same connection keeps
        /// both, and another device's aim and ball stay put.</summary>
        [Fact]
        public void ANewConnection_StartsTheIrAimFromCenter()
        {
            const int slot = 12;
            var prev = SourceCoercion.IrTuningProvider;
            var first = new SdlDeviceWrapper { SdlInstanceId = 7 };
            var same = new SdlDeviceWrapper { SdlInstanceId = 7 };
            var next = new SdlDeviceWrapper { SdlInstanceId = 8 };
            string guid = null;
            try
            {
                SourceCoercion.IrTuningProvider = null;
                var ud = new UserDevice();
                ud.LoadFromSdlDevice(first);
                guid = ud.InstanceGuid.ToString();
                var mine = new MappingSource { Descriptor = "IR Pointer X", DeviceGuid = guid };
                var other = new MappingSource { Descriptor = "IR Pointer X", DeviceGuid = "conn-other" };
                SourceCoercion.BeginPollFrame();
                ReadIr(IrAim(0.25f), mine, slot);
                ReadIr(IrAim(0.25f), other, slot);
                AddStickBall(slot, guid);
                AddStickBall(slot, "conn-other");

                // The same wrapper again, and a fresh one on the same SDL
                // instance, are the same connection.
                ud.LoadFromSdlDevice(first);
                ud.LoadFromSdlDevice(same);
                SourceCoercion.BeginPollFrame();
                Assert.Equal(0.45f, ReadIr(IrLost(), mine, slot), precision: 5);
                Assert.True(HasStickBall(slot, guid));

                ud.LoadFromSdlDevice(next);
                SourceCoercion.BeginPollFrame();
                Assert.Equal(0f, ReadIr(IrLost(), mine, slot), precision: 5);
                Assert.False(HasStickBall(slot, guid));
                Assert.Equal(0.45f, ReadIr(IrLost(), other, slot), precision: 5);
                Assert.True(HasStickBall(slot, "conn-other"));
            }
            finally
            {
                SourceCoercion.IrTuningProvider = prev;
                foreach (var dev in new[] { guid, "conn-other" })
                {
                    if (dev == null) continue;
                    SourceCoercion.ForgetIrPointerForDevice(dev);
                    SourceCoercion.ResetTouchMomentumForDevice(dev);
                }
                first.Dispose();
                same.Dispose();
                next.Dispose();
            }
        }

        // ── The IR pointer's cursor lane reads the active row ──

        private static readonly MethodInfo FindIr = typeof(InputManager).GetMethod(
            "FindIrPointerSource", BindingFlags.Static | BindingFlags.NonPublic);

        private static MappingSource IrSourceFor(CustomInputState state, MappingSet ms, string target, string legacy, int slot)
            => (MappingSource)FindIr.Invoke(null, new object[] { state, ms, target, legacy, "", slot });

        private static MappingSet ViewLayer(bool inherit)
        {
            var ms = new MappingSet();
            ms.ShiftActivators.Add(new ShiftActivator
            {
                DeviceGuid = "", Descriptor = "Button 28", Mode = "Hold",
                LayerMask = "View", LayerName = "View", Kind = "Button", DelayMs = 0,
                InheritUnmapped = inherit,
            });
            return ms;
        }

        private static MappingRow Row(string target, string layer, params MappingSource[] sources)
        {
            var row = new MappingRow { Target = target, LayerMask = layer };
            row.Sources.AddRange(sources);
            return row;
        }

        /// <summary>An IR row on a shift layer drives the absolute cursor while
        /// the layer is engaged, and holds it there once the bar leaves view.
        /// The Base-only walk missed the row, and the velocity lane read the
        /// held aim as a speed, so the cursor drifted on.</summary>
        [Theory]
        [InlineData("KbmMouseX", "IR Pointer X")]
        [InlineData("KbmMouseY", "IR Pointer Y")]
        public void ALayersIrRow_DrivesTheAbsoluteCursor(string target, string ir)
        {
            const int slot = 14;
            InputManager.ClearAllShiftRuntime();
            try
            {
                var ms = ViewLayer(inherit: true);
                var layerIr = new MappingSource { Descriptor = ir };
                ms.Rows.Add(Row(target, "View", layerIr));
                var state = new CustomInputState();
                state.Ir.Detected = true;

                InputManager.ResolveActiveLayerMask(slot, ms, state, "");
                Assert.Null(IrSourceFor(state, ms, target, null, slot));

                state.Buttons[28] = true;
                Assert.Equal("View", InputManager.ResolveActiveLayerMask(slot, ms, state, ""));
                Assert.Same(layerIr, IrSourceFor(state, ms, target, null, slot));
                state.Ir.Detected = false;
                Assert.Same(layerIr, IrSourceFor(state, ms, target, null, slot));
            }
            finally
            {
                InputManager.ClearAllShiftRuntime();
            }
        }

        /// <summary>A layer that maps the mouse to a stick takes the target
        /// from a Base IR row while it is engaged, and the IR row drives the
        /// cursor again once the layer lets go.</summary>
        [Fact]
        public void ALayersStickRow_OverridesABaseIrRow()
        {
            const int slot = 14;
            InputManager.ClearAllShiftRuntime();
            try
            {
                var ms = ViewLayer(inherit: true);
                var baseIr = new MappingSource { Descriptor = "IR Pointer X" };
                ms.Rows.Add(Row("KbmMouseX", "Base", baseIr));
                ms.Rows.Add(Row("KbmMouseX", "View", new MappingSource { Descriptor = "Axis 3" }));
                var state = new CustomInputState();
                state.Ir.Detected = true;

                InputManager.ResolveActiveLayerMask(slot, ms, state, "");
                Assert.Same(baseIr, IrSourceFor(state, ms, "KbmMouseX", null, slot));
                state.Buttons[28] = true;
                Assert.Equal("View", InputManager.ResolveActiveLayerMask(slot, ms, state, ""));
                Assert.Null(IrSourceFor(state, ms, "KbmMouseX", null, slot));
                state.Buttons[28] = false;
                InputManager.ResolveActiveLayerMask(slot, ms, state, "");
                Assert.Same(baseIr, IrSourceFor(state, ms, "KbmMouseX", null, slot));
            }
            finally
            {
                InputManager.ClearAllShiftRuntime();
            }
        }

        /// <summary>An engaged activator's gate is suppressed for the Axis
        /// kind alone, the one kind that reads it (ReadActivatorInput). A gate
        /// kept from an Axis activator changed to a Button is no leg, and
        /// suppressing it swallowed a key the layer's rows map.</summary>
        [Fact]
        public void AButtonActivator_LeavesAKeptAxisGateAlone()
        {
            const int slot = 14;
            InputManager.ClearAllShiftRuntime();
            try
            {
                var ms = new MappingSet();
                ms.ShiftActivators.Add(new ShiftActivator
                {
                    DeviceGuid = "", Descriptor = "Button 28", Mode = "Hold",
                    LayerMask = "View", LayerName = "View", Kind = "Button", DelayMs = 0,
                    GateDescriptor = "Button 5",
                });
                var state = new CustomInputState();
                state.Buttons[28] = true;
                state.Buttons[5] = true;
                Assert.Equal("View", InputManager.ResolveActiveLayerMask(slot, ms, state, ""));
                Assert.True(InputManager.IsSourceSuppressedPostpone(slot, "", "Button 28"));
                Assert.False(InputManager.IsSourceSuppressedPostpone(slot, "", "Button 5"));

                // An Axis activator reads its gate, which stays suppressed.
                InputManager.ClearAllShiftRuntime();
                ms.ShiftActivators[0].Kind = "Axis";
                ms.ShiftActivators[0].Descriptor = "Axis 0";
                state.Axis[0] = 65535;
                Assert.Equal("View", InputManager.ResolveActiveLayerMask(slot, ms, state, ""));
                Assert.True(InputManager.IsSourceSuppressedPostpone(slot, "", "Button 5"));
            }
            finally
            {
                InputManager.ClearAllShiftRuntime();
            }
        }

        /// <summary>A Cycle activator's Next press suppresses everything its
        /// input reads, as a press in every other mode does: the input, the
        /// Axis gate or the Chord partner, and the third leg. The Cycle branch
        /// suppressed the input alone, so an imported Axis Cycle let its held
        /// gate and third leg fire the layer's mappings. A Previous press
        /// alone suppresses the Previous button and none of Next's legs, and
        /// Postpone Mapping opts out.</summary>
        [Fact]
        public void ACycleActivatorsNextPress_SuppressesItsLegs()
        {
            const int slot = 14;
            InputManager.ClearAllShiftRuntime();
            try
            {
                var act = new ShiftActivator
                {
                    DeviceGuid = "", Mode = "Cycle", Kind = "Axis", Descriptor = "Axis 0",
                    GateDescriptor = "Button 5", Gate2Descriptor = "Button 6",
                    CyclePrevDescriptor = "Button 9", CycleLayers = "View|Other", DelayMs = 0,
                };
                var ms = new MappingSet();
                ms.ShiftActivators.Add(act);
                var state = new CustomInputState();
                state.Axis[0] = 65535;
                state.Buttons[5] = true;
                state.Buttons[6] = true;
                InputManager.ResolveActiveLayerMask(slot, ms, state, "");
                Assert.True(InputManager.IsSourceSuppressedPostpone(slot, "", "Axis 0"));
                Assert.True(InputManager.IsSourceSuppressedPostpone(slot, "", "Button 5"));
                Assert.True(InputManager.IsSourceSuppressedPostpone(slot, "", "Button 6"));

                // A Chord Cycle suppresses its partner.
                InputManager.ClearAllShiftRuntime();
                act.Kind = "Chord";
                act.Descriptor = "Button 28";
                act.ChordSecondDescriptor = "Button 7";
                act.GateDescriptor = "";
                act.Gate2Descriptor = "";
                state = new CustomInputState();
                state.Buttons[28] = true;
                state.Buttons[7] = true;
                InputManager.ResolveActiveLayerMask(slot, ms, state, "");
                Assert.True(InputManager.IsSourceSuppressedPostpone(slot, "", "Button 7"));

                // Previous alone: its own button, none of Next's legs.
                InputManager.ClearAllShiftRuntime();
                state = new CustomInputState();
                state.Buttons[9] = true;
                state.Buttons[7] = true;
                InputManager.ResolveActiveLayerMask(slot, ms, state, "");
                Assert.True(InputManager.IsSourceSuppressedPostpone(slot, "", "Button 9"));
                Assert.False(InputManager.IsSourceSuppressedPostpone(slot, "", "Button 7"));

                // Postpone Mapping opts out.
                InputManager.ClearAllShiftRuntime();
                act.PostponeMapping = true;
                state = new CustomInputState();
                state.Buttons[28] = true;
                state.Buttons[7] = true;
                InputManager.ResolveActiveLayerMask(slot, ms, state, "");
                Assert.False(InputManager.IsSourceSuppressedPostpone(slot, "", "Button 7"));
            }
            finally
            {
                InputManager.ClearAllShiftRuntime();
            }
        }

        /// <summary>A pinned activator's companion input, a chord's second
        /// input or a Cycle's Previous button, with no device of its own is
        /// read from the activator's device and suppressed there and on an
        /// any-device row, not on another device's input of the same name.
        /// Its empty guid used to suppress the key on every device. A
        /// companion pinned to another device is read and suppressed
        /// there.</summary>
        [Fact]
        public void AnActivatorsCompanion_IsSuppressedOnTheDeviceItReads()
        {
            const int slot = 14;
            const string a = "a0000000-0000-0000-0000-00000000000a";
            const string b = "b0000000-0000-0000-0000-00000000000b";
            var devices = SettingsManager.UserDevices;
            InputManager.ClearAllShiftRuntime();
            try
            {
                var act = new ShiftActivator
                {
                    DeviceGuid = a, Mode = "Hold", Kind = "Chord", Descriptor = "Button 28",
                    ChordSecondDescriptor = "Button 7", LayerMask = "View", LayerName = "View", DelayMs = 0,
                };
                var ms = new MappingSet();
                ms.ShiftActivators.Add(act);
                var state = new CustomInputState();
                state.Buttons[28] = true;
                state.Buttons[7] = true;
                Assert.Equal("View", InputManager.ResolveActiveLayerMask(slot, ms, state, a));
                Assert.True(InputManager.IsSourceSuppressedPostpone(slot, a, "Button 7"));
                Assert.True(InputManager.IsSourceSuppressedPostpone(slot, "", "Button 7"));
                Assert.False(InputManager.IsSourceSuppressedPostpone(slot, b, "Button 7"));

                // A Cycle's Previous button with no device of its own.
                InputManager.ClearAllShiftRuntime();
                act.Mode = "Cycle";
                act.CycleLayers = "View|Other";
                act.CyclePrevDescriptor = "Button 9";
                state = new CustomInputState();
                state.Buttons[9] = true;
                InputManager.ResolveActiveLayerMask(slot, ms, state, a);
                Assert.True(InputManager.IsSourceSuppressedPostpone(slot, a, "Button 9"));
                Assert.False(InputManager.IsSourceSuppressedPostpone(slot, b, "Button 9"));

                // A chord partner pinned to the other device.
                InputManager.ClearAllShiftRuntime();
                var other = new UserDevice
                {
                    InstanceGuid = Guid.Parse(b), ProductGuid = Guid.Parse(b),
                    InstanceName = "Other", ProductName = "Other", IsOnline = true,
                    InputState = new CustomInputState(),
                };
                other.InputState.Buttons[7] = true;
                SettingsManager.UserDevices = new DeviceCollection();
                lock (SettingsManager.UserDevices.SyncRoot) SettingsManager.UserDevices.Items.Add(other);
                act.Mode = "Hold";
                act.ChordSecondDeviceGuid = b;
                state = new CustomInputState();
                state.Buttons[28] = true;
                Assert.Equal("View", InputManager.ResolveActiveLayerMask(slot, ms, state, a));
                Assert.True(InputManager.IsSourceSuppressedPostpone(slot, b, "Button 7"));
                Assert.False(InputManager.IsSourceSuppressedPostpone(slot, a, "Button 7"));
            }
            finally
            {
                SettingsManager.UserDevices = devices;
                InputManager.ClearAllShiftRuntime();
            }
        }

        /// <summary>A layer switched to Passive (No Button) keeps the input it
        /// had, and that input reads nothing: the layer stays off and the key
        /// is not suppressed. The same activator in Hold mode engages and
        /// suppresses it.</summary>
        [Fact]
        public void APassiveLayer_ReadsNoInputItKept()
        {
            const int slot = 14;
            InputManager.ClearAllShiftRuntime();
            try
            {
                var ms = ViewLayer(inherit: true);
                var state = new CustomInputState();
                state.Buttons[28] = true;
                Assert.Equal("View", InputManager.ResolveActiveLayerMask(slot, ms, state, ""));
                Assert.True(InputManager.IsSourceSuppressedPostpone(slot, "", "Button 28"));

                InputManager.ClearAllShiftRuntime();
                ms.ShiftActivators[0].Mode = "Passive";
                Assert.Equal("Base", InputManager.ResolveActiveLayerMask(slot, ms, state, ""));
                Assert.False(InputManager.IsSourceSuppressedPostpone(slot, "", "Button 28"));
            }
            finally
            {
                InputManager.ClearAllShiftRuntime();
            }
        }

        /// <summary>The legacy per-key descriptor drives the cursor only where
        /// the row leaves the target to it, the row evaluators' rule
        /// (TryEvaluateMappingSetBipolarAxis): no row, an empty row, or a row
        /// of modifiers alone. A row with a source owns the target, and a
        /// layer that forces the target off keeps it off.</summary>
        [Fact]
        public void TheLegacyDescriptor_RoutesOnlyWhereTheRowLeavesTheTarget()
        {
            const int slot = 14;
            InputManager.ClearAllShiftRuntime();
            try
            {
                var state = new CustomInputState();
                state.Ir.Detected = true;

                var legacy = IrSourceFor(state, new MappingSet(), "KbmMouseY", "IIR Pointer Y", slot);
                Assert.NotNull(legacy);
                Assert.Equal("IR Pointer Y", legacy.Descriptor);
                Assert.True(legacy.Invert);

                var empty = new MappingSet();
                empty.Rows.Add(Row("KbmMouseY", "Base"));
                Assert.NotNull(IrSourceFor(state, empty, "KbmMouseY", "IR Pointer Y", slot));
                var modifiers = new MappingSet();
                modifiers.Rows.Add(Row("KbmMouseY", "Base",
                    new MappingSource { Descriptor = "Button 3", Kind = "InvertOnHold" }));
                Assert.NotNull(IrSourceFor(state, modifiers, "KbmMouseY", "IR Pointer Y", slot));

                var stick = new MappingSet();
                stick.Rows.Add(Row("KbmMouseY", "Base", new MappingSource { Descriptor = "Axis 4" }));
                Assert.Null(IrSourceFor(state, stick, "KbmMouseY", "IR Pointer Y", slot));

                // Replace mode, and a NoInherit block on an inheriting layer.
                var replace = ViewLayer(inherit: false);
                replace.Rows.Add(Row("KbmMouseY", "Base", new MappingSource { Descriptor = "IR Pointer Y" }));
                var blocked = ViewLayer(inherit: true);
                blocked.Rows.Add(Row("KbmMouseY", "Base", new MappingSource { Descriptor = "IR Pointer Y" }));
                blocked.Rows.Add(new MappingRow { Target = "KbmMouseY", LayerMask = "View", NoInherit = true });
                state.Buttons[28] = true;
                foreach (var ms in new[] { replace, blocked })
                {
                    Assert.Equal("View", InputManager.ResolveActiveLayerMask(slot, ms, state, ""));
                    Assert.Null(IrSourceFor(state, ms, "KbmMouseY", "IR Pointer Y", slot));
                }

                // An inheriting layer with no row of its own leaves the Base IR
                // row driving.
                var inherit = ViewLayer(inherit: true);
                var baseIr = new MappingSource { Descriptor = "IR Pointer Y" };
                inherit.Rows.Add(Row("KbmMouseY", "Base", baseIr));
                Assert.Equal("View", InputManager.ResolveActiveLayerMask(slot, inherit, state, ""));
                Assert.Same(baseIr, IrSourceFor(state, inherit, "KbmMouseY", null, slot));
            }
            finally
            {
                InputManager.ClearAllShiftRuntime();
            }
        }

        /// <summary>An Invert on Hold modifier is no source, whatever its
        /// descriptor names, the row evaluators' rule. A modifier left holding
        /// "IR Pointer X" or "Touchpad 0 Pointer X" on a row whose stick owns
        /// the mouse took the cursor absolute.</summary>
        [Fact]
        public void AModifierCarryingAPointerDescriptor_IsNoCursorSource()
        {
            const int slot = 14;
            var findTouch = typeof(InputManager).GetMethod("FindEngagedTouchpadPointerSource",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.NotNull(findTouch);
            InputManager.ClearAllShiftRuntime();
            try
            {
                var ir = new MappingSet();
                ir.Rows.Add(Row("KbmMouseX", "Base", new MappingSource { Descriptor = "Axis 3" },
                    new MappingSource { Descriptor = "IR Pointer X", Kind = "InvertOnHold" }));
                var state = new CustomInputState();
                state.Ir.Detected = true;
                state.Ir.X = 0.3f;
                Assert.Null(IrSourceFor(state, ir, "KbmMouseX", null, slot));

                var touch = new MappingSet();
                touch.Rows.Add(Row("KbmMouseX", "Base", new MappingSource { Descriptor = "Axis 3" },
                    new MappingSource { Descriptor = "Touchpad 0 Pointer X", Kind = "InvertOnHold" }));
                var pad = new TouchpadInputState(2);
                pad.FingerDown[0] = true;
                pad.FingerX[0] = pad.FingerY[0] = 0.5f;
                var touchState = new CustomInputState { Touchpads = new[] { pad } };
                Assert.Null(findTouch.Invoke(null, new object[] { touchState, touch, "KbmMouseX", slot, "" }));

                // Positive control: the same descriptor as a source drives it.
                var plain = new MappingSet();
                plain.Rows.Add(Row("KbmMouseX", "Base", new MappingSource { Descriptor = "Touchpad 0 Pointer X" }));
                Assert.NotNull(findTouch.Invoke(null, new object[] { touchState, plain, "KbmMouseX", slot, "" }));
            }
            finally
            {
                InputManager.ClearAllShiftRuntime();
            }
        }

        // ── PadForge's own output passes the hooks and the Raw Input reader ──

        private static bool[] HookedKeys()
        {
            var keys = new bool[256];
            InputHookManager.MergeHookedKeyState(keys, keys.Length);
            return keys;
        }

        private static bool[] HookedButtons()
        {
            var buttons = new bool[5];
            InputHookManager.MergeHookedMouseState(buttons, buttons.Length);
            return buttons;
        }

        private static bool PhysicallyDown(int vk)
            => ((bool[])typeof(InputHookManager)
                .GetField("_physKeyDown", BindingFlags.NonPublic | BindingFlags.Static)
                .GetValue(null))[vk];

        /// <summary>PadForge's own key output passes the keyboard hook
        /// untouched: it is not consumed, not kept as a hooked key, and not
        /// counted as a physical key for the global hotkeys. A physical press
        /// of the same key is consumed, and so is a replayed chord prefix,
        /// which is a physical key.</summary>
        [Fact]
        public void TheKeyboardHook_PassesPadForgesOwnKeys()
        {
            const int A = 0x41;
            const uint ScanA = 0x1E;
            var hooks = new InputHookManager();
            hooks.SetSuppressedKeys(new HashSet<int> { A });
            try
            {
                Assert.False(InputHookManager.HandleKeyboardEvent(A, ScanA, 0, InputHookManager.OutputTag, isDown: true));
                Assert.False(HookedKeys()[A]);
                Assert.False(PhysicallyDown(A));

                Assert.True(InputHookManager.HandleKeyboardEvent(A, ScanA, 0, IntPtr.Zero, isDown: true));
                Assert.True(HookedKeys()[A]);
                Assert.True(PhysicallyDown(A));
                Assert.True(InputHookManager.HandleKeyboardEvent(A, ScanA, 0, IntPtr.Zero, isDown: false));

                Assert.True(InputHookManager.HandleKeyboardEvent(A, ScanA, 0, InputHookManager.ReplayTag, isDown: true));
                Assert.True(InputHookManager.HandleKeyboardEvent(A, ScanA, 0, InputHookManager.ReplayTag, isDown: false));
                Assert.False(HookedKeys()[A]);
            }
            finally
            {
                hooks.SetSuppressedKeys(new HashSet<int>());
            }
        }

        /// <summary>The keyboard hook hands the chord engine the physical key
        /// behind each event, so a held Numpad Enter replays as Numpad Enter,
        /// and both replay drains and the replay itself carry that key. The
        /// release goes through the hook only after the engine is gone, so the
        /// test injects nothing.</summary>
        [Fact]
        public void TheKeyboardHook_HandsTheChordEngineThePhysicalKey()
        {
            const int Return = 0x0D, L = 0x4C;
            var engine = new HandheldChordEngine();
            engine.SetChords(new[] { new HandheldChordDefinition { Name = "Enter L", Button = 3, Keys = new[] { Return, L } } });
            var prev = InputHookManager.ChordEngine;
            InputHookManager.ChordEngine = engine;
            try
            {
                Assert.True(InputHookManager.HandleKeyboardEvent(Return, 0x1C, 0x01, IntPtr.Zero, isDown: true));
                engine.Tick(Environment.TickCount64 + HandheldChordEngine.HoldMs);
                Assert.Equal(new[] { (Return, true, 0x11C) }, engine.PendingReplays);
            }
            finally
            {
                InputHookManager.ChordEngine = prev;
                InputHookManager.HandleKeyboardEvent(Return, 0x1C, 0x01, IntPtr.Zero, isDown: false);
            }

            string hook = RepoText("PadForge.Engine/Common/InputHookManager.cs");
            Assert.Contains("foreach (var (c, d, id) in _hookReplays) InjectReplay(c, d, id);", hook);
            Assert.Contains("var (scan, flags) = ReplayKey(code, down, ident);", hook);
            Assert.Contains("InputHookManager.InjectReplay(code, down, ident);",
                RepoText("PadForge.App/Common/Input/HandheldChordRuntime.cs"));
        }

        /// <summary>PadForge's own clicks pass the mouse hook the same way.</summary>
        [Fact]
        public void TheMouseHook_PassesPadForgesOwnClicks()
        {
            var hooks = new InputHookManager();
            hooks.SetSuppressedMouseButtons(new HashSet<int> { 0 });
            try
            {
                Assert.False(InputHookManager.HandleMouseButton(0, true, InputHookManager.OutputTag, 0));
                Assert.False(HookedButtons()[0]);

                Assert.True(InputHookManager.HandleMouseButton(0, true, IntPtr.Zero, 0));
                Assert.True(HookedButtons()[0]);
                Assert.True(InputHookManager.HandleMouseButton(0, false, IntPtr.Zero, 0));
                Assert.False(HookedButtons()[0]);

                // A button nothing consumes passes.
                Assert.False(InputHookManager.HandleMouseButton(1, true, IntPtr.Zero, 0));
            }
            finally
            {
                hooks.SetSuppressedMouseButtons(new HashSet<int>());
            }
            // The callback hands the decision the event's own extra info.
            Assert.Contains("if (HandleMouseButton(buttonId, IsMouseDown(msg), ms.dwExtraInfo, ms.flags))",
                RepoText("PadForge.Engine/Common/InputHookManager.cs"));
        }

        /// <summary>Every input event the Keyboard + Mouse virtual controller
        /// and the macro emitters build carries the output tag. Both files
        /// run only under SendInput, so pin that the tag rides each one.</summary>
        [Fact]
        public void EveryOutputEvent_CarriesTheTag()
        {
            const string tag = "dwExtraInfo = PadForge.Engine.Common.InputHookManager.OutputTag";
            foreach (var (file, events) in new[]
            {
                ("PadForge.App/Common/Input/InputManager.Step4b.EvaluateMacros.cs", 9),
                ("PadForge.App/Common/Input/KeyboardMouseVirtualController.cs", 3),
            })
            {
                string src = RepoText(file);
                Assert.Equal(events, Regex.Matches(src, @"new (MOUSEINPUT|KEYBDINPUT)\b").Count);
                Assert.Equal(events, Regex.Matches(src, Regex.Escape(tag)).Count);
            }

            string reader = RepoText("PadForge.Engine/Common/RawInputListener.cs");
            Assert.Contains("ApplyKeyboardRecord(hDevice, kb.MakeCode, kb.Flags, kb.VKey, kb.ExtraInformation);", reader);
            Assert.Contains("ApplyMouseRecord(hDevice, mouse.usFlags, mouse.usButtonFlags, mouse.usButtonData,\n                        mouse.lLastX, mouse.lLastY, mouse.ulExtraInformation);", reader);
        }

        private const ushort RiKeyBreak = 0x0001, RiKeyE0 = 0x0002, RiKeyE1 = 0x0004;

        // A keyboard handle of these tests' own, which no iCade driver knows.
        // Handle 0 is shared with tests outside this collection.
        private static readonly IntPtr Kbd = new(0x0A0D1003);

        private static void Record(ushort make, ushort flags, ushort vKey, uint extra = 0)
            => RawInputListener.ApplyKeyboardRecord(Kbd, make, flags, vKey, extra);

        private static bool[] RawKeys()
        {
            var keys = new bool[256];
            RawInputListener.GetKeyboardState(Kbd, keys, keys.Length);
            return keys;
        }

        /// <summary>A keyboard record that is no key changes nothing: an
        /// overrun, a record with no VKey (half of an escaped sequence, such
        /// as the fake Shift around PrintScreen), and PadForge's own output.
        /// RawInputDemo's keyboard handler drops the first two
        /// (RawInputDeviceKeyboard.cpp 228-235).</summary>
        [Fact]
        public void ARawRecordThatIsNoKey_ChangesNothing()
        {
            var controls = new (ushort Make, ushort Flags, ushort VKey)[]
            {
                (0x1E, 0, 0x41),        // A
                (0x37, RiKeyE0, 0x2C),  // PrintScreen
                (0x1D, RiKeyE1, 0x13),  // Pause
                (0x1C, RiKeyE0, 0x0D),  // Numpad Enter
                (0x36, 0, 0x10),        // right Shift, reported neutral
            };
            try
            {
                Record(0xFF, 0, 0x41);
                Record(0x2A, RiKeyE0, 0xFF);
                Record(0x1E, 0, 0x41, InputHookManager.OutputTagValue);
                var keys = RawKeys();
                Assert.False(keys[0x41]);
                Assert.False(keys[0xFF]);
                Assert.False(keys[0xA0]);

                // Positive controls in the same window.
                foreach (var (make, flags, vk) in controls)
                    Record(make, flags, vk);
                keys = RawKeys();
                Assert.True(keys[0x41]);
                Assert.True(keys[0x2C]);
                Assert.True(keys[0x13]);
                Assert.True(keys[RawInputListener.NumpadEnterKey]);
                Assert.True(keys[0xA1]);
            }
            finally
            {
                foreach (var (make, flags, vk) in controls)
                    Record(make, (ushort)(flags | RiKeyBreak), vk);
            }
            Assert.DoesNotContain(true, RawKeys());
        }

        private const ushort RiMouseLeftDown = 0x0001, RiMouseLeftUp = 0x0002, RiMouseWheel = 0x0400;

        // A mouse handle of these tests' own.
        private static readonly IntPtr Mouse = new(0x0A0D1004);

        /// <summary>A mouse record carrying the output tag is PadForge's own
        /// output, and the merged mouse must not read it back: no motion, no
        /// click and no scroll. An untagged record in the same window lands.</summary>
        [Fact]
        public void ARawMouseRecordFromPadForge_ChangesNothing()
        {
            var buttons = new bool[5];
            try
            {
                RawInputListener.ApplyMouseRecord(Mouse, 0, RiMouseLeftDown | RiMouseWheel, 120, 5, 7,
                    InputHookManager.OutputTagValue);
                RawInputListener.GetMouseButtons(Mouse, buttons);
                Assert.False(buttons[0]);
                RawInputListener.ConsumeMouseDelta(Mouse, out int dx, out int dy);
                Assert.Equal((0, 0), (dx, dy));
                Assert.Equal(0, RawInputListener.ConsumeMouseScroll(Mouse));

                RawInputListener.ApplyMouseRecord(Mouse, 0, RiMouseLeftDown | RiMouseWheel, 120, 5, 7, 0);
                RawInputListener.GetMouseButtons(Mouse, buttons);
                Assert.True(buttons[0]);
                RawInputListener.ConsumeMouseDelta(Mouse, out dx, out dy);
                Assert.Equal((5, 7), (dx, dy));
                Assert.Equal(120, RawInputListener.ConsumeMouseScroll(Mouse));
            }
            finally
            {
                RawInputListener.ApplyMouseRecord(Mouse, 0, RiMouseLeftUp, 0, 0, 0, 0);
                RawInputListener.ConsumeMouseDelta(RawInputListener.AggregateMouseHandle, out _, out _);
                RawInputListener.ConsumeMouseScroll(RawInputListener.AggregateMouseHandle);
            }
        }

        /// <summary>The On-Screen Keyboard sets the break bit in a record's
        /// make code (RawInputDeviceKeyboard.cpp 239-244). Its Shift's release
        /// still lands on the left Shift it pressed, where it once landed on
        /// the neutral Shift that nothing pressed and left the left one
        /// down.</summary>
        [Fact]
        public void TheOnScreenKeyboardsShift_ReleasesTheSideItPressed()
        {
            Record(0x2A, 0, 0x10);
            try
            {
                Assert.True(RawKeys()[0xA0]);
            }
            finally
            {
                Record(0xAA, RiKeyBreak, 0x10);
            }
            var keys = RawKeys();
            Assert.False(keys[0xA0]);
            Assert.False(keys[0x10]);
        }

        // ── The light gun's aim and screen ──

        /// <summary>A GunCon 2's aim crosses Remote Link marked calibrated, so
        /// the receiving PC adds no bar offset to it either.</summary>
        [Fact]
        public void AGunCon2sCalibratedAim_CrossesRemoteLink()
        {
            var s = new CustomInputState();
            SdlDeviceWrapper.ApplyGunCon2(s, 300, 130, GunCon2Calibration.Default);
            var caps = new CustomInputStateCodec.Caps(gyro: false, accel: false);
            var rt = CustomInputStateCodec.Decode(CustomInputStateCodec.Encode(s, caps));
            Assert.True(rt.Ir.Detected);
            Assert.True(rt.Ir.Calibrated);
            Assert.Equal(s.Ir.Y, rt.Ir.Y);

            var prev = SourceCoercion.IrTuningProvider;
            try
            {
                SourceCoercion.IrTuningProvider = (dev, slot) => (-0.3f, 0f);
                var src = new MappingSource { Descriptor = "IR Pointer Y", DeviceGuid = "link-gun" };
                SourceCoercion.BeginPollFrame();
                Assert.Equal(0f, ReadIr(rt, src, 12), precision: 5);
            }
            finally
            {
                SourceCoercion.IrTuningProvider = prev;
                SourceCoercion.ForgetIrPointerForDevice("link-gun");
            }
        }

        /// <summary>The Light Gun section shows for a GunCon 2 and for a Wii
        /// Remote with an IR camera, online or offline, and never on a gun
        /// shared over Remote Link, which is calibrated on the PC it is
        /// plugged into (#248).</summary>
        [Fact]
        public void TheLightGunSection_ShowsForGunsOnThisPcOnly()
        {
            static bool Shows(ushort vid, ushort pid, string name, string path)
            {
                var ud = new UserDevice { VendorId = vid, ProdId = pid, ProductName = name, DevicePath = path };
                return DeviceRowViewModel.ComputeShowGunCalibration(ud.IsGunCon2 || ud.HasIrCamera, ud.DevicePath);
            }
            const string Local = @"\\?\HID#VID_0000&PID_0000#1", Peer = "peer://a1b2c3/7";
            Assert.True(Shows(0x0B9A, 0x016A, "GunCon 2", Local));
            Assert.True(Shows(0x0B9A, 0x016A, "GunCon 2", ""));             // offline, no path
            Assert.True(Shows(0x057E, 0x0306, "Nintendo Wii Remote", Local));
            Assert.False(Shows(0x0B9A, 0x016A, "GunCon 2", Peer));
            Assert.False(Shows(0x057E, 0x0306, "Nintendo Wii Remote", Peer));
            Assert.False(Shows(0x045E, 0x0B12, "Xbox Series X Controller", Local));
        }

        /// <summary>The calibration screen ends when its gun leaves: the device
        /// goes offline, or a reconnect hands the device a new wrapper while
        /// the screen still reads the old one, as a Wii Remote's extension
        /// change does when it reopens in place.</summary>
        [Fact]
        public void TheCalibrationScreen_EndsWhenTheGunLeaves()
        {
            var first = new SdlDeviceWrapper();
            var second = new SdlDeviceWrapper();
            try
            {
                var device = new UserDevice();
                foreach (var gun in new[] { CalibrationGun.ForWiiRemote(first), CalibrationGun.ForGunCon2(device, first) })
                {
                    device.IsOnline = true;
                    device.Device = first;
                    Assert.False(GunCalibrationScreen.ShotSourceGone(device, gun));
                    device.Device = second;
                    Assert.True(GunCalibrationScreen.ShotSourceGone(device, gun));
                    device.Device = first;
                    device.IsOnline = false;
                    Assert.True(GunCalibrationScreen.ShotSourceGone(device, gun));
                }
                Assert.Contains("if (ShotSourceGone(_device, _gun))", RepoText("PadForge.App/Views/GunCalibrationScreen.cs"));
            }
            finally
            {
                first.Dispose();
                second.Dispose();
            }
        }

        // ── Numpad Enter as a Keyboard + Mouse output ──

        /// <summary>Numpad Enter has a row, and goes out as VK_RETURN with the
        /// extended flag. Every other index is its own VK.</summary>
        [Fact]
        public void NumpadEnter_GoesOutAsTheExtendedReturn()
        {
            Assert.Equal("Numpad Enter", SDL3.SDL.VirtualKeyName[RawInputListener.NumpadEnterKey]);
            Assert.Equal(((ushort)0x0D, true), InputHookManager.OutputKey(RawInputListener.NumpadEnterKey));
            Assert.Equal(((ushort)0x0D, false), InputHookManager.OutputKey(0x0D));
            Assert.Equal(((ushort)0x2D, true), InputHookManager.OutputKey(0x2D));
            Assert.Equal(((ushort)0x41, false), InputHookManager.OutputKey(0x41));

            var codes = (byte[])typeof(InputManager)
                .GetField("KbmKeyVkCodes", BindingFlags.NonPublic | BindingFlags.Static)
                .GetValue(null);
            Assert.Contains((byte)0x88, codes);
        }

        /// <summary>The key events both emitters build, down and up: Numpad
        /// Enter's index goes out as VK_RETURN with Enter's scan code and the
        /// extended flag, the way the keyboard reports it, and an ordinary key
        /// goes out as itself.</summary>
        [Fact]
        public void BothEmitters_SendNumpadEnterAsTheExtendedReturn()
        {
            const uint KeyUp = 0x0002, Extended = 0x0001;
            const ushort ScanEnter = 0x1C, ScanA = 0x1E;
            foreach (var send in new Func<ushort, bool, (ushort Vk, ushort Scan, uint Flags)>[]
            {
                (vk, down) => KeyboardMouseVirtualController.KeyEvent(vk, down),
                (vk, down) => InputManager.MacroKeyEvent(vk, keyUp: !down),
            })
            {
                Assert.Equal(((ushort)0x0D, ScanEnter, Extended), send(0x88, true));
                Assert.Equal(((ushort)0x0D, ScanEnter, KeyUp | Extended), send(0x88, false));
                Assert.Equal(((ushort)0x0D, ScanEnter, 0u), send(0x0D, true));
                Assert.Equal(((ushort)0x0D, ScanEnter, KeyUp), send(0x0D, false));
                Assert.Equal(((ushort)0x41, ScanA, 0u), send(0x41, true));
                Assert.Equal(((ushort)0x41, ScanA, KeyUp), send(0x41, false));
            }
            Assert.Contains("var (outVk, scan, flags) = KeyEvent(vk, down);",
                RepoText("PadForge.App/Common/Input/KeyboardMouseVirtualController.cs"));
            Assert.Contains("var (vk, scanCode, flags) = MacroKeyEvent(virtualKeyCode, keyUp);",
                RepoText("PadForge.App/Common/Input/InputManager.Step4b.EvaluateMacros.cs"));
        }

        /// <summary>A keyboard or a mouse shared over Remote Link names its
        /// objects the way the PC it is plugged into does. The owner ships no
        /// objects for either, and the receiver named a keyboard's keys and a
        /// mouse's controls as a gamepad's: key 0 read "A".</summary>
        [Fact]
        public void APeerKeyboardAndMouse_NameTheirObjectsLikeLocalOnes()
        {
            DeviceObjectItem[] Objects(int type, int buttons) => new RemotePeerDevice(new RemotePeerDeviceInfo
            {
                PeerFingerprintHex = "a1b2c3",
                PeerLocalDeviceId = "dev" + type,
                InputDeviceType = type,
                NumButtons = buttons,
                RawButtonCount = buttons,
            }).GetDeviceObjects();

            var keyboard = Objects(InputDeviceType.Keyboard, 256);
            var local = SdlKeyboardWrapper.KeyObjects(Enumerable.Range(0, 256).ToArray());
            Assert.Equal(local.Select(o => (o.InputIndex, o.Name, o.ObjectTypeGuid)),
                keyboard.Select(o => (o.InputIndex, o.Name, o.ObjectTypeGuid)));
            Assert.Contains(keyboard, o => o.InputIndex == 0x88 && o.Name == "Numpad Enter");
            Assert.Contains(keyboard, o => o.InputIndex == 0x41 && o.Name == "A");

            var mouse = Objects(InputDeviceType.Mouse, 5);
            Assert.Equal(SdlMouseWrapper.MouseObjects().Select(o => (o.InputIndex, o.Name, o.ObjectTypeGuid, o.ObjectType)),
                mouse.Select(o => (o.InputIndex, o.Name, o.ObjectTypeGuid, o.ObjectType)));

            // A gamepad keeps its own labels: its button 136 is a button.
            var pad = Objects(InputDeviceType.Gamepad, 140);
            Assert.Contains(pad, o => o.InputIndex == 0x88 && o.ObjectTypeGuid == ObjectGuid.Button && o.Name != "Numpad Enter");
        }
    }

    /// <summary>Delta-audit 2026-10-03: an Invert on Hold modifier keeps the
    /// descriptor and gates its source had, which are no input to it. The
    /// Combined D-Pad row, the consumed-key set, steering feedback, the
    /// Sticks tab's mouse preview and the controller annotations took them
    /// for one.</summary>
    [Collection("SettingsManagerStatics")]
    public class AuditDelta20261003ModifierTests : IDisposable
    {
        private const int Slot = 6;
        private readonly SettingsCollection _settings = SettingsManager.UserSettings;
        private readonly DeviceCollection _devices = SettingsManager.UserDevices;
        private readonly MappingSet[] _sets = SettingsManager.SlotMappingSets;

        public AuditDelta20261003ModifierTests()
        {
            SettingsManager.UserSettings = new SettingsCollection();
            SettingsManager.UserDevices = new DeviceCollection();
            SettingsManager.SlotMappingSets = new MappingSet[InputManager.MaxPads];
        }

        public void Dispose()
        {
            SettingsManager.UserSettings = _settings;
            SettingsManager.UserDevices = _devices;
            SettingsManager.SlotMappingSets = _sets;
        }

        private static MappingRow Row(string target, params MappingSource[] sources)
        {
            var row = new MappingRow { Target = target, LayerMask = "Base" };
            row.Sources.AddRange(sources);
            return row;
        }

        /// <summary>A modifier on the Combined D-Pad row presses nothing. One
        /// that kept "POV 0" pressed D-Pad Up while the hat pointed up.</summary>
        [Fact]
        public void TheCombinedDpad_ReadsNoModifier()
        {
            var apply = typeof(InputManager).GetMethod("ApplyMappingSetToGamepad",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.NotNull(apply);
            var state = new CustomInputState();
            state.Povs[0] = 0; // up
            bool Up(MappingSource src)
            {
                var ms = new MappingSet();
                ms.Rows.Add(Row("DPad", src));
                InputManager.GetSlotSourceKindRuntime(Slot).FrameSeq++;
                var args = new object[] { state, ms, "", 50, Slot, new Gamepad() };
                apply.Invoke(null, args);
                return (((Gamepad)args[5]).Buttons & Gamepad.DPAD_UP) != 0;
            }
            Assert.True(Up(new MappingSource { Descriptor = "POV 0" }));
            Assert.False(Up(new MappingSource { Descriptor = "POV 0", Kind = "InvertOnHold" }));
        }

        /// <summary>Consume Mapped Inputs swallows a modifier's own key and
        /// not the descriptor it kept, which no mapping reads.</summary>
        [Fact]
        public void TheConsumedKeys_SkipAModifiersOldDescriptor()
        {
            var collect = typeof(InputService).GetMethod("CollectSuppressedInputs",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(collect);
            var g = Guid.NewGuid();
            var ud = new UserDevice();
            ud.LoadInstance(g, "Keyboard", g, "Keyboard");
            ud.LoadCapabilities(0, 256, 0, InputDeviceType.Keyboard);
            SettingsManager.UserDevices.Items.Add(ud);
            var setting = new UserSetting { InstanceGuid = g, ProductGuid = g, MapTo = Slot };
            setting.SetPadSetting(new PadSetting());
            SettingsManager.UserSettings.Items.Add(setting);
            var ms = new MappingSet();
            ms.Rows.Add(Row("ButtonA", new MappingSource { Descriptor = "Button 65", DeviceGuid = g.ToString() }));
            ms.Rows.Add(Row("LeftThumbAxisX", new MappingSource
            {
                Descriptor = "Button 66", Kind = "InvertOnHold", ParamModifier = "Button 67", DeviceGuid = g.ToString(),
                GateDescriptor = "Button 68", Gate2Descriptor = "Button 69",
            }));
            // A Direct source's gate is its input, and stays consumed.
            ms.Rows.Add(Row("ButtonB", new MappingSource
            {
                Descriptor = "Button 71", GateDescriptor = "Button 70", DeviceGuid = g.ToString(),
            }));
            SettingsManager.SlotMappingSets[Slot] = ms;

            var keys = new HashSet<int>();
            collect.Invoke(null, new object[] { ud, keys, new HashSet<int>() });
            Assert.Contains(65, keys);
            Assert.Contains(67, keys);
            Assert.Contains(70, keys);
            Assert.Contains(71, keys);
            Assert.DoesNotContain(66, keys);
            Assert.DoesNotContain(68, keys);
            Assert.DoesNotContain(69, keys);
        }

        /// <summary>A row's mapped chip counts its own descriptor only while
        /// the primary is a descriptor kind. An Invert On Hold primary keeps
        /// the descriptor of its earlier kind, which feeds nothing, so with no
        /// modifier key and an unbound secondary the row is unmapped. The
        /// secondary is what lets the primary be a modifier at all.</summary>
        [Fact]
        public void ARowsChip_CountsTheDescriptorOnlyForADescriptorKind()
        {
            var row = new MappingItem("A", "ButtonA", MappingCategory.Buttons);
            row.SourceDescriptor = "Button 65";
            Assert.True(row.HasAnySource);
            row.AddExtraSourceCommand.Execute(null);
            row.PrimaryKindSource.Kind = "InvertOnHold";
            Assert.Equal("InvertOnHold", row.PrimaryKindSource.Kind);
            Assert.False(row.HasAnySource);
            row.PrimaryKindSource.ParamModifier = "Button 67";
            Assert.True(row.HasAnySource);
        }

        /// <summary>The Sticks tab's preview of a stick a mouse drives skips a
        /// modifier. One that kept "Mouse Position X" showed the cursor's
        /// place on the stick while the engine read the row's other
        /// source.</summary>
        [Fact]
        public void TheMouseStickPreview_ReadsNoModifier()
        {
            var value = typeof(InputService).GetMethod("MouseCursorStickValue",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(value);
            var prevCursor = SourceCoercion.MouseCursorProvider;
            try
            {
                SourceCoercion.MouseCursorProvider = () => (0.9f, 0.5f);
                short Read(MappingSource src)
                {
                    var ms = new MappingSet();
                    ms.Rows.Add(Row("LeftThumbAxisX", src));
                    return (short)value.Invoke(null, new object[] { ms, "LeftThumbAxisX", "", new CustomInputState(), Slot });
                }
                Assert.NotEqual((short)0, Read(new MappingSource { Descriptor = "Mouse Position X" }));
                Assert.Equal((short)0, Read(new MappingSource { Descriptor = "Mouse Position X", Kind = "InvertOnHold" }));
            }
            finally
            {
                SourceCoercion.MouseCursorProvider = prevCursor;
            }
        }

        /// <summary>A modifier's only feed is its modifier key: with none set
        /// it feeds nothing, whatever descriptor it kept, and both controller
        /// previews annotate only that key.</summary>
        [Fact]
        public void AModifiersOnlyFeed_IsItsModifierKey()
        {
            var modifier = new MappingSourceItem { Kind = "InvertOnHold", Descriptor = "Button 65" };
            Assert.False(modifier.HasAnyBoundFeed);
            modifier.ParamModifier = "Button 67";
            Assert.True(modifier.HasAnyBoundFeed);
            Assert.True(new MappingSourceItem { Descriptor = "Button 65" }.HasAnyBoundFeed);

            foreach (var view in new[] { "ControllerModelView", "ControllerModel2DView" })
            {
                string src = AuditDelta20261003EngineTests.RepoText("PadForge.App/Views/" + view + ".Annotations.cs");
                Assert.Contains("            if (src.IsInvertOnHoldKind)\n            {\n                // The descriptor a modifier kept from its source is no input.\n                AppendAnnotationParamWire(rows, src, src.ParamModifier, src.ParamModifierInputChoice);\n                return;\n            }", src);
            }
        }

        /// <summary>Steering feedback reads no modifier. One that kept a
        /// Motion Lean descriptor served its old lock state as feedback.
        /// The feedback runs only with a live steering device, so pin the
        /// skip.</summary>
        [Fact]
        public void SteeringFeedback_SkipsAModifier()
        {
            string src = AuditDelta20261003EngineTests.RepoText(
                "PadForge.App/Common/Input/InputManager.Step3.SteeringLockFeedback.cs");
            Assert.Contains("if (src == null || IsRowModifierSource(src)\n                        || !(IsSteeringKind(src.Kind)", src);
        }
    }

    /// <summary>Delta-audit 2026-10-03: an imported profile's gesture
    /// families arm from what the engine reads as a gesture.</summary>
    public class AuditDelta20261003AutoArmTests
    {
        private static TouchpadGestureSettings Armed(MappingSource src, string target = "ButtonA")
            => ArmedRow(target, src);

        private static TouchpadGestureSettings ArmedRow(string target, params MappingSource[] sources)
        {
            var set = new MappingSet { Authoritative = true };
            var row = new MappingRow { Target = target };
            row.Sources.AddRange(sources);
            set.Rows.Add(row);
            return PadForge.Engine.Touchpad.TouchpadGestureAutoArm.Apply(TouchpadGestureSettings.Default(), set);
        }

        private static TouchpadGestureSettings Armed(ShiftActivator act)
        {
            var set = new MappingSet { Authoritative = true };
            set.ShiftActivators.Add(act);
            return PadForge.Engine.Touchpad.TouchpadGestureAutoArm.Apply(TouchpadGestureSettings.Default(), set);
        }

        /// <summary>A modifier arms the family its modifier input names, and
        /// the descriptor and gates it kept from its source arm nothing.
        /// Incremental and Ramp sources read their up and down keys through a
        /// reader with no gesture read, so neither those keys nor a kept
        /// descriptor arm anything. A steering or motion kind reads no
        /// gesture on a stick row and reads its descriptor as Direct
        /// elsewhere. A Direct source arms from its descriptor and its gate,
        /// and a blank one from neither. The combined D-pad row reads POV
        /// descriptors alone and no gates, so it arms nothing.</summary>
        [Fact]
        public void TheGestureAutoArm_ReadsWhatEachKindReads()
        {
            var dormant = Armed(new MappingSource
            {
                Kind = "InvertOnHold", Descriptor = "Touchpad 0 DoubleTap",
                GateDescriptor = "Touchpad 0 TouchLeft", ParamModifier = "Button 3",
            });
            Assert.False(dormant.EnableTaps);
            Assert.False(dormant.EnableTouchSpots);

            Assert.True(ArmedRow("LeftThumbAxisX", new MappingSource { Descriptor = "Axis 0" },
                new MappingSource { Kind = "InvertOnHold", ParamModifier = "Touchpad 0 SwipeUp" }).EnableFourWaySwipes);

            foreach (string kind in new[] { "Incremental", "Ramped" })
            {
                var keyed = Armed(new MappingSource
                {
                    Kind = kind, Descriptor = "Touchpad 0 DoubleTap",
                    ParamUp = "Touchpad 0 SwipeUp", ParamDown = "Touchpad 0 TouchLeft",
                }, "LeftThumbAxisX");
                Assert.False(keyed.EnableFourWaySwipes, kind);
                Assert.False(keyed.EnableTouchSpots, kind);
                Assert.False(keyed.EnableTaps, kind);
            }

            var wheel = new MappingSource
            {
                Kind = "WindingStick", Descriptor = "Touchpad 0 DoubleTap", ParamYDescriptor = "Touchpad 0 SwipeUp",
            };
            var onStick = Armed(wheel, "LeftThumbAxisX");
            Assert.False(onStick.EnableTaps);
            Assert.False(onStick.EnableFourWaySwipes);
            var onButton = Armed(wheel);
            Assert.True(onButton.EnableTaps);
            Assert.False(onButton.EnableFourWaySwipes);
            Assert.False(Armed(new MappingSource { Kind = "MotionLeanX", Descriptor = "Touchpad 0 DoubleTap" },
                "LeftThumbAxisX").EnableTaps);

            var direct = Armed(new MappingSource { Descriptor = "Touchpad 0 DoubleTap", GateDescriptor = "Touchpad 0 TouchLeft" });
            Assert.True(direct.EnableTaps);
            Assert.True(direct.EnableTouchSpots);
            Assert.True(Armed(new MappingSource { Descriptor = "Touchpad 0 StickX" }, "LeftThumbAxisX")
                .EnableJoystickOutput);
            Assert.False(Armed(new MappingSource { GateDescriptor = "Touchpad 0 TouchLeft" }).EnableTouchSpots);

            var dpad = ArmedRow("DPad", new MappingSource { Descriptor = "POV 0", GateDescriptor = "Touchpad 0 TouchLeft" },
                new MappingSource { Descriptor = "Touchpad 0 DoubleTap" });
            Assert.False(dpad.EnableTouchSpots);
            Assert.False(dpad.EnableTaps);
        }

        /// <summary>An activator arms from what its kind and mode read: the
        /// input and its second AND companion, the second input for a Chord
        /// alone, the AND gate for an Axis activator alone, and the Previous
        /// button in Cycle mode alone. One with no input, or a Passive layer,
        /// reads nothing of its own. A Chord changed to a Button keeps its
        /// second input, which then arms nothing.</summary>
        [Fact]
        public void AnActivatorArmsWhatItsKindAndModeRead()
        {
            static ShiftActivator Act(string kind, string mode = "Hold", string input = "Button 0") => new()
            {
                Kind = kind, Mode = mode, Descriptor = input,
                ChordSecondDescriptor = "Touchpad 0 SwipeUp",
                GateDescriptor = "Touchpad 0 TouchLeft",
                CyclePrevDescriptor = "Touchpad 0 DoubleTap",
            };

            var button = Armed(Act("Button"));
            Assert.False(button.EnableFourWaySwipes);
            Assert.False(button.EnableTouchSpots);
            Assert.False(button.EnableTaps);

            var chord = Armed(Act("Chord"));
            Assert.True(chord.EnableFourWaySwipes);
            Assert.False(chord.EnableTouchSpots);

            var axis = Armed(Act("Axis", input: "Axis 0"));
            Assert.True(axis.EnableTouchSpots);
            Assert.False(axis.EnableFourWaySwipes);

            Assert.True(Armed(Act("Button", "Cycle")).EnableTaps);
            Assert.False(Armed(Act("Chord", input: "")).EnableFourWaySwipes);
            Assert.True(Armed(Act("Chord", "Cycle", input: "")).EnableTaps);

            var companion = Act("Button");
            companion.Gate2Descriptor = "Touchpad 0 SwipeDown";
            Assert.True(Armed(companion).EnableFourWaySwipes);
            companion.Descriptor = "";
            Assert.False(Armed(companion).EnableFourWaySwipes);

            var passive = Armed(Act("Chord", "Passive", input: "Touchpad 0 SwipeDown"));
            Assert.False(passive.EnableFourWaySwipes);
            Assert.False(passive.EnableTaps);
        }
    }

    /// <summary>Delta-audit 2026-10-03: a held chord prefix replays as the
    /// physical key it was.</summary>
    public class AuditDelta20261003ChordReplayTests
    {
        private const uint KeyUp = 0x0002, Extended = 0x0001;

        /// <summary>Numpad Enter and Enter share VK_RETURN, and only the scan
        /// code and the extended flag tell them apart, so a held Numpad Enter
        /// replayed as Enter. A second Enter going down while the first is
        /// held is a repeat to the engine and leaves the held key's identity
        /// alone.</summary>
        [Fact]
        public void AHeldPrefix_ReplaysAsThePhysicalKeyItWas()
        {
            const int Return = 0x0D, L = 0x4C;
            int numpadEnter = InputHookManager.ReplayIdentity(0x1C, 0x01);
            int enter = InputHookManager.ReplayIdentity(0x1C, 0x00);
            Assert.Equal(0x11C, numpadEnter);
            Assert.Equal(0x01C, enter);
            Assert.Equal(0x01C, InputHookManager.ReplayIdentity(0x1C, 0x10)); // injected, not extended

            var e = new HandheldChordEngine();
            e.SetChords(new[] { new HandheldChordDefinition { Name = "Enter L", Button = 3, Keys = new[] { Return, L } } });
            Assert.Equal(ChordDecision.Swallow, e.OnEvent(Return, true, 0, numpadEnter));
            Assert.Equal(ChordDecision.Swallow, e.OnEvent(Return, true, 5, enter));
            Assert.Equal(ChordDecision.Swallow, e.OnEvent(Return, false, 10, numpadEnter));
            Assert.Equal(new[] { (Return, true, numpadEnter), (Return, false, numpadEnter) }, e.PendingReplays);

            // A prefix held past the hold time replays the same way.
            var timed = new HandheldChordEngine();
            timed.SetChords(new[] { new HandheldChordDefinition { Name = "Enter L", Button = 3, Keys = new[] { Return, L } } });
            Assert.Equal(ChordDecision.Swallow, timed.OnEvent(Return, true, 0, numpadEnter));
            timed.Tick(HandheldChordEngine.HoldMs);
            Assert.Equal(new[] { (Return, true, numpadEnter) }, timed.PendingReplays);
        }

        /// <summary>A replay types the captured key: its scan code, with the
        /// extended flag when the hook saw it. With nothing captured it falls
        /// back to the VK's own scan code and the extended-key table, and the
        /// Win mask's reserved 0xFF has neither.</summary>
        [Fact]
        public void AReplay_TypesTheCapturedKey()
        {
            Assert.Equal(((ushort)0x1C, Extended), InputHookManager.ReplayKey(0x0D, true, 0x11C));
            Assert.Equal(((ushort)0x1C, KeyUp | Extended), InputHookManager.ReplayKey(0x0D, false, 0x11C));
            Assert.Equal(((ushort)0x1C, 0u), InputHookManager.ReplayKey(0x0D, true, 0x01C));
            // PrintScreen reaches the hook as scan 0x37 with the flag.
            Assert.Equal(((ushort)0x37, Extended),
                InputHookManager.ReplayKey(0x2C, true, InputHookManager.ReplayIdentity(0x37, 0x01)));

            var (scan, flags) = InputHookManager.ReplayKey(0x2D, true, 0); // Insert
            Assert.NotEqual((ushort)0, scan);
            Assert.Equal(Extended, flags);
            Assert.Equal(KeyUp, InputHookManager.ReplayKey(0x41, false, 0).Flags);
            Assert.Equal(((ushort)0, 0u), InputHookManager.ReplayKey(0xFF, true, 0));
        }
    }

    /// <summary>Delta-audit 2026-10-03 contracts that switch the UI
    /// language.</summary>
    [Collection("CultureSwitching")]
    public class AuditDelta20261003CultureTests
    {
        /// <summary>A language change updates the Light Gun section's status
        /// line and the Calibrate button's tooltip, as it updates the row's
        /// other localized text.</summary>
        [Fact]
        public void TheGunSection_FollowsALanguageChange()
        {
            var before = System.Globalization.CultureInfo.CurrentUICulture;
            try
            {
                Strings.ChangeCulture(System.Globalization.CultureInfo.GetCultureInfo("en"));
                var row = new DeviceRowViewModel { GunIsWiiRemote = true };
                string english = row.GunCalibrateTooltip;
                var raised = new List<string>();
                row.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

                Strings.ChangeCulture(System.Globalization.CultureInfo.GetCultureInfo("es"));
                Assert.Contains(nameof(DeviceRowViewModel.GunCalibrationStatus), raised);
                Assert.Contains(nameof(DeviceRowViewModel.GunCalibrateTooltip), raised);
                Assert.Equal(Strings.Instance.Devices_WiiCalibrateTooltip, row.GunCalibrateTooltip);
                Assert.NotEqual(english, row.GunCalibrateTooltip);
            }
            finally
            {
                Strings.ChangeCulture(before);
            }
        }

        /// <summary>Numpad Enter takes the localized numpad name wherever a
        /// key is named: the Keyboard + Mouse grid's row, the SOCD key list,
        /// and the keyboard's object name as the source picker shows it.</summary>
        [Fact]
        public void NumpadEnter_IsNamedInTheLanguage()
        {
            var before = System.Globalization.CultureInfo.CurrentUICulture;
            try
            {
                Strings.ChangeCulture(System.Globalization.CultureInfo.GetCultureInfo("es"));
                string name = string.Format(Strings.Instance.Key_Numpad, Strings.Instance.Key_Enter);
                Assert.NotEqual("Numpad Enter", name);

                var vm = RestoredPad.Build(0, VirtualControllerType.KeyboardMouse);
                Assert.Equal(name, vm.Mappings.Single(m => m.TargetSettingName == "KbmKey88").TargetLabel);
                Assert.Contains(KbmSlotConfig.GetKeyOptions(), o => o.Vk == 0x88 && o.Label == name);
                Assert.Equal(name, MappingDisplayResolver.LocalizeObjectName(SDL3.SDL.VirtualKeyName[0x88]));
            }
            finally
            {
                Strings.ChangeCulture(before);
            }
        }
    }
}
