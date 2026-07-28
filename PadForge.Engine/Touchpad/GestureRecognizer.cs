using System;
using System.Collections.Generic;
using System.Numerics;

namespace PadForge.Engine.Touchpad
{
    /// <summary>
    /// Per-tick gesture recognizer. Reads a <see cref="TouchpadInputState"/>
    /// + the slot's <see cref="TouchpadGestureSettings"/> against a
    /// per-(device, touchpad-index) <see cref="TouchpadGestureContext"/>
    /// and populates <see cref="TouchpadGestureContext.FiredGesturesThisFrame"/>
    /// with the names of any gestures that fired this frame.
    ///
    /// <para>Tier 1 (direction-based, runs every frame):
    /// 4-way/8-way swipes, radial-zone fire, tap/double-tap/triple-tap,
    /// long-press. Cheap delta-math, no template matching.</para>
    ///
    /// <para>Tier 2 (multi-finger, runs every frame while 2+ fingers
    /// active): pinch, spread, rotate, two-finger swipe. Tracks
    /// inter-finger distance + angle baseline per session.</para>
    ///
    /// <para>Tier 3 (shape templates) lives in
    /// <c>ShapeRecognizer</c>; this class invokes it at the
    /// <c>Accumulating → Recognizing</c> transition when shape gestures
    /// are enabled and a custom-template catalog is provided.</para>
    /// </summary>
    public static class GestureRecognizer
    {
        /// <summary>True when the per-pad Mode setting allows in-box
        /// fires (swipes, taps, longpress, radial, pinch/spread/rotate,
        /// in-box shape templates). Mirrors the MappingDisplayResolver
        /// gate used by the InputChoice picker — without this, an
        /// existing in-box binding made under Mode=Both would keep
        /// firing after the user switched to Mode=CustomOnly even
        /// though the picker stopped listing the descriptor.
        /// Custom shape templates always evaluate regardless; that gating
        /// lives inside MaybeFireShape's template-filter pass.</summary>
        private static bool InBoxAllowed(TouchpadGestureSettings settings)
            => settings == null
               || settings.Mode == null
               || string.Equals(settings.Mode, "Both", StringComparison.OrdinalIgnoreCase)
               || string.Equals(settings.Mode, "InBoxOnly", StringComparison.OrdinalIgnoreCase);

        // Tier 2 gating: don't enter the multi-finger session until both
        // fingers have been down for at least this many ms. Avoids
        // single-finger gestures that briefly land a second contact from
        // immediately flipping into 2-finger mode.
        private const int TwoFingerSessionEntryDelayMs = 30;

        // Per-finger path ceiling. One point is appended per poll
        // (~1000Hz) while a contact is down, so ~8s of history triggers
        // the first compaction in UpdateActivePaths (anchor kept exact,
        // older half thinned, newest half intact). Caps a long hold
        // (touchpad-as-joystick, touch spots) at ~64KB per finger
        // instead of unbounded growth.
        private const int MaxPathPoints = 8192;

        /// <summary>Per-tick update. Walks <paramref name="ctx"/> from its
        /// current state, mutates it according to <paramref name="pad"/>'s
        /// finger snapshot, and fires gestures into
        /// <see cref="TouchpadGestureContext.FiredGesturesThisFrame"/>.
        ///
        /// <para>Callers must NOT clear <c>FiredGesturesThisFrame</c>: the
        /// set deliberately latches through the cooldown window so slower
        /// consumers catch the rising edge, and held sources (radial
        /// zones, touch spots) add and remove their keys explicitly. The
        /// recognizer clears it itself at cooldown expiry and at
        /// fresh-gesture start. (An earlier version of this doc said the
        /// opposite; following it would break every latched consumer.
        /// Round 33, S6.)</para>
        ///
        /// <para>Shape-gesture matching (Tier 3) is delegated to
        /// <paramref name="shapeTemplates"/>: pass null to skip Tier 3
        /// entirely. The caller's job is to assemble the active catalog
        /// (in-box shapes filtered by settings + custom gestures filtered
        /// by device-class + per-gesture enable) and pass it in.</para>
        /// </summary>
        /// <param name="padIdx">Touchpad index (for descriptor naming
        /// like "Touchpad N SwipeUp"). Multi-touchpad devices have
        /// separate contexts per pad.</param>
        /// <param name="ctx">Per-(device, pad) state.</param>
        /// <param name="pad">Current-frame finger snapshot.</param>
        /// <param name="settings">Per-pad detection settings.</param>
        /// <param name="nowMs">Current timestamp in ms (any monotonic
        /// reference; the recognizer only uses deltas).</param>
        /// <param name="shapeTemplates">Tier 3 template catalog. Null
        /// or empty = Tier 3 disabled this tick.</param>
        public static void Update(
            int padIdx,
            TouchpadGestureContext ctx,
            TouchpadInputState pad,
            TouchpadGestureSettings settings,
            long nowMs,
            IReadOnlyList<ShapeTemplate> shapeTemplates = null)
        {
            if (ctx == null || pad == null || settings == null) return;
            // FiredGesturesThisFrame is the consumer-facing "did this
            // gesture fire" set. The name is historical — it actually
            // latches across the cooldown window so downstream readers
            // (mapping evaluator → button output → macro trigger) see
            // a stable fire long enough to pick up the rising edge at
            // any reasonable polling rate. A 1-tick (~1ms) clear-on-
            // every-tick made gestures invisible to anything except a
            // mapping evaluator that happened to sample the exact
            // tick. Clear here only when the cooldown window closes,
            // not unconditionally at the top of each Update.

            // Path tracking has to happen whenever EITHER gesture
            // recognition OR joystick output is enabled — the joystick
            // path reads anchor + current from the same FingerPaths
            // structure the gesture engine builds. If only joystick is
            // enabled (user wants the touchpad as a virtual stick but
            // doesn't want swipes / taps / etc. firing), we still need
            // the path. Skip the whole tick only when both are off.
            bool gesturesEnabled = settings.Enabled;
            bool joystickEnabled = settings.EnableJoystickOutput;
            if (!gesturesEnabled && !joystickEnabled)
            {
                // One reset per transition: re-clearing an already-clean
                // context was per-tick churn on every assigned touchpad
                // with gestures off (the default).
                if (!ctx.IsCleanReset)
                {
                    ctx.Reset();
                    ctx.IsCleanReset = true;
                }
                return;
            }
            ctx.IsCleanReset = false;
            if (ctx.State == GestureState.Cooldown)
            {
                if (nowMs >= ctx.CooldownUntilTimestampMs)
                {
                    ctx.State = GestureState.Idle;
                    ctx.FiredGesturesThisFrame.Clear();
                }
                else
                {
                    // The cooldown gates GESTURE recognition, not the
                    // joystick surface. Round-33 audit: returning before
                    // path tracking blacked out joystick-only mode for
                    // CooldownMs after every lift, and a touch landing in
                    // the window got its anchor planted at whatever
                    // position tracking eventually resumed at. Track paths
                    // through the cooldown when the joystick is on, so
                    // ComputeJoystickAxis follows the finger from its true
                    // touchdown; skip all detectors either way.
                    if (joystickEnabled)
                        UpdateActivePaths(ctx, pad, nowMs);
                    return;
                }
            }

            UpdateActivePaths(ctx, pad, nowMs);

            if (ctx.State == GestureState.Idle && ctx.ActiveFingerCount == 0)
                return;

            if (ctx.State == GestureState.Idle && ctx.ActiveFingerCount > 0)
            {
                ctx.State = GestureState.Accumulating;
                ctx.GestureStartTimestampMs = nowMs;
                // A cooldown-window touch (joystick path above) may have
                // already accumulated peak state; the gesture's own peak
                // starts from the CURRENT contacts.
                ctx.PeakActiveFingerCount = ctx.ActiveFingerCount;
                // Fresh gesture begins. Discard any leftover latched
                // fires from the prior gesture so they don't bleed into
                // this one's recognition window.
                ctx.FiredGesturesThisFrame.Clear();
            }

            bool inBoxAllowed = gesturesEnabled && InBoxAllowed(settings);

            // Tier 1 mid-gesture fires (touch spots, radial zone entry,
            // long-press). Gated by gesturesEnabled. Joystick-only
            // setups skip these.
            if (inBoxAllowed && settings.EnableTouchSpots)
                DetectTouchSpots(padIdx, ctx, pad);
            else
                ReleaseCurrentTouchSpot(ctx);
            if (inBoxAllowed && settings.EnableRadialZones && ctx.ActiveFingerCount == 1)
                DetectRadialZones(padIdx, ctx, pad, settings);
            if (inBoxAllowed && settings.EnableLongPress && ctx.ActiveFingerCount == 1)
                DetectLongPress(padIdx, ctx, pad, settings, nowMs);

            // Tier 2 continuous + threshold fires while 2 fingers active.
            if (inBoxAllowed && ctx.ActiveFingerCount >= 2)
                DetectTwoFingerContinuous(padIdx, ctx, pad, settings, nowMs);
            else if (ctx.TwoFingerSessionActive)
            {
                // Session closed; reset baselines so the next 2-finger
                // contact starts fresh.
                ctx.TwoFingerSessionActive = false;
                ctx.FiredPinchThisSession = false;
                ctx.FiredSpreadThisSession = false;
                ctx.FiredRotateCWThisSession = false;
                ctx.FiredRotateCCWThisSession = false;
            }

            // Transition into Recognizing when all fingers lifted.
            if (ctx.State == GestureState.Accumulating && ctx.ActiveFingerCount == 0)
            {
                // Touch spots are held-while-touching buttons: release
                // BEFORE the cooldown latch so the mapped button lets go
                // on lift, not CooldownMs later. Radial zones latch on
                // purpose (pie-menu fire-on-release); spots must not.
                ReleaseCurrentTouchSpot(ctx);
                if (gesturesEnabled)
                    RunEndOfGestureRecognition(padIdx, ctx, settings, nowMs, shapeTemplates);
                ctx.State = GestureState.Cooldown;
                ctx.CooldownUntilTimestampMs = nowMs + Math.Max(0, settings.CooldownMs);
                ctx.FingerPaths.Clear();
                ctx.FingerStartTimestampsMs.Clear();
                ctx.FingerContactIds.Clear();
                ctx.FingerSlotIndices.Clear();
                ctx.FingerPathLive.Clear();
                ctx.FingerPathLastLiveMs.Clear();
                ctx.PeakActiveFingerCount = 0;
                ctx.CurrentRadialZone = -1;
            }
        }

        /// <summary>Maintains the parallel
        /// FingerPaths / FingerStartTimestampsMs / FingerContactIds /
        /// FingerSlotIndices lists. Detects per-slot contact-ID
        /// transitions and adds / removes paths accordingly. Each path
        /// is one continuous contact-ID lifetime in one slot — a finger
        /// lifting and a new one landing in the same slot opens a fresh
        /// path so the gesture engine doesn't stitch them together.</summary>
        private static void UpdateActivePaths(TouchpadGestureContext ctx,
            TouchpadInputState pad, long nowMs)
        {
            // For each currently-down slot, append the position to the
            // matching open path (by slot + contact ID). For each newly-
            // down slot, open a new path. For each newly-up slot, mark
            // the path as ended (ActiveFingerCount drops; the path data
            // stays so end-of-gesture recognition can read it).
            int active = 0;
            // Liveness recompute: every path defaults to dead this tick and
            // is re-marked below when its (slot, contact id) is still down.
            // The flags are what lets every selector distinguish a live
            // finger's path from a lifted one's frozen tail (round 33).
            for (int i = 0; i < ctx.FingerPathLive.Count; i++)
                ctx.FingerPathLive[i] = false;
            for (int s = 0; s < pad.MaxFingers; s++)
            {
                bool down = pad.FingerDown[s];
                int cid = pad.FingerContactId[s];

                // Find matching open path: same slot AND same contact ID.
                int pathIdx = -1;
                for (int i = 0; i < ctx.FingerSlotIndices.Count; i++)
                {
                    if (ctx.FingerSlotIndices[i] == s
                        && ctx.FingerContactIds[i] == cid
                        && cid >= 0)
                    {
                        pathIdx = i; break;
                    }
                }

                if (down && pathIdx < 0 && cid >= 0)
                {
                    // New contact on this slot. Open a fresh path.
                    ctx.FingerPaths.Add(new List<Vector2>());
                    ctx.FingerStartTimestampsMs.Add(nowMs);
                    ctx.FingerContactIds.Add(cid);
                    ctx.FingerSlotIndices.Add(s);
                    ctx.FingerPathLive.Add(false);
                    ctx.FingerPathLastLiveMs.Add(nowMs);
                    pathIdx = ctx.FingerPaths.Count - 1;
                }

                if (down && pathIdx >= 0)
                {
                    var path = ctx.FingerPaths[pathIdx];
                    path.Add(new Vector2(pad.FingerX[s], pad.FingerY[s]));

                    // Bound the path: this appends every poll (~1000Hz) for as
                    // long as the contact lives, and a long hold (touchpad-as-
                    // joystick, touch spots) otherwise grows the list without
                    // limit. Compact by keeping the anchor (index 0) exact,
                    // thinning the older half by stride 2, and keeping the
                    // newest half intact. Every consumer survives that: the
                    // anchor/current readers use path[0] and path[Count-1],
                    // the wind-down tail reader scans the newest quarter
                    // (inside the intact half), and the end-of-gesture shape
                    // matchers resample the path to a fixed point count
                    // anyway, so a sparser old half doesn't move the match.
                    if (path.Count >= MaxPathPoints)
                    {
                        int half = path.Count / 2;
                        var compact = new List<Vector2>(path.Count * 3 / 4 + 2);
                        compact.Add(path[0]);
                        for (int p = 1; p < half; p += 2)
                            compact.Add(path[p]);
                        for (int p = half; p < path.Count; p++)
                            compact.Add(path[p]);
                        ctx.FingerPaths[pathIdx] = compact;
                    }
                    ctx.FingerPathLive[pathIdx] = true;
                    ctx.FingerPathLastLiveMs[pathIdx] = nowMs;
                    active++;
                }
                // No special handling for lifts. The path stays in the
                // list with its terminal positions (FingerPathLive[i]
                // false), and ActiveFingerCount tracks how many slots
                // are currently down.
            }
            ctx.ActiveFingerCount = active;
            if (active > ctx.PeakActiveFingerCount)
                ctx.PeakActiveFingerCount = active;
        }

        /// <summary>Pie-menu semantics. Holds exactly one zone at a
        /// time as long as the finger is outside the center deadzone.
        /// Entering a different zone releases the previously-held one
        /// and presses the new one; re-entering a previously-visited
        /// zone re-fires because the release cleared the prior latch.
        /// Falling back into the deadzone releases without pressing
        /// anything else, letting the user "cancel" mid-gesture.
        /// Zone 0 is centered on -Y (up / 12 o'clock). Zones increase
        /// clockwise in visual touchpad space (atan2 over Y-grows-
        /// downward coordinates with a +π/2 offset that anchors zone
        /// 0 to the top), so an 8-zone wheel reads:
        ///   0 = up, 2 = right, 4 = down, 6 = left.</summary>
        private static void DetectRadialZones(int padIdx,
            TouchpadGestureContext ctx, TouchpadInputState pad,
            TouchpadGestureSettings settings)
        {
            int liveIdx = NthLivePathIndex(ctx, 0);
            if (liveIdx < 0) return;
            var path = ctx.FingerPaths[liveIdx];
            if (path.Count < 2) return;

            int zones = settings.RadialZoneCount;
            if (zones < 2) return;

            Vector2 start = path[0];
            Vector2 cur = path[path.Count - 1];
            Vector2 delta = cur - start;
            float dist = delta.Length();

            // Inside the deadzone: release whatever zone was held so
            // the user can cancel a selection by pulling back to centre.
            if (dist < settings.RadialCenterDeadzone)
            {
                ReleaseCurrentRadialZone(padIdx, ctx, zones);
                return;
            }

            // Angle in radians measured from -Y (up / 12 o'clock).
            // atan2 over (deltaY, deltaX) on touchpad-space (Y grows
            // downward) produces clockwise-increasing angles measured
            // from +X (right). Adding π/2 rotates the reference so
            // zone 0 anchors to the top — the convention everyone
            // expects from compass dials and clock faces.
            float ang = MathF.Atan2(delta.Y, delta.X) + MathF.PI / 2f;
            if (ang < 0) ang += 2f * MathF.PI;
            // Zone width = 2π / zones. Zone 0 spans -half_width..+half_width
            // around the top; each subsequent zone is the next clockwise
            // wedge.
            float zoneWidth = 2f * MathF.PI / zones;
            int zone = (int)MathF.Floor((ang + zoneWidth / 2f) / zoneWidth) % zones;
            if (zone != ctx.CurrentRadialZone)
            {
                ReleaseCurrentRadialZone(padIdx, ctx, zones);
                ctx.FiredGesturesThisFrame.Add($"Touchpad {padIdx} RadialZone{zones}_{zone}");
                ctx.CurrentRadialZone = zone;
            }
        }

        // Touch-spot boundaries, DS4Windows-grounded. The Left/Right
        // split sits at 2/5 of the pad width (DS4W Mouse.cs isLeft:
        // HwX < 1920 * 2 / 5), not at half. The Top band is the top
        // quarter; DS4W's "Upper Touch" is a DS4 hardware quirk (a
        // click above the sensor's coverage) with no coordinate to
        // borrow, so the quarter mirrors DS4W's only outer-band zone
        // math (the lower-right-corner click zone at 3/4 boundaries).
        private const float TouchSpotLeftRightSplit = 0.4f;
        private const float TouchSpotTopBand = 0.25f;

        /// <summary>Held-state touch spots (Left / Right / Top /
        /// Multitouch). DS4Windows candidate ladder: 2+ fingers is
        /// Multitouch; otherwise the single finger's CURRENT position
        /// classifies top band first, then the left/right split. At
        /// most one spot key is asserted at a time; moving across a
        /// boundary releases the old spot and presses the new one, and
        /// lifting releases outright (see the lift transition in
        /// Update). Keys ride <see cref="TouchpadGestureContext.FiredGesturesThisFrame"/>
        /// like radial zones, so the mapping evaluator and macro
        /// triggers read them through the existing gesture provider.</summary>
        private static void DetectTouchSpots(int padIdx,
            TouchpadGestureContext ctx, TouchpadInputState pad)
        {
            string spot = null;
            if (ctx.ActiveFingerCount >= 2)
            {
                spot = "TouchMulti";
            }
            else if (ctx.ActiveFingerCount == 1)
            {
                // Current position of the first still-active path
                // (DS4W evaluates Touches[0] on every touch event).
                Vector2? cur = null;
                for (int i = 0; i < ctx.FingerPaths.Count; i++)
                {
                    int slot = ctx.FingerSlotIndices[i];
                    if (slot < 0 || slot >= pad.MaxFingers) continue;
                    if (!pad.FingerDown[slot]) continue;
                    if (pad.FingerContactId[slot] != ctx.FingerContactIds[i]) continue;
                    var path = ctx.FingerPaths[i];
                    if (path.Count == 0) continue;
                    cur = path[path.Count - 1];
                    break;
                }
                if (cur.HasValue)
                {
                    if (cur.Value.Y < TouchSpotTopBand) spot = "TouchTop";
                    else if (cur.Value.X < TouchSpotLeftRightSplit) spot = "TouchLeft";
                    else spot = "TouchRight";
                }
            }

            // Runs on the ~1000 Hz poll thread. The stored key is
            // "Touchpad {padIdx} {spot}" (padIdx fixed per context), so a held touch's
            // no-change case is decided by comparing the interned spot literal against
            // the stored key's tail, allocating no per-poll string. The full key is
            // built only on an actual spot transition. (Spot names are mutually
            // non-suffixing, so EndsWith uniquely identifies the current spot.)
            bool unchanged = spot == null
                ? ctx.CurrentTouchSpot == null
                : ctx.CurrentTouchSpot != null && ctx.CurrentTouchSpot.EndsWith(spot, StringComparison.Ordinal);
            if (unchanged) return;

            ReleaseCurrentTouchSpot(ctx);
            if (spot != null)
            {
                string key = $"Touchpad {padIdx} {spot}";
                ctx.FiredGesturesThisFrame.Add(key);
                ctx.CurrentTouchSpot = key;
            }
        }

        /// <summary>Drops the currently-held touch spot's fire entry.
        /// No-op when none is held. Called on spot transitions, on
        /// finger lift, and when the category gate turns off mid-hold
        /// so a stale key can never stick in the fired set.</summary>
        private static void ReleaseCurrentTouchSpot(TouchpadGestureContext ctx)
        {
            if (ctx.CurrentTouchSpot == null) return;
            ctx.FiredGesturesThisFrame.Remove(ctx.CurrentTouchSpot);
            ctx.CurrentTouchSpot = null;
        }

        /// <summary>Drops the currently-held radial zone's fire entry
        /// from the gesture context. No-op when no zone is held.
        /// Called on every zone transition (so the prior zone's
        /// mapped button releases before the next one engages) and
        /// when the finger re-enters the centre deadzone.</summary>
        private static void ReleaseCurrentRadialZone(int padIdx,
            TouchpadGestureContext ctx, int zones)
        {
            if (ctx.CurrentRadialZone < 0) return;
            ctx.FiredGesturesThisFrame.Remove(
                $"Touchpad {padIdx} RadialZone{zones}_{ctx.CurrentRadialZone}");
            ctx.CurrentRadialZone = -1;
        }

        /// <summary>Fires <c>Touchpad N LongPress</c> when a single
        /// finger has been down for at least the configured threshold
        /// and the path stayed within the max-motion bound.</summary>
        private static string[] s_longPressKeys = new string[4];
        private static string LongPressKey(int padIdx)
        {
            var keys = s_longPressKeys;
            if ((uint)padIdx < (uint)keys.Length && keys[padIdx] != null)
                return keys[padIdx];
            string key = $"Touchpad {padIdx} LongPress";
            if ((uint)padIdx >= (uint)keys.Length)
            {
                var grown = new string[padIdx + 1];
                System.Array.Copy(keys, grown, keys.Length);
                s_longPressKeys = keys = grown;
            }
            keys[padIdx] = key;
            return key;
        }

        private static void DetectLongPress(int padIdx,
            TouchpadGestureContext ctx, TouchpadInputState pad,
            TouchpadGestureSettings settings, long nowMs)
        {
            // Exactly one LIVE finger, and the hold is timed from ITS
            // touchdown: after a contact bounce the fresh contact restarts
            // the hold rather than inheriting the dead path's clock.
            int liveIdx = NthLivePathIndex(ctx, 0);
            if (liveIdx < 0 || NthLivePathIndex(ctx, 1) >= 0) return;
            // Cached per pad: the interpolation allocated a string every
            // ~1 kHz frame a single finger was touching.
            string key = LongPressKey(padIdx);
            // One-shot per gesture: skip if already fired this gesture.
            if (ctx.FiredGesturesThisFrame.Contains(key)) return;

            long elapsed = nowMs - ctx.FingerStartTimestampsMs[liveIdx];
            if (elapsed < settings.LongPressTimeWindowMs) return;

            var path = ctx.FingerPaths[liveIdx];
            if (path.Count < 2) return;

            // "Recent stillness" gate rather than "max distance from
            // touchdown." Users naturally land a finger and settle it
            // into a slightly-different position before holding still —
            // common on sensitive touchpads (DualSense in particular)
            // where the contact patch shifts the reported position by a
            // few percent during the first hundred ms or so. Measuring
            // from the touchdown point punishes that settle even when
            // the finger is now perfectly stable. Measure instead the
            // bounding-box span of the most recent quarter of the path:
            // if THAT span is small, the finger is currently still and
            // the user clearly means a long-press regardless of how it
            // got there.
            int tailStart = path.Count * 3 / 4;
            if (tailStart < 1) tailStart = 1;
            float minX = float.MaxValue, maxX = float.MinValue;
            float minY = float.MaxValue, maxY = float.MinValue;
            for (int i = tailStart; i < path.Count; i++)
            {
                if (path[i].X < minX) minX = path[i].X;
                if (path[i].X > maxX) maxX = path[i].X;
                if (path[i].Y < minY) minY = path[i].Y;
                if (path[i].Y > maxY) maxY = path[i].Y;
            }
            float recentSpan = MathF.Max(maxX - minX, maxY - minY);
            if (recentSpan > settings.LongPressMaxMotion) return;

            ctx.FiredGesturesThisFrame.Add(key);
            // Don't clear the path here — DetectRadialZones reads
            // path[0] each tick to compute the angle from touchdown,
            // and a cleared path collapses start ≈ end so the next
            // RadialZones tick sees dist < RadialCenterDeadzone and
            // releases the held zone. End-of-gesture swipe/tap/shape
            // recognition checks for the LongPress entry in
            // FiredGesturesThisFrame and skips itself instead.
        }

        /// <summary>Manages the 2-finger session lifecycle:
        /// captures baseline distance + angle on entry, updates
        /// continuous pinch / rotate axis state, and fires the one-shot
        /// Pinch / Spread / RotateCW / RotateCCW threshold gestures.</summary>
        private static void DetectTwoFingerContinuous(int padIdx,
            TouchpadGestureContext ctx, TouchpadInputState pad,
            TouchpadGestureSettings settings, long nowMs)
        {
            // Pick the two oldest LIVE paths (longest-held = primary,
            // most-recent = secondary). Selecting merely non-empty paths
            // paired a lifted finger's frozen endpoint with the live one
            // after an A+B, A-lifts, C-lands sequence, so pinch/rotate
            // baselines tracked a dead contact (round 33).
            int firstIdx = NthLivePathIndex(ctx, 0);
            int secondIdx = NthLivePathIndex(ctx, 1);
            if (firstIdx < 0 || secondIdx < 0) return;

            var p0 = ctx.FingerPaths[firstIdx][ctx.FingerPaths[firstIdx].Count - 1];
            var p1 = ctx.FingerPaths[secondIdx][ctx.FingerPaths[secondIdx].Count - 1];
            Vector2 delta = p1 - p0;
            float dist = delta.Length();
            float ang = MathF.Atan2(delta.Y, delta.X);

            if (!ctx.TwoFingerSessionActive)
            {
                // Enter the session only after both have been down for
                // a brief minimum window so a transient second touch
                // doesn't immediately commit baselines.
                long elapsedSecond = nowMs - ctx.FingerStartTimestampsMs[secondIdx];
                if (elapsedSecond < TwoFingerSessionEntryDelayMs) return;
                ctx.TwoFingerSessionActive = true;
                ctx.TwoFingerInitialDistance = dist;
                ctx.TwoFingerInitialAngle = ang;
                ctx.TwoFingerLastAngle = ang;
                ctx.TwoFingerAccumRotateRad = 0f;
                return;
            }

            // Continuous-axis state — bipolar -1..+1 representations of
            // the pinch progress + rotation delta.
            if (settings.EnablePinchSpread && ctx.TwoFingerInitialDistance > 0.001f)
            {
                float ratio = dist / ctx.TwoFingerInitialDistance - 1f; // -1..+inf
                ctx.CurrentPinchAxis = Math.Clamp(ratio, -1f, 1f);

                if (!ctx.FiredPinchThisSession && ratio < -settings.PinchThreshold)
                {
                    ctx.FiredGesturesThisFrame.Add($"Touchpad {padIdx} Pinch");
                    ctx.FiredPinchThisSession = true;
                }
                if (!ctx.FiredSpreadThisSession && ratio > settings.PinchThreshold)
                {
                    ctx.FiredGesturesThisFrame.Add($"Touchpad {padIdx} Spread");
                    ctx.FiredSpreadThisSession = true;
                }
            }

            if (settings.EnableRotate)
            {
                // Per-frame unwrap. Folding the TOTAL delta into -PI..+PI
                // (the old scheme) made a continuous rotation past 180
                // degrees wrap negative and fire the opposite direction
                // on top of the already-fired one, with CurrentRotateAxis
                // jumping discontinuously from +1 toward -1 (round 33).
                // Per-frame steps at ~1 kHz are tiny, so wrapping the STEP
                // and accumulating is exact and unbounded.
                float step = ang - ctx.TwoFingerLastAngle;
                while (step > MathF.PI) step -= 2f * MathF.PI;
                while (step < -MathF.PI) step += 2f * MathF.PI;
                ctx.TwoFingerLastAngle = ang;
                ctx.TwoFingerAccumRotateRad += step;
                float angDelta = ctx.TwoFingerAccumRotateRad;
                ctx.CurrentRotateAxis = Math.Clamp(angDelta / MathF.PI, -1f, 1f);

                float threshRad = settings.RotateThresholdDegrees * MathF.PI / 180f;
                if (!ctx.FiredRotateCWThisSession && angDelta > threshRad)
                {
                    ctx.FiredGesturesThisFrame.Add($"Touchpad {padIdx} RotateCW");
                    ctx.FiredRotateCWThisSession = true;
                }
                if (!ctx.FiredRotateCCWThisSession && angDelta < -threshRad)
                {
                    ctx.FiredGesturesThisFrame.Add($"Touchpad {padIdx} RotateCCW");
                    ctx.FiredRotateCCWThisSession = true;
                }
            }
        }

        /// <summary>Runs at the all-fingers-lifted transition. Picks the
        /// most-fitting end-of-gesture interpretation: swipe (Tier 1),
        /// tap/double/triple (Tier 1), or shape match (Tier 3). Multi-
        /// finger swipes also handled here. Skipped entirely when
        /// LongPress fired mid-gesture so the lift doesn't double-
        /// fire a tap or shape.</summary>
        private static void RunEndOfGestureRecognition(int padIdx,
            TouchpadGestureContext ctx, TouchpadGestureSettings settings,
            long nowMs, IReadOnlyList<ShapeTemplate> shapeTemplates)
        {
            // Skip end-of-gesture interpretations when LongPress
            // already claimed this gesture mid-hold. The held
            // RadialZone (if any) stays asserted in
            // FiredGesturesThisFrame and releases when the gesture
            // transitions to Cooldown along with the rest of the
            // gesture-context state.
            if (ctx.FiredGesturesThisFrame.Contains($"Touchpad {padIdx} LongPress"))
                return;

            // Classify by PEAK SIMULTANEOUS contacts, not accumulated path
            // count: a finger that bounces (lift + re-land, new contact id)
            // opens a second path, and counting paths turned a two-finger
            // swipe into a "three finger" gesture that no branch below
            // handles (round 33). The representative paths are the FINAL
            // contacts (selection from the end); with no bounce that is
            // exactly the old front-to-back selection.
            int fingerCount = ctx.PeakActiveFingerCount;
            if (fingerCount == 0)
                for (int i = 0; i < ctx.FingerPaths.Count; i++)
                    if (ctx.FingerPaths[i].Count > 0) { fingerCount = 1; break; }

            if (fingerCount == 0) return;

            bool inBoxAllowed = InBoxAllowed(settings);

            var repBuf = new int[Math.Max(1, fingerCount)];

            // Single-finger end-of-gesture: swipe vs tap.
            if (fingerCount == 1)
            {
                if (SelectRepresentativePaths(ctx, 1, repBuf) < 1) return;
                int pIdx = repBuf[0];
                var path = ctx.FingerPaths[pIdx];
                if (path.Count < 1) return;
                Vector2 start = path[0];
                Vector2 end = path[path.Count - 1];
                float dist = (end - start).Length();
                long startTs = ctx.FingerStartTimestampsMs[pIdx];
                long elapsed = nowMs - startTs;

                // Tap branch: short, no significant motion.
                if (inBoxAllowed
                    && settings.EnableTaps
                    && elapsed <= settings.TapTimeWindowMs
                    && dist <= settings.TapMaxMotion)
                {
                    long gap = startTs - ctx.LastTapEndTimestampMs;
                    if (gap > settings.MultiTapGapMs) ctx.RecentTapCount = 0;
                    ctx.RecentTapCount++;
                    ctx.LastTapEndTimestampMs = nowMs;
                    string tapName = ctx.RecentTapCount switch
                    {
                        1 => "Tap",
                        2 => "DoubleTap",
                        _ => "TripleTap"
                    };
                    ctx.FiredGesturesThisFrame.Add($"Touchpad {padIdx} {tapName}");
                    if (ctx.RecentTapCount >= 3) ctx.RecentTapCount = 0;
                    return;
                }
                ctx.RecentTapCount = 0;

                // Swipe branch: long-enough motion within the time window.
                if (inBoxAllowed
                    && (settings.EnableFourWaySwipes || settings.EnableEightWaySwipes)
                    && elapsed <= settings.SwipeTimeWindowMs
                    && dist >= settings.SwipeDistanceThreshold)
                {
                    string dir = ClassifyDirection(end - start, settings.EnableEightWaySwipes);
                    if (dir != null)
                        ctx.FiredGesturesThisFrame.Add($"Touchpad {padIdx} Swipe{dir}");
                }

                // Tier 3 shape match for single-finger custom + in-box.
                MaybeFireShape(padIdx, ctx, settings, shapeTemplates, 1);
                return;
            }

            // Two-finger end-of-gesture: 2-finger swipe (parallel motion)
            // + 2-finger tap (short, no significant motion on either path).
            if (fingerCount == 2)
            {
                if (inBoxAllowed && settings.EnableTwoFingerSwipes)
                {
                    int got = SelectRepresentativePaths(ctx, 2, repBuf);
                    var firstPath = got >= 1 ? ctx.FingerPaths[repBuf[0]] : null;
                    var secondPath = got >= 2 ? ctx.FingerPaths[repBuf[1]] : null;
                    if (firstPath != null && secondPath != null
                        && firstPath.Count > 0 && secondPath.Count > 0)
                    {
                        Vector2 d0 = firstPath[firstPath.Count - 1] - firstPath[0];
                        Vector2 d1 = secondPath[secondPath.Count - 1] - secondPath[0];
                        float dot = Vector2.Dot(Vector2.Normalize(d0), Vector2.Normalize(d1));
                        float angDeg = MathF.Acos(Math.Clamp(dot, -1f, 1f)) * 180f / MathF.PI;
                        if (angDeg <= settings.TwoFingerSwipeAngularTolerance
                            && d0.Length() >= settings.SwipeDistanceThreshold
                            && d1.Length() >= settings.SwipeDistanceThreshold)
                        {
                            string dir = ClassifyDirection((d0 + d1) * 0.5f, settings.EnableEightWaySwipes);
                            if (dir != null)
                                ctx.FiredGesturesThisFrame.Add($"Touchpad {padIdx} TwoFingerSwipe{dir}");
                        }
                    }
                }
                if (inBoxAllowed && settings.EnableTaps)
                {
                    // 2-finger tap: both paths short + small motion.
                    int got = SelectRepresentativePaths(ctx, 2, repBuf);
                    var firstPath = got >= 1 ? ctx.FingerPaths[repBuf[0]] : null;
                    var secondPath = got >= 2 ? ctx.FingerPaths[repBuf[1]] : null;
                    long startTs = got >= 1 ? ctx.FingerStartTimestampsMs[repBuf[0]] : nowMs;
                    long elapsed = nowMs - startTs;
                    if (elapsed <= settings.TapTimeWindowMs
                        && firstPath != null && secondPath != null
                        && (firstPath[firstPath.Count - 1] - firstPath[0]).Length() <= settings.TapMaxMotion
                        && (secondPath[secondPath.Count - 1] - secondPath[0]).Length() <= settings.TapMaxMotion)
                    {
                        ctx.FiredGesturesThisFrame.Add($"Touchpad {padIdx} TwoFingerTap");
                    }
                }
                MaybeFireShape(padIdx, ctx, settings, shapeTemplates, 2);
                return;
            }

            // Three+ finger end-of-gesture: tap + swipe variants gated
            // on the matching settings toggle. Less common; uses the
            // same parallel-vectors test as 2-finger swipe.
            if (fingerCount >= 3)
            {
                bool gate = inBoxAllowed && fingerCount switch
                {
                    3 => settings.EnableThreeFingerGestures,
                    4 => settings.EnableFourFingerGestures,
                    5 => settings.EnableFiveFingerGestures,
                    _ => false
                };
                if (!gate)
                {
                    // Even when 3+-finger in-box fires are blocked, give
                    // the shape recognizer a chance — a 3-finger custom
                    // gesture should still fire under Mode=CustomOnly.
                    MaybeFireShape(padIdx, ctx, settings, shapeTemplates, fingerCount);
                    return;
                }

                string countWord = fingerCount switch
                {
                    3 => "ThreeFinger",
                    4 => "FourFinger",
                    5 => "FiveFinger",
                    _ => null
                };
                if (countWord == null) return;

                // Parallel-vector swipe + small-motion tap, mirroring
                // the 2-finger logic.
                Vector2 sumDelta = Vector2.Zero;
                bool allShort = true;
                bool parallel = true;
                Vector2 firstNorm = Vector2.Zero;
                int contributing = 0;
                long startTs = ctx.FingerStartTimestampsMs.Count > 0 ? ctx.FingerStartTimestampsMs[0] : nowMs;
                long elapsed = nowMs - startTs;
                // Iterate the REPRESENTATIVE paths, the way the one- and
                // two-finger branches and the shape matcher all do. Walking
                // ctx.FingerPaths directly took in paths the selection exists
                // to exclude, so a lifted finger's stale delta still fed
                // sumDelta and the allShort tap test: a three-finger swipe
                // could be pulled off-axis, or classified as a tap, by a
                // finger that was no longer down.
                int repCount = SelectRepresentativePaths(ctx, fingerCount, repBuf);
                for (int r = 0; r < repCount; r++)
                {
                    var p = ctx.FingerPaths[repBuf[r]];
                    if (p == null || p.Count == 0) continue;
                    Vector2 d = p[p.Count - 1] - p[0];
                    sumDelta += d;
                    contributing++;
                    if (d.Length() > settings.TapMaxMotion) allShort = false;
                    if (d.Length() >= settings.SwipeDistanceThreshold)
                    {
                        if (firstNorm == Vector2.Zero) firstNorm = Vector2.Normalize(d);
                        else
                        {
                            float dot = Vector2.Dot(firstNorm, Vector2.Normalize(d));
                            float angDeg = MathF.Acos(Math.Clamp(dot, -1f, 1f)) * 180f / MathF.PI;
                            if (angDeg > settings.TwoFingerSwipeAngularTolerance)
                                parallel = false;
                        }
                    }
                    else
                    {
                        parallel = false; // not all fingers moved a swipe distance
                    }
                }
                if (contributing == 0) return;

                if (parallel && firstNorm != Vector2.Zero
                    && settings.EnableTwoFingerSwipes)
                {
                    string dir = ClassifyDirection(sumDelta / contributing, settings.EnableEightWaySwipes);
                    if (dir != null)
                        ctx.FiredGesturesThisFrame.Add($"Touchpad {padIdx} {countWord}Swipe{dir}");
                }
                if (allShort && elapsed <= settings.TapTimeWindowMs && settings.EnableTaps)
                {
                    ctx.FiredGesturesThisFrame.Add($"Touchpad {padIdx} {countWord}Tap");
                }
                MaybeFireShape(padIdx, ctx, settings, shapeTemplates, fingerCount);
            }
        }

        /// <summary>Classifies a delta vector into "Up"/"Down"/"Left"/"Right"
        /// (4-way) or those plus "NE"/"NW"/"SE"/"SW" (8-way). Touchpad
        /// space convention: X grows right, Y grows down — same as SDL
        /// and PTP. So "Up" = negative Y, "Down" = positive Y.</summary>
        private static string ClassifyDirection(Vector2 d, bool eightWay)
        {
            if (d == Vector2.Zero) return null;
            float ang = MathF.Atan2(-d.Y, d.X); // -π..+π, 0 = right, π/2 = up
            // Convert to compass-like 0..2π where 0 = right, increasing CCW.
            if (ang < 0) ang += 2f * MathF.PI;
            float deg = ang * 180f / MathF.PI;

            if (eightWay)
            {
                // 8 buckets of 45° each; bucket centers at 0, 45, 90 ...
                // Offset by 22.5° so bucket 0 (Right) spans -22.5..+22.5.
                int b = (int)MathF.Floor((deg + 22.5f) / 45f) % 8;
                return b switch
                {
                    0 => "Right",
                    1 => "NE",
                    2 => "Up",
                    3 => "NW",
                    4 => "Left",
                    5 => "SW",
                    6 => "Down",
                    7 => "SE",
                    _ => null
                };
            }
            else
            {
                // 4 buckets of 90°; bucket centers at 0, 90, 180, 270.
                int b = (int)MathF.Floor((deg + 45f) / 90f) % 4;
                return b switch
                {
                    0 => "Right",
                    1 => "Up",
                    2 => "Left",
                    3 => "Down",
                    _ => null
                };
            }
        }

        /// <summary>Walks the shape-template catalog with the
        /// <see cref="ShapeRecognizer"/> if shapes are enabled + the
        /// catalog has templates matching the finger count. Fires the
        /// best match's name when the match score is under the per-
        /// template (or fallback to per-settings) threshold.</summary>
        private static void MaybeFireShape(int padIdx,
            TouchpadGestureContext ctx, TouchpadGestureSettings settings,
            IReadOnlyList<ShapeTemplate> templates, int fingerCount)
        {
            if (templates == null || templates.Count == 0) return;

            // Filter the template catalog by the per-pad detection-mode
            // setting before any matching runs. The dropdown surfaced as
            // "Recognize: In-box only / Custom only / Both" used to be a
            // no-op — both matchers always saw the full catalog. Filter
            // here so the user's selection actually constrains what fires.
            // Tier 1 (swipes / taps / longpress / radial) and Tier 2
            // (pinch / rotate / two-finger) are unaffected — they have
            // no in-box-vs-custom split.
            // Every other Mode read in the app is case-insensitive
            // (InBoxAllowed above, MappingDisplayResolver, the VM); a
            // hand-edited PadForge.xml Mode="customonly" must not silently
            // fall back to Both here (round 33).
            string mode = settings.Mode ?? "Both";
            bool wantInBoxOnly = string.Equals(mode, "InBoxOnly", StringComparison.OrdinalIgnoreCase);
            bool wantCustomOnly = string.Equals(mode, "CustomOnly", StringComparison.OrdinalIgnoreCase);
            if (wantInBoxOnly || wantCustomOnly)
            {
                var filtered = new List<ShapeTemplate>(templates.Count);
                for (int i = 0; i < templates.Count; i++)
                {
                    var t = templates[i];
                    if (t == null) continue;
                    if (t.IsCustom != wantCustomOnly) continue;
                    filtered.Add(t);
                }
                templates = filtered;
                if (templates.Count == 0) return;
            }

            if (!settings.EnableShapeGestures && !HasCustomFingerCount(templates, fingerCount))
                return;

            // With shape gestures OFF, only CUSTOM templates may match.
            // The angular matcher below always had this gate per template;
            // the point-cloud matcher received the unfiltered list, so an
            // enabled custom gesture sharing the finger count let IN-BOX
            // shapes (Circle etc.) fire with EnableShapeGestures off
            // (round 33). Filter the cloud list to the same rule.
            if (!settings.EnableShapeGestures)
            {
                var customOnly = new List<ShapeTemplate>(templates.Count);
                for (int i = 0; i < templates.Count; i++)
                {
                    var t = templates[i];
                    if (t != null && t.IsCustom) customOnly.Add(t);
                }
                templates = customOnly;
                if (templates.Count == 0) return;
            }

            // Collect this gesture's finger paths: the fingerCount contacts
            // that were live LATEST, in touchdown order (see
            // RunEndOfGestureRecognition's peak-count note).
            var shapeBuf = new int[fingerCount];
            if (SelectRepresentativePaths(ctx, fingerCount, shapeBuf) != fingerCount) return;
            var fingerPaths = new List<List<Vector2>>(fingerCount);
            for (int n = 0; n < fingerCount; n++)
                fingerPaths.Add(ctx.FingerPaths[shapeBuf[n]]);

            string cloudMatchName = ShapeRecognizer.MatchByFingerCount(
                fingerPaths, templates, fingerCount,
                settings.GestureMatchThreshold, out _);

            // Single-finger shapes also run through the angular-margin
            // recognizer (GestureSign-style). It picks up direction-
            // dependent shapes like Square / Z / Triangle / Checkmark
            // that the point-cloud matcher softens because cloud distance
            // is permutation-invariant and ignores stroke direction. The
            // two matchers produce different score scales:
            //   point-cloud: lower = better (returns lowest distance under threshold)
            //   angular-margin: higher = better, 1.0 = identical at every segment
            // We accept whichever matcher fired its match. When both
            // fire (often the same name), prefer the angular-margin
            // result because it's the more discriminative algorithm
            // for the corner-shapes it was designed to detect.
            string angName = null;
            float angScore = 0f;
            if (fingerCount == 1)
            {
                // Build a single-finger angular candidate-template list
                // by selecting templates that carry an AngularSignature
                // AND that pass the same finger-count + IsCustom / shape-
                // gestures gating ShapeRecognizer.MatchByFingerCount uses.
                var angTemplates = new List<AngularTemplate>();
                for (int i = 0; i < templates.Count; i++)
                {
                    var t = templates[i];
                    if (t == null || !t.Enabled) continue;
                    if (t.FingerCount != 1) continue;
                    if (t.AngularSignature == null) continue;
                    if (!t.IsCustom && !settings.EnableShapeGestures) continue;
                    angTemplates.Add(new AngularTemplate
                    {
                        Name = t.Name,
                        Angles = t.AngularSignature,
                        Enabled = true,
                        IsCustom = t.IsCustom,
                        IsClosed = t.AngularIsClosed,
                        IsDirectionAgnostic = t.AngularIsDirectionAgnostic,
                    });
                }
                var path = fingerPaths.Count > 0 ? fingerPaths[0] : null;
                if (path != null && angTemplates.Count > 0)
                {
                    (angName, angScore) = AngularMarginRecognizer.Match(path, angTemplates);
                    if (angScore < AngularMarginRecognizer.DefaultAcceptScore)
                        angName = null;
                }
            }

            string firedName = angName ?? cloudMatchName;
            if (!string.IsNullOrEmpty(firedName))
                ctx.FiredGesturesThisFrame.Add($"Touchpad {padIdx} {firedName}");
        }

        private static bool HasCustomFingerCount(IReadOnlyList<ShapeTemplate> templates, int n)
        {
            for (int i = 0; i < templates.Count; i++)
                if (templates[i].FingerCount == n && templates[i].IsCustom) return true;
            return false;
        }

        /// <summary>Index of the Nth (0-based) path whose contact is still
        /// DOWN this tick, or -1. The mid-gesture detectors and the
        /// joystick output select by liveness so a lifted finger's frozen
        /// tail never drives them while a live finger is ignored
        /// (round 33: radial zones, the two-finger pair, and both
        /// joystick APIs all had exactly that bug).</summary>
        private static int NthLivePathIndex(TouchpadGestureContext ctx, int n)
        {
            int seen = 0;
            for (int i = 0; i < ctx.FingerPaths.Count && i < ctx.FingerPathLive.Count; i++)
            {
                if (!ctx.FingerPathLive[i] || ctx.FingerPaths[i].Count == 0) continue;
                if (seen == n) return i;
                seen++;
            }
            return -1;
        }

        /// <summary>Fills <paramref name="dst"/> with the indices of the
        /// <paramref name="n"/> non-empty paths whose contacts were live
        /// LATEST (FingerPathLastLiveMs), ordered oldest-touchdown first.
        /// End-of-gesture classification pairs this with
        /// PeakActiveFingerCount: after a contact bounce the fragment
        /// sits later in LIST order than the other finger's path, so
        /// positional selection picks the dead fragment. Recency
        /// selection picks the contacts that actually finished the
        /// gesture. With no bounce this selects exactly the paths
        /// front-to-back selection did. Returns the count filled.</summary>
        private static int SelectRepresentativePaths(TouchpadGestureContext ctx, int n, int[] dst)
        {
            int count = 0;
            for (int i = 0; i < ctx.FingerPaths.Count; i++)
            {
                if (ctx.FingerPaths[i].Count == 0) continue;
                long live = i < ctx.FingerPathLastLiveMs.Count ? ctx.FingerPathLastLiveMs[i] : 0;
                // Insertion by descending last-live; capacity n.
                int at = count < n ? count : -1;
                for (int j = 0; j < count; j++)
                {
                    long other = ctx.FingerPathLastLiveMs[dst[j]];
                    if (live > other) { at = j; break; }
                }
                if (at < 0) continue;
                int limit = Math.Min(count + 1, n);
                for (int j = limit - 1; j > at; j--) dst[j] = dst[j - 1];
                dst[at] = i;
                if (count < n) count++;
            }
            // Present in touchdown order (primary = oldest), the order the
            // pre-fix front-to-back selection produced for the no-bounce case.
            for (int a = 0; a < count; a++)
                for (int b = a + 1; b < count; b++)
                    if (ctx.FingerStartTimestampsMs[dst[b]] < ctx.FingerStartTimestampsMs[dst[a]])
                        (dst[a], dst[b]) = (dst[b], dst[a]);
            return count;
        }

        // ─── Joystick / D-pad output ──────────────────────────────────
        //
        // Anchor-relative continuous output. Reads anchor (first path
        // point) and current (last path point) from the same FingerPaths
        // structure the gesture engine builds, applies per-pad joystick
        // settings, and returns analog stick X/Y and (optionally) D-pad
        // direction bools. Caller (InputService providers) routes the
        // values to mapping evaluators via the SourceCoercion layer.
        //
        // Single-finger only for v1. Uses FingerPaths[0] (the first
        // active path). Returns (0, 0) / all-false when no finger is
        // active or output is disabled.

        /// <summary>Anchor-relative analog stick output. Returns
        /// (0, 0) when the finger isn't down, output is disabled, or
        /// the magnitude falls inside <c>JoystickInnerDeadzone</c>.
        /// Y is NOT flipped: touchpad +Y (finger-down) maps to positive
        /// stick Y, matching the SDL_GAMEPAD_AXIS_LEFTY / RIGHTY
        /// convention (see the inline note below; a flipped version was
        /// a reverted regression).</summary>
        public static (float x, float y) ComputeJoystickAxis(
            TouchpadGestureContext ctx, TouchpadGestureSettings settings)
        {
            if (ctx == null || settings == null || !settings.EnableJoystickOutput)
                return (0f, 0f);
            // LIVE path only: reading the first non-empty path froze the
            // stick at a lifted finger's last deflection while another
            // finger was still down (round 33).
            int liveIdx = NthLivePathIndex(ctx, 0);
            if (liveIdx < 0) return (0f, 0f);
            var path = ctx.FingerPaths[liveIdx];
            if (path.Count < 1) return (0f, 0f);
            Vector2 anchor = path[0];
            Vector2 cur = path[path.Count - 1];
            float dx = cur.X - anchor.X;
            float dy = cur.Y - anchor.Y;
            float mag = MathF.Sqrt(dx * dx + dy * dy);
            if (mag < settings.JoystickInnerDeadzone) return (0f, 0f);

            float maxR = settings.JoystickMaxRadius > 0f ? settings.JoystickMaxRadius : 0.30f;
            float sx = dx / maxR;
            // Touchpad +Y is down, PadForge/SDL stick axis +Y is also down
            // (SDL_GAMEPAD_AXIS_LEFTY / RIGHTY convention). Output raw dy
            // so finger-down on the touchpad = stick-down in-game. Earlier
            // versions of this method flipped sy assuming an XInput-style
            // +Y=up convention; that produced inverted vertical motion.
            float sy = dy / maxR;

            // Clamp to unit-circle so combined magnitude can't exceed 1.
            float scaledMag = MathF.Sqrt(sx * sx + sy * sy);
            if (scaledMag > 1f)
            {
                sx /= scaledMag;
                sy /= scaledMag;
            }
            return (sx, sy);
        }

        /// <summary>Anchor-relative D-pad output. Returns a 4-tuple of
        /// (up, right, down, left) bools per <c>JoystickDPadMode</c>:
        /// "Off" returns all false; "FourWay" emits one direction at
        /// a time inside its 90° wedge; "EightWay" emits two directions
        /// for diagonals (matching how physical D-pads report NE / NW
        /// / SE / SW). Magnitude below <c>JoystickDPadActivationThreshold</c>
        /// suppresses all output.</summary>
        public static (bool up, bool right, bool down, bool left) ComputeJoystickDPad(
            TouchpadGestureContext ctx, TouchpadGestureSettings settings)
        {
            if (ctx == null || settings == null || !settings.EnableJoystickOutput)
                return (false, false, false, false);
            string mode = settings.JoystickDPadMode ?? "FourWay";
            if (string.Equals(mode, "Off", StringComparison.OrdinalIgnoreCase))
                return (false, false, false, false);
            // LIVE path only, same rule as ComputeJoystickAxis (round 33).
            int liveIdx = NthLivePathIndex(ctx, 0);
            if (liveIdx < 0) return (false, false, false, false);
            var path = ctx.FingerPaths[liveIdx];
            if (path.Count < 1) return (false, false, false, false);
            Vector2 anchor = path[0];
            Vector2 cur = path[path.Count - 1];
            float dx = cur.X - anchor.X;
            float dy = cur.Y - anchor.Y;
            float mag = MathF.Sqrt(dx * dx + dy * dy);
            if (mag < settings.JoystickDPadActivationThreshold)
                return (false, false, false, false);

            // Same angle convention as radial zones: 0° = up (north),
            // clockwise from there. atan2(dy, dx) on touchpad-space
            // (Y grows down) is clockwise from +X; +π/2 rotates so
            // zero anchors at -Y (up).
            float ang = MathF.Atan2(dy, dx) + MathF.PI / 2f;
            if (ang < 0) ang += 2f * MathF.PI;

            if (string.Equals(mode, "EightWay", StringComparison.OrdinalIgnoreCase))
            {
                float zoneWidth = MathF.PI / 4f; // 45°
                int zone = (int)MathF.Floor((ang + zoneWidth / 2f) / zoneWidth) % 8;
                return zone switch
                {
                    0 => (true,  false, false, false), // up
                    1 => (true,  true,  false, false), // up + right
                    2 => (false, true,  false, false), // right
                    3 => (false, true,  true,  false), // down + right
                    4 => (false, false, true,  false), // down
                    5 => (false, false, true,  true),  // down + left
                    6 => (false, false, false, true),  // left
                    7 => (true,  false, false, true),  // up + left
                    _ => (false, false, false, false),
                };
            }

            // 4-way: four 90° wedges centered on each cardinal.
            {
                float zoneWidth = MathF.PI / 2f; // 90°
                int zone = (int)MathF.Floor((ang + zoneWidth / 2f) / zoneWidth) % 4;
                return zone switch
                {
                    0 => (true,  false, false, false), // up
                    1 => (false, true,  false, false), // right
                    2 => (false, false, true,  false), // down
                    3 => (false, false, false, true),  // left
                    _ => (false, false, false, false),
                };
            }
        }

    }
}
