using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace PadForge.Engine
{
    /// <summary>
    /// Looks up a stable physical-device PnP instance ID for an XInput-backed
    /// Xbox controller given its VID/PID. Used by <see cref="SdlDeviceWrapper.BuildInstanceGuid"/>
    /// when SDL reports a synthetic "XInput#N" path — the slot number in
    /// those paths is not stable (xinputhid can reshuffle slots when a
    /// second Xbox-VID device appears), so mapping persistent device
    /// identity to SDL's slot number breaks on reshuffle.
    ///
    /// A real Xbox controller's underlying HID instance ID is stable per
    /// physical device (it contains the BT MAC for Bluetooth devices and a
    /// USB hub/port address for wired devices). HIDMaestro virtual devices
    /// are filtered by checking for the "HIDMAESTRO" / "HMCOMPANION" /
    /// "HMXINPUT" substring in any ancestor's instance ID, or for the
    /// ROOT\VID_*&amp;IG_* root-enumerator pattern that HIDMaestro's xinputhid
    /// profiles use.
    /// </summary>
    public static class StableXInputInstance
    {
        /// <summary>
        /// App-provided predicate that returns true if the given XInput slot
        /// (0..3) is currently hidden by PadForge's hook. Wired at startup
        /// so Engine's <see cref="SdlDeviceWrapper.BuildInstanceGuid"/> can
        /// distinguish a virtual at a masked slot from a physical sharing the
        /// same VID/PID, without taking a layering dependency on the App
        /// assembly. Null when the hook isn't installed or the delegate
        /// hasn't been wired yet — in that case callers fall back to the
        /// non-HM-biased physical resolution.
        /// </summary>
        public static Func<int, bool> IsXInputSlotHiddenByHook;

        /// <summary>
        /// Set of (vid, pid) pairs that have ever been spawned as a
        /// HIDMaestro virtual in this process. Built up as App's Step 5
        /// creates virtuals. Used by AuthMask as a fallback "is this a HM
        /// VID/PID" signal when PnP enumeration lags behind the kernel —
        /// HIDMaestro removes its HID child from PnP several seconds before
        /// xinputhid's slot binding goes down, so FindHidMaestroChild can
        /// return null while the kernel slot is still holding the dying
        /// virtual. Any slot whose CapsEx matches a VID/PID in this set
        /// AND has no real (non-HM) HID counterpart in PnP is unambiguously
        /// an HM virtual and must stay masked.
        /// </summary>
        private static readonly System.Collections.Generic.HashSet<(ushort vid, ushort pid)>
            _sessionHmVidPids = new();
        private static readonly object _sessionHmVidPidsLock = new();

        /// <summary>Record that a HIDMaestro virtual with the given VID/PID
        /// has been (or is about to be) spawned this session. Intentionally
        /// additive: once recorded, the pair stays in the set until the
        /// process exits. HIDMaestro profile VID/PIDs aren't secrets, and
        /// matching any of them doesn't false-mask a real controller
        /// because the <see cref="Find"/> real-HID lookup gates that.</summary>
        public static void RememberHidMaestroProfile(ushort vid, ushort pid)
        {
            if (vid == 0 && pid == 0) return;
            lock (_sessionHmVidPidsLock)
            {
                _sessionHmVidPids.Add((vid, pid));
            }
        }

        /// <summary>Returns true if the (vid, pid) has been spawned as a HM
        /// virtual in this session (see <see cref="RememberHidMaestroProfile"/>).</summary>
        public static bool IsKnownHidMaestroVidPid(ushort vid, ushort pid)
        {
            lock (_sessionHmVidPidsLock)
            {
                return _sessionHmVidPids.Contains((vid, pid));
            }
        }

        /// <summary>
        /// Returns the first HIDMaestro-owned HID-class device instance ID
        /// whose PnP tree contains the given VID/PID, or null if none found.
        /// This is the INVERSE of <see cref="Find"/> — used to build a
        /// HIDMaestro-distinct InstanceGuid for virtuals that slip past the
        /// hook mask (so they never collide with the physical's GUID even
        /// if SDL briefly enumerates them).
        /// </summary>
        public static string FindHidMaestroChild(ushort vid, ushort pid)
        {
            string vidPidUsb = $"VID_{vid:X4}&PID_{pid:X4}";

            var guid = GUID_DEVCLASS_HIDCLASS;
            IntPtr devInfoSet = SetupDiGetClassDevsW(ref guid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT);
            if (devInfoSet == (IntPtr)(-1)) return null;

            try
            {
                var devInfoData = new SP_DEVINFO_DATA();
                devInfoData.cbSize = Marshal.SizeOf<SP_DEVINFO_DATA>();
                char[] buffer = new char[512];

                for (uint i = 0; SetupDiEnumDeviceInfo(devInfoSet, i, ref devInfoData); i++)
                {
                    if (!SetupDiGetDeviceInstanceIdW(devInfoSet, ref devInfoData, buffer, (uint)buffer.Length, out _))
                        continue;

                    int nullIdx = Array.IndexOf(buffer, '\0');
                    string instanceId = nullIdx >= 0 ? new string(buffer, 0, nullIdx) : new string(buffer);

                    // Only consider HID\ leaf children (skip ROOT\ enumerators).
                    // See the same reasoning in FindHidMaestroXInputSlots: the
                    // ROOT node would double-count and carries no useful slot
                    // identity.
                    if (!instanceId.StartsWith(@"HID\", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (!instanceId.Contains(vidPidUsb, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (!IsHidMaestroInstance(instanceId))
                        continue;

                    return instanceId;
                }
            }
            finally
            {
                SetupDiDestroyDeviceInfoList(devInfoSet);
            }

            return null;
        }

        /// <summary>
        /// Returns the first non-HIDMaestro HID-class device instance ID whose
        /// PnP tree contains the given VID/PID, or null if none found.
        /// </summary>
        public static string Find(ushort vid, ushort pid)
        {
            // USB HID format: VID_045E&PID_0B13
            string vidPidUsb = $"VID_{vid:X4}&PID_{pid:X4}";
            // BLE HID-over-GATT format: VID&02045E_PID&0B13 (02 = USB-assigned source)
            //                            VID&01045E_PID&0B13 (01 = Bluetooth SIG)
            string vidBle02 = $"VID&02{vid:X4}";
            string vidBle01 = $"VID&01{vid:X4}";
            string pidBle = $"PID&{pid:X4}";

            var guid = GUID_DEVCLASS_HIDCLASS;
            IntPtr devInfoSet = SetupDiGetClassDevsW(ref guid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT);
            if (devInfoSet == (IntPtr)(-1)) return null;

            try
            {
                var devInfoData = new SP_DEVINFO_DATA();
                devInfoData.cbSize = Marshal.SizeOf<SP_DEVINFO_DATA>();
                char[] buffer = new char[512];

                for (uint i = 0; SetupDiEnumDeviceInfo(devInfoSet, i, ref devInfoData); i++)
                {
                    if (!SetupDiGetDeviceInstanceIdW(devInfoSet, ref devInfoData, buffer, (uint)buffer.Length, out _))
                        continue;

                    int nullIdx = Array.IndexOf(buffer, '\0');
                    string instanceId = nullIdx >= 0 ? new string(buffer, 0, nullIdx) : new string(buffer);

                    bool match = instanceId.Contains(vidPidUsb, StringComparison.OrdinalIgnoreCase)
                        || (instanceId.Contains(pidBle, StringComparison.OrdinalIgnoreCase)
                            && (instanceId.Contains(vidBle02, StringComparison.OrdinalIgnoreCase)
                                || instanceId.Contains(vidBle01, StringComparison.OrdinalIgnoreCase)));

                    if (!match) continue;
                    if (IsHidMaestroInstance(instanceId)) continue;

                    return instanceId;
                }
            }
            finally
            {
                SetupDiDestroyDeviceInfoList(devInfoSet);
            }

            return null;
        }

        private static bool IsHidMaestroInstance(string instanceId)
        {
            if (MatchesHmPattern(instanceId)) return true;

            if (CM_Locate_DevNodeW(out uint devInst, instanceId, 0) != 0)
                return false;

            // Depth-0 hardware-ID check. Every HIDMaestro HID child has
            // "HID\HIDMaestro" in its Hardware IDs (CM_DRP_HARDWAREID,
            // REG_MULTI_SZ). This is the authoritative marker — unlike the
            // instance-ID walk below which can miss xinputhid-upper-filter
            // profiles (xbox-series-xs-bt and similar) whose HID child
            // lives under a parent chain that doesn't contain "HIDMAESTRO"
            // in any instance ID string. Matches the same signal Step 1's
            // IsHidMaestroAncestor uses.
            if (HasHidMaestroHardwareId(devInst))
                return true;

            var idBuf = new StringBuilder(512);
            for (int depth = 0; depth < 16; depth++)
            {
                idBuf.Clear();
                idBuf.EnsureCapacity(512);
                if (CM_Get_Device_IDW(devInst, idBuf, idBuf.Capacity, 0) == 0)
                {
                    if (MatchesHmPattern(idBuf.ToString())) return true;
                }

                // Also check hardware IDs at this level.
                if (HasHidMaestroHardwareId(devInst))
                    return true;

                if (CM_Get_Parent(out uint parent, devInst, 0) != 0) break;
                if (parent == 0 || parent == devInst) break;
                devInst = parent;
            }
            return false;
        }

        /// <summary>CM_DRP_HARDWAREID = 0x02. Returns REG_MULTI_SZ list of
        /// hardware IDs. True if any ID contains "HIDMaestro".</summary>
        private static bool HasHidMaestroHardwareId(uint devInst)
        {
            const uint CM_DRP_HARDWAREID = 0x02;
            var buf = new char[1024];
            int lenBytes = buf.Length * 2;
            if (CM_Get_DevNode_Registry_PropertyW(devInst, CM_DRP_HARDWAREID, out _, buf, ref lenBytes, 0) != 0)
                return false;

            int charCount = lenBytes / 2;
            int start = 0;
            for (int i = 0; i < charCount; i++)
            {
                if (buf[i] == '\0')
                {
                    if (i > start)
                    {
                        string id = new string(buf, start, i - start);
                        if (id.IndexOf("HIDMaestro", StringComparison.OrdinalIgnoreCase) >= 0)
                            return true;
                    }
                    start = i + 1;
                    // Double-null terminates the REG_MULTI_SZ.
                    if (i + 1 < charCount && buf[i + 1] == '\0') break;
                }
            }
            return false;
        }

        private static bool MatchesHmPattern(string id)
        {
            if (id == null) return false;
            if (id.IndexOf("HIDMAESTRO", StringComparison.OrdinalIgnoreCase) >= 0
                || id.IndexOf("HMCOMPANION", StringComparison.OrdinalIgnoreCase) >= 0
                || id.IndexOf("HMXINPUT", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
            if (id.StartsWith(@"ROOT\VID_", StringComparison.OrdinalIgnoreCase)
                && (id.IndexOf("&IG_", StringComparison.OrdinalIgnoreCase) >= 0
                    || id.IndexOf("&XI_", StringComparison.OrdinalIgnoreCase) >= 0))
                return true;
            return false;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SP_DEVINFO_DATA
        {
            public int cbSize;
            public Guid ClassGuid;
            public uint DevInst;
            public IntPtr Reserved;
        }

        private const uint DIGCF_PRESENT = 0x00000002;
        private static Guid GUID_DEVCLASS_HIDCLASS = new Guid("745a17a0-74d3-11d0-b6fe-00a0c90f57da");

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr SetupDiGetClassDevsW(ref Guid ClassGuid, IntPtr Enumerator, IntPtr hwndParent, uint Flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetupDiEnumDeviceInfo(IntPtr DeviceInfoSet, uint MemberIndex, ref SP_DEVINFO_DATA DeviceInfoData);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetupDiGetDeviceInstanceIdW(IntPtr DeviceInfoSet, ref SP_DEVINFO_DATA DeviceInfoData, [Out] char[] DeviceInstanceId, uint DeviceInstanceIdSize, out uint RequiredSize);

        [DllImport("setupapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetupDiDestroyDeviceInfoList(IntPtr DeviceInfoSet);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern int CM_Locate_DevNodeW(out uint devInst, string deviceId, int flags);

        [DllImport("cfgmgr32.dll")]
        private static extern int CM_Get_Parent(out uint parent, uint devInst, int flags);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern int CM_Get_Device_IDW(uint devInst, StringBuilder buffer, int len, int flags);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern int CM_Get_DevNode_Registry_PropertyW(
            uint devInst, uint ulProperty, out uint pulRegDataType,
            [Out] char[] buffer, ref int pulLength, int ulFlags);

        /// <summary>
        /// Returns the set of XInput slot indices (0–3) that are CURRENTLY
        /// bound to HIDMaestro-owned devices. Two detection paths:
        /// <list type="number">
        /// <item>xinputhid path (e.g. xbox-series-xs-bt): HID device has
        /// <c>Device Parameters\XInputUserIndex</c> DWORD set by
        /// xinputhid.sys. Cross-referenced against HIDMaestro hardware IDs.</item>
        /// <item>XUSB-companion path (e.g. xbox-360-wired): HID child has
        /// NO XInputUserIndex (xinputhid isn't bound; the XUSB companion
        /// surfaces the XInput slot). Match the slot's VID/PID reported by
        /// XInputGetCapabilitiesEx against HIDMaestro HID children's
        /// VID/PID.</item>
        /// </list>
        /// </summary>
        public static bool[] FindHidMaestroXInputSlots()
        {
            var result = new bool[4];
            var diag = new StringBuilder();
            diag.AppendLine($"[FindHMSlots @ {DateTime.Now:HH:mm:ss.fff}]");

            // Collect HIDMaestro HID children with their VID/PID and any
            // XInputUserIndex registry value.
            var hmChildren = new List<(ushort vid, ushort pid, int xInputIdx, string instId)>();

            var guid = GUID_DEVCLASS_HIDCLASS;
            IntPtr devInfoSet = SetupDiGetClassDevsW(ref guid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT);
            if (devInfoSet == (IntPtr)(-1)) return result;

            try
            {
                var devInfoData = new SP_DEVINFO_DATA();
                devInfoData.cbSize = Marshal.SizeOf<SP_DEVINFO_DATA>();
                char[] idBuffer = new char[512];

                for (uint i = 0; SetupDiEnumDeviceInfo(devInfoSet, i, ref devInfoData); i++)
                {
                    uint devInst = devInfoData.DevInst;

                    if (!IsHidMaestroByHardwareIdOrAncestor(devInst))
                        continue;

                    // Extract VID/PID from the device's instance ID.
                    if (!SetupDiGetDeviceInstanceIdW(devInfoSet, ref devInfoData, idBuffer, (uint)idBuffer.Length, out _))
                        continue;
                    int nullIdx = Array.IndexOf(idBuffer, '\0');
                    string instId = nullIdx >= 0 ? new string(idBuffer, 0, nullIdx) : new string(idBuffer);

                    // HIDMaestro exposes TWO HIDClass entries per virtual:
                    // 1. ROOT\VID_XXXX&PID_YYYY&IG_00\0000 — PnP root enumerator
                    // 2. HID\VID_XXXX&PID_YYYY&IG_00\...   — HID leaf child
                    // Both carry the same VID/PID, but they are one logical
                    // device. If we count both, `hmCount` doubles, and the
                    // count-safety gate below wrongly thinks every matching
                    // XInput slot is HM-owned — which masks the REAL
                    // controller that happens to share the VID/PID. Skip
                    // the ROOT enumerator and count only the HID leaf.
                    if (instId.StartsWith(@"ROOT\", StringComparison.OrdinalIgnoreCase))
                    {
                        diag.AppendLine($"  hmRoot (skipped): inst={instId}");
                        continue;
                    }

                    (ushort vid, ushort pid) = ParseVidPid(instId);
                    if (vid == 0 && pid == 0) continue;

                    int slot = ReadXInputUserIndex(devInfoSet, ref devInfoData);
                    hmChildren.Add((vid, pid, slot, instId));
                    diag.AppendLine($"  hmChild: VID={vid:X4} PID={pid:X4} XInputIdx={slot} inst={instId}");

                    // Primary: xinputhid-bound virtuals → direct slot mapping.
                    if (slot >= 0 && slot < 4)
                        result[slot] = true;
                }
            }
            finally
            {
                SetupDiDestroyDeviceInfoList(devInfoSet);
            }

            // Secondary: XUSB-companion virtuals lack XInputUserIndex. Match
            // by VID/PID via XInputGetCapabilitiesEx at each slot, but with
            // a count-safety gate: mark a slot only if every XInput slot
            // currently reporting the same VID/PID can be accounted for by
            // a HIDMaestro child. If there are fewer HM virtuals than
            // matching slots (i.e. at least one real controller shares the
            // VID/PID), we CANNOT distinguish which slots are virtual vs
            // real by VID/PID alone, so we mark nothing in that VID/PID
            // group. Missing a virtual here is recoverable: the packet-count
            // reconciler catches idle virtuals on later cycles. Falsely
            // masking a real controller (the scenario this gate prevents) is
            // user-visible and catastrophic.
            //
            // Real-world case: HIDMaestro xbox-series-xs-bt profile uses
            // VID=045E PID=0B13, which is also the VID/PID of the genuine
            // Xbox Series wireless controller over BT. Without this gate,
            // a single BT virtual would cause BOTH the virtual's slot AND
            // the real controller's slot to be masked, making the real
            // controller disappear from PadForge's Devices list.
            var slotVidPid = new (ushort vid, ushort pid, bool ok)[4];
            for (int s = 0; s < 4; s++)
            {
                if (GetXInputCapabilitiesEx((uint)s, out ushort v, out ushort p))
                {
                    slotVidPid[s] = (v, p, true);
                    diag.AppendLine($"  slot{s}: capsEx VID={v:X4} PID={p:X4} (already={result[s]})");
                }
                else
                {
                    slotVidPid[s] = (0, 0, false);
                    diag.AppendLine($"  slot{s}: capsEx FAILED (already={result[s]})");
                }
            }

            // Group slots by VID/PID and count HM children per VID/PID.
            var processed = new HashSet<(ushort, ushort)>();
            for (int s = 0; s < 4; s++)
            {
                if (!slotVidPid[s].ok) continue;
                var key = (slotVidPid[s].vid, slotVidPid[s].pid);
                if (!processed.Add(key)) continue;

                int slotCount = 0;
                for (int t = 0; t < 4; t++)
                {
                    if (slotVidPid[t].ok
                        && slotVidPid[t].vid == key.Item1
                        && slotVidPid[t].pid == key.Item2)
                        slotCount++;
                }
                int hmCount = 0;
                foreach (var c in hmChildren)
                    if (c.vid == key.Item1 && c.pid == key.Item2) hmCount++;

                diag.AppendLine($"    group VID={key.Item1:X4} PID={key.Item2:X4}: slots={slotCount} hm={hmCount}");

                if (hmCount == 0) continue;
                if (hmCount < slotCount)
                {
                    // Ambiguous — at least one real controller shares this
                    // VID/PID. Skip to avoid false-masking the real one.
                    diag.AppendLine($"      -> ambiguous (real shares VID/PID), NOT masking");
                    continue;
                }
                // hmCount >= slotCount: every slot with this VID/PID is
                // accounted for by a HM virtual, safe to mark them all.
                for (int t = 0; t < 4; t++)
                {
                    if (!slotVidPid[t].ok) continue;
                    if (slotVidPid[t].vid != key.Item1 || slotVidPid[t].pid != key.Item2) continue;
                    if (result[t]) continue;
                    result[t] = true;
                    diag.AppendLine($"      -> marking slot {t} (all {slotCount} matching slots are HM)");
                }
            }

            diag.Append("  RESULT: [");
            for (int r = 0; r < 4; r++) diag.Append((r == 0 ? "" : ",") + (result[r] ? "HM" : "-"));
            diag.AppendLine("]");
            try { System.IO.File.AppendAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "padforge-sort-diag.log"), diag.ToString()); } catch { }

            return result;
        }

        private static (ushort vid, ushort pid) ParseVidPid(string instanceId)
        {
            int vidIdx = instanceId.IndexOf("VID_", StringComparison.OrdinalIgnoreCase);
            int pidIdx = instanceId.IndexOf("PID_", StringComparison.OrdinalIgnoreCase);
            if (vidIdx < 0 || pidIdx < 0) return (0, 0);
            if (vidIdx + 8 > instanceId.Length || pidIdx + 8 > instanceId.Length) return (0, 0);
            try
            {
                ushort vid = Convert.ToUInt16(instanceId.Substring(vidIdx + 4, 4), 16);
                ushort pid = Convert.ToUInt16(instanceId.Substring(pidIdx + 4, 4), 16);
                return (vid, pid);
            }
            catch { return (0, 0); }
        }

        // XINPUT_CAPABILITIES_EX (xinput1_4.dll ordinal 108) layout:
        //   XINPUT_CAPABILITIES (20 bytes):
        //     BYTE Type, BYTE SubType, WORD Flags (4 bytes)
        //     XINPUT_GAMEPAD (12 bytes)
        //     XINPUT_VIBRATION (4 bytes: WORD wLeft, WORD wRight)  ← not bytes!
        //   WORD VendorId, WORD ProductId, WORD VersionNumber, WORD Reserved
        [StructLayout(LayoutKind.Sequential)]
        private struct XINPUT_CAPABILITIES_EX
        {
            public byte Type;
            public byte SubType;
            public ushort Flags;
            public ushort wButtons;
            public byte bLeftTrigger;
            public byte bRightTrigger;
            public short sThumbLX;
            public short sThumbLY;
            public short sThumbRX;
            public short sThumbRY;
            public ushort wLeftMotorSpeed;
            public ushort wRightMotorSpeed;
            public ushort VendorId;
            public ushort ProductId;
            public ushort VersionNumber;
            public ushort Reserved;
        }

        private static bool GetXInputCapabilitiesEx(uint slot, out ushort vid, out ushort pid)
        {
            vid = 0; pid = 0;
            try
            {
                if (XInputGetCapabilitiesEx(1, slot, 0, out var caps) != 0) return false;
                vid = caps.VendorId;
                pid = caps.ProductId;
                return vid != 0 || pid != 0;
            }
            catch { return false; }
        }

        /// <summary>
        /// Public wrapper around <c>XInputGetCapabilitiesEx</c> (ordinal 108).
        /// Returns the VID/PID currently reported at the given XInput slot,
        /// or false if the slot is empty / the call fails. Used by the Step 1
        /// defensive mask loop to classify each slot as HM vs real.
        /// </summary>
        public static bool TryGetXInputSlotVidPid(uint slot, out ushort vid, out ushort pid)
            => GetXInputCapabilitiesEx(slot, out vid, out pid);

        [DllImport("xinput1_4.dll", EntryPoint = "#108")]
        private static extern uint XInputGetCapabilitiesEx(
            uint dwVersion, uint dwUserIndex, uint dwFlags,
            out XINPUT_CAPABILITIES_EX pCapabilities);

        private static bool IsHidMaestroByHardwareIdOrAncestor(uint devInst)
        {
            if (HasHidMaestroHardwareId(devInst)) return true;

            var idBuf = new StringBuilder(512);
            uint current = devInst;
            for (int depth = 0; depth < 16; depth++)
            {
                idBuf.Clear();
                idBuf.EnsureCapacity(512);
                if (CM_Get_Device_IDW(current, idBuf, idBuf.Capacity, 0) == 0)
                {
                    if (MatchesHmPattern(idBuf.ToString())) return true;
                }

                if (HasHidMaestroHardwareId(current)) return true;

                if (CM_Get_Parent(out uint parent, current, 0) != 0) break;
                if (parent == 0 || parent == current) break;
                current = parent;
            }
            return false;
        }

        private static int ReadXInputUserIndex(IntPtr devInfoSet, ref SP_DEVINFO_DATA devInfoData)
        {
            IntPtr hKey = SetupDiOpenDevRegKey(devInfoSet, ref devInfoData,
                DICS_FLAG_GLOBAL, 0, DIREG_DEV, KEY_READ);
            if (hKey == IntPtr.Zero || hKey == (IntPtr)(-1))
                return -1;

            try
            {
                uint type;
                int value = 0;
                int size = sizeof(int);
                int rc = RegQueryValueExW(hKey, "XInputUserIndex", IntPtr.Zero, out type,
                    ref value, ref size);
                if (rc == 0 && type == REG_DWORD)
                    return value;
                return -1;
            }
            finally
            {
                RegCloseKey(hKey);
            }
        }

        private const uint DICS_FLAG_GLOBAL = 0x00000001;
        private const uint DIREG_DEV = 0x00000001;
        private const uint KEY_READ = 0x20019;
        private const uint REG_DWORD = 4;

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern IntPtr SetupDiOpenDevRegKey(IntPtr DeviceInfoSet, ref SP_DEVINFO_DATA DeviceInfoData,
            uint Scope, uint HwProfile, uint KeyType, uint samDesired);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int RegQueryValueExW(IntPtr hKey, string lpValueName, IntPtr lpReserved,
            out uint lpType, ref int lpData, ref int lpcbData);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern int RegCloseKey(IntPtr hKey);
    }
}
