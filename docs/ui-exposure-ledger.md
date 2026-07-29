# UI exposure ledger

Every persisted setting the engine reads, checked for a way for the user to
reach it. A field with no card is a bug, not a gap.

Method: enumerate `[XmlAttribute]` properties on the settings classes, then
trace each to its app-side writer. Three outcomes.

- **Card**. A bound VM property writes it. Fine.
- **Preserve-only**. The app writes it only from a captured `p.stamp`, i.e.
  it round-trips whatever an import put there and the user can never author
  or change it. This is the defect class.
- **Internal**. Not a user setting (import bookkeeping, derived state).

Audit run 2026-07-29 against `v4-dev`.

## TouchpadGestureSettings (50 persisted)

| Field | Status |
|---|---|
| `TapMaxMotion` | Card as of `cacf627c+`. Touchpad tab, beside its own toggle |
| `LongPressMaxMotion` | Card as of `cacf627c+`. Touchpad tab, beside its own toggle |
| `TwoFingerSwipeAngularTolerance` | Card as of `cacf627c+`. Touchpad tab, beside its own toggle |
| `PinchThreshold` | Card as of `cacf627c+`. Touchpad tab, beside its own toggle |
| `RotateThresholdDegrees` | Card as of `cacf627c+`. Touchpad tab, beside its own toggle |
| `PointerRegionAuthored` | Internal. The handover flag, set by the region setters |
| `PointerStretchX/Y` | Internal. Deserialize-only legacy shims |
| all others | Card |

The five are one sibling set: gesture thresholds. Their siblings
(`SwipeDistanceThreshold`, `TapTimeWindowMs`, `LongPressTimeWindowMs`,
`RadialCenterDeadzone`, `GestureMatchThreshold`) all have cards on the
Touchpad tab. The family is half-exposed, so a user can set how LONG a tap
may take but not how far it may travel.

## MappingSource (56 persisted)

| Field | Status |
|---|---|
| `ParamCurveExponent` | **Preserve-only** (`SettingsService:1277`) |
| `ParamRangeOuter` | **Preserve-only** (`:1278`) |
| `ParamAntiDeadzone` | **Preserve-only** (`:1279`) |
| `GateDescriptor` | Card as of the gate expansion: a real source plus the row's Custom expression |
| `Gate2Descriptor` | Card, same expansion (second ANDed gate source) |
| `ParamStickDeadZoneInner` | **Preserve-only** (`:1287`) |
| `ParamStickDeadZoneShape` | **Preserve-only** (read at `:1248`) |
| `ParamFlickRotationOffsetDeg` | **Preserve-only** (read at `:1250`) |
| `ParamStickDeadzone` | **Never written by the app at all** |
| `ParamPointerCenter` / `ParamPointerExtent` | Card as of `5e1f0567` (import carrier, folds into the pad's region) |
| `ParamMotionInnerDz` / `OuterDz` / `ControllerOrientation` | Card via `MotionSteer*` |
| `ParamFlickDeadzoneAngle` | Card via `FlickForwardDeadzone` |
| `ParamYDescriptor` | Internal. Derived from the paired axis target |
| `InvertOutput` | **No card**. Documented as such in `MappingSourceItem.ToDomain` |

## MappingSet (12 persisted)

| Field | Status |
|---|---|
| `BaseLayerName` / `BaseColor` / `BaseIcon` | Card. Layer tab appearance (#119) |
| `SocdMode` / `SocdPairs` | Card. The SOCD card (#240) |
| `Authoritative` | Internal. An import flag |
| `Workshop*` (6 fields) | Folded into the device's OWN settings at assignment (`WorkshopTuningApplier`). The runtime overlays are gone |

## MappingRow (8 persisted)

All eight have cards.

## The rule

A setting PadForge does not expose is not imported. Not parked, not applied
invisibly, not reported. An imported profile must be reachable through the
same cards as a hand-built one, or the two feel different with nothing on
screen to explain why.

Removed under this rule: `mouse_smoothing`, `mouse_move_threshold` and
trackball `friction`. Touchpad glide IS supported, through the Touchpad tab's
Momentum card; the per-source decay was a second implementation of it on a
different physical model, reachable only when a pad drove a non-mouse target.

Binding STRUCTURE is not a setting either, but that does not exempt it from
needing a surface. `GateDescriptor` carried Steam's `requires_click`, so
dropping it would stop bindings firing rather than remove a knob. It got the
surface instead: the gate is now a real source plus a Custom combine
expression, both of which the user can already see and edit.

`pow`, `hypot` and `deadzone` joined the expression registry for the same
reason. The transforms the engine applies internally should be writable where
the user can reach them. That makes most of the remaining per-source `Param*`
family expressible as an expression rather than needing a card each:
`ParamRangeOuter` and `ParamStickDeadZoneInner` collapse into one `deadzone`
call, `ParamCurveExponent` is `pow`, `ParamStickDeadZoneShape` is `hypot` over
the pair.

## The pattern

A Steam config assumes ONE controller, so its tuning is per physical input.
PadForge already owns settings for those things, with cards. The import
cannot write them because it runs before a device is assigned and they are
keyed by device guid. Parking them somewhere else and reading the parking
spot at runtime is what creates a second, invisible settings system.

The fix is always the same shape: apply at assignment, into the user's own
setting, then clear the carrier. `WorkshopTuningApplier` is that seam. The
remaining per-source `Param*` family should join it rather than growing
cards of its own.

## Closed so far

- Pointer region (center + size): card, per pad, `5e1f0567` / `c9cfa0ac`.
- The five gesture thresholds: cards, `cacf627c+`.

- The six `Workshop*` slot stamps: applied at assignment, `WorkshopTuningApplier`.

Still open: the per-source `MappingSource` `Param*` family, which should route
through the same applier rather than gaining its own cards.

## Standing rule

A new persisted field ships with its card in the same commit. Re-run this
audit before any release that adds settings.
