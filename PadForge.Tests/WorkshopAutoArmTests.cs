using System.Linq;
using PadForge.Engine.Data;
using PadForge.Engine.Touchpad;
using PadForge.Services;
using PadForge.SteamWorkshop.Translation;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Translator v14 self-arming imports: a Workshop profile's
    /// authoritative MappingSet references gated touchpad gesture
    /// descriptors, and the engine's gesture gate treats a referenced
    /// descriptor as enabled (gate = user toggle OR referenced-by-mapping,
    /// TouchpadGestureAutoArm). These tests pin the arming table, the
    /// authoritative scoping that keeps manual Touchpad-tab toggles
    /// meaningful, and the runtime proof that a gated gesture read fires
    /// for an imported mapping with every Touchpad-tab toggle off.
    /// </summary>
    public class WorkshopAutoArmTests
    {
        private static MappingSet AuthoritativeSet(params string[] descriptors)
        {
            var set = new MappingSet { Authoritative = true };
            foreach (var d in descriptors)
            {
                set.Rows.Add(new MappingRow
                {
                    Target = "ButtonA",
                    Sources = { new MappingSource { Descriptor = d } },
                });
            }
            return set;
        }

        // ─── Arming table ────────────────────────────────────────────────

        [Fact]
        public void SwipeDescriptor_ArmsMasterAndFourWaySwipes()
        {
            var armed = TouchpadGestureAutoArm.Apply(
                TouchpadGestureSettings.Default(), AuthoritativeSet("Touchpad 0 SwipeUp"));
            Assert.True(armed.Enabled);
            Assert.True(armed.EnableFourWaySwipes);
            Assert.False(armed.EnableTaps);
            Assert.False(armed.EnableTouchSpots);
            Assert.False(armed.EnableJoystickOutput);
        }

        [Fact]
        public void DiagonalSwipe_ArmsEightWay()
        {
            var armed = TouchpadGestureAutoArm.Apply(
                TouchpadGestureSettings.Default(), AuthoritativeSet("Touchpad 1 SwipeNE"));
            Assert.True(armed.Enabled);
            Assert.True(armed.EnableEightWaySwipes);
            Assert.False(armed.EnableFourWaySwipes);
        }

        [Fact]
        public void DoubleTapDescriptor_ArmsTaps()
        {
            var armed = TouchpadGestureAutoArm.Apply(
                TouchpadGestureSettings.Default(), AuthoritativeSet("Touchpad 1 DoubleTap"));
            Assert.True(armed.Enabled);
            Assert.True(armed.EnableTaps);
        }

        [Fact]
        public void TouchSpotDescriptor_ArmsTouchSpots()
        {
            var armed = TouchpadGestureAutoArm.Apply(
                TouchpadGestureSettings.Default(), AuthoritativeSet("Touchpad 0 TouchLeft"));
            Assert.True(armed.Enabled);
            Assert.True(armed.EnableTouchSpots);
        }

        [Fact]
        public void JoystickDescriptors_ArmJoystickOutputWithoutMaster()
        {
            // StickX and the D-pad wedges ride the joystick lane, which is
            // independent of the gesture master switch.
            var armed = TouchpadGestureAutoArm.Apply(
                TouchpadGestureSettings.Default(),
                AuthoritativeSet("Touchpad 0 StickX", "Touchpad 0 DPadUp"));
            Assert.True(armed.EnableJoystickOutput);
            Assert.False(armed.Enabled);
        }

        [Fact]
        public void DPadWedge_UnOffsTheDPadMode()
        {
            var resolved = TouchpadGestureSettings.Default();
            resolved.JoystickDPadMode = "Off";
            var armed = TouchpadGestureAutoArm.Apply(
                resolved, AuthoritativeSet("Touchpad 0 DPadLeft"));
            Assert.True(armed.EnableJoystickOutput);
            Assert.Equal("FourWay", armed.JoystickDPadMode);
        }

        [Fact]
        public void ChordActivatorLeg_ArmsTouchSpots()
        {
            // The single-pad half click materializes as a Kind=Chord
            // activator whose second leg reads the half's touch spot.
            var set = new MappingSet { Authoritative = true };
            set.ShiftActivators.Add(new ShiftActivator
            {
                Descriptor = "Touchpad 0 Click",
                ChordSecondDescriptor = "Touchpad 0 TouchRight",
                Kind = "Chord",
            });
            var armed = TouchpadGestureAutoArm.Apply(TouchpadGestureSettings.Default(), set);
            Assert.True(armed.Enabled);
            Assert.True(armed.EnableTouchSpots);
        }

        [Fact]
        public void MacroTriggerDescriptors_ArmTheirFamilies()
        {
            var armed = TouchpadGestureAutoArm.Apply(
                TouchpadGestureSettings.Default(),
                new MappingSet { Authoritative = true },
                new[] { "Touchpad 0 SwipeLeft", "Touchpad 0 DPadUp" });
            Assert.True(armed.Enabled);
            Assert.True(armed.EnableFourWaySwipes);
            Assert.True(armed.EnableJoystickOutput);
        }

        [Fact]
        public void CustomOnlyMode_WidensToBoth_WhenInBoxFamilyReferenced()
        {
            var resolved = TouchpadGestureSettings.Default();
            resolved.Mode = "CustomOnly";
            var armed = TouchpadGestureAutoArm.Apply(
                resolved, AuthoritativeSet("Touchpad 0 SwipeDown"));
            Assert.Equal("Both", armed.Mode);
        }

        // ─── Scoping ─────────────────────────────────────────────────────

        [Fact]
        public void NonAuthoritativeSet_NeverArms()
        {
            // Manual mappings keep the Touchpad tab as the single gate:
            // the same descriptors on a non-authoritative set change
            // nothing (documented slot fan-out contract).
            var set = AuthoritativeSet("Touchpad 0 SwipeUp", "Touchpad 0 StickX");
            set.Authoritative = false;
            var resolved = TouchpadGestureSettings.Default();
            var armed = TouchpadGestureAutoArm.Apply(resolved, set);
            Assert.Same(resolved, armed);
            Assert.False(armed.Enabled);
            Assert.False(armed.EnableJoystickOutput);
        }

        [Fact]
        public void NonGestureAndUnknownDescriptors_ArmNothing()
        {
            var resolved = TouchpadGestureSettings.Default();
            var armed = TouchpadGestureAutoArm.Apply(resolved, AuthoritativeSet(
                "Gamepad ButtonA", "Touchpad 0 Click", "Touchpad 0 Finger 0 X",
                "Touchpad 0 Pointer X", "Touchpad 0 Custom_MyShape"));
            Assert.Same(resolved, armed);
        }

        [Fact]
        public void AlreadyEnabledFeatures_ReturnTheSameInstance()
        {
            var resolved = TouchpadGestureSettings.Default();
            resolved.Enabled = true;
            resolved.EnableFourWaySwipes = true;
            var armed = TouchpadGestureAutoArm.Apply(
                resolved, AuthoritativeSet("Touchpad 0 SwipeUp"));
            Assert.Same(resolved, armed);
        }

        [Fact]
        public void UserTuningSurvivesArming()
        {
            // Arming clones and flips toggles only. Thresholds the user
            // dialed in stay.
            var resolved = TouchpadGestureSettings.Default();
            resolved.SwipeDistanceThreshold = 0.25f;
            resolved.CooldownMs = 42;
            var armed = TouchpadGestureAutoArm.Apply(
                resolved, AuthoritativeSet("Touchpad 0 SwipeUp"));
            Assert.NotSame(resolved, armed);
            Assert.Equal(0.25f, armed.SwipeDistanceThreshold);
            Assert.Equal(42, armed.CooldownMs);
            Assert.False(resolved.Enabled); // the input instance is untouched
        }

        // ─── Runtime proof ───────────────────────────────────────────────

        /// <summary>Drives the real recognizer with a synthetic up-swipe
        /// under the armed settings of a materialized import and asserts
        /// the gated swipe fire lands, with NO Touchpad-tab toggle on. The
        /// unarmed control proves the fire is the auto-arm's doing.</summary>
        [Fact]
        public void ImportedSwipeRow_FiresGesture_WithoutAnyToggle()
        {
            // The exact shape the translator emits for a trackpad-hosted
            // 2dscroll member (pinned in GapClosureTranslationTests) and
            // the materializer stamps Authoritative on.
            var translated = new TranslatedProfile { NeedsKbmSlot = true };
            translated.KbmMappingSet.Rows.Add(new MappingRow
            {
                Target = "KbmKey74",
                Sources = { new MappingSource { Descriptor = "Touchpad 0 SwipeUp" } },
            });
            var profile = WorkshopProfileMaterializer.Materialize(translated);
            var set = profile.SlotMappingSets[0];
            Assert.True(set.Authoritative);

            var userSettings = TouchpadGestureSettings.Default();
            Assert.False(userSettings.Enabled); // no Touchpad-tab toggle anywhere
            var armed = TouchpadGestureAutoArm.Apply(userSettings, set);

            Assert.Contains("Touchpad 0 SwipeUp", RunSwipeUp(armed));
            Assert.DoesNotContain("Touchpad 0 SwipeUp", RunSwipeUp(userSettings));
        }

        /// <summary>Feeds one finger-down / drag-up / lift sequence through
        /// <see cref="GestureRecognizer.Update"/> and returns the fired set
        /// after the lift.</summary>
        private static System.Collections.Generic.IReadOnlyCollection<string> RunSwipeUp(
            TouchpadGestureSettings settings)
        {
            var ctx = new TouchpadGestureContext();
            var pad = new PadForge.Engine.TouchpadInputState(2);
            long now = 1000;

            pad.FingerDown[0] = true;
            pad.FingerContactId[0] = 1;
            pad.FingerX[0] = 0.5f;
            pad.FingerY[0] = 0.8f;
            GestureRecognizer.Update(0, ctx, pad, settings, now);

            for (int i = 1; i <= 5; i++)
            {
                now += 20;
                pad.FingerY[0] = 0.8f - 0.1f * i;
                GestureRecognizer.Update(0, ctx, pad, settings, now);
            }

            now += 10;
            pad.FingerDown[0] = false;
            pad.FingerContactId[0] = -1;
            GestureRecognizer.Update(0, ctx, pad, settings, now);

            return ctx.FiredGesturesThisFrame.ToArray();
        }
    }
}
