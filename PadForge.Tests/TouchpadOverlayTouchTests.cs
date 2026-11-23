using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using PadForge.Views;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>The Touchpad Overlay's touch slots: what the engine reads
    /// after touches come and go. The window is never shown.</summary>
    [Collection("ScreenOverlayStatics")]
    public class TouchpadOverlayTouchTests
    {
        private static void OnSta(Action body)
        {
            Exception failure = null;
            var thread = new Thread(() =>
            {
                try { body(); }
                catch (Exception e) { failure = e; }
                finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
            }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "the window run timed out");
            if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
        }

        /// <summary>A touch device that reports one fixed point.</summary>
        private sealed class FixedTouch : TouchDevice
        {
            public FixedTouch(int id) : base(id) { }

            public override TouchPoint GetTouchPoint(IInputElement relativeTo)
                => new TouchPoint(this, new Point(20, 20), new Rect(18, 18, 4, 4), TouchAction.Down);

            public override TouchPointCollection GetIntermediateTouchPoints(IInputElement relativeTo)
                => new TouchPointCollection();
        }

        private static void Touch(TouchpadOverlay overlay, string handler, TouchDevice device)
        {
            var method = typeof(TouchpadOverlay).GetMethod(handler, BindingFlags.Instance | BindingFlags.NonPublic);
            var args = new TouchEventArgs(device, 0)
            {
                RoutedEvent = handler == "OnTouchDown" ? UIElement.TouchDownEvent : UIElement.TouchUpEvent,
            };
            try { method.Invoke(overlay, new object[] { args }); }
            catch (TargetInvocationException e) { ExceptionDispatchInfo.Capture(e.InnerException).Throw(); }
        }

        /// <summary>Three fingers drag the window. The first two keep their
        /// slots through the drag, and lifting one of them ended the drag and
        /// returned before its slot was freed, so the engine read that finger
        /// resting on the pad until the overlay was hidden. A finger lifted
        /// during the drag is no tap, even right after one.</summary>
        [Fact]
        public void AFingerLiftedDuringAThreeFingerDrag_LeavesThePad()
            => OnSta(() =>
            {
                var overlay = new TouchpadOverlay();
                try
                {
                    var first = new FixedTouch(101);
                    var second = new FixedTouch(102);
                    Touch(overlay, "OnTouchDown", first);
                    Touch(overlay, "OnTouchDown", second);
                    // The third touch starts the drag. Its start reads the
                    // window's place on the screen, which a window never
                    // shown does not have, so the test leaves the state that
                    // touch leaves: its id counted and the drag on.
                    var active = (HashSet<int>)typeof(TouchpadOverlay)
                        .GetField("_activeTouchIds", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(overlay);
                    active.Add(103);
                    typeof(TouchpadOverlay).GetField("_isDragging", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(overlay, true);

                    var during = overlay.GetMultiFingerState(out _);
                    Assert.True(during.FingerDown[0]);
                    Assert.True(during.FingerDown[1]);

                    // A tap just before the drag: a lift of the first finger
                    // that counted as a tap now would pulse the click, so the
                    // click below stays off only while a lift during a drag
                    // is no tap.
                    typeof(TouchpadOverlay).GetField("_lastTapTime", BindingFlags.Instance | BindingFlags.NonPublic)
                        .SetValue(overlay, DateTime.UtcNow);

                    Touch(overlay, "OnTouchUp", first);
                    var after = overlay.GetMultiFingerState(out bool click);
                    Assert.False(after.FingerDown[0]);
                    Assert.Equal(-1, after.FingerContactId[0]);
                    Assert.True(after.FingerDown[1]);
                    Assert.False(click);
                    Assert.False((bool)typeof(TouchpadOverlay)
                        .GetField("_isDragging", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(overlay));

                    // The third finger lifts too. The second finger is alone
                    // on the pad, and the freed slot takes the next touch.
                    var third = new FixedTouch(103);
                    Touch(overlay, "OnTouchUp", third);
                    var fourth = new FixedTouch(104);
                    Touch(overlay, "OnTouchDown", fourth);
                    var last = overlay.GetMultiFingerState(out _);
                    Assert.True(last.FingerDown[0]);
                    Assert.True(last.FingerDown[1]);
                }
                finally { overlay.Close(); }
            });
    }
}
