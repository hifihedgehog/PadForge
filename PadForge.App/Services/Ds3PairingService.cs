using System;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32;

namespace PadForge.Services
{
    /// <summary>
    /// In-app Bluetooth pairing for the Sony DualShock 3 (SIXAXIS, VID 054C PID 0268),
    /// the DS3 counterpart to <see cref="WiiPairingService"/> (issue #116). The DS3
    /// cannot be paired through Windows' "Add a device" dialog: it connects device-
    /// initiated on the reserved L2CAP HID PSMs the inbox stack refuses, which is why
    /// the BthPS3 filter driver exists. So pairing is a guided USB ceremony:
    ///
    ///   1. The pad is docked over USB and its interface bound to inbox WinUSB (its
    ///      magic reports 0xF2/0xF5 are absent from the HID descriptor, so HidUsb can't
    ///      send them; WinUSB sends the raw control transfers the firmware answers).
    ///   2. Sixpair: the PC's Bluetooth radio address is written into the pad
    ///      (SET_REPORT FEATURE 0xF5) so it pages THIS radio on PS-press.
    ///   3. A Name record is written into the radio's device list (BthPS3 identifies the
    ///      pad by name), AND a synthetic link key is written under Keys\&lt;radiomac&gt;
    ///      so bthport treats the pad as a remembered device and serves that Name to
    ///      BthPS3 on every connect. The clone reports a BLANK name over the air, so
    ///      without the remembered anchor bthport's live name request overwrites the
    ///      seeded Name and the minimal record is pruned on radio re-enumeration.
    ///   4. The radio is cycled so BthPS3/BthPS3PSM pick the record up.
    ///
    /// Then the user unplugs the pad and presses PS; <see cref="Common.Input.Ds3DirectService"/>
    /// opens the resulting BthPS3 raw PDO and streams it through SDL. All steps run from
    /// the always-elevated app: Administrators have FullControl of the BTHPORT device
    /// list; the Keys subtree is SYSTEM-ACL'd, so the link-key write goes through
    /// REG_OPTION_BACKUP_RESTORE after enabling the token's backup/restore privileges.
    /// </summary>
    public sealed class Ds3PairingService
    {
        public const ushort DS3_VID = 0x054C;
        public const ushort DS3_PID = 0x0268;

        private readonly Action<string> _log;
        public Ds3PairingService(Action<string> log = null)
            => _log = msg => { LogLine(msg); log?.Invoke(msg); };

        // Serializes every operation that touches the Bluetooth radio (pair, unpair).
        // Two radio cycles overlapping, or a cycle racing another teardown, is a path
        // into the same freed-context crash the forced PDO removal caused
        // (BthPS3.sys BSOD 0xD1, 2026-07-09). One radio op at a time, always.
        private static readonly object _radioGate = new();

        /// <summary>Pairing narration goes to the in-memory diagnostics
        /// ring (crash context) and to the dialog via the injected
        /// callback. PadForge writes no pairing log file.</summary>
        private static void LogLine(string message)
            => PadForge.Engine.SdlDiagLog.WriteLine("DS3PAIR " + message);

        /// <summary>True when at least one DualShock 3 is currently paired (a
        /// BTHPORT device record with the DS3 VID/PID that PadForge wrote at
        /// pair time). Read-only registry scan, no radio contact, safe to call
        /// from any thread. Mirrors <see cref="UnpairAllDs3"/>'s enumeration.
        /// The crash-safety policy uses this to decide whether BthPS3 PSM
        /// patching should be armed.</summary>
        public static bool AnyDs3Paired()
        {
            try
            {
                using var root = Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Services\BTHPORT\Parameters\Devices");
                if (root == null) return false;
                foreach (string mac in root.GetSubKeyNames())
                {
                    try
                    {
                        using var sub = root.OpenSubKey(mac);
                        if (sub?.GetValue("VID") is int vid && sub.GetValue("PID") is int pid
                            && vid == DS3_VID && pid == DS3_PID)
                            return true;
                    }
                    catch { /* skip records we can't read */ }
                }
            }
            catch { /* absent hive / access error => treat as none paired */ }
            return false;
        }

        /// <summary>The pure PSM-patch policy (issue #199 crash safety +
        /// the 2026-07-24 DsHidMini coexistence audit). Returns whether
        /// PadForge takes sole ownership of arming (AutoEnableFilter=0)
        /// and whether patching should be on.
        ///
        /// <para>With DsHidMini installed, that stack IS the system's DS3
        /// story and its pads connect only while BthPS3 patching is armed.
        /// Its pads leave NO BTHPORT VID/PID record (nothing in the
        /// Nefarius ecosystem writes one; BthPS3 identifies by remote name,
        /// BusLogic.c), so the AnyDs3Paired probe reads false there and the
        /// old policy disarmed patching at PadForge startup, breaking the
        /// foreign setup persistently (AutoEnableFilter=0 outlived
        /// PadForge). Policy now: never own, always armed, and the caller
        /// repairs any earlier ownership grab.</para>
        ///
        /// <para>Without DsHidMini, the crash-safety policy stands: PadForge
        /// owns arming, on only while this machine actually has a DS3. Patching
        /// off makes BthPS3's use-after-free-on-disconnect path (upstream
        /// nefarius/BthPS3 #48, unfixed at the bundled v2.10.470.0)
        /// unreachable, which is what turned a stray Wii Remote connect
        /// into a 0x50 bugcheck on 2026-07-10.</para>
        ///
        /// <para>The second argument is "does a DS3 live here", NOT "did
        /// PadForge pair one" (#265). Those differ, and the difference is a
        /// silent breakage: a pad paired outside our ceremony has no BTHPORT
        /// record, so the narrow reading disarms patching on a machine whose
        /// DS3 connects over BthPS3 daily. See Ds3DriverInstaller.MachineHasDs3.
        /// </para></summary>
        /// <remarks>The OR lives HERE, not at the call site. It was in the
        /// caller, where no test could reach it, and a mutation that narrowed
        /// it back to paired-only survived the suite untouched. A decision the
        /// tests cannot observe is a decision nothing is guarding.</remarks>
        internal static (bool TakeOwnership, bool Patching) PsmPatchPolicy(
            bool dsHidMiniInstalled, bool anyDs3Paired, bool machineHasDs3Node)
            => dsHidMiniInstalled ? (false, true) : (true, anyDs3Paired || machineHasDs3Node);

        /// <summary>Drives BthPS3 PSM patching to the policy state (see
        /// <see cref="PsmPatchPolicy"/>). No-op when BthPS3 isn't installed.
        /// Idempotent; the IOCTL toggle contacts no radio and needs no
        /// <see cref="_radioGate"/>.</summary>
        public static void ReconcilePsmPatchForCrashSafety(string reason)
        {
            try
            {
                if (!Ds3DriverInstaller.IsBthPs3Installed()) return;
                bool dshm = Ds3DriverInstaller.IsDsHidMiniInstalled();
                // "Is there a DS3 here" is NOT the same question as "did PadForge
                // pair one" (#265). A pad paired outside our ceremony leaves no
                // BTHPORT record at all, because BthPS3 identifies by remote name
                // and the pairing lives inside the controller. Asking only
                // AnyDs3Paired disarms patching on those machines and the pad
                // silently stops connecting, so the durable devnode marker counts
                // too.
                bool paired = AnyDs3Paired();
                bool hasNode = Ds3DriverInstaller.MachineHasDs3();
                var (takeOwnership, wantPatching) = PsmPatchPolicy(dshm, paired, hasNode);
                if (takeOwnership)
                    Ds3DriverInstaller.EnsurePadForgeOwnsPsmPatch();
                else
                    Ds3DriverInstaller.RestoreBthPs3AutoArm();
                LogLine($"PSM patch reconcile ({reason}): dshidmini={dshm} "
                        + $"paired={paired} node={hasNode} patching={wantPatching}.");
                // Wait for the filter when ARMING. This reconcile runs right
                // after the ceremony's radio cycle, and the control device is
                // absent while the filter re-attaches, so the arm used to
                // no-op and leave the pad to be refused on the very first
                // pairing of a clean machine. Disarming needs no wait: a
                // filter that is not attached is already not patching.
                int patched = Ds3DriverInstaller.SetPsmPatching(
                    wantPatching, LogLine, wantPatching ? 20000 : 0);
                if (wantPatching && patched == 0)
                    LogLine("WARNING: PSM patching is not armed; the pad will be refused over Bluetooth.");
            }
            catch (Exception ex) { LogLine("PSM patch reconcile failed: " + ex.Message); }
        }

        public sealed class PairResult
        {
            /// <summary>The pad's own BT MAC (from 0xF2), lowercase hex no separators.</summary>
            public string Ds3Mac { get; set; }
            /// <summary>The radio MAC written into the pad (human/big-endian order).</summary>
            public byte[] RadioMac { get; set; }
            public bool Success { get; set; }
            /// <summary>One of: no-radio, no-ds3-usb, winusb-bind-failed, sixpair-failed,
            /// identity-inject-failed, install-failed, ok.</summary>
            public string Error { get; set; }
        }

        // ── the six-step ceremony ───────────────────────────────────────────────

        /// <summary>
        /// Runs the full USB pairing ceremony. The DS3 must be connected over USB.
        /// Progress is reported through the constructor log callback.
        /// </summary>
        public PairResult RunPairing(CancellationToken ct = default)
        {
            var r = new PairResult();

            // 0. Ensure the driver stack is present (install once). Filled after grounding.
            if (!EnsureBthPs3Installed())
            {
                _log("BthPS3 driver install failed.");
                r.Error = "install-failed";
                return r;
            }

            // Release the runtime reader for the whole ceremony: it may be streaming the
            // pad over USB (WinUSB) or BT, and pairing needs exclusive WinUSB access for
            // the 0xF2/0xF5 magic reports. AllowReconnect in the finally re-arms it.
            PadForge.Common.Input.Ds3DirectService.SuppressAndRelease();
            System.Threading.Thread.Sleep(300);   // let the reader release the WinUSB handle
            try
            {
                return RunPairingCore(r, ct);
            }
            finally
            {
                PadForge.Common.Input.Ds3DirectService.AllowReconnect();
                // Reconcile PSM patching to the post-ceremony reality (issue
                // #199). Install armed patching for the ceremony; on success a
                // DS3 is now paired so it stays armed, and on any failure exit
                // with no DS3 paired it disarms so BthPS3 doesn't sit exposed.
                ReconcilePsmPatchForCrashSafety("ds3-pair-end");
            }
        }

        private PairResult RunPairingCore(PairResult r, CancellationToken ct)
        {
            // 1. Read this PC's Bluetooth radio address (the pairing target).
            byte[] radio = ReadRadioMac();
            if (radio == null) { _log("No Bluetooth radio found."); r.Error = "no-radio"; return r; }
            r.RadioMac = radio;
            _log($"Bluetooth radio: {Hex(radio, ':')}");

            // 2. Ensure the docked DS3 is bound to WinUSB so we can send its magic reports.
            //    Distinguish "no pad plugged in" from "Windows will not accept
            //    our driver package": the second is not something plugging the
            //    cable in fixes, and telling the user to check the cable sent
            //    discussion #283 chasing the wrong thing.
            if (!EnsureWinUsbBound(ct))
            {
                // The installer reports WHICH step failed. Asking
                // IsWinUsbPackageTrusted here instead blamed the certificate
                // for a missing signing tool or a rejected INF just as
                // readily, and a wrong cause is what left #283 with nothing
                // to act on.
                string why = Ds3DriverInstaller.LastWinUsbFailure;
                if (why == "sign-failed" || why == "driver-untrusted")
                {
                    _log("Windows will not install PadForge's USB driver for the DS3. "
                         + "The diagnostics log records which step failed.");
                    r.Error = "driver-untrusted";
                    return r;
                }
                _log("Could not bind the DS3 to WinUSB. Is it connected by USB cable?");
                r.Error = "winusb-bind-failed";
                return r;
            }

            // The dialog cancels its token when the user closes it mid-ceremony. Bail
            // before touching the pad or the radio so nothing runs headless after the
            // window is gone (the radio cycle in particular drops every BT device).
            if (ct.IsCancellationRequested) { r.Error = "cancelled"; return r; }

            string path = FindWinUsbDs3();
            if (path == null) { _log("DS3 not found on USB."); r.Error = "no-ds3-usb"; return r; }
            _log($"DS3 WinUSB interface: {path}");

            // WinUsb_Initialize requires the device handle opened FILE_FLAG_OVERLAPPED
            // (WinUSB contract; proven prototype ds3winusb Program.cs:153).
            IntPtr dev = CreateFile(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_RW, IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, IntPtr.Zero);
            if (dev == INVALID_HANDLE) { _log($"Opening the DS3 failed (err={Marshal.GetLastWin32Error()})."); r.Error = "no-ds3-usb"; return r; }
            try
            {
                if (!WinUsb_Initialize(dev, out IntPtr ifh))
                { _log($"WinUsb_Initialize failed (err={Marshal.GetLastWin32Error()})."); r.Error = "no-ds3-usb"; return r; }
                try
                {
                    // 3. Read the pad's own MAC (0xF2 bytes 4-9) - the registry key name.
                    byte[] f2 = new byte[17];
                    if (!GetFeature(ifh, 0xF2, f2))
                    {
                        int reportErr = Marshal.GetLastWin32Error();
                        // Split the failure at the transfer layer before
                        // giving up: a standard descriptor read through the
                        // SAME handle separates "the pad refuses the class
                        // request" from "this handle reaches nothing". A
                        // stale interface path opens and initializes but
                        // dies exactly here with ERROR_GEN_FAILURE (#285).
                        var dd = new byte[18];
                        bool ddOk = WinUsb_GetDescriptor(ifh, 0x01, 0, 0, dd, (uint)dd.Length, out uint ddLen);
                        _log(ddOk && ddLen >= 12
                            ? $"GET_REPORT 0xF2 failed (err={reportErr}); descriptor reads OK "
                              + $"(VID={dd[9]:X2}{dd[8]:X2} PID={dd[11]:X2}{dd[10]:X2}): the pad is refusing the request."
                            : $"GET_REPORT 0xF2 failed (err={reportErr}) and the descriptor read failed too "
                              + $"(err={(ddOk ? 0 : Marshal.GetLastWin32Error())}): this WinUSB handle reaches no live pad.");
                        System.Threading.Thread.Sleep(300);
                        if (!GetFeature(ifh, 0xF2, f2))
                        { _log($"Reading the DS3 MAC failed (err={Marshal.GetLastWin32Error()})."); r.Error = "sixpair-failed"; return r; }
                        _log("GET_REPORT 0xF2 succeeded on retry.");
                    }
                    byte[] ds3mac = new byte[6];
                    Array.Copy(f2, 4, ds3mac, 0, 6);
                    r.Ds3Mac = Hex(ds3mac, null).ToLowerInvariant();
                    _log($"DS3 address: {Hex(ds3mac, ':')}");

                    // 4. Sixpair: write the radio MAC into the pad (SET_REPORT FEATURE 0xF5).
                    //    Both proven references read 0xF5 before writing it (ds3winusb
                    //    Program.cs:182, DsHidMini reads HostAddress in device init), so
                    //    do the same read-before-write, then read back to confirm the
                    //    firmware actually committed the master (a returned-true control
                    //    transfer is not proof the pad stored it).
                    byte[] before = new byte[8];
                    if (GetFeature(ifh, 0xF5, before))
                        _log($"Master before sixpair: {Hex(before[2..8], ':')}");

                    byte[] set = new byte[8];
                    set[0] = 0x01; set[1] = 0x00;
                    Array.Copy(radio, 0, set, 2, 6);
                    if (!SetFeature(ifh, 0xF5, set)) { _log($"Sixpair write failed (err={Marshal.GetLastWin32Error()})."); r.Error = "sixpair-failed"; return r; }

                    byte[] after = new byte[8];
                    if (GetFeature(ifh, 0xF5, after))
                    {
                        byte[] got = after[2..8];
                        _log($"Master after sixpair: {Hex(got, ':')}");
                        if (!got.AsSpan().SequenceEqual(radio))
                        {
                            _log($"WARNING: pad did not store the radio address (wanted {Hex(radio, ':')}).");
                            r.Error = "sixpair-not-committed"; return r;
                        }
                    }
                    _log("Sixpair written and confirmed.");
                }
                finally { WinUsb_Free(ifh); }
            }
            finally { CloseHandle(dev); }

            // 5. Write the full REMEMBERED-device record: Name into Devices\<mac>, owner
            // set to SYSTEM (else bthport prunes the record on radio re-enumeration), and
            // a synthetic link key under Keys\<radio> so the pad is flagged
            // remembered+authenticated and its stored Name is served to BthPS3 on every
            // connect instead of the clone's blank over-air name. Hardware-confirmed
            // 2026-07-09 (rem=16, identified as SIXAXIS, survives cycles, no security block).
            if (ct.IsCancellationRequested) { r.Error = "cancelled"; return r; }

            // Steps 5-6 touch the radio: serialize against any concurrent unpair so
            // two cycles can't overlap. The pad is on USB here (no live BthPS3 link),
            // so the cycle disconnects nothing.
            lock (_radioGate)
            {
                if (!Ds3DriverInstaller.WriteRememberedDeviceRecord(radio, r.Ds3Mac, _log))
                { _log("Registering the pad failed."); r.Error = "identity-inject-failed"; return r; }
                _log("Pad registered with the Bluetooth stack.");

                // 6. Cycle the radio so the drivers pick up the new record.
                CycleRadio();
            }

            // 7. The cycle just detached the filter. Do not tell the user to
            // press PS until it is back AND patching is armed on it, because
            // in that window nothing rewrites the pad's reserved PSM and the
            // inbox HID stack refuses the connection: the pad then flashes
            // forever and the only recovery the user finds is deleting the
            // record and running the whole ceremony again.
            //
            // CycleRadio now sits out the fast-cycle teardown horizon before
            // returning, so this wait probes the POST-cycle filter instance,
            // never the dying one it used to pass against (bench 2026-08-07:
            // that false positive armed a corpse and the pad flashed after a
            // "successful" ceremony).
            if (!Ds3DriverInstaller.WaitForPsmControlDevice(20000))
            {
                // The filter is registered but its control device is gone:
                // the known BthPS3PSM wedge after an overlapped radio cycle
                // (its control device can only be re-created when its filter
                // collection is briefly empty, and a leaked name blocks every
                // retry until the driver reloads). Nothing PadForge can do
                // in-process recovers it, so say the truth instead of
                // reporting success into a pad that can only flash.
                _log("The Bluetooth PSM filter is wedged (known driver limitation "
                     + "after a fast radio restart). Restart the PC, then press the "
                     + "PS button; the pairing itself is already written.");
                ReconcilePsmPatchForCrashSafety("ds3-pair-wedged");
                r.Error = "psm-wedged";
                return r;
            }
            ReconcilePsmPatchForCrashSafety("ds3-pair-armed");

            _log("Bluetooth radio cycled. Unplug the DS3 and press the PS button.");

            r.Success = true;
            r.Error = "ok";
            return r;
        }

        /// <summary>Clears the pad's pairing (record + link-key anchor) and cycles the
        /// radio so a clean dry run (or a user "forget this controller") starts from a
        /// first-time state. Does not force-remove the PDO node (see the BSOD note).</summary>
        public void Unpair(string ds3Mac)
        {
            lock (_radioGate)
            {
                if (!string.IsNullOrEmpty(ds3Mac))
                {
                    // The Devices record is SYSTEM-owned, so this takes ownership back before
                    // deleting, and drops the Keys link-key anchor too (else the pad stays
                    // half-remembered and a later re-pair is confused).
                    byte[] radio = ReadRadioMac();
                    if (radio != null) Ds3DriverInstaller.DeleteRememberedDeviceRecord(radio, ds3Mac, _log);
                }
                // Do NOT force-remove the BthPS3 PDO node. dev.Remove() frees the driver's
                // per-connection context out of band; the radio cycle then drops the link
                // and BthPS3's remote-disconnect callback dereferences the freed context
                // -> BSOD 0xD1 (2026-07-09, confirmed in the crash dump). The cycle alone
                // drives BthPS3's normal in-order disconnect against a VALID context, the
                // same path a real power-off takes, and the transient PDO self-destroys.
                CycleRadio();
                _log("Pairing cleared.");
                // A DS3 was just forgotten. If none remain, disarm PSM patching
                // so BthPS3 goes dormant (issue #199 crash mitigation).
                ReconcilePsmPatchForCrashSafety("ds3-unpair");
            }
        }

        /// <summary>
        /// Clears the Bluetooth pairing for every DS3 record PadForge wrote, so a pad
        /// "forgotten" from the device list won't silently reconnect. Used from the
        /// device-list Remove action, where only the pad's VID/PID is known (the SDL
        /// virtual joystick carries no serial/MAC). Enumerates BTHPORT's device list for
        /// records with the DS3 VID/PID and drops each one's record + link-key anchor,
        /// then cycles the radio once (which drives BthPS3's own in-order disconnect of a
        /// still-connected pad). A machine with two DS3s clears both; that is acceptable
        /// for a "forget" action and is logged.
        /// </summary>
        public int UnpairAllDs3()
        {
          lock (_radioGate)
          {
            byte[] radio = ReadRadioMac();
            var macs = new System.Collections.Generic.List<string>();
            try
            {
                using var root = Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Services\BTHPORT\Parameters\Devices");
                if (root != null)
                {
                    foreach (string mac in root.GetSubKeyNames())
                    {
                        try
                        {
                            using var sub = root.OpenSubKey(mac);
                            if (sub?.GetValue("VID") is int vid && sub.GetValue("PID") is int pid
                                && vid == DS3_VID && pid == DS3_PID)
                                macs.Add(mac);
                        }
                        catch { /* skip records we can't read */ }
                    }
                }
            }
            catch (Exception ex) { _log("Enumerating DS3 records failed: " + ex.Message); }

            // Detach the live pad and stop the reader re-grabbing it, so deleting the
            // records + cycling the radio doesn't flash a ghost joystick back into the
            // list mid-unpair. AllowReconnect ALWAYS runs (even on the no-records early
            // return), else the caller's earlier SuppressAndRelease strands the pad.
            PadForge.Common.Input.Ds3DirectService.SuppressAndRelease();
            try
            {
                if (macs.Count == 0) return 0;
                if (radio != null)
                    foreach (string mac in macs)
                        Ds3DriverInstaller.DeleteRememberedDeviceRecord(radio, mac, _log);
                // No forced PDO node removal: dev.Remove() frees BthPS3's per-connection
                // context, and the cycle's HCI disconnect then faults on it (BSOD 0xD1,
                // 2026-07-09). The cycle alone disconnects the live pad through BthPS3's
                // normal path against a VALID context.
                CycleRadio();
                _log($"Unpaired {macs.Count} DualShock 3 controller(s).");
                // With these records gone, reconcile PSM patching: disarm it if
                // no DS3 remains paired (issue #199 crash mitigation).
                ReconcilePsmPatchForCrashSafety("ds3-unpair-all");
                return macs.Count;
            }
            finally { PadForge.Common.Input.Ds3DirectService.AllowReconnect(); }
          }
        }

        // ── local Bluetooth radio address (human/big-endian order per DsHidMini) ─

        /// <summary>The local radio's MAC in the byte order the DS3 expects (human /
        /// big-endian, i.e. rgBytes reversed - DsHidMini Ds3.c:364-368).</summary>
        /// <summary>Reads the local radio's address. Waits for the radio
        /// first: this is step 1 of the ceremony and it runs immediately after
        /// the driver install, whose last act is a radio re-enumeration. A
        /// straight read there returned null 300 ms after the install reported
        /// success, so the FIRST Pair click failed with "No Bluetooth radio
        /// found" and only a second click, seconds later, got through
        /// (arcade-PC log, 00:09:11.807 then 00:09:24.294).</summary>
        public byte[] ReadRadioMac()
        {
            Ds3DriverInstaller.WaitForBluetoothRadio(20000);
            var fp = new BLUETOOTH_FIND_RADIO_PARAMS { dwSize = (uint)Marshal.SizeOf<BLUETOOTH_FIND_RADIO_PARAMS>() };
            IntPtr hFind = BluetoothFindFirstRadio(ref fp, out IntPtr hRadio);
            if (hFind == IntPtr.Zero) return null;
            try
            {
                var info = new BLUETOOTH_RADIO_INFO { dwSize = (uint)Marshal.SizeOf<BLUETOOTH_RADIO_INFO>() };
                if (BluetoothGetRadioInfo(hRadio, ref info) != 0) return null;
                byte[] be = new byte[6];
                for (int i = 0; i < 6; i++) be[i] = (byte)((info.address.ullLong >> (8 * (5 - i))) & 0xFF);
                return be;
            }
            finally { CloseHandle(hRadio); BluetoothFindRadioClose(hFind); }
        }

        // ── WinUSB DS3 discovery + magic reports (proven prototype path) ─────────

        // Interface GUID from the shipped ds3_winusb.inf.
        private static readonly Guid DS3_WINUSB_IF = new Guid("B35924D6-3E16-4A9E-9782-5524A4B79BAC");

        private static string FindWinUsbDs3() => FindInterfacePath(DS3_WINUSB_IF);

        private static bool GetFeature(IntPtr ifh, byte reportId, byte[] buf)
        {
            var s = new WINUSB_SETUP_PACKET
            {
                RequestType = 0xA1, Request = 0x01,
                Value = (ushort)((0x03 << 8) | reportId), Index = 0, Length = (ushort)buf.Length
            };
            return WinUsb_ControlTransfer(ifh, s, buf, (uint)buf.Length, out _, IntPtr.Zero);
        }

        private static bool SetFeature(IntPtr ifh, byte reportId, byte[] buf)
        {
            var s = new WINUSB_SETUP_PACKET
            {
                RequestType = 0x21, Request = 0x09,
                Value = (ushort)((0x03 << 8) | reportId), Index = 0, Length = (ushort)buf.Length
            };
            return WinUsb_ControlTransfer(ifh, s, buf, (uint)buf.Length, out _, IntPtr.Zero);
        }

        // ── driver install + radio cycle + node removal (filled from grounding) ──

        private bool EnsureBthPs3Installed() => Ds3DriverInstaller.EnsureInstalled(_log);
        private bool EnsureWinUsbBound(CancellationToken ct) => Ds3DriverInstaller.EnsureWinUsbBound(_log, ct);
        private void CycleRadio() => Ds3DriverInstaller.CycleBluetoothRadio(_log);

        // ── helpers ─────────────────────────────────────────────────────────────

        private static string Hex(byte[] b, char? sep)
        {
            var sb = new System.Text.StringBuilder(b.Length * 3);
            for (int i = 0; i < b.Length; i++)
            {
                if (i > 0 && sep.HasValue) sb.Append(sep.Value);
                sb.Append(b[i].ToString("X2"));
            }
            return sb.ToString();
        }

        private static string FindInterfacePath(Guid ifGuid)
        {
            IntPtr set = SetupDiGetClassDevs(ref ifGuid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
            if (set == INVALID_HANDLE) return null;
            var did = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
            try
            {
                for (int i = 0; SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref ifGuid, i, ref did); i++)
                {
                    // ACTIVE only: a registration persists in the registry
                    // after the driver changes, and DIGCF_PRESENT filters by
                    // DEVICE presence, not by interface state. A stale
                    // registration on a pad now riding HidUsb enumerates
                    // here and hands back a path whose transfers die with
                    // ERROR_GEN_FAILURE (#285).
                    if ((did.Flags & SPINT_ACTIVE) == 0) continue;
                    int req = 0;
                    SetupDiGetDeviceInterfaceDetail(set, ref did, IntPtr.Zero, 0, ref req, IntPtr.Zero);
                    IntPtr det = Marshal.AllocHGlobal(req);
                    try
                    {
                        Marshal.WriteInt32(det, IntPtr.Size == 8 ? 8 : 6);
                        if (SetupDiGetDeviceInterfaceDetail(set, ref did, det, req, ref req, IntPtr.Zero))
                            return Marshal.PtrToStringUni(det + 4);
                    }
                    finally { Marshal.FreeHGlobal(det); }
                }
            }
            finally { SetupDiDestroyDeviceInfoList(set); }
            return null;
        }

        // ── P/Invoke ────────────────────────────────────────────────────────────

        private static readonly IntPtr INVALID_HANDLE = new IntPtr(-1);
        private const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000, FILE_SHARE_RW = 3, OPEN_EXISTING = 3, FILE_FLAG_OVERLAPPED = 0x40000000;
        private const int DIGCF_PRESENT = 0x2, DIGCF_DEVICEINTERFACE = 0x10, SPINT_ACTIVE = 0x1;

        [StructLayout(LayoutKind.Sequential)] private struct BLUETOOTH_FIND_RADIO_PARAMS { public uint dwSize; }
        [StructLayout(LayoutKind.Sequential)] private struct BLUETOOTH_ADDRESS { public ulong ullLong; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct BLUETOOTH_RADIO_INFO
        {
            public uint dwSize; public BLUETOOTH_ADDRESS address;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 248)] public string szName;
            public uint ulClassofDevice; public ushort lmpSubversion; public ushort manufacturer;
        }
        [DllImport("bthprops.cpl", SetLastError = true)] private static extern IntPtr BluetoothFindFirstRadio(ref BLUETOOTH_FIND_RADIO_PARAMS p, out IntPtr phRadio);
        [DllImport("bthprops.cpl", SetLastError = true)] private static extern bool BluetoothFindRadioClose(IntPtr hFind);
        [DllImport("bthprops.cpl")] private static extern uint BluetoothGetRadioInfo(IntPtr hRadio, ref BLUETOOTH_RADIO_INFO info);

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct WINUSB_SETUP_PACKET { public byte RequestType; public byte Request; public ushort Value; public ushort Index; public ushort Length; }
        [DllImport("winusb.dll", SetLastError = true)] private static extern bool WinUsb_Initialize(IntPtr dev, out IntPtr ifh);
        [DllImport("winusb.dll", SetLastError = true)] private static extern bool WinUsb_Free(IntPtr ifh);
        [DllImport("winusb.dll", SetLastError = true)]
        private static extern bool WinUsb_ControlTransfer(IntPtr ifh, WINUSB_SETUP_PACKET setup, byte[] buf, uint len, out uint moved, IntPtr ov);
        [DllImport("winusb.dll", SetLastError = true)]
        private static extern bool WinUsb_GetDescriptor(IntPtr ifh, byte type, byte index, ushort langId, byte[] buf, uint len, out uint transferred);

        [StructLayout(LayoutKind.Sequential)] private struct SP_DEVICE_INTERFACE_DATA { public int cbSize; public Guid InterfaceClassGuid; public int Flags; public IntPtr Reserved; }
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr SetupDiGetClassDevs(ref Guid g, IntPtr e, IntPtr w, int f);
        [DllImport("setupapi.dll", SetLastError = true)] private static extern bool SetupDiEnumDeviceInterfaces(IntPtr s, IntPtr d, ref Guid g, int i, ref SP_DEVICE_INTERFACE_DATA data);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr s, ref SP_DEVICE_INTERFACE_DATA d, IntPtr det, int ds, ref int req, IntPtr di);
        [DllImport("setupapi.dll")] private static extern bool SetupDiDestroyDeviceInfoList(IntPtr s);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateFile(string n, uint a, uint s, IntPtr sa, uint d, uint f, IntPtr t);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr h);
    }
}
