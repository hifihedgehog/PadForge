using System;
using System.Collections.Generic;
using System.Globalization;
using PadForge.Common.Input;
using PadForge.Engine.Data;

namespace PadForge.Services
{
    /// <summary>
    /// <para>Moves a Workshop import's device tuning into the device's OWN
    /// settings, once a device is actually assigned to the slot.</para>
    ///
    /// <para>A Steam config assumes one controller, so its tuning is per
    /// physical input: "the right stick uses this deadzone shape", "gyro
    /// engages on this button". PadForge already has settings for exactly
    /// those things, with cards the user can edit. The import could not write
    /// them because it runs before any device is assigned and those settings
    /// are keyed by device guid, so it parked them on the slot as
    /// <c>MappingSet.Workshop*</c> stamps and the engine consulted the stamps
    /// at runtime instead.</para>
    ///
    /// <para>That parking spot became a second, invisible settings system.
    /// The stick deadzone shape was the worst of it: the runtime read
    /// returned the stamp unconditionally for an Authoritative slot, so the
    /// user's own Dead Zone Shape control was overridden and editing it did
    /// nothing, with nothing on screen to say why.</para>
    ///
    /// <para>So the stamps are applied HERE, at assignment, and cleared. From
    /// then on the values live in the user's settings, the existing cards show
    /// and edit them, and the engine has one place to read.</para>
    ///
    /// <para>Two shapes of parked value get folded. Slot-level
    /// <c>Workshop*</c> stamps, and the per-SOURCE response shaping the import
    /// writes onto its rows (see <see cref="FoldSourceShaping"/>), which had
    /// the same defect one level down: live in the engine, absent from the
    /// cards that own those very knobs.</para>
    ///
    /// <para>Applied only where the user has not already chosen something, so
    /// re-assigning a device cannot silently overwrite tuning the user set by
    /// hand. Cleared unconditionally, because a stamp that has been offered
    /// once has done its job: leaving it would let it re-apply after the user
    /// deliberately changed the value back.</para>
    /// </summary>
    public static class WorkshopTuningApplier
    {
        /// <summary><para>Folds the slot's import stamps into
        /// <paramref name="ps"/>. Returns true when anything changed, so the
        /// caller can mark dirty.</para>
        /// <para>Call this from EVERY path that assigns a device to a slot.
        /// The runtime overlays this replaced applied on every path by
        /// construction, so wiring it into one assignment entry point and not
        /// its sibling silently dropped the tuning for the other. There are
        /// two today, DeviceService.OnAssignToSlot (the device list's assign
        /// command) and DeviceService.AssignDeviceToSlot (drag-drop and
        /// programmatic), and a third added later must not have to know this
        /// exists. It is idempotent and cheap, so calling it too often is
        /// free and calling it too seldom is a silent regression.</para>
        /// <para>N/A by design, and each for its own reason:</para>
        /// <para>WorkshopGyroRatchetDescriptors has no ratchet field on
        /// PadSetting and no ratchet control in any view, so there is no
        /// user-facing setting to fold it into. It stays a runtime overlay
        /// (InputManager's gyro engage config) until a card exists.</para>
        /// <para>ParamFlickRotationOffsetDeg has no card. It is read by the
        /// flick-stick angle path (SourceKindRuntime) and there is no
        /// rotation-offset control to fold it into, so it stays on the source
        /// until one exists. That is a real remaining gap, not a deliberate
        /// exclusion.</para></summary>
        public static bool ApplyToAssignedDevice(int slotIndex, PadSetting ps, string deviceGuid = null)
        {
            if (ps == null) return false;
            var sets = SettingsManager.SlotMappingSets;
            if (sets == null || slotIndex < 0 || slotIndex >= sets.Length) return false;
            var set = sets[slotIndex];
            if (set == null) return false;

            bool changed = false;

            // ── stick deadzone shape ──────────────────────────────────────
            if (!string.IsNullOrEmpty(set.WorkshopLeftStickDeadZoneShape))
            {
                if (IsDefaultShape(ps.LeftThumbDeadZoneShape))
                {
                    ps.LeftThumbDeadZoneShape = set.WorkshopLeftStickDeadZoneShape;
                    changed = true;
                }
                set.WorkshopLeftStickDeadZoneShape = "";
            }
            if (!string.IsNullOrEmpty(set.WorkshopRightStickDeadZoneShape))
            {
                if (IsDefaultShape(ps.RightThumbDeadZoneShape))
                {
                    ps.RightThumbDeadZoneShape = set.WorkshopRightStickDeadZoneShape;
                    changed = true;
                }
                set.WorkshopRightStickDeadZoneShape = "";
            }

            // ── gyro engage button ────────────────────────────────────────
            if (!string.IsNullOrEmpty(set.WorkshopGyroEngageDescriptor))
            {
                if (string.IsNullOrEmpty(ps.GyroAimEngageButton))
                {
                    ps.GyroAimEngageButton = set.WorkshopGyroEngageDescriptor;
                    // The import's descriptor is device-free by construction:
                    // it names a control on whatever device drives the slot.
                    ps.GyroAimEngageDeviceGuid = "";
                    ps.GyroAimEngageMode =
                        set.WorkshopGyroEngageToggle ? "Toggle"
                        : set.WorkshopGyroEngageInvert ? "ReleaseToEngage"
                        : "Hold";
                    changed = true;
                }
                set.WorkshopGyroEngageDescriptor = "";
                set.WorkshopGyroEngageToggle = false;
                set.WorkshopGyroEngageInvert = false;
            }

            // ── per-source response shaping ───────────────────────────────
            changed |= FoldSourceShaping(set, ps);

            // ── per-pad cursor acceleration ───────────────────────────────
            changed |= FoldTouchpadAcceleration(set, ps, deviceGuid);

            // ── gyro cursor acceleration ──────────────────────────────────
            changed |= FoldGyroAcceleration(set, ps);

            return changed;
        }


        /// <summary><para>Moves a stamped cursor acceleration onto the pad's own
        /// Mouse Acceleration card.</para>
        ///
        /// <para>Keyed off the SOURCE descriptor, not the row target, which is
        /// the opposite of the stick fold above and for a concrete reason: the
        /// target of a touchpad mouse row is Mouse X or Mouse Y, which names no
        /// pad. "Touchpad 1 Finger 0 X" names pad 1, and the setting is
        /// per-(device, pad), so the source is the only end of the row that
        /// identifies the card.</para>
        ///
        /// <para>NOT behaviour-preserving on the cursor lane, deliberately.
        /// ReadTouchpadMouseCounts never applied ParamAccel at all, so an
        /// import whose acceleration rode a touchpad-finger row into Mouse X
        /// or Mouse Y had it silently DROPPED, while the same stamp on an axis
        /// row did apply. The pad setting is read by both lanes, so folding
        /// makes the imported value take effect on the cursor for the first
        /// time. That is the point: the alternative is a value Steam authored,
        /// PadForge stored, and nothing honoured.</para>
        ///
        /// <para>On the axis lane it IS exactly preserving: the same
        /// v x (1 + accel x |v|) curve, and the fold only runs while the card
        /// sits at 0 where that curve is the identity. Nothing stacks either
        /// way, because the stamp is cleared.</para></summary>
        /// <summary><para>Moves a gyro source's stamped acceleration onto the
        /// Gyro Acceleration card.</para>
        ///
        /// <para>The gyro mouse lane runs both accelerations back to back
        /// (ApplyGyroAcceleration with the card's value, then
        /// ApplyPerSourceAccel with the stamp), so an import stacked invisibly
        /// on whatever the user set. Worse, ApplyPerSourceAccel hard-caps its
        /// result at full-scale because it is shared with deflection lanes,
        /// while the gyro lane is a RATE output whose own comment says that
        /// clamp is deliberately dropped ("a fast spin past the curve's
        /// reference rate must stay a fast spin"). So the imported value was
        /// also throttled by an accident of code sharing. After the fold it
        /// rides the card's own uncapped path, which means imported configs'
        /// fast flicks travel further than they did before: the ceiling was
        /// the artifact, not the fix.</para>
        ///
        /// <para>Same units both sides, verified not assumed: the card is
        /// parsed by TryParseFloatPs with no scaling (InputService, the gyro
        /// tuning build) and both formulas are 1 + accel x |v|. The card's
        /// range was widened to 5 to cover the translator's maximum
        /// (clamp(0..10) x 0.5), so the copy is lossless.</para>
        ///
        /// <para>Gyro descriptors only. A stick-hosted mouse row can carry
        /// this stamp too (EmitMouseAxes serves joystick_mouse), and there is
        /// no card for that one, so it stays per-source rather than being
        /// eaten looking for a home.</para></summary>
        private static bool FoldGyroAcceleration(MappingSet set, PadSetting ps)
        {
            if (set.Rows == null) return false;

            bool changed = false;
            foreach (var row in set.Rows)
            {
                if (row?.Sources == null) continue;
                foreach (var src in row.Sources)
                {
                    if (src == null || src.ParamAccel <= 0.0) continue;
                    if (!(src.Descriptor ?? "").StartsWith("Gyro ", StringComparison.Ordinal))
                        continue;

                    if (IsPercent(ps.GyroAcceleration, 0))
                    {
                        // F2 invariant, the card's own save format
                        // (InputService writes GyroAcceleration.ToString("F2", ic)).
                        ps.GyroAcceleration = src.ParamAccel.ToString("F2",
                            CultureInfo.InvariantCulture);
                        changed = true;
                    }
                    src.ParamAccel = 0.0;
                }
            }
            return changed;
        }

        private static bool FoldTouchpadAcceleration(MappingSet set, PadSetting ps, string deviceGuid)
        {
            if (set.Rows == null || string.IsNullOrEmpty(deviceGuid)) return false;

            bool changed = false;
            var list = ps.TouchpadSettings != null
                ? new List<PadForge.Engine.Touchpad.TouchpadSettingsEntry>(ps.TouchpadSettings)
                : new List<PadForge.Engine.Touchpad.TouchpadSettingsEntry>();
            bool listGrew = false;

            foreach (var row in set.Rows)
            {
                if (row?.Sources == null) continue;
                foreach (var src in row.Sources)
                {
                    if (src == null || src.ParamAccel <= 0.0) continue;
                    if (!TryPadIndexOfTouchpadSource(src.Descriptor, out int pad)) continue;

                    var entry = FindOrAddPadEntry(list, deviceGuid, pad, ref listGrew);
                    if (entry.Settings.MouseAcceleration <= 0f)
                    {
                        entry.Settings.MouseAcceleration = (float)src.ParamAccel;
                        changed = true;
                    }
                    src.ParamAccel = 0.0;
                }
            }

            if (listGrew) ps.TouchpadSettings = list.ToArray();
            return changed;
        }

        /// <summary>Pad index from a "Touchpad N ..." source descriptor.</summary>
        private static bool TryPadIndexOfTouchpadSource(string descriptor, out int pad)
        {
            pad = 0;
            if (string.IsNullOrEmpty(descriptor)) return false;
            const string prefix = "Touchpad ";
            if (!descriptor.StartsWith(prefix, StringComparison.Ordinal)) return false;
            int i = prefix.Length, start = i;
            while (i < descriptor.Length && char.IsDigit(descriptor[i])) i++;
            return i > start
                && int.TryParse(descriptor.Substring(start, i - start), out pad);
        }

        /// <summary>The (device, pad) entry, created at defaults if absent.
        /// Same create-or-find shape the Touchpad tab's own push uses, so the
        /// tab and this fold cannot disagree about which entry owns a pad.</summary>
        private static PadForge.Engine.Touchpad.TouchpadSettingsEntry FindOrAddPadEntry(
            List<PadForge.Engine.Touchpad.TouchpadSettingsEntry> list,
            string deviceGuid, int pad, ref bool listGrew)
        {
            foreach (var e in list)
            {
                if (e?.Settings == null) continue;
                if (e.TouchpadIndex != pad) continue;
                if (!string.Equals(e.DeviceGuid, deviceGuid, StringComparison.OrdinalIgnoreCase)) continue;
                return e;
            }
            var added = new PadForge.Engine.Touchpad.TouchpadSettingsEntry
            {
                DeviceGuid = deviceGuid,
                TouchpadIndex = pad,
                Settings = PadForge.Engine.Touchpad.TouchpadGestureSettings.Default(),
            };
            list.Add(added);
            listGrew = true;
            return added;
        }

        /// <summary>True when the stored shape is absent or the serialized
        /// default, i.e. the user has not chosen one.</summary>
        private static bool IsDefaultShape(string shape) =>
            string.IsNullOrEmpty(shape) || shape == "2";

        /// <summary><para>Moves the per-SOURCE response shaping an import
        /// stamps on its rows (curve exponent, outer range, anti-deadzone)
        /// into the per-input cards that already exist for those three, then
        /// clears the stamps so exactly one layer applies them.</para>
        ///
        /// <para>These are the same three knobs twice over. The engine reads
        /// <c>ParamCurveExponent</c> / <c>ParamRangeOuter</c> /
        /// <c>ParamAntiDeadzone</c> off the source when the row is read, and
        /// Step3 reads SensitivityCurve / MaxRange / AntiDeadZone off the
        /// device when the output pad is built. Both are live, so a stamp that
        /// stayed put while its value was also copied to a card would apply
        /// the curve TWICE. Hence move, never copy.</para>
        ///
        /// <para>Keyed on the row's TARGET, not its source descriptor,
        /// because the card applies to the OUTPUT axis: a Steam config that
        /// redirects the left stick onto the right pair (output_joystick)
        /// must land on the right stick's card, which is the same rule the
        /// slot-level deadzone_shape stamp already follows.</para>
        ///
        /// <para>The outer range is folded ONLY when no stick geometry is
        /// stamped. With a shape present the engine spends
        /// <c>ParamRangeOuter</c> as the deadzone's outer radius inside
        /// ApplyStickDeadZoneShape and deliberately suppresses the scalar
        /// tail (its own <c>hasOuter</c> requires shape == 0), so folding it
        /// there would move a radius out of the geometry that still needs
        /// it. This mirrors that guard rather than restating it.</para>
        ///
        /// <para>Semantics were checked against the card pipeline rather than
        /// assumed: Steam's outer radius is <c>mag / outer</c> and MaxRange is
        /// the same upper bound, so percent = outer x 100; anti-deadzone is
        /// <c>a + (1-a) x mag</c> on BOTH sides; and both apply range, then
        /// curve, then anti, in that order. The exponent becomes explicit
        /// control points instead of the legacy single-number curve, whose
        /// scale is <c>4^(-v/100)</c> and would read a bare "2.0" as very
        /// nearly linear.</para></summary>
        private static bool FoldSourceShaping(MappingSet set, PadSetting ps)
        {
            if (set.Rows == null) return false;

            bool changed = false;
            foreach (var row in set.Rows)
            {
                if (row?.Sources == null) continue;
                var card = ShapingCardFor(row.Target, ps);
                if (card == null) continue;

                foreach (var src in row.Sources)
                {
                    if (src == null) continue;

                    if (src.ParamCurveExponent > 0.0 && src.ParamCurveExponent != 1.0)
                    {
                        if (Common.CurveLut.IsLinear(card.GetCurve()))
                        {
                            card.SetCurve(CurveFromExponent(src.ParamCurveExponent));
                            changed = true;
                        }
                        src.ParamCurveExponent = 0.0;
                    }

                    if (src.ParamAntiDeadzone > 0.0 && src.ParamAntiDeadzone < 1.0)
                    {
                        if (IsPercent(card.GetAnti(), 0))
                        {
                            card.SetAnti(Percent(src.ParamAntiDeadzone));
                            changed = true;
                        }
                        src.ParamAntiDeadzone = 0.0;
                    }

                    if (src.ParamStickDeadZoneShape != 0)
                    {
                        changed |= FoldStickGeometry(src, card);
                    }
                    else if (src.ParamRangeOuter > 0.0 && src.ParamRangeOuter < 1.0)
                    {
                        if (IsPercent(card.GetRange(), 100))
                        {
                            card.SetRange(Percent(src.ParamRangeOuter));
                            changed = true;
                        }
                        src.ParamRangeOuter = 0.0;
                    }
                }
            }
            return changed;
        }

        /// <summary><para>Moves a stamped stick deadzone geometry (shape, inner
        /// radius, outer radius) onto the stick's own Dead Zone / Dead Zone
        /// Shape / Max Range cards, as one unit.</para>
        ///
        /// <para>The three travel together or not at all. The engine reads them
        /// as a single band in ApplyStickDeadZoneShape (<c>mag &lt;= inner</c>
        /// zeroes, then <c>(mag-inner)/(outer-inner)</c> rescales), so folding
        /// the inner radius while leaving the outer behind would hand the row
        /// read a band with no floor and the card a floor with no band.</para>
        ///
        /// <para>Equivalence was checked against both card paths, not assumed.
        /// Steam Circle is engine shape 2, and Step3's ComputeRadial scaled
        /// branch computes <c>(rawMag - dzR)/(mrR - dzR)</c> where dzR is the
        /// deadzone ellipse radius along the input direction, which collapses
        /// to plain dz once both axes carry the same value, giving the same
        /// circle and the same band. Steam Cross / Square is engine shape 1,
        /// and the Axial path's ApplySingleDeadZone computes
        /// <c>(magnitude - dz)/(maxRange - dz)</c> per axis, which is that
        /// same band in one dimension. The shape mapping (Circle to card "2",
        /// Cross / Square to card "0") is the one the slot-level deadzone_shape
        /// stamp already uses.</para>
        ///
        /// <para>Each axis writes only its own Dead Zone field, and the pair's
        /// two rows carry the same stamped value, so the pair lands as
        /// dzX == dzY: the circle Steam authored. The shape field is per stick
        /// rather than per axis, so whichever row arrives first sets it and the
        /// second agrees.</para></summary>
        private static bool FoldStickGeometry(MappingSource src, ShapingCard card)
        {
            bool changed = false;

            // Shape first, so a card left at its ScaledRadial default is
            // switched to Axial before the radii that ride it are written.
            if (card.SetShape != null && IsDefaultShape(card.GetShape()))
            {
                string mapped = src.ParamStickDeadZoneShape == 2 ? "2" : "0";
                if (!string.Equals(card.GetShape(), mapped, StringComparison.Ordinal))
                {
                    card.SetShape(mapped);
                    changed = true;
                }
            }

            if (src.ParamStickDeadZoneInner > 0.0 && src.ParamStickDeadZoneInner < 1.0
                && IsPercent(card.GetDeadZone(), 0))
            {
                card.SetDeadZone(Percent(src.ParamStickDeadZoneInner));
                changed = true;
            }

            if (src.ParamRangeOuter > 0.0 && src.ParamRangeOuter < 1.0
                && IsPercent(card.GetRange(), 100))
            {
                card.SetRange(Percent(src.ParamRangeOuter));
                changed = true;
            }

            // Cleared as a unit, including the shape: leaving the shape would
            // keep ApplyStickDeadZoneShape live over radii it no longer has,
            // and leaving the outer would keep it out of the scalar tail's
            // reach too (that guard reads shape == 0).
            src.ParamStickDeadZoneShape = 0;
            src.ParamStickDeadZoneInner = 0.0;
            src.ParamRangeOuter = 0.0;
            return changed;
        }

        /// <summary>The card fields for a row target, or null when the
        /// target is not one of the six shaped inputs. Named targets only:
        /// a RawAxis{n} target's stick is resolved from the slot's own axis
        /// layout, which lives on the view model, not here.</summary>
        private static ShapingCard ShapingCardFor(string target, PadSetting ps) => target switch
        {
            "LeftThumbAxisX" => new ShapingCard(
                () => ps.LeftThumbSensitivityCurveX, v => ps.LeftThumbSensitivityCurveX = v,
                () => ps.LeftThumbMaxRangeX, v => ps.LeftThumbMaxRangeX = v,
                () => ps.LeftThumbAntiDeadZoneX, v => ps.LeftThumbAntiDeadZoneX = v,
                () => ps.LeftThumbDeadZoneX, v => ps.LeftThumbDeadZoneX = v,
                () => ps.LeftThumbDeadZoneShape, v => ps.LeftThumbDeadZoneShape = v),
            "LeftThumbAxisY" => new ShapingCard(
                () => ps.LeftThumbSensitivityCurveY, v => ps.LeftThumbSensitivityCurveY = v,
                () => ps.LeftThumbMaxRangeY, v => ps.LeftThumbMaxRangeY = v,
                () => ps.LeftThumbAntiDeadZoneY, v => ps.LeftThumbAntiDeadZoneY = v,
                () => ps.LeftThumbDeadZoneY, v => ps.LeftThumbDeadZoneY = v,
                () => ps.LeftThumbDeadZoneShape, v => ps.LeftThumbDeadZoneShape = v),
            "RightThumbAxisX" => new ShapingCard(
                () => ps.RightThumbSensitivityCurveX, v => ps.RightThumbSensitivityCurveX = v,
                () => ps.RightThumbMaxRangeX, v => ps.RightThumbMaxRangeX = v,
                () => ps.RightThumbAntiDeadZoneX, v => ps.RightThumbAntiDeadZoneX = v,
                () => ps.RightThumbDeadZoneX, v => ps.RightThumbDeadZoneX = v,
                () => ps.RightThumbDeadZoneShape, v => ps.RightThumbDeadZoneShape = v),
            "RightThumbAxisY" => new ShapingCard(
                () => ps.RightThumbSensitivityCurveY, v => ps.RightThumbSensitivityCurveY = v,
                () => ps.RightThumbMaxRangeY, v => ps.RightThumbMaxRangeY = v,
                () => ps.RightThumbAntiDeadZoneY, v => ps.RightThumbAntiDeadZoneY = v,
                () => ps.RightThumbDeadZoneY, v => ps.RightThumbDeadZoneY = v,
                () => ps.RightThumbDeadZoneShape, v => ps.RightThumbDeadZoneShape = v),
            // A trigger is one-dimensional, so it has a Dead Zone but no Dead
            // Zone Shape. The import never stamps a geometry on one anyway
            // (the translator gates stickShape on IsStick), so the null shape
            // setter is belt and braces rather than a live path.
            "LeftTrigger" => new ShapingCard(
                () => ps.LeftTriggerSensitivityCurve, v => ps.LeftTriggerSensitivityCurve = v,
                () => ps.LeftTriggerMaxRange, v => ps.LeftTriggerMaxRange = v,
                () => ps.LeftTriggerAntiDeadZone, v => ps.LeftTriggerAntiDeadZone = v,
                () => ps.LeftTriggerDeadZone, v => ps.LeftTriggerDeadZone = v,
                null, null),
            "RightTrigger" => new ShapingCard(
                () => ps.RightTriggerSensitivityCurve, v => ps.RightTriggerSensitivityCurve = v,
                () => ps.RightTriggerMaxRange, v => ps.RightTriggerMaxRange = v,
                () => ps.RightTriggerAntiDeadZone, v => ps.RightTriggerAntiDeadZone = v,
                () => ps.RightTriggerDeadZone, v => ps.RightTriggerDeadZone = v,
                null, null),
            _ => null,
        };

        private sealed class ShapingCard
        {
            public ShapingCard(Func<string> getCurve, Action<string> setCurve,
                Func<string> getRange, Action<string> setRange,
                Func<string> getAnti, Action<string> setAnti,
                Func<string> getDeadZone, Action<string> setDeadZone,
                Func<string> getShape, Action<string> setShape)
            {
                GetCurve = getCurve; SetCurve = setCurve;
                GetRange = getRange; SetRange = setRange;
                GetAnti = getAnti; SetAnti = setAnti;
                GetDeadZone = getDeadZone; SetDeadZone = setDeadZone;
                GetShape = getShape; SetShape = setShape;
            }

            public Func<string> GetCurve { get; }
            public Action<string> SetCurve { get; }
            public Func<string> GetRange { get; }
            public Action<string> SetRange { get; }
            public Func<string> GetAnti { get; }
            public Action<string> SetAnti { get; }
            public Func<string> GetDeadZone { get; }
            public Action<string> SetDeadZone { get; }

            /// <summary>Null on a one-dimensional input, which has no shape.</summary>
            public Func<string> GetShape { get; }
            public Action<string> SetShape { get; }
        }

        /// <summary>A 0..1 fraction as the card's percent string.</summary>
        private static string Percent(double fraction) =>
            Math.Round(fraction * 100.0, 2).ToString(CultureInfo.InvariantCulture);

        /// <summary>True when a percent field still holds the given default,
        /// i.e. the user has not chosen a value. An unparseable or empty
        /// field counts as the default, matching how Step3 reads it.</summary>
        private static bool IsPercent(string stored, double expected)
        {
            if (string.IsNullOrWhiteSpace(stored)) return true;
            return double.TryParse(stored, NumberStyles.Float, CultureInfo.InvariantCulture,
                out double v) && Math.Abs(v - expected) < 0.001;
        }

        /// <summary>y = x^exponent as the card's control-point string.
        /// Explicit points rather than the legacy single number, which the
        /// card would read on its own 4^(-v/100) scale and get wrong.</summary>
        private static string CurveFromExponent(double exponent)
        {
            const int steps = 8;
            var points = new List<(double X, double Y)>(steps + 1);
            for (int i = 0; i <= steps; i++)
            {
                double x = (double)i / steps;
                points.Add((x, Math.Clamp(Math.Pow(x, exponent), 0.0, 1.0)));
            }
            return Common.CurveLut.Serialize(points);
        }
    }
}
