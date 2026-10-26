# Fanatec CSL DD native force-feedback output

## Report-ID mismatch

A CSL DD (`VID_0EB7/PID_0020`) can be recognized as input while its
native FFB reports never reach a usable output collection. On the
Windows 11 device used to diagnose this failure, Windows reported:

| Collection | Output Report ID | OutputReportByteLength |
|---|---|---|
| COL01 | `0x01` | 8 |
| COL02 | `0x02` | 8 |
| COL03 | `0xFF` | 64 |

The eight-byte lengths include one Report ID byte and seven command
bytes. The joystick collections expose the vendor output value
`UsagePage=0xFF00`, `Usage=0x0002`, `BitSize=8`, `ReportCount=7`.
COL03 is a different 63-byte payload protocol.

`BuildWheelReport` previously prepended `0x00`, which was not an output
ID in any of those collections. `HidP_InitializeReportForID` returned
`HIDP_STATUS_REPORT_DOES_NOT_EXIST` for ID zero in memory-only checks.
Padding to OutputReportByteLength cannot repair an incompatible ID.
The Linux driver retaining an enumerated report object without assigning
its ID does not imply an unnumbered Windows output report.

## Endpoint resolution

For PID `0x0020`, resolve the command endpoint separately from the
collection SDL selected for input. Match the source's immediate PnP
parent and actual VID/PID, joystick top-level usage and seven-byte
vendor output field. Validate each candidate Report ID with the Windows
HID parser. Prefer the unique primary ID `0x01`; a sole other eligible
endpoint uses its own validated ID. Refuse ambiguous endpoints and
COL03 rather than trying guessed IDs or unrelated physical wheels.

Discovery uses access-zero metadata handles and sends no feature or
output reports. `--fanatec-hid-probe <output.json>` runs this discovery
before normal app, updater, HidHide and HIDMaestro startup. Existing
transport selection for other Fanatec PIDs and pedal rumble is retained.

## Delivery state and stopping

The previous `_appliedWheelFfb` cache recorded a requested signature even
when the writer returned false. An identical request could subsequently
be suppressed as though it had reached the wheel.

Only complete successful deliveries now count as applied. Failed writes
retry after 250 ms, including unchanged requests. A newly requested stop
can bypass backoff once; repeated failed stops remain throttled. Handle
failure/recovery invalidates the Fanatec output generation so configuration
and force state can be re-established.

Fanatec range/autocentre initialization precedes the requested effect.
The v5.1 switch that leaves range to the wheel is respected. Stopping
disables the constant-force, spring, damper, inertia and friction slots;
changing a condition disables the preceding condition slot.

Rate-limited diagnostics identify the source/resolved path, Report ID,
logical/final lengths, operation phase, Win32 error, bytes transferred,
elapsed time and unchanged/backoff skips. A successful Windows transfer
confirms delivery to the stack, not firmware-level acceptance.

## Validation and limits

The hardware owner reported that corrected builds based on both
PadForge 5.0 and 5.1 restored FFB in the GUI and in-game. The local 5.1
validation build used file version 5.1.0.1. Game titles and measurements
of force fidelity were not supplied; other Fanatec models were not tested.

Targeted regressions cover same-device endpoint selection, invalid and
ambiguous IDs, unrelated devices, report framing, short writes,
failure/retry caching, stop propagation and rotation-range behavior.
Automated validation does not send motor commands. The native metadata
test checks routing when the device is accessible; it is conditional
when no CSL DD collection is available to the test host.
