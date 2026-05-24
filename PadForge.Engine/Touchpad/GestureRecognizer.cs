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
    /// <c>PDollarRecognizer</c>; this class invokes it at the
    /// <c>Accumulating → Recognizing</c> transition when shape gestures
    /// are enabled and a custom-template catalog is provided.</para>
    /// </summary>
    public static class GestureRecognizer
    {
        // 100ms cooldown is the recipe default — tight enough that the
        // user doesn't feel a stutter between gestures, loose enough to
        // prevent the bounce-fire scenario where a quick reverse motion
        // re-fires the opposite-direction swipe immediately.
        private const float CooldownAdditionalMs = 100f;

        // Tier 2 gating: don't enter the multi-finger session until both
        // fingers have been down for at least this many ms. Avoids
        // single-finger gestures that briefly land a second contact from
        // immediately flipping into 2-finger mode.
        private const int TwoFingerSessionEntryDelayMs = 30;

        /// <summary>Per-tick update. Walks <paramref name="ctx"/> from its
        /// current state, mutates it according to <paramref name="pad"/>'s
        /// finger snapshot, and fires gestures into
        /// <see cref="TouchpadGestureContext.FiredGesturesThisFrame"/>.
        ///
        /// <para>Caller should clear <c>FiredGesturesThisFrame</c> before
        /// calling, then read it after; this method appends to it but
        /// does not clear so the caller can compose multiple sources.</para>
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
            IReadOnlyList<PDollarTemplate> shapeTemplates = null)
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
            if (ctx.State == GestureState.Suspended) return;
            if (!settings.Enabled)
            {
                ctx.Reset();
                return;
            }
            if (ctx.State == GestureState.Cooldown)
            {
                if (nowMs >= ctx.CooldownUntilTimestampMs)
                {
                    ctx.State = GestureState.Idle;
                    ctx.FiredGesturesThisFrame.Clear();
                }
                else
                {
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
                // Fresh gesture begins — discard any leftover latched
                // fires from the prior gesture so they don't bleed into
                // this one's recognition window.
                ctx.FiredGesturesThisFrame.Clear();
            }

            // Tier 1 mid-gesture fires (radial zone entry, long-press).
            if (settings.EnableRadialZones && ctx.ActiveFingerCount == 1)
                DetectRadialZones(padIdx, ctx, pad, settings);
            if (settings.EnableLongPress && ctx.ActiveFingerCount == 1)
                DetectLongPress(padIdx, ctx, pad, settings, nowMs);

            // Tier 2 continuous + threshold fires while 2 fingers active.
            if (ctx.ActiveFingerCount >= 2)
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
                RunEndOfGestureRecognition(padIdx, ctx, settings, nowMs, shapeTemplates);
                ctx.State = GestureState.Cooldown;
                ctx.CooldownUntilTimestampMs = nowMs + Math.Max(0, settings.CooldownMs);
                ctx.FingerPaths.Clear();
                ctx.FingerStartTimestampsMs.Clear();
                ctx.FingerContactIds.Clear();
                ctx.FingerSlotIndices.Clear();
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
                    // New contact on this slot — open a fresh path.
                    ctx.FingerPaths.Add(new List<Vector2>());
                    ctx.FingerStartTimestampsMs.Add(nowMs);
                    ctx.FingerContactIds.Add(cid);
                    ctx.FingerSlotIndices.Add(s);
                    pathIdx = ctx.FingerPaths.Count - 1;
                }

                if (down && pathIdx >= 0)
                {
                    ctx.FingerPaths[pathIdx].Add(new Vector2(pad.FingerX[s], pad.FingerY[s]));
                    active++;
                }
                // No special handling for lifts — the path stays in the
                // list with its terminal positions; ActiveFingerCount
                // tracks how many slots are currently down.
            }
            ctx.ActiveFingerCount = active;
        }

        /// <summary>Fires <c>Touchpad N RadialZone{count}_{i}</c> when
        /// the finger is past the center deadzone and in zone i.
        /// Stable-zone fire-on-entry semantics: each new zone entry
        /// fires once; re-entering the same zone doesn't re-fire.</summary>
        private static void DetectRadialZones(int padIdx,
            TouchpadGestureContext ctx, TouchpadInputState pad,
            TouchpadGestureSettings settings)
        {
            if (ctx.FingerPaths.Count == 0) return;
            var path = ctx.FingerPaths[0];
            if (path.Count < 2) return;

            Vector2 start = path[0];
            Vector2 cur = path[path.Count - 1];
            Vector2 delta = cur - start;
            float dist = delta.Length();
            if (dist < settings.RadialCenterDeadzone) return;

            int zones = settings.RadialZoneCount;
            if (zones < 2) return;
            // Angle in radians, 0 = right, π/2 = down (touchpad space:
            // Y grows downward). Normalize to 0..2π.
            float ang = MathF.Atan2(delta.Y, delta.X);
            if (ang < 0) ang += 2f * MathF.PI;
            // Zone width = 2π / zones. Zone 0 is centered on +X (right);
            // offset by half-width so zone 0 spans -half_width..+half_width.
            float zoneWidth = 2f * MathF.PI / zones;
            int zone = (int)MathF.Floor((ang + zoneWidth / 2f) / zoneWidth) % zones;
            if (zone != ctx.CurrentRadialZone)
            {
                ctx.FiredGesturesThisFrame.Add($"Touchpad {padIdx} RadialZone{zones}_{zone}");
                ctx.CurrentRadialZone = zone;
            }
        }

        /// <summary>Fires <c>Touchpad N LongPress</c> when a single
        /// finger has been down for at least the configured threshold
        /// and the path stayed within the max-motion bound.</summary>
        private static void DetectLongPress(int padIdx,
            TouchpadGestureContext ctx, TouchpadInputState pad,
            TouchpadGestureSettings settings, long nowMs)
        {
            if (ctx.FingerPaths.Count != 1) return;
            string key = $"Touchpad {padIdx} LongPress";
            // One-shot per gesture: skip if already fired this gesture
            // (LastTapPosition is repurposed as a "fired LongPress this
            // session" sentinel via the X-coord).
            if (ctx.FiredGesturesThisFrame.Contains(key)) return;

            long elapsed = nowMs - ctx.FingerStartTimestampsMs[0];
            if (elapsed < settings.LongPressTimeWindowMs) return;

            var path = ctx.FingerPaths[0];
            if (path.Count < 2) return;
            float maxMotion = 0f;
            Vector2 start = path[0];
            for (int i = 1; i < path.Count; i++)
            {
                float d = (path[i] - start).Length();
                if (d > maxMotion) maxMotion = d;
            }
            if (maxMotion > settings.LongPressMaxMotion) return;

            ctx.FiredGesturesThisFrame.Add(key);
            // Skip the end-of-gesture swipe / tap recognition for this
            // gesture: clear the path so they have nothing to evaluate
            // against on the upcoming Accumulating → Recognizing pass.
            ctx.FingerPaths[0].Clear();
        }

        /// <summary>Manages the 2-finger session lifecycle:
        /// captures baseline distance + angle on entry, updates
        /// continuous pinch / rotate axis state, and fires the one-shot
        /// Pinch / Spread / RotateCW / RotateCCW threshold gestures.</summary>
        private static void DetectTwoFingerContinuous(int padIdx,
            TouchpadGestureContext ctx, TouchpadInputState pad,
            TouchpadGestureSettings settings, long nowMs)
        {
            int firstIdx = -1, secondIdx = -1;
            // Pick the two oldest active paths (longest-held = primary,
            // most-recent = secondary). Indices into ctx.FingerPaths.
            for (int i = 0; i < ctx.FingerPaths.Count; i++)
            {
                if (ctx.FingerPaths[i].Count == 0) continue;
                if (firstIdx < 0) firstIdx = i;
                else if (secondIdx < 0) { secondIdx = i; break; }
            }
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
                float angDelta = ang - ctx.TwoFingerInitialAngle;
                // Wrap into -π..+π.
                while (angDelta > MathF.PI) angDelta -= 2f * MathF.PI;
                while (angDelta < -MathF.PI) angDelta += 2f * MathF.PI;
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
        /// finger swipes also handled here. Long-press already fired
        /// mid-gesture in DetectLongPress and cleared its path, so it
        /// won't double-fire here.</summary>
        private static void RunEndOfGestureRecognition(int padIdx,
            TouchpadGestureContext ctx, TouchpadGestureSettings settings,
            long nowMs, IReadOnlyList<PDollarTemplate> shapeTemplates)
        {
            // Count fingers in this gesture by counting non-empty paths.
            int fingerCount = 0;
            for (int i = 0; i < ctx.FingerPaths.Count; i++)
                if (ctx.FingerPaths[i].Count > 0) fingerCount++;

            if (fingerCount == 0) return;

            // Single-finger end-of-gesture: swipe vs tap.
            if (fingerCount == 1)
            {
                var path = FirstNonEmptyPath(ctx);
                if (path == null || path.Count < 1) return;
                Vector2 start = path[0];
                Vector2 end = path[path.Count - 1];
                float dist = (end - start).Length();
                long startTs = ctx.FingerStartTimestampsMs[0];
                long elapsed = nowMs - startTs;

                // Tap branch: short, no significant motion.
                if (settings.EnableTaps
                    && elapsed <= settings.TapTimeWindowMs
                    && dist <= settings.TapMaxMotion)
                {
                    long gap = startTs - ctx.LastTapEndTimestampMs;
                    if (gap > settings.MultiTapGapMs) ctx.RecentTapCount = 0;
                    ctx.RecentTapCount++;
                    ctx.LastTapEndTimestampMs = nowMs;
                    ctx.LastTapPosition = end;
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
                if ((settings.EnableFourWaySwipes || settings.EnableEightWaySwipes)
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
                if (settings.EnableTwoFingerSwipes)
                {
                    var firstPath = FirstNonEmptyPath(ctx);
                    var secondPath = NthNonEmptyPath(ctx, 1);
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
                if (settings.EnableTaps)
                {
                    // 2-finger tap: both paths short + small motion.
                    var firstPath = FirstNonEmptyPath(ctx);
                    var secondPath = NthNonEmptyPath(ctx, 1);
                    long startTs = ctx.FingerStartTimestampsMs[0];
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
                bool gate = fingerCount switch
                {
                    3 => settings.EnableThreeFingerGestures,
                    4 => settings.EnableFourFingerGestures,
                    5 => settings.EnableFiveFingerGestures,
                    _ => false
                };
                if (!gate) return;

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
                for (int i = 0; i < ctx.FingerPaths.Count; i++)
                {
                    var p = ctx.FingerPaths[i];
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

        /// <summary>Walks the shape-template catalog with the $P
        /// recognizer if shapes are enabled + the catalog has templates
        /// matching the finger count. Fires the best match's name when
        /// the match score is under the per-template (or fallback to
        /// per-settings) threshold.</summary>
        private static void MaybeFireShape(int padIdx,
            TouchpadGestureContext ctx, TouchpadGestureSettings settings,
            IReadOnlyList<PDollarTemplate> templates, int fingerCount)
        {
            if (templates == null || templates.Count == 0) return;
            if (!settings.EnableShapeGestures && !HasCustomFingerCount(templates, fingerCount))
                return;

            // Collect this gesture's normalized finger paths.
            var fingerPaths = new List<List<Vector2>>(fingerCount);
            for (int i = 0; i < ctx.FingerPaths.Count; i++)
            {
                var p = ctx.FingerPaths[i];
                if (p != null && p.Count > 0) fingerPaths.Add(p);
            }
            if (fingerPaths.Count != fingerCount) return;

            string pdollarName = PDollarRecognizer.MatchByFingerCount(
                fingerPaths, templates, fingerCount,
                settings.GestureMatchThreshold, out _);

            // Single-finger shapes also run through the angular-margin
            // recognizer (GestureSign-style). It picks up direction-
            // dependent shapes like Square / Z / Triangle / Checkmark
            // that $P softens because point-cloud distance is permutation-
            // invariant and ignores stroke direction. The two matchers
            // produce different score scales:
            //   $P: lower = better (returns the lowest distance under threshold)
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
                // gestures gating PDollarRecognizer.MatchByFingerCount uses.
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
                var path = FirstNonEmptyPath(ctx);
                if (path != null && angTemplates.Count > 0)
                {
                    (angName, angScore) = AngularMarginRecognizer.Match(path, angTemplates);
                    if (angScore < AngularMarginRecognizer.DefaultAcceptScore)
                        angName = null;
                }
            }

            string firedName = angName ?? pdollarName;
            if (!string.IsNullOrEmpty(firedName))
                ctx.FiredGesturesThisFrame.Add($"Touchpad {padIdx} {firedName}");
        }

        private static bool HasCustomFingerCount(IReadOnlyList<PDollarTemplate> templates, int n)
        {
            for (int i = 0; i < templates.Count; i++)
                if (templates[i].FingerCount == n && templates[i].IsCustom) return true;
            return false;
        }

        private static List<Vector2> FirstNonEmptyPath(TouchpadGestureContext ctx)
        {
            for (int i = 0; i < ctx.FingerPaths.Count; i++)
                if (ctx.FingerPaths[i].Count > 0) return ctx.FingerPaths[i];
            return null;
        }

        private static List<Vector2> NthNonEmptyPath(TouchpadGestureContext ctx, int n)
        {
            int seen = 0;
            for (int i = 0; i < ctx.FingerPaths.Count; i++)
            {
                if (ctx.FingerPaths[i].Count > 0)
                {
                    if (seen == n) return ctx.FingerPaths[i];
                    seen++;
                }
            }
            return null;
        }
    }
}
