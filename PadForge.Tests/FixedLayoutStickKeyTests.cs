using System;
using System.Collections.Generic;
using System.Threading;
using HIDMaestro;
using PadForge.Common.Input;
using PadForge.Engine;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// An Xbox or PlayStation slot hands HIDMaestro its sticks and triggers
    /// at the keys the profile gives them. HIDMaestro's descriptor lane reads
    /// every declared axis by its key (HidReportBuilder.BuildReportInto), and
    /// its own reference for filling a state, HMGamepadStateHelpers.StandardAxes,
    /// puts each stick on the profile's own stick axes and never lets a
    /// trigger land on an axis a stick holds: "a profile whose layout declares
    /// no triggers and whose right stick sits on the canonical trigger axes
    /// (switch-pro-usb, dualshock-3, logitech-f310: Z and Rz) keeps its right
    /// stick, and the trigger values go nowhere."
    ///
    /// <para>The plain DualShock 3 (SIXAXIS) profile has no axis map, its
    /// right stick on Z and Rz and no triggers. PadForge took the right stick
    /// to Rx and Ry and the triggers to Z and Rz, so the pad's right stick
    /// followed the triggers and the right stick itself went nowhere.</para>
    ///
    /// <para>A trigger is kept off a stick the profile declares, and only
    /// such a stick: a stick the profile does not declare takes the XInput
    /// convention and gives way to a declared trigger on the same key.</para>
    /// </summary>
    public class FixedLayoutStickKeyTests
    {
        private sealed class Recorder : IHMaestroSubmitTarget
        {
            private HMGamepadState? _last;

            public void SubmitState(in HMGamepadState state)
            {
                var copy = state;
                copy.Axes = new Dictionary<HMAxis, float>(state.Axes);
                lock (this) _last = copy;
            }

            public void SubmitRawReport(ReadOnlySpan<byte> report) { }
            public void SubmitRawExtendedReport(ReadOnlySpan<byte> report) { }

            public HMGamepadState Last()
            {
                long until = Environment.TickCount64 + 5000;
                while (true)
                {
                    lock (this)
                        if (_last != null) return _last.Value;
                    if (Environment.TickCount64 > until) throw new TimeoutException("no state reached HIDMaestro");
                    Thread.Sleep(1);
                }
            }
        }

        private static float At(HMGamepadState s, HMAxis axis, float rest)
            => s.Axes.TryGetValue(axis, out float v) ? v : rest;

        [Fact]
        public void EveryXboxAndPlayStationProfile_GetsItsSticksAndTriggersAtItsOwnKeys()
        {
            // Left stick full right and half up, right stick a quarter left,
            // the left trigger at full pull: four values no two of which are
            // alike, so a stick on the wrong key or a trigger on a stick's key
            // shows.
            var gp = new Gamepad
            {
                ThumbLX = 32767, ThumbLY = 16384, ThumbRX = -8192, ThumbRY = 0,
                LeftTrigger = 65535, RightTrigger = 0,
            };
            var wrong = new List<string>();
            int seen = 0;
            foreach (var (type, list) in new[]
                     {
                         (VirtualControllerType.Xbox, HMaestroProfileCatalog.XboxProfiles),
                         (VirtualControllerType.PlayStation, HMaestroProfileCatalog.PlayStationProfiles),
                     })
            {
                foreach (var p in list)
                {
                    // A USB/IP persona's Connect registers it with Step 1's
                    // persona scope, which other tests read.
                    if (p.RequiresUsbipBackend) continue;
                    var rec = new Recorder();
                    var shell = (HMContext)System.Runtime.CompilerServices.RuntimeHelpers
                        .GetUninitializedObject(typeof(HMContext));
                    var vc = new HMaestroVirtualController(shell, p, type)
                    {
                        CreateControllerSeam = (_, _) => null,
                        SubmitTargetSeam = () => rec,
                    };
                    try
                    {
                        vc.Connect();
                        vc.SubmitGamepadState(gp);
                        var s = rec.Last();
                        seen++;
                        var sticks = p.Sticks;
                        void Check(string name, HMAxis axis, float want)
                        {
                            if (axis == HMAxis.None) return;
                            float got = At(s, axis, 0.5f);
                            if (Math.Abs(got - want) > 0.01f) wrong.Add($"{p.Id} {name} at {axis}: {got:0.000}, want {want:0.000}");
                        }
                        if (sticks.Count > 0)
                        {
                            Check("left stick X", sticks[0].XAxis, 1.0f);
                            Check("left stick Y", sticks[0].YAxis, 0.25f);
                        }
                        if (sticks.Count > 1)
                        {
                            Check("right stick X", sticks[1].XAxis, 0.375f);
                            Check("right stick Y", sticks[1].YAxis, 0.5f);
                        }
                        // A declared trigger keeps its own key too. The Xbox
                        // 360 flight stick declares one stick and its
                        // throttle and rudder on Rx and Ry, where an
                        // undeclared right stick must not land over them.
                        var triggers = p.Triggers;
                        if (triggers.Count > 0) Check("left trigger", triggers[0].Axis, 1.0f);
                        if (triggers.Count > 1) Check("right trigger", triggers[1].Axis, 0.0f);
                    }
                    finally { vc.Dispose(); }
                }
            }
            Assert.True(seen > 20, $"only {seen} profiles reached the check");
            Assert.True(wrong.Count == 0, string.Join(" | ", wrong));
        }
    }
}
