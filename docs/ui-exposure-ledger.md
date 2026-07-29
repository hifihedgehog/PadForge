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
| `TapMaxMotion` | **NO UI**. Zero app references |
| `LongPressMaxMotion` | **NO UI**. Zero app references |
| `TwoFingerSwipeAngularTolerance` | **NO UI**. Zero app references |
| `PinchThreshold` | **NO UI**. Zero app references |
| `RotateThresholdDegrees` | **NO UI**. Zero app references |
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
| `ParamSmoothingAlpha` | **Preserve-only** (`:1280`) |
| `ParamMoveThreshold` | **Preserve-only** (`:1281`) |
| `ParamTrackballDecay` | **Preserve-only** (`:1283`) |
| `GateDescriptor` | **Preserve-only** (`:1284`) |
| `Gate2Descriptor` | **Preserve-only** (`:1285`) |
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
| `Workshop*` (6 fields) | **Preserve-only**. Slot-level stamps an import writes |

## MappingRow (8 persisted)

All eight have cards.

## Standing rule

A new persisted field ships with its card in the same commit. Re-run this
audit before any release that adds settings.
