# UI exposure ledger

Every persisted setting the engine reads, checked for a way for the user to
reach it. A field with no card is a bug, not a gap.

Method: enumerate the `[XmlAttribute]` and `[XmlElement]` members of the
settings classes, then trace each to its app-side writer. Four outcomes.

- **Card**. A bound VM property writes it. Fine.
- **Folded**. An import carrier the engine never reads. `WorkshopTuningApplier`
  moves it onto the assigned device's own card and clears it.
- **Preserve-only**. The app writes it only from a captured `p.stamp`, so it
  round-trips whatever an import put there and the user can never author or
  change it. This is the defect class.
- **Internal**. Not a user setting (import bookkeeping, derived state, a
  migration shim).

Last run 2026-11-20 against `v6-dev` at `ab57ed58d`, which covers the window
`bf5dc2135..aa64c9d64`.

## TouchpadGestureSettings (59 persisted)

| Field | Status |
|---|---|
| `PointerRegionAuthored` | Internal. The handover flag, set by the region setters |
| `RegionSchema` | Internal. The region schema version, set on save |
| `PointerStretchX/Y` | Internal. Deserialize-only legacy shims |
| all 55 others | Card. Touchpad tab (`PadViewModel.Touchpad.cs` load and save) |

## MappingSource (60 persisted)

| Field | Status |
|---|---|
| `Kind`, `DeviceGuid`, `Descriptor`, `DescriptorDeviceGuid` | Card. The source line's mode, device and input pickers |
| `Invert`, `HalfAxis`, `Bidirectional`, `InvertOutput`, `DeadZone` | Card. The source line's switches and dials |
| `ParamUp` / `ParamDown` / `ParamModifier` and their `*DeviceGuid` | Card. The key pickers of the Incremental and Modifier modes |
| `ParamRate`, `ParamSticky`, `ParamMin`, `ParamMax` | Card. Incremental mode |
| `ParamAttackTime`, `ParamReleaseTime`, `ParamAutocenter`, `ParamReverseMultiplier` | Card. Ramp mode |
| `ParamRapidTriggerDistance` | Card. Rapid Trigger mode |
| `GyroSensitivity`, `MouseCursorSensitivity`, `IrPointerSensitivity`, `Sensitivity`, `ParamAccel` | Card. The source dials |
| `NoInherit` | Card. The row's Do Not Inherit switch, stamped onto its sources |
| `ParamWindRangeDeg` / `ParamWindPower` / `ParamWindUnwindRate` / `ParamAngleInnerDz` / `ParamAngleOuterDz` | Card. The stick's Steering rows, stamped per device |
| `ParamStickDeadzone` | Card as of this run: the Steering rows' Stick Deadzone, stamped per device. Before it, no app path wrote it and both steering modes read a fixed 15 percent |
| `ParamTiltRangeDeg` / `ParamTiltInnerDz` | Card. Gyro Tilt card (#292) |
| `ParamMotionInnerDz` / `OuterDz` / `ControllerOrientation` | Card via `MotionSteer*` |
| `ParamFlick*` (9 fields) | Card. Flick Stick card, pinned by `FlickStickTests.EveryFlickParamHasACardRowAndAResetButton` |
| `ParamPointerCenter` / `ParamPointerExtent` | Card. The import carrier folds into the pad's pointer region |
| `ParamYDescriptor` | Internal. Derived from the paired axis target |
| `ParamCurveExponent`, `ParamRangeOuter`, `ParamAntiDeadzone`, `ParamStickDeadZoneShape`, `ParamStickDeadZoneInner` | Card. The source line's Curve, Outer Range, Anti-Deadzone, Deadzone Shape and Inner Deadzone dials, on the row and on each added source. They show where the source holds a value, and on a continuous source whose output has no device card. `FoldSourceShaping` still moves them onto the stick and trigger cards at assignment where it can |
| `GateDescriptor`, `Gate2Descriptor` | Card. The source line's Only While and And While rows, shown where the source has a gate: the gate in words (an import's zone test reads as the stick or trigger range it checks), a picker to change it and a reset to clear it |

The save no longer captures and restores these seven. The row editor's two
view models load them (`MappingItem.LoadShapingAndGates`,
`MappingSourceItem.FromDomain`) and the save rebuilds every source from them
(`StoreShapingAndGates`, `ToDomain`), so each direction of an axis keeps its
own and a value set to off stays off. The capture now carries only a flick
source's Flick Stick values, for the saves where that card does not push them.

## MappingSet (26 persisted attributes, 4 elements)

| Field | Status |
|---|---|
| `Row`, `ShiftActivator`, `Menu`, `RumbleAudio` | Card. Mappings tab, layer tabs, Menus tab, Rumble Audio card |
| `BaseLayerName` / `BaseColor` / `BaseIcon` | Card. Layer tab appearance (#119) |
| `BaseFlyout` | Card. The Flyout row of Configure Base (#581), shown while the Shift Layer Flyout is on |
| `SocdMode` / `SocdPairs` | Card. The SOCD card (#240) |
| `KeepAwakeEnabled` / `KeepAwakeAxis` / `KeepAwakeDeflection` / `KeepAwakeMotion` | Card. The Keep Controller Awake card (#270) |
| `Authoritative` | Internal. An import flag |
| `Workshop*` (15 fields) | Folded. `WorkshopTuningApplier` clears every one at assignment, and the engine reads none (`UiExposureLedgerTests`) |

The gyro trio was the last runtime overlay. `WorkshopGyroRatchetDescriptors`,
`WorkshopGyroActivation` and `WorkshopGyroRecenterOnEngage` were read by
`InputManager.RefreshGyroEngageCfg` on every Authoritative slot with no card.
They now fold onto the Gyro tab's Engage card at assignment, and when
settings load for a device assigned before this change (the live slots,
every stored profile, the Default snapshot and an imported profile archive).
The Gyro On rows (a layer picker, While Held, While Not Held or Toggle, the
buttons, Any or Every)
store `PadSetting.GyroActivation`, and Re-Center When Gyro Turns On stores
`PadSetting.GyroRecenterOnEngage`. Steam's ratchet is Base's While Not Held on
any of its buttons, so it folds into the same field.

## MappingRow (14 persisted)

| Field | Status |
|---|---|
| `Target`, `LayerMask`, `CombineMode`, `CombineExpression`, `NoInherit` | Card. The row, its layer tab, the combine picker and formula box |
| `TrimDeadzone` / `TrimRate` / `TrimResetOnRelease` | Card. Stick Trim combine (#155) |
| `MotionResponse` / `MotionSpeed` / `MotionMinSpeed` / `MotionAngle` / `MotionDeadzone` | Card. The motion response rows |
| `SuppressBipolarPair` | Internal. Set by the row editor's own Custom build so a formula's first two letters stay two sources |

## Added in bf5dc2135..aa64c9d64

| Class | Field | Status |
|---|---|---|
| `CrosshairData` | `Number` | Internal. The crosshair's index |
| `CrosshairData` | the other 28 | Card. Extras page, Crosshair card (`CrosshairItem`) |
| `MagnifierData` | all 15 | Card. Extras page, Magnifier card (`MagnifierItem`) |
| `AppSettings` | `Crosshairs`, `Magnifier` | Card. The two Extras cards |
| `AppSettings` | `HomeCards` | Card. Home's Detailed, Compact and Auto picker |
| `AppSettings` | `CrosshairsLastOn` | Internal. Which crosshairs the Extras switch turns back on |
| `AppSettings` | `FlydigiScreenBindings` | Internal. Driver bindings owed back on the next start (#586) |
| `AppSettings` / `Profile` | `PollingRateMs`, `PollingRateOverrideMs` | Internal. Read-once migration shims, never written |
| `Profile` | `Crosshairs`, `Magnifier` | Card. The same cards, per profile |
| Macro action | `CrosshairNumber` | Card. The Toggle Crosshair step's picker |
| `PadSetting` | `Centering*` (12 fields) | Card. The Centering card (#588) |
| `ShiftActivator` | `Flyout` | Card. The Flyout row of Configure Layer (#581) |
| `MappingSet` | `BaseFlyout` | Card. See above |
| `UserDevice` | `WheelCalibration` | Card. Written by the Release Test, read by the Centering card |
| `UserDevice` | `FlydigiApexModel` | Internal. What the pad reported, kept so its tabs stay up while it sleeps |

## The rule

A setting PadForge does not expose is not imported. Not parked, not applied
invisibly, not reported. An imported profile must be reachable through the
same cards as a hand-built one, or the two feel different with nothing on
screen to explain why.

Removed under this rule: `mouse_smoothing`, `mouse_move_threshold` and
trackball `friction`. Touchpad glide IS supported, through the Touchpad tab's
Momentum card. The per-source decay was a second implementation of it on a
different physical model, reachable only when a pad drove a non-mouse target.

Binding structure is not a setting either, but that does not exempt it from
needing a surface. `GateDescriptor` carried Steam's `requires_click`, so
dropping it would stop bindings firing rather than remove a knob. The Steam
expansion gave it a surface: a real source plus a Custom combine expression,
both of which the user can see and edit. The importers that write the field
directly (#561 and later Steam paths) are covered by the source line's Only
While row.

`pow`, `hypot` and `deadzone` joined the expression registry for the same
reason. The transforms the engine applies internally should be writable where
the user can reach them: `ParamRangeOuter` and `ParamStickDeadZoneInner`
collapse into one `deadzone` call, `ParamCurveExponent` is `pow`,
`ParamStickDeadZoneShape` is `hypot` over the pair.

## The pattern

A Steam config assumes one controller, so its tuning is per physical input.
PadForge already owns settings for those things, with cards. The import
cannot write them because it runs before a device is assigned and they are
keyed by device guid. Parking them somewhere else and reading the parking
spot at run time is what creates a second, invisible settings system.

The fix is always the same shape: apply at assignment, into the user's own
setting, then clear the carrier. `WorkshopTuningApplier` is that seam.

## Standing rule

A new persisted field ships with its card in the same commit. Re-run this
audit before any release that adds settings. `UiExposureLedgerTests` fails
when a `Workshop*` stamp is not cleared at assignment or is read by the
engine.
