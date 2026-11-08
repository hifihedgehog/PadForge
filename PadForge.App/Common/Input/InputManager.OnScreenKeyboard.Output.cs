using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using PadForge.Common.Input.OnScreenKeyboard;
using PadForge.Engine;
using PadForge.Engine.Menus;

namespace PadForge.Common.Input
{
    /// <summary>
    /// Where the On-Screen Keyboard's keys go (#566), the keyboard menu's
    /// Types Through: Windows Input, the Keyboard device of a Keyboard + Mouse
    /// slot, or a HIDMaestro virtual keyboard the menu makes itself. A key the
    /// chosen keyboard cannot send goes through Windows Input, and so does
    /// text no key types, as Unicode, since a keyboard device sends keys, not
    /// characters. Poll thread only, apart from the create and dispose
    /// workers of the menus' own keyboards, which publish under the
    /// virtual-controller lifecycle lock as Step 5's create worker does.
    /// </summary>
    public partial class InputManager
    {
        /// <summary>False on the idle path, where Step 5 feeds no slot's
        /// keyboard this poll.</summary>
        private bool _oskStep5Runs = true;
        private IOskOutput _oskWindowsOut;

        private enum OskDestKind { Windows, Slot, Menu }

        /// <summary>One thing the keyboard sends, in the order it typed it: a
        /// report for a keyboard device, or a tap or text for Windows Input.</summary>
        private sealed class OskOut
        {
            public OskDestKind Kind;
            public int Slot = -1;
            public OskMenuKeyboard Menu;
            public byte[] Keys;
            public bool Text;
            public ushort Scan;
            public OskMods Mods;
            public string Chars;
        }

        /// <summary>What waits to go out. A report goes out per poll, so each
        /// change reaches the device as its own report: the modifiers, the key
        /// with them, the modifiers alone, then nothing. While reports wait,
        /// Windows Input waits behind them, so nothing types out of order.</summary>
        private readonly Queue<OskOut> _oskOut = new();

        private const byte VkLShift = 0xA0, VkLControl = 0xA2, VkLMenu = 0xA4, VkRMenu = 0xA5, VkLWin = 0x5B;

        /// <summary>Set by tests: the virtual key a scan code has, in place of
        /// the Windows layout's answer.</summary>
        internal Func<ushort, ushort> OnScreenKeyboardVirtualKeyOverride { get; set; }

        private sealed class OskRouter : IOskOutput
        {
            private readonly InputManager _im;
            private readonly IOskOutput _windows;

            public OskRouter(InputManager im, IOskOutput windows)
            {
                _im = im;
                _windows = windows;
            }

            public void TapKey(ushort scan, OskMods mods)
            {
                if (_im.QueueDeviceTap(scan, mods)) return;
                if (_im._oskOut.Count > 0)
                    _im._oskOut.Enqueue(new OskOut { Kind = OskDestKind.Windows, Scan = scan, Mods = mods });
                else
                    _windows.TapKey(scan, mods);
            }

            public void TypeText(string text)
            {
                if (string.IsNullOrEmpty(text)) return;
                if (_im._oskOut.Count > 0)
                    _im._oskOut.Enqueue(new OskOut { Kind = OskDestKind.Windows, Text = true, Chars = text });
                else
                    _windows.TypeText(text);
            }
        }

        /// <summary>The owner menu's Types Through.</summary>
        private (OskDestKind Kind, int Slot) OskDestination()
        {
            string t = (_oskDef?.TypesThrough ?? "").Trim();
            if (t.Length == 0) return (OskDestKind.Windows, -1);
            if (TryParseSlotTarget(t, out int slot)) return (OskDestKind.Slot, slot);
            return (OskDestKind.Menu, -1);
        }

        /// <summary>The "slot:N" form, N a pad index.</summary>
        internal static bool TryParseSlotTarget(string typesThrough, out int slot)
        {
            slot = -1;
            if (string.IsNullOrEmpty(typesThrough) || !typesThrough.StartsWith("slot:", StringComparison.Ordinal)) return false;
            return int.TryParse(typesThrough.AsSpan(5), System.Globalization.NumberStyles.None,
                       System.Globalization.CultureInfo.InvariantCulture, out slot)
                   && slot >= 0 && slot < MaxPads;
        }

        /// <summary>The HIDMaestro keyboard profile a Types Through names, or
        /// null for Windows Input and for a Keyboard + Mouse slot.</summary>
        internal static string MenuKeyboardProfileOf(string typesThrough)
        {
            string t = (typesThrough ?? "").Trim();
            if (t.Length == 0 || t.StartsWith("slot:", StringComparison.Ordinal)) return null;
            return t;
        }

        /// <summary>Queues a tap for the keyboard device Types Through names.
        /// False when it goes through Windows Input instead: Types Through is
        /// Windows Input, the layout gives the key no virtual key, the slot or
        /// the menu has no keyboard device right now, or the device's report
        /// does not reach the key.</summary>
        private bool QueueDeviceTap(ushort scan, OskMods mods)
        {
            var (kind, slot) = OskDestination();
            if (kind == OskDestKind.Windows) return false;
            var o = OnScreenKeyboardVirtualKeyOverride;
            IntPtr layout = ForegroundOrAppliedLayout();
            ushort vk = o != null ? o(scan) : OskWindowsOutput.VirtualKeyOf(scan, layout);
            if (vk == 0 || vk > 0xFF) return false;
            OskMenuKeyboard menu = null;
            if (kind == OskDestKind.Slot)
            {
                if (!SlotKeyboardSends(slot, (byte)vk, layout)) return false;
            }
            else
            {
                if (!_oskHid.TryGetValue((_oskSlot, _oskMenuId), out menu) || menu.Vc == null
                    || !CapsSend(KbmDeviceCatalog.CapsOf(menu.ProfileId), (byte)vk, layout))
                    return false;
            }
            // The same modifiers, in the same order, as the Windows Input tap
            // (OskWindowsOutput.TapEvents): AltGr is left Ctrl with right Alt.
            var mod = new List<byte>(5);
            if ((mods & (OskMods.Ctrl | OskMods.AltGr)) != 0) mod.Add(VkLControl);
            if ((mods & OskMods.Alt) != 0) mod.Add(VkLMenu);
            if ((mods & OskMods.AltGr) != 0) mod.Add(VkRMenu);
            if ((mods & OskMods.Win) != 0) mod.Add(VkLWin);
            if ((mods & OskMods.Shift) != 0) mod.Add(VkLShift);
            var down = new byte[mod.Count + 1];
            mod.CopyTo(down);
            down[^1] = (byte)vk;
            if (mod.Count > 0) Enqueue(kind, slot, menu, mod.ToArray());
            Enqueue(kind, slot, menu, down);
            if (mod.Count > 0) Enqueue(kind, slot, menu, mod.ToArray());
            Enqueue(kind, slot, menu, Array.Empty<byte>());
            return true;
        }

        private void Enqueue(OskDestKind kind, int slot, OskMenuKeyboard menu, byte[] keys)
            => _oskOut.Enqueue(new OskOut { Kind = kind, Slot = slot, Menu = menu, Keys = keys });

        /// <summary>Whether a Keyboard + Mouse slot's keyboard sends a key
        /// this poll. Windows Input sends every key. A keyboard model sends
        /// the usages its report reaches, the key read the way its sender reads
        /// it (KbmHidKeyboard.Apply), on the layout the key came from. The
        /// model is the one Step 5 builds the slot with, none while the slot's
        /// keyboard half is off. On the idle path Step 5 feeds no slot.</summary>
        private bool SlotKeyboardSends(int slot, byte vk, IntPtr layout)
        {
            if (!_oskStep5Runs || slot < 0 || slot >= MaxPads) return false;
            if (SlotControllerTypes[slot] != VirtualControllerType.KeyboardMouse) return false;
            if (_virtualControllers[slot] is not KeyboardMouseVirtualController) return false;
            var build = DesiredKbmBuild(slot);
            if (_kbmConfigs[slot] is { KeyboardEnabled: false }) return false;
            var caps = KbmDeviceCatalog.CapsOf(build.Keyboard);
            return caps == null || CapsSend(caps, vk, layout);
        }

        private static bool CapsSend(KbmDeviceCaps caps, byte vk, IntPtr layout)
        {
            if (caps == null) return false;
            var key = KbmHidKeyMap.Resolve(vk, layout);
            return key.IsKeyboard ? key.Usage <= 0xFF && caps.CanSendKey((byte)key.Usage)
                : key.IsConsumer && caps.CanSendConsumer(key.Usage);
        }

        /// <summary>End of the tick: Windows Input that waited runs, then one
        /// report goes out. A slot's report rides its combined state, which
        /// Step 4 builds anew every poll, so its keys last one poll. A menu's
        /// own keyboard takes its report at once.</summary>
        private void ApplyOutput()
        {
            while (_oskOut.Count > 0)
            {
                var o = _oskOut.Peek();
                if (o.Kind == OskDestKind.Windows)
                {
                    _oskOut.Dequeue();
                    var w = _oskWindowsOut;
                    if (w == null) continue;
                    if (o.Text) w.TypeText(o.Chars);
                    else w.TapKey(o.Scan, o.Mods);
                    continue;
                }
                _oskOut.Dequeue();
                if (o.Kind == OskDestKind.Slot)
                {
                    if (o.Slot >= 0 && o.Slot < MaxPads)
                        foreach (var vk in o.Keys) CombinedKbmRawStates[o.Slot].SetKey(vk, true);
                }
                else if (o.Menu?.Vc is { } vc)
                {
                    var raw = new KbmRawState();
                    foreach (var vk in o.Keys) raw.SetKey(vk, true);
                    vc.SubmitKbmState(raw);
                    o.Menu.KeysDown = o.Keys.Length > 0;
                }
                break;
            }
        }

        /// <summary>Drops what waits, letting go of any key a menu's keyboard
        /// still holds.</summary>
        private void ResetOutput()
        {
            _oskOut.Clear();
            foreach (var m in _oskHid.Values)
            {
                if (!m.KeysDown || m.Vc == null) continue;
                try { m.Vc.SubmitKbmState(default); } catch { /* the device is going away */ }
                m.KeysDown = false;
            }
        }

        // ── The keyboard menus on every slot ───────────────────

        /// <summary>An enabled keyboard menu on a slot that exists.</summary>
        private readonly record struct OskMenuRef(int Slot, MenuDefinitionEntry Def);

        private OskMenuRef[] _oskMenuScan = Array.Empty<OskMenuRef>();
        private long _oskScanMs = long.MinValue / 2;

        /// <summary>Four times a second: the enabled keyboard menus of every
        /// slot that exists, in slot order and the order of each slot's list,
        /// for the text-focus watcher and the menus' own keyboards. Opens With
        /// and layers read the menus Step 2 noted, every poll.</summary>
        private void RefreshKeyboardMenus(long now)
        {
            if (now - _oskScanMs < 250) return;
            _oskScanMs = now;
            var sets = SettingsManager.SlotMappingSets;
            var list = new List<OskMenuRef>();
            bool focus = false;
            if (sets != null)
            {
                for (int slot = 0; slot < sets.Length && slot < MaxPads; slot++)
                {
                    if (!SettingsManager.SlotCreated[slot]) continue;
                    var menus = sets[slot]?.Menus;
                    if (menus == null) continue;
                    // Defensive index walk: the UI thread edits this list.
                    for (int i = 0; i < menus.Count; i++)
                    {
                        MenuDefinitionEntry def;
                        try { def = menus[i]; } catch { break; }
                        if (def == null || def.Kind != MenuKind.Keyboard || !def.Enabled) continue;
                        list.Add(new OskMenuRef(slot, def));
                        focus |= def.OpenOnTextFocus;
                    }
                }
            }
            _oskMenuScan = list.Count == 0 ? Array.Empty<OskMenuRef>() : list.ToArray();
            _oskWantsTextFocus = focus;
        }

        // ── A keyboard menu's own HIDMaestro keyboard ──────────

        /// <summary>The virtual keyboard a keyboard menu makes for itself
        /// when its Types Through names a HIDMaestro keyboard. It exists while
        /// the menu is enabled and the engine runs, so it is ready when the
        /// keyboard opens. A create that failed is not retried until the menu
        /// asks for another keyboard or is turned off and on.</summary>
        private sealed class OskMenuKeyboard
        {
            public int Slot, MenuId;
            public string ProfileId;
            /// <summary>Published by the create worker under the lifecycle
            /// lock, cleared by the poll thread under it.</summary>
            public volatile KeyboardMouseVirtualController Vc;
            public volatile bool Failed, Abandoned;
            public Task Create;
            public bool KeysDown;
        }

        private readonly Dictionary<(int Slot, int MenuId), OskMenuKeyboard> _oskHid = new();
        /// <summary>The creates and disposes still running. Locked: Stop
        /// reads it, and a poll thread that outlived Stop's join can still
        /// add to it.</summary>
        private readonly List<Task> _oskHidTasks = new();
        private readonly List<(int Slot, int MenuId)> _oskHidRetire = new();
        private readonly Dictionary<(int Slot, int MenuId), string> _oskHidWanted = new();
        private long _oskHidCheckedMs = long.MinValue / 2;

        /// <summary>Set by tests: makes a menu's keyboard in place of
        /// HIDMaestro, from the slot, the menu id and the profile id. The test
        /// host makes no device of its own.</summary>
        internal Func<int, int, string, KeyboardMouseVirtualController> OnScreenKeyboardDeviceFactory { get; set; }
        /// <summary>Set at engine stop, under the lifecycle lock: a create
        /// that finishes after it disposes its keyboard.</summary>
        private volatile bool _oskHidClosed;

        /// <summary>The identity key a keyboard menu's virtual keyboard is
        /// made under, so it comes back at the same device path after a
        /// restart, and never meets a slot's own (KeyboardMouseVirtualController.IdentityKeyFor).</summary>
        internal static string MenuKeyboardIdentityKey(int slot, int menuId)
            => "padforge:pad" + slot.ToString(System.Globalization.CultureInfo.InvariantCulture)
               + ":KeyboardMenu" + menuId.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":keyboard";

        /// <summary>True while a menu keyboard's create or dispose runs. Step
        /// 5 waits on it as on its own, one HIDMaestro create at a time.</summary>
        internal bool MenuKeyboardLifecyclePending
        {
            get
            {
                lock (_oskHidTasks)
                    foreach (var t in _oskHidTasks)
                        if (!t.IsCompleted) return true;
                return false;
            }
        }

        private bool SlotLifecyclePending()
        {
            for (int i = 0; i < MaxPads; i++)
            {
                if (_pendingConnectTask[i] is { IsCompleted: false }) return true;
                if (_pendingDisposeTask[i] is { IsCompleted: false }) return true;
            }
            return false;
        }

        /// <summary>Four times a second: makes the keyboards the enabled
        /// keyboard menus ask for and removes the ones nothing asks for.</summary>
        private void ReconcileMenuKeyboards(long now)
        {
            if (now - _oskHidCheckedMs < 250) return;
            _oskHidCheckedMs = now;
            lock (_oskHidTasks) _oskHidTasks.RemoveAll(t => t.IsCompleted);

            _oskHidWanted.Clear();
            if (VirtualControllersEnabled && (OnScreenKeyboardDeviceFactory != null || !HMaestroVirtualController.OffLimitsForTest))
                foreach (var m in _oskMenuScan)
                    if (MenuKeyboardProfileOf(m.Def.TypesThrough) is { } profile && KbmDeviceCatalog.HasKeyboard(profile))
                        _oskHidWanted[(m.Slot, m.Def.MenuId)] = profile;

            _oskHidRetire.Clear();
            foreach (var kv in _oskHid)
                if (!_oskHidWanted.TryGetValue(kv.Key, out var p)
                    || !string.Equals(p, kv.Value.ProfileId, StringComparison.OrdinalIgnoreCase))
                    _oskHidRetire.Add(kv.Key);
            foreach (var k in _oskHidRetire) RetireMenuKeyboard(k);

            foreach (var kv in _oskHidWanted)
            {
                if (_oskHid.ContainsKey(kv.Key)) continue;
                // One HIDMaestro create at a time, Step 5's own and these.
                if (MenuKeyboardLifecyclePending || SlotLifecyclePending()) break;
                var rec = new OskMenuKeyboard { Slot = kv.Key.Slot, MenuId = kv.Key.MenuId, ProfileId = kv.Value };
                // Under the lifecycle lock, where the stop empties this table,
                // and nothing is made after the stop.
                lock (_vcLifecycleLock)
                {
                    if (_oskHidClosed) break;
                    _oskHid[kv.Key] = rec;
                }
                rec.Create = Task.Run(() => CreateMenuKeyboard(rec));
                lock (_oskHidTasks) _oskHidTasks.Add(rec.Create);
            }
        }

        /// <summary>The create worker: the HIDMaestro context and the
        /// keyboard's profile as Step 5's CreateKeyboardMouseController takes
        /// them, a keyboard-only Keyboard + Mouse controller under the menu's
        /// own identity, and its connect off the poll thread, since making the
        /// device waits on Windows.</summary>
        private void CreateMenuKeyboard(OskMenuKeyboard rec)
        {
            KeyboardMouseVirtualController vc = null;
            try
            {
                var factory = OnScreenKeyboardDeviceFactory;
                if (factory != null)
                {
                    vc = factory(rec.Slot, rec.MenuId, rec.ProfileId);
                }
                else
                {
                    EnsureHMaestroContext();
                    var ctx = _hmaestroContext;
                    var profile = ctx == null ? null : ResolveKbmProfile(rec.ProfileId);
                    if (profile != null)
                        vc = new KeyboardMouseVirtualController(rec.Slot, ctx, profile, null)
                        {
                            KeyboardIdentityKey = MenuKeyboardIdentityKey(rec.Slot, rec.MenuId),
                        };
                }
                if (vc == null)
                {
                    rec.Failed = true;
                    return;
                }
                vc.Connect();
                lock (_vcLifecycleLock)
                {
                    if (!_oskHidClosed && !rec.Abandoned)
                    {
                        rec.Vc = vc;
                        vc = null;
                    }
                }
                PadForge.Engine.SdlDiagLog.WriteLine(
                    $"VCTRACE keyboard menu slot={rec.Slot} menu={rec.MenuId} keyboard={rec.ProfileId} {(vc == null ? "ready" : "not needed any more")}");
            }
            catch (Exception ex)
            {
                rec.Failed = true;
                RaiseError($"Could not make the On-Screen Keyboard's virtual keyboard '{rec.ProfileId}'.", ex);
            }
            finally
            {
                if (vc != null)
                {
                    try { vc.Dispose(); } catch { /* the create's own failure is the one to report */ }
                }
            }
        }

        /// <summary>Stops using a menu's keyboard and removes it on a worker,
        /// since the removal waits on Windows. Under the lifecycle lock, so a
        /// create that finishes now either publishes before this takes the
        /// keyboard or sees the record abandoned and disposes it itself.</summary>
        private void RetireMenuKeyboard((int Slot, int MenuId) key)
        {
            KeyboardMouseVirtualController vc;
            lock (_vcLifecycleLock)
            {
                if (!_oskHid.Remove(key, out var rec)) return;
                rec.Abandoned = true;
                vc = rec.Vc;
                rec.Vc = null;
            }
            if (vc == null) return;
            var dispose = Task.Run(() =>
            {
                try { vc.Dispose(); }
                catch (Exception ex) { RaiseError("Error removing the On-Screen Keyboard's virtual keyboard", ex); }
            });
            lock (_oskHidTasks) _oskHidTasks.Add(dispose);
        }

        /// <summary>Engine stop, from DestroyAllVirtualControllers under the
        /// lifecycle lock: every menu keyboard goes, in order, as the slots'
        /// HIDMaestro controllers do, and a create still running disposes its
        /// own.</summary>
        private void DestroyMenuKeyboardsLocked()
        {
            _oskHidClosed = true;
            foreach (var rec in _oskHid.Values)
            {
                rec.Abandoned = true;
                var vc = rec.Vc;
                rec.Vc = null;
                if (vc == null) continue;
                try { vc.Dispose(); } catch { /* best effort */ }
            }
            _oskHid.Clear();
        }

        /// <summary>A keyboard menu's own keyboard once it is made, for tests.</summary>
        internal KeyboardMouseVirtualController OnScreenKeyboardDeviceOf(int slot, int menuId)
            => _oskHid.TryGetValue((slot, menuId), out var rec) ? rec.Vc : null;

        /// <summary>The menu keyboards' creates and disposes still running, for
        /// AwaitPendingLifecycleTasks.</summary>
        private void AddMenuKeyboardTasks(List<Task> tasks)
        {
            lock (_oskHidTasks)
                foreach (var t in _oskHidTasks)
                    if (!t.IsCompleted) tasks.Add(t);
        }
    }
}
