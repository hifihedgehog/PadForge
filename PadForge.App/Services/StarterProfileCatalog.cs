using System;
using System.Collections.Generic;
using System.Linq;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Engine.Menus;
using PadForge.Resources.Strings;
using PadForge.ViewModels;

namespace PadForge.Services
{
    /// <summary>
    /// The bundled starter profiles (#256): general-purpose archetypes a user
    /// picks, assigns any controller to, and plays. Cookie-cutter genre
    /// coverage rather than per-game configs.
    ///
    /// <para>Built in code rather than shipped as embedded XML. The plan
    /// originally called for XML resources, but five profiles of roughly
    /// twenty-five rows each is a few thousand lines of hand-authored nested
    /// elements, which is exactly where a silent typo hides. A builder is
    /// compile-checked, the tests assert against the built object, and there
    /// is no resource-missing or serializer-drift failure mode. The output is
    /// an ordinary <see cref="ProfileData"/> either way.</para>
    ///
    /// <para>Authoring contract, enforced by StarterProfileCatalogTests:
    /// every source carries an EMPTY DeviceGuid (the "(Any device)" choice),
    /// every pad input uses an abstract <c>Gamepad *</c> descriptor so it
    /// canonicalizes onto whatever pad is assigned, and every MappingSet is
    /// <see cref="MappingSet.Authoritative"/> so the legacy automap merge
    /// cannot inject the device's own descriptors on top and double-fire
    /// every input.</para>
    /// </summary>
    public static class StarterProfileCatalog
    {
        // ── Authoring helpers ───────────────────────────────────────────

        /// <summary>One source on a row. DeviceGuid stays empty on purpose:
        /// that is the "(Any device)" choice, so the row reads whichever pad
        /// is assigned to the slot.</summary>
        private static MappingSource Src(string descriptor, bool invert = false)
            => new() { DeviceGuid = "", Descriptor = descriptor, Invert = invert };

        /// <summary>A row with one or more sources, in priority order.</summary>
        private static MappingRow Row(string target, params MappingSource[] sources)
        {
            var row = new MappingRow { Target = target, LayerMask = "Base" };
            row.Sources.AddRange(sources);
            return row;
        }

        /// <summary>One WEDGE of a stick axis, i.e. "push the stick this way".
        /// The engine reads a direction as a HALF-AXIS of the bipolar axis,
        /// with <see cref="MappingSource.Invert"/> acting as the half
        /// SELECTOR rather than a sign flip. SDL's convention is +X right and
        /// +Y down, so north is the Y lower half (half + invert), south the Y
        /// upper half, east the X upper half, and west the X lower half. This
        /// is the same table PhysicalSlotResolver uses for stick-as-dpad.
        /// </summary>
        private static MappingSource Dir(string axisDescriptor, bool lowerHalf)
            => new()
            {
                DeviceGuid = "",
                Descriptor = axisDescriptor,
                HalfAxis = true,
                Invert = lowerHalf,
            };

        private static MappingSource Up(string stickY) => Dir(stickY, lowerHalf: true);
        private static MappingSource Down(string stickY) => Dir(stickY, lowerHalf: false);
        private static MappingSource Left(string stickX) => Dir(stickX, lowerHalf: true);
        private static MappingSource Right(string stickX) => Dir(stickX, lowerHalf: false);

        /// <summary>A row on a named shift layer.</summary>
        private static MappingRow LayerRow(string layer, string target, params MappingSource[] sources)
        {
            var row = Row(target, sources);
            row.LayerMask = layer;
            return row;
        }

        /// <summary>Keyboard target for a Windows virtual-key. The KbM row
        /// engine's key targets are <c>KbmKey{vk:X2}</c>. Keys outside its
        /// closed set (media, volume, browser) are NOT row targets and must
        /// ride a macro instead, so this is deliberately only used for the
        /// ordinary letter / digit / arrow / modifier keys.</summary>
        private static string Key(byte vk) => "KbmKey" + vk.ToString("X2");

        // Virtual-key constants, named so the tables below read as bindings
        // rather than as hex. Values are the Windows VK_* codes.
        // NOTE the modifier codes. The KbM row engine's key set is CLOSED and
        // carries only the SIDED modifiers (0xA0..0xA5). The unsided
        // VK_SHIFT 0x10 / VK_CONTROL 0x11 / VK_MENU 0x12 are NOT in it, and a
        // row authored against one is silently dead. VK_LWIN 0x5B is absent
        // too, so the Windows key is not reachable as a row target at all.
        private const byte VkTab = 0x09, VkReturn = 0x0D, VkEscape = 0x1B, VkSpace = 0x20;
        private const byte VkShift = 0xA0;    // VK_LSHIFT
        private const byte VkControl = 0xA2;  // VK_LCONTROL
        private const byte VkMenu = 0xA4;     // VK_LMENU (Alt)
        private const byte VkLeft = 0x25, VkUp = 0x26, VkRight = 0x27, VkDown = 0x28;
        private const byte VkPageUp = 0x21, VkPageDown = 0x22;
        private const byte VkPause = 0x13;   // VK_PAUSE / Break
        private const byte Vk0 = 0x30, Vk1 = 0x31, Vk2 = 0x32, Vk3 = 0x33, Vk4 = 0x34;
        private const byte Vk5 = 0x35, Vk6 = 0x36, Vk7 = 0x37, Vk8 = 0x38, Vk9 = 0x39;
        private const byte VkA = 0x41, VkC = 0x43, VkD = 0x44, VkE = 0x45, VkF = 0x46;
        private const byte VkG = 0x47, VkI = 0x49, VkM = 0x4D, VkQ = 0x51, VkR = 0x52;
        private const byte VkS = 0x53, VkV = 0x56, VkW = 0x57;
        private const byte VkJ = 0x4A, VkL = 0x4C;
        private const byte VkF1 = 0x70, VkF2 = 0x71, VkF3 = 0x72, VkF4 = 0x73, VkF5 = 0x74;
        private const byte VkF6 = 0x75, VkF7 = 0x76, VkF8 = 0x77, VkF9 = 0x78;
        private const byte VkF10 = 0x79, VkF11 = 0x7A, VkF12 = 0x7B;
        // Numpad 1-8 (0x61..0x68), a bank of keys almost nothing else claims.
        private const byte VkNum1 = 0x61, VkNum2 = 0x62, VkNum3 = 0x63, VkNum4 = 0x64;
        private const byte VkNum5 = 0x65, VkNum6 = 0x66, VkNum7 = 0x67, VkNum8 = 0x68;

        /// <summary>Valve's shipped double_tap_time, and the value the
        /// Workshop translator already uses for Steam's Double_Press
        /// activator.</summary>
        private const int DoubleTapMs = 442;
        private const byte VkOemMinus = 0xBD, VkOemPlus = 0xBB;

        // Abstract pad descriptors. Every one of these is a member of
        // SourceCoercion.GamepadAliasTable, so it canonicalizes to the
        // assigned device's own button or axis at read time.
        private const string PadA = "Gamepad ButtonA", PadB = "Gamepad ButtonB";
        private const string PadX = "Gamepad ButtonX", PadY = "Gamepad ButtonY";
        private const string PadLB = "Gamepad LeftShoulder", PadRB = "Gamepad RightShoulder";
        private const string PadBack = "Gamepad ButtonBack", PadStart = "Gamepad ButtonStart";
        private const string PadGuide = "Gamepad ButtonGuide";
        private const string PadLS = "Gamepad LeftStick", PadRS = "Gamepad RightStick";
        private const string PadUp = "Gamepad DPadUp", PadDown = "Gamepad DPadDown";
        private const string PadLeft = "Gamepad DPadLeft", PadRight = "Gamepad DPadRight";
        private const string PadLX = "Gamepad LeftStickX", PadLY = "Gamepad LeftStickY";
        private const string PadRX = "Gamepad RightStickX", PadRY = "Gamepad RightStickY";
        private const string PadLT = "Gamepad LeftTrigger", PadRT = "Gamepad RightTrigger";

        // Primary gyro channels. Pitch is the nose-up/down rate and yaw the
        // horizontal sweep; the engine also carries an aux pair ("Gyro L *")
        // for the left Joy-Con, which a device-agnostic profile must not
        // assume exists.
        private const string GyroPitch = "Gyro Pitch", GyroYaw = "Gyro Yaw";

        // Absolute touchpad pointer. Grammar is "Touchpad N Pointer X|Y"
        // (whole pad; the optional 5th token selects a half). A pointer
        // source whose pad index does not exist on the assigned device
        // reports not-engaged and contributes nothing, so naming pad 1
        // costs a single-pad Sony controller exactly nothing and naming
        // either costs a pad-less controller nothing. Multi-pad hardware
        // indexes left as 0 and right as 1; single-pad Sony hardware has
        // one pad at index 0. Listing pad 1 first makes the right-hand
        // surface win on a two-pad controller, since the first ENGAGED
        // source takes the row.
        private const string Pad1PtrX = "Touchpad 1 Pointer X", Pad1PtrY = "Touchpad 1 Pointer Y";
        private const string Pad0PtrX = "Touchpad 0 Pointer X", Pad0PtrY = "Touchpad 0 Pointer Y";

        // Keyboard + mouse targets.
        private const string MouseX = "KbmMouseX", MouseY = "KbmMouseY";
        private const string Scroll = "KbmScroll", ScrollH = "KbmScrollH";
        private const string ScrollNeg = "KbmScrollNeg";
        private const string MLeft = "KbmMBtn0", MRight = "KbmMBtn1", MMiddle = "KbmMBtn2";

        /// <summary>The cursor pair every mouse-driving starter profile
        /// shares: both touchpads when the hardware has them, with the stick
        /// alongside as a rate cursor. While no finger is down the pointer
        /// contributes nothing and the stick drives; the moment a finger
        /// lands the row routes absolute.</summary>
        private static IEnumerable<MappingRow> CursorRows(double stickSensitivity = 1.0,
            bool leftStick = false)
        {
            MappingSource Stick(string d)
            {
                var src = Src(d);
                if (stickSensitivity != 1.0) src.MouseCursorSensitivity = stickSensitivity;
                return src;
            }
            // The touchpad is always the primary pointer. WHICH STICK backs it
            // up is per profile: the cursor-driven genres (Gilbert's classic
            // point-and-click mode, Larian's CRPG layout) put the cursor on
            // the LEFT stick and keep the right for the camera.
            string sx = leftStick ? PadLX : PadRX;
            string sy = leftStick ? PadLY : PadRY;
            return new[]
            {
                Row(MouseX, Src(Pad1PtrX), Src(Pad0PtrX), Stick(sx)),
                Row(MouseY, Src(Pad1PtrY), Src(Pad0PtrY), Stick(sy)),
            };
        }

        // ── Profile bodies ──────────────────────────────────────────────

        /// <summary>Desktop: run Windows from the couch. Valve ships one
        /// desktop scheme for every standard gamepad (desktop_xboxone.vdf and
        /// desktop_ps4.vdf are byte-identical apart from the controller type),
        /// and this follows it: cursor on the right stick, arrows on the
        /// D-pad, Ctrl and Alt held on the bumpers so Alt+Tab and Ctrl+C are
        /// reachable without dedicated bindings.</summary>
        private static MappingSet BuildDesktop()
        {
            var set = NewKbmSet();
            set.Rows.AddRange(CursorRows());
            set.Rows.AddRange(new[]
            {
                // Scroll on the left stick, with NO invert, which is the
                // non-obvious part. SDL's +Y is DOWN, and Step 3 negates the
                // scroll axis on the way out (KbmScroll positive = up after
                // that negation). The two cancel, so the raw stick value
                // already scrolls up when pushed up. Adding an invert here
                // would reverse it.
                Row(Scroll, Src(PadLY)),
                Row(ScrollH, Src(PadLX)),

                Row(MLeft, Src(PadRT)),
                Row(MRight, Src(PadLT)),
                Row(MMiddle, Src(PadLS)),

                Row(Key(VkReturn), Src(PadA)),
                Row(Key(VkEscape), Src(PadB), Src(PadStart)),
                Row(Key(VkSpace), Src(PadY)),
                // X is Show Keyboard, which Valve's desktop scheme puts here.
                // It rides a macro because the Windows key is not a row target.

                Row(Key(VkUp), Src(PadUp)),
                Row(Key(VkDown), Src(PadDown)),
                Row(Key(VkLeft), Src(PadLeft)),
                Row(Key(VkRight), Src(PadRight)),

                Row(Key(VkControl), Src(PadLB)),
                Row(Key(VkMenu), Src(PadRB)),
                Row(Key(VkTab), Src(PadBack)),

                // Valve's Deck paddle assignment, carried over where the pad
                // has them. Paddle 1 is the Windows key, which is not a row
                // target, so it rides the macro lane with the others.
                Row(Key(VkShift), Src("Gamepad Paddle2")),
                Row(Key(VkPageDown), Src("Gamepad Paddle3")),
                Row(Key(VkPageUp), Src("Gamepad Paddle4")),
            });
            AddQuietLayer(set);
            return set;
        }

        /// <summary>Desktop's two system keys, both unreachable as rows.
        /// X opens the on-screen keyboard the way Windows does it
        /// (Win+Ctrl+O), matching the Show Keyboard that Valve's desktop
        /// scheme puts on X.
        ///
        /// <para>The Windows key sits on Paddle 1, which is Valve's own Deck
        /// assignment. It stays off Start deliberately: the quiet layer is a
        /// long press of Start, and a macro there would open the Start menu
        /// every time the user reached for silence.</para>
        /// </summary>
        private static IEnumerable<MacroData> DesktopMacros() => new[]
        {
            Tap("Show Keyboard", PadX, VkLWin, VkControl, VkO),
            Tap("Windows Key", "Gamepad Paddle1", VkLWin),
        };

        /// <summary>WASD and Mouse: the shooter and action-game default.
        /// Read from Valve's wasd.vdf, the one non-gamepad template every
        /// controller family can reach, including the labels Valve attaches
        /// to each binding (Sprint on the stick click, Jump/Use/Reload/
        /// Flashlight on the face, weapon slots on the D-pad).</summary>
        private static MappingSet BuildWasdAndMouse()
        {
            var set = NewKbmSet();
            set.Rows.AddRange(CursorRows());
            set.Rows.AddRange(new[]
            {
                // Left stick as WASD. SOCD Neutral on the two opposing key
                // pairs stops a roll across the deadzone leaving two
                // opposite keys held.
                Row(Key(VkW), Up(PadLY)),
                Row(Key(VkS), Down(PadLY)),
                Row(Key(VkA), Left(PadLX)),
                Row(Key(VkD), Right(PadLX)),

                Row(MRight, Src(PadLT)),
                // Valve puts LEFT click on the right-stick click, not middle.
                Row(MLeft, Src(PadRT), Src(PadRS)),

                // Valve's Xbox Elite variant mirrors these same four actions
                // onto the paddles, which is the whole point of a paddle: the
                // action stays reachable without lifting a thumb. They are
                // extra SOURCES on the existing rows, not duplicate rows.
                Row(Key(VkSpace), Src(PadA), Src("Gamepad Paddle4")),   // Jump
                Row(Key(VkE), Src(PadB), Src("Gamepad Paddle3")),       // Use
                Row(Key(VkR), Src(PadX), Src("Gamepad Paddle1")),       // Reload
                Row(Key(VkF), Src(PadY), Src("Gamepad Paddle2")),       // Flashlight
                // Valve labels these Previous / Next Weapon and binds them to
                // the wheel rather than letter keys, which is what makes them
                // work in games that number their slots differently.
                Row(ScrollNeg, Src(PadLB)),
                Row(Scroll, Src(PadRB)),
                Row(Key(VkShift), Src(PadLS)),  // Sprint

                Row(Key(Vk1), Src(PadUp)),
                Row(Key(Vk3), Src(PadDown)),
                Row(Key(Vk2), Src(PadRight)),
                Row(Key(Vk4), Src(PadLeft)),

                Row(Key(VkEscape), Src(PadStart)),
                Row(Key(VkTab), Src(PadBack)),
            });
            set.SocdMode = "Neutral";
            set.SocdPairs = SocdKeyPairs(VkA, VkD, VkW, VkS);
            AddQuietLayer(set);
            return set;
        }

        /// <summary>Hotbar: thirty-two abilities behind two triggers. This is
        /// Final Fantasy XIV's Cross Hotbar, which Square Enix documents
        /// precisely: hold L2 for eight slots (four D-pad plus four face
        /// buttons), hold R2 for a different eight, and double-tap either for
        /// a further sixteen. The double-tap tier is a first-class activator
        /// mode here rather than a macro workaround.</summary>
        private static MappingSet BuildHotbar()
        {
            var set = NewKbmSet();
            set.Rows.AddRange(CursorRows());
            set.Rows.AddRange(new[]
            {
                Row(Key(VkW), Up(PadLY)),
                Row(Key(VkS), Down(PadLY)),
                Row(Key(VkA), Left(PadLX)),
                Row(Key(VkD), Right(PadLX)),

                Row(MLeft, Src(PadRT)),
                Row(MRight, Src(PadLT)),

                Row(Key(VkSpace), Src(PadA)),
                Row(Key(VkEscape), Src(PadB)),
                Row(Key(VkTab), Src(PadX)),
                Row(Key(VkF), Src(PadY)),
                Row(Key(VkR), Src(PadLB)),
                Row(Key(VkC), Src(PadRB)),
                Row(Key(VkShift), Src(PadLS)),
                Row(Key(VkV), Src(PadRS)),
                Row(Key(VkM), Src(PadBack)),
                Row(Key(VkI), Src(PadStart)),
            });
            set.SocdMode = "Neutral";
            set.SocdPairs = SocdKeyPairs(VkA, VkD, VkW, VkS);

            // Hold LT for eight slots, hold RT for a different eight.
            AddBank(set, "Bank1", Strings.Instance.Starter_Layer_CrossHotbarL,
                PadLT, doublePressMs: 0,
                new[] { Vk1, Vk2, Vk3, Vk4, Vk5, Vk6, Vk7, Vk8 });
            AddBank(set, "Bank2", Strings.Instance.Starter_Layer_CrossHotbarR,
                PadRT, doublePressMs: 0,
                new[] { Vk9, Vk0, VkOemMinus, VkOemPlus, VkF1, VkF2, VkF3, VkF4 });

            // Double-tap-and-hold either trigger for sixteen more. This is the
            // Double Cross Hotbar, and DoublePressMs is what makes it a real
            // activator: the input counts as engaged only on the SECOND press
            // of a press-release-press pair, and Hold then holds from that
            // second press until its release. Both tiers can be engaged at
            // once during the second press; last-engaged wins, which is the
            // double-tap bank, so the plain bank does not shadow it.
            AddBank(set, "Bank3", Strings.Instance.Starter_Layer_DoubleCrossL,
                PadLT, doublePressMs: DoubleTapMs,
                new[] { VkF5, VkF6, VkF7, VkF8, VkF9, VkF10, VkF11, VkF12 });
            AddBank(set, "Bank4", Strings.Instance.Starter_Layer_DoubleCrossR,
                PadRT, doublePressMs: DoubleTapMs,
                new[] { VkNum1, VkNum2, VkNum3, VkNum4, VkNum5, VkNum6, VkNum7, VkNum8 });

            AddQuietLayer(set);
            return set;
        }

        /// <summary>Emulation: RetroArch and the frontends built on it.
        /// RetroArch ships the hotkey mechanism but essentially no gamepad
        /// defaults, so every scheme in the wild is a frontend convention.
        /// RetroPie, EmuDeck, RetroDECK, Batocera and RetroBat converge on
        /// Back as a held modifier with a second button picking the verb,
        /// which is what the shift layer reproduces.
        ///
        /// <para>The left stick mirrors the D-pad because NES, SNES and
        /// Genesis cores carry no analog axes at all, so a stick-first player
        /// gets nothing otherwise.</para></summary>
        private static MappingSet BuildEmulation()
        {
            var set = NewPadSet();
            set.Rows.AddRange(new[]
            {
                Row("ButtonA", Src(PadA)),
                Row("ButtonB", Src(PadB)),
                Row("ButtonX", Src(PadX)),
                Row("ButtonY", Src(PadY)),
                Row("LeftShoulder", Src(PadLB)),
                Row("RightShoulder", Src(PadRB)),
                Row("ButtonBack", Src(PadBack)),
                Row("ButtonStart", Src(PadStart)),
                Row("ButtonGuide", Src(PadGuide)),
                Row("LeftThumbButton", Src(PadLS)),
                Row("RightThumbButton", Src(PadRS)),
                Row("LeftTrigger", Src(PadLT)),
                Row("RightTrigger", Src(PadRT)),

                // D-pad driven by the D-pad AND the left stick's four
                // directions, so a stick-first player reaches cores that
                // have no analog axes.
                Row("DPadUp", Src(PadUp), Up(PadLY)),
                Row("DPadDown", Src(PadDown), Down(PadLY)),
                Row("DPadLeft", Src(PadLeft), Left(PadLX)),
                Row("DPadRight", Src(PadRight), Right(PadLX)),

                Row("LeftThumbAxisX", Src(PadLX)),
                Row("LeftThumbAxisY", Src(PadLY)),
                Row("RightThumbAxisX", Src(PadRX)),
                Row("RightThumbAxisY", Src(PadRY)),
            });

            // Hotkey layer: hold Back, then a second button picks the verb.
            // That is RetroArch's own input_enable_hotkey shape, and the
            // consensus of RetroPie, EmuDeck, RetroDECK, Batocera and
            // RetroBat.
            //
            // The VERBS are keyboard keys, which a gamepad slot cannot send,
            // so this profile is a SPLIT CONFIG: an Xbox slot for the pad and
            // a keyboard slot for the hotkeys, which is the same shape the
            // Workshop importer produces for configs that need both. This set
            // is the pad half, and its job on the layer is only to STOP the
            // gamepad outputs those buttons drive, so holding Back and
            // tapping RB saves a state instead of also pressing RB in game.
            var hotkeyButtons = new HashSet<string>(StringComparer.Ordinal)
            {
                PadBack, PadY, PadStart, PadLB, PadRB, PadLT, PadRT, PadLeft, PadRight,
            };
            BlockInheritedTargets(set, "Hotkey", hotkeyButtons);

            set.ShiftActivators.Add(HotkeyActivator());
            AddQuietLayer(set);
            return set;
        }

        /// <summary>The keyboard half of the Emulation split config. Only the
        /// hotkey layer carries rows: with nothing on Base this slot is silent
        /// until Back is held, so it never types into the emulator during
        /// play.
        ///
        /// <para>The verbs are RetroArch's DEFAULT keyboard hotkeys, so they
        /// work against a stock install with no remapping: F2 save state, F4
        /// load state, F6 and F7 to step the state slot, Space fast-forward,
        /// R rewind, F1 menu, Escape exit.</para></summary>
        private static MappingSet BuildEmulationHotkeys()
        {
            var set = NewKbmSet();
            var hotkeys = new (byte Vk, string Button)[]
            {
                (VkF2, PadRB),        // save state
                (VkF4, PadLB),        // load state
                (VkF7, PadRight),     // next state slot
                (VkF6, PadLeft),      // previous state slot
                (VkSpace, PadRT),     // fast-forward
                (VkR, PadLT),         // rewind
                (VkF1, PadY),         // menu
                (VkEscape, PadStart), // exit content
            };
            foreach (var (vk, button) in hotkeys)
                set.Rows.Add(LayerRow("Hotkey", Key(vk), Src(button)));

            set.ShiftActivators.Add(HotkeyActivator());
            return set;
        }

        /// <summary>The shared Back-held activator both halves of the
        /// Emulation split config carry. A shift layer is per mapping set, so
        /// each slot needs its own copy of the same activator for the two
        /// halves to engage together.</summary>
        private static ShiftActivator HotkeyActivator() => new()
        {
            DeviceGuid = "",
            Descriptor = PadBack,
            Mode = "Hold",
            LayerMask = "Hotkey",
            LayerName = Strings.Instance.Starter_Layer_Hotkeys,
            InheritUnmapped = true,
        };

        /// <summary>Fighting Games: tournament-legal out of the box.
        ///
        /// <para>SOCD is Neutral on BOTH axes. Capcom's rules require
        /// simultaneous opposing directions to produce no movement on the
        /// horizontal and the vertical axis alike, which only neutral
        /// cleaning (or no cleaning) satisfies. Evo's baseline is looser and
        /// also permits first- and last-input priority, so Neutral is the
        /// only setting legal under both rulesets.</para>
        ///
        /// <para>Exactly ONE directional surface is bound. Capcom caps
        /// movement at one input system per direction and states that the
        /// same movement action cannot be assigned to more than one, so the
        /// D-pad drives and the left stick is left unbound. That is the
        /// opposite of what the emulation profile wants, which is why these
        /// cannot be one profile.</para>
        ///
        /// <para>No macros. Evo prohibits hardware macros outright.</para>
        /// </summary>
        private static MappingSet BuildFightingGames()
        {
            var set = NewPadSet();
            set.Rows.AddRange(new[]
            {
                // Six-button arcade face layout: light/medium/heavy punch on
                // X, Y, RB and the matching kicks on A, B, RT.
                Row("ButtonX", Src(PadX)),
                Row("ButtonY", Src(PadY)),
                Row("RightShoulder", Src(PadRB)),
                Row("ButtonA", Src(PadA)),
                Row("ButtonB", Src(PadB)),
                Row("RightTrigger", DigitalTrigger(PadRT)),

                Row("LeftShoulder", Src(PadLB)),
                Row("LeftTrigger", DigitalTrigger(PadLT)),
                Row("ButtonBack", Src(PadBack)),
                Row("ButtonStart", Src(PadStart)),
                Row("ButtonGuide", Src(PadGuide)),

                // One directional surface only. The left stick stays unbound
                // on purpose; see the summary.
                Row("DPadUp", Src(PadUp)),
                Row("DPadDown", Src(PadDown)),
                Row("DPadLeft", Src(PadLeft)),
                Row("DPadRight", Src(PadRight)),
            });
            set.SocdMode = "Neutral";
            // Gamepad slots pair mapping TARGET names.
            set.SocdPairs = "DPadLeft:DPadRight|DPadUp:DPadDown";
            AddQuietLayer(set);
            return set;
        }

        /// <summary>Point and Click: adventure games, hidden object, anything
        /// driven by a cursor and two verbs. Ron Gilbert published Thimbleweed
        /// Park's controller design, the only first-party writeup of this
        /// genre's problem, and his stated goal was that the game stays
        /// playable with one thumbstick and the A button.
        ///
        /// <para>Clicks sit on the triggers rather than the bumpers on
        /// purpose: pressing a bumper jogs the thumb resting on the pointing
        /// surface, which breaks click-and-drag.</para></summary>
        private static MappingSet BuildPointAndClick()
        {
            var set = NewKbmSet();
            // Hotspots are small and static, so the stick trades speed for
            // precision. The touchpad pointer is absolute and unaffected.
            set.Rows.AddRange(CursorRows(stickSensitivity: 0.6, leftStick: true));
            set.Rows.AddRange(new[]
            {
                Row(MLeft, Src(PadRT), Src(PadA)),
                Row(MRight, Src(PadLT)),

                Row(Key(VkEscape), Src(PadB), Src(PadStart)),
                Row(Key(VkI), Src(PadX)),
                // Gilbert's Y skips dialogue, and Back shares that row.
                // Cycling hotspots FORWARD is Tab on LB; backward is the
                // Shift+Tab chord on RB, which rides the macro lane because no
                // single row target is a chord. Hold-to-reveal-hotspots is
                // widespread but never standardised, and Tab is the half of
                // that split this profile picks.
                Row(Key(VkSpace), Src(PadY), Src(PadBack)),
                Row(Key(VkTab), Src(PadLB)),

                Row(Key(VkUp), Src(PadUp)),
                Row(Key(VkDown), Src(PadDown)),
                Row(Key(VkLeft), Src(PadLeft)),
                Row(Key(VkRight), Src(PadRight)),
            });
            AddQuietLayer(set);
            return set;
        }

        /// <summary>Point and Click's one chord: cycling hotspots BACKWARDS is
        /// Shift+Tab, which no single row target can express.</summary>
        private static IEnumerable<MacroData> PointAndClickMacros() => new[]
        {
            Tap("Previous Hotspot", PadRB, VkShift, VkTab),
        };

        /// <summary>Strategy: RTS, 4X, grand strategy, city builders.
        ///
        /// <para>The camera goes on the LEFT stick and the cursor on the
        /// right, which is what both verified references do (the AntiMicroX
        /// Civilization V profile and a fully specified Command and Conquer
        /// layout), and what Valve's own strategy guidance implies by putting
        /// the camera on a directional surface. Clicks stay on the triggers so
        /// the pointing device and the click button are physically
        /// independent, which is the only way box-select works.</para></summary>
        /// <summary>Strategy's macros. Pause/Break is the pause key several
        /// strategy games bind, and it is outside the KbM row engine's closed
        /// VK set, so it rides the macro lane like the Windows key does.
        /// Nothing else in the profile claims Back.</summary>
        private static IEnumerable<MacroData> StrategyMacros() => new[]
        {
            Tap("Pause", PadBack, VkPause),
        };

        private static MappingSet BuildStrategy()
        {
            var set = NewKbmSet();
            set.Rows.AddRange(CursorRows());
            set.Rows.AddRange(new[]
            {
                // Camera pan on the left stick, as arrow keys.
                Row(Key(VkUp), Up(PadLY)),
                Row(Key(VkDown), Down(PadLY)),
                Row(Key(VkLeft), Left(PadLX), Src(PadLeft)),
                Row(Key(VkRight), Right(PadLX), Src(PadRight)),

                Row(MLeft, Src(PadRT)),
                Row(MRight, Src(PadLT)),
                Row(MMiddle, Src(PadRS)),

                // The two modifiers the genre lives on.
                Row(Key(VkShift), Src(PadLB)),
                Row(Key(VkControl), Src(PadRB)),

                Row(Key(VkSpace), Src(PadA)),
                Row(Key(VkEscape), Src(PadB)),
                Row(Key(VkReturn), Src(PadX)),
                Row(Key(VkTab), Src(PadY)),
                // Zoom on the D-pad's vertical, which is where both verified
                // references put camera verbs.
                Row(Key(VkOemMinus), Src(PadDown)),
                Row(Key(VkOemPlus), Src(PadUp)),
            });
            // Ten number keys on a held modifier is the difference between the
            // genre being playable on a pad and not: hold RB, flick the right
            // stick, release on the cell.
            AddRadial(set, 1, "Hotbar", PadRB, PadRS, Strings.Instance.Starter_Layer_Hotbar,
                ("1", Vk1), ("2", Vk2), ("3", Vk3), ("4", Vk4), ("5", Vk5),
                ("6", Vk6), ("7", Vk7), ("8", Vk8), ("9", Vk9), ("0", Vk0));
            AddQuietLayer(set);
            return set;
        }

        /// <summary>Isometric RPG: party-based CRPGs, real-time-with-pause,
        /// turn-based tactics. Larian shipped controller layouts for both
        /// Baldur's Gate 3 and Divinity: Original Sin 2 and they share one
        /// skeleton, whose structural moves are the bumpers paging the action
        /// wheels, a stick click toggling a free cursor, and a held stick
        /// click highlighting every interactable.</summary>
        private static MappingSet BuildIsometricRpg()
        {
            var set = NewKbmSet();
            set.Rows.AddRange(CursorRows(leftStick: true));
            set.Rows.AddRange(new[]
            {
                // Camera pan on the RIGHT stick; the left drives the cursor,
                // which is always live because this drives a mouse-only game.
                Row(Key(VkUp), Up(PadRY)),
                Row(Key(VkDown), Down(PadRY)),
                Row(Key(VkLeft), Left(PadRX), Src(PadLeft)),
                Row(Key(VkRight), Right(PadRX), Src(PadRight)),

                Row(MLeft, Src(PadRT)),
                Row(MRight, Src(PadLT)),

                Row(Key(VkSpace), Src(PadA), Src(PadUp)),   // pause, and jump on the D-pad
                Row(Key(VkEscape), Src(PadB)),
                Row(Key(VkI), Src(PadX)),       // inventory
                Row(Key(VkReturn), Src(PadY)),  // Larian's End Turn
                Row(Key(VkTab), Src(PadRS)),    // highlight interactables

                // Larian's bumpers PAGE the action wheels rather than opening
                // menus, so they step the hotbar instead of carrying verbs.
                Row(Key(VkOemMinus), Src(PadLB)),
                Row(Key(VkOemPlus), Src(PadRB)),

                // Larian's D-pad examines and toggles stealth. Cycling
                // interactables shares the camera's arrow rows above, and
                // jump shares A's Space row, so neither duplicates a target.
                Row(Key(VkV), Src(PadDown)),       // examine

                // Quicksave stays OFF Start. Holding Start for 600 ms is the
                // quiet-layer gesture, and the button's Base binding is live
                // for those 600 ms, so a quicksave here would fire every time
                // the user reached for silence. Back is unbound in this
                // profile, so moving it costs nothing.
                Row(Key(VkF5), Src(PadBack)),      // quicksave
            });

            // Larian's two wheels, on the triggers exactly as Divinity ships
            // them: "radial menus (accessed with the triggers) give you all
            // your skills, items, and actions without needing a hotbar".
            AddRadial(set, 1, "Party", PadLT, PadRS, Strings.Instance.Starter_Layer_Party,
                ("Next", VkTab), ("Char", VkC), ("Journal", VkJ),
                ("Map", VkM), ("Rest", VkR), ("Group", VkG));
            AddRadial(set, 2, "Shortcuts", PadRT, PadRS, Strings.Instance.Starter_Layer_Shortcuts,
                ("1", Vk1), ("2", Vk2), ("3", Vk3), ("4", Vk4),
                ("5", Vk5), ("6", Vk6), ("7", Vk7), ("8", Vk8));
            AddQuietLayer(set);
            return set;
        }

        /// <summary>Twin-Stick: top-down shooters and roguelites that only
        /// accept keys to move and a mouse to aim.
        ///
        /// <para>On a controller with a touchpad this is exact, because the
        /// absolute pointer maps the pad 1:1 to the screen, which is what
        /// aiming with space means. On a stick-only pad the cursor is a rate
        /// and the profile aims with time instead, which is what every
        /// stick-only twin-stick setup has always done.</para></summary>
        private static MappingSet BuildTwinStick()
        {
            var set = NewKbmSet();
            set.Rows.AddRange(CursorRows());
            set.Rows.AddRange(new[]
            {
                Row(Key(VkW), Up(PadLY)),
                Row(Key(VkS), Down(PadLY)),
                Row(Key(VkA), Left(PadLX)),
                Row(Key(VkD), Right(PadLX)),

                Row(MLeft, Src(PadRT)),
                Row(MRight, Src(PadLT)),

                Row(Key(VkSpace), Src(PadA)),   // dash
                Row(Key(VkShift), Src(PadB)),   // roll
                Row(Key(VkR), Src(PadX)),       // reload
                Row(Key(VkE), Src(PadY)),       // interact
                Row(Key(VkQ), Src(PadLB)),
                Row(Key(VkF), Src(PadRB)),
                Row(Key(VkEscape), Src(PadStart)),
                Row(Key(VkTab), Src(PadBack)),
            });
            set.SocdMode = "Neutral";
            set.SocdPairs = SocdKeyPairs(VkA, VkD, VkW, VkS);
            AddQuietLayer(set);
            return set;
        }

        /// <summary>Media Remote: playback, seeking, and a cursor for the
        /// ten-foot experience. Kodi ships a joystick keymap with a distinct
        /// fullscreen-video context and Valve ships videoplayer.vdf, and they
        /// agree on the shape.
        ///
        /// <para>The transport runs on REAL system media keys through macros,
        /// not on in-page letter keys, so play/pause, track skip, volume and
        /// mute work with the player unfocused or minimised. Those keys sit
        /// outside the row engine's closed VK set, which is why they ride the
        /// macro lane rather than rows.</para>
        ///
        /// <para>Deliberately NOT Desktop with different letters. The left
        /// stick scrubs instead of scrolling, the bumpers skip tracks instead
        /// of holding modifiers, and the D-pad carries volume and seek instead
        /// of arrows.</para></summary>
        private static MappingSet BuildMediaRemote()
        {
            var set = NewKbmSet();
            set.Rows.AddRange(CursorRows());
            set.Rows.AddRange(new[]
            {
                // Scrub with the left stick. Arrow keys are what every in-page
                // and desktop player binds seeking to.
                Row(Key(VkLeft), Left(PadLX)),
                Row(Key(VkRight), Right(PadLX)),

                // Y is fullscreen and LS is play/pause for the players that
                // ignore media keys (most in-page video responds to F and
                // Space). The macro lane covers the rest.
                Row(Key(VkF), Src(PadY)),
                Row(Key(VkSpace), Src(PadLS)),

                // Rewind and fast-forward. J and L are what YouTube, VLC and
                // mpv all bind seeking to, and Valve puts skip on the
                // triggers in videoplayer.vdf.
                Row(Key(VkJ), Src(PadLT)),
                Row(Key(VkL), Src(PadRT)),

                Row(MLeft, Src(PadRS)),
                Row(Key(VkI), Src(PadStart)),   // info / OSD
                Row(Key(VkTab), Src(PadBack)),
            });
            AddQuietLayer(set);
            return set;
        }

        /// <summary>Media Remote's transport. Play/pause, stop, mute, the
        /// volume pair, track skip and browser back are all SYSTEM keys, so
        /// they reach the player whether or not it has focus, which is the
        /// whole difference between a media remote and a desktop profile that
        /// happens to press Space. Seek is the exception: there is no system
        /// seek key, so those two send plain arrows and ride the macro lane
        /// only to keep the whole transport in one place.</summary>
        private static IEnumerable<MacroData> MediaRemoteMacros() => new[]
        {
            Tap("Play / Pause", PadA, VkMediaPlayPause),
            Tap("Back", PadB, VkBrowserBack),
            Tap("Mute", PadX, VkVolumeMute),
            Tap("Previous Track", PadLB, VkMediaPrevTrack),
            Tap("Next Track", PadRB, VkMediaNextTrack),
            Tap("Volume Up", PadUp, VkVolumeUp),
            Tap("Volume Down", PadDown, VkVolumeDown),
            Tap("Seek Back", PadLeft, VkLeft),
            Tap("Seek Forward", PadRight, VkRight),
            Tap("Stop", PadGuide, VkMediaStop),
        };

        /// <summary>Racing: finer throttle and brake, calmer steering, on any
        /// pad. A pad gives about a centimetre of travel to cover full lock,
        /// so a linear map makes small corrections at speed impossible.
        ///
        /// <para>The steering curve is an exponent above 1, which shrinks
        /// small deflections and leaves full lock reachable: fine control
        /// near centre, unchanged at the edge. The anti-deadzone is a floor
        /// on the output magnitude, which pre-compensates for the deadzone
        /// most racing games apply on top of the pad's own. Exact centre
        /// still reads zero, so the car does not creep.</para>
        ///
        /// <para>Numbers are a shape, not a truth. Every engine names these
        /// transforms differently and they disagree on which value means
        /// linear (F1 treats 0 as linear, EA WRC 5, Forza 50, Assetto Corsa's
        /// gamma 1.0), so this ships a sane starting point to nudge rather
        /// than a claim about any particular title.</para></summary>
        private static MappingSet BuildRacing()
        {
            var set = NewPadSet();

            var steerX = Src(PadLX);
            steerX.ParamCurveExponent = 1.5;   // between linear and x squared
            steerX.ParamAntiDeadzone = 0.05;   // floor, so the first degree of lock registers

            set.Rows.AddRange(new[]
            {
                Row("LeftThumbAxisX", steerX),
                Row("LeftThumbAxisY", Src(PadLY)),
                Row("RightThumbAxisX", Src(PadRX)),
                Row("RightThumbAxisY", Src(PadRY)),

                // No curve on either pedal: the feel that matters is the
                // game's own trigger response, and a second shaping layer here
                // would fight it. The inside guard is the one exception, and it
                // is asymmetric on purpose, and it is the one genuinely
                // transferable trigger convention in the research: Forza
                // ships throttle at 0 inside and brake at 2 inside, the 2
                // being a deliberate guard for a finger resting on the brake.
                Row("RightTrigger", Src(PadRT)),
                Row("LeftTrigger", BrakeTrigger(PadLT)),

                Row("ButtonA", Src(PadA)),
                Row("ButtonB", Src(PadB)),
                Row("ButtonX", Src(PadX)),
                Row("ButtonY", Src(PadY)),
                Row("LeftShoulder", Src(PadLB)),
                Row("RightShoulder", Src(PadRB)),
                Row("ButtonBack", Src(PadBack)),
                Row("ButtonStart", Src(PadStart)),
                Row("ButtonGuide", Src(PadGuide)),
                Row("LeftThumbButton", Src(PadLS)),
                Row("RightThumbButton", Src(PadRS)),
                Row("DPadUp", Src(PadUp)),
                Row("DPadDown", Src(PadDown)),
                Row("DPadLeft", Src(PadLeft)),
                Row("DPadRight", Src(PadRight)),
            });
            AddQuietLayer(set);
            return set;
        }

        /// <summary><para>Space Sim: six degrees of freedom on two sticks.</para>
        ///
        /// <para>Read out of Frontier's shipped Elite Dangerous presets
        /// (references/edrefcard2, "Defaults 3.5"). Roll on left-stick X,
        /// pitch on left-stick Y and vertical thrust on right-stick Y are
        /// unanimous across ConsoleX360, ConsoleX360Classic and
        /// AdvancedControlPad. Right-stick X is the ONLY axis the set
        /// disagrees on: ConsoleX360 binds YawAxisRaw to it, ConsoleX360Classic
        /// binds LateralThrustRaw. Pitch ships Inverted="0", so pull-back-to-
        /// climb is the raw reading and this profile adds no inversion.</para>
        ///
        /// <para>The bumpers are the throttle (BackwardKey on LB, ForwardKey
        /// on RB) and the triggers are the guns (SecondaryFire on LT,
        /// PrimaryFire on RT). Frontier never puts an absolute throttle on a
        /// thumbstick in any gamepad preset, because a self-centering stick
        /// cannot hold a setting; the absolute binding appears only on their
        /// HOTAS presets. So the throttle stays where they put it and the
        /// profile does not invent an axis for it.</para>
        ///
        /// <para>What this profile adds over a bare pad is the response
        /// shape. Docking and formation flying happen in the first tenth of
        /// stick travel, so every flight axis gets a softened curve and a
        /// small floor, and the Precision layer on right-stick click softens
        /// it much further for close work.</para></summary>
        private static MappingSet BuildSpaceSim()
        {
            var set = NewPadSet();

            // Flight axes: soft near centre, full authority at the rim.
            MappingSource Fly(string descriptor, double exponent)
            {
                var src = Src(descriptor);
                src.ParamCurveExponent = exponent;
                src.ParamAntiDeadzone = 0.02;   // the first degree of input registers
                return src;
            }

            set.Rows.AddRange(new[]
            {
                // Roll and pitch, unanimous across every shipped preset.
                Row("LeftThumbAxisX", Fly(PadLX, 1.6)),
                Row("LeftThumbAxisY", Fly(PadLY, 1.6)),

                // Yaw and vertical thrust. Yaw is the twitchier of the two,
                // so it gets the softer curve.
                Row("RightThumbAxisX", Fly(PadRX, 1.8)),
                Row("RightThumbAxisY", Fly(PadRY, 1.6)),

                // Guns, unshaped: a fire button is a fire button.
                Row("RightTrigger", Src(PadRT)),
                Row("LeftTrigger", Src(PadLT)),

                // Throttle down / up, where Frontier puts it.
                Row("LeftShoulder", Src(PadLB)),
                Row("RightShoulder", Src(PadRB)),

                Row("ButtonA", Src(PadA)),
                Row("ButtonB", Src(PadB)),
                Row("ButtonX", Src(PadX)),
                Row("ButtonY", Src(PadY)),
                Row("ButtonBack", Src(PadBack)),
                Row("ButtonStart", Src(PadStart)),
                Row("ButtonGuide", Src(PadGuide)),
                Row("LeftThumbButton", Src(PadLS)),
                Row("RightThumbButton", Src(PadRS)),
                Row("DPadUp", Src(PadUp)),
                Row("DPadDown", Src(PadDown)),
                Row("DPadLeft", Src(PadLeft)),
                Row("DPadRight", Src(PadRight)),
            });

            // Precision layer, toggled on right-stick click. Same axes, much
            // softer, for docking and station approach. It replaces the four
            // flight rows and inherits everything else.
            //
            // This is NOT the yaw-versus-lateral-thrust swap the proposal
            // asked for on this button, and that swap is not implementable
            // here: Frontier ships both variants on the SAME physical output
            // and the difference lives in Elite's own binding file, which a
            // virtual pad cannot reach. Shipping a toggle that moved the axis
            // to some other output would need the pilot to re-bind Elite,
            // which is the opposite of "assign a controller and play".
            foreach (var (target, descriptor) in new[]
                     { ("LeftThumbAxisX", PadLX), ("LeftThumbAxisY", PadLY),
                       ("RightThumbAxisX", PadRX), ("RightThumbAxisY", PadRY) })
            {
                var fine = Fly(descriptor, 2.6);
                fine.Sensitivity = 0.5;
                var row = Row(target, fine);
                row.LayerMask = "Precision";
                set.Rows.Add(row);
            }
            set.ShiftActivators.Add(new ShiftActivator
            {
                DeviceGuid = "",
                Descriptor = PadRS,
                Mode = "Toggle",
                LayerMask = "Precision",
                LayerName = Strings.Instance.Starter_SpaceSim_PrecisionLayerName,
                InheritUnmapped = true,
            });
            // R3 is the activator AND a Base binding, so without this the
            // click double-fires while Precision is up. Same closure every
            // other bank in the catalog uses.
            BlockInheritedTargets(set, "Precision",
                new HashSet<string>(StringComparer.Ordinal) { PadRS });

            AddQuietLayer(set);
            return set;
        }

        /// <summary>Gyro Aim: motion for fine aim in games that only ever
        /// expected a stick. The gyro drives the right stick alongside the
        /// stick itself rather than replacing it, so motion adds precision
        /// instead of taking the stick away.
        ///
        /// <para>Engage sits on the left trigger. JoyShockMapper's reference
        /// configs ship gyro OFF and switch it on deliberately, because
        /// always-on motion reads as drift during normal play, and aiming is
        /// exactly when you want it.</para></summary>
        /// <summary>Gyro Aim's macros: the calibrate button.</summary>
        private static IEnumerable<MacroData> GyroAimMacros() => new[]
        {
            RecenterMacro("Recenter Gyro", PadBack, 800),
        };

        private static MappingSet BuildGyroAim()
        {
            var set = NewPadSet();
            set.Rows.AddRange(new[]
            {
                Row("LeftThumbAxisX", Src(PadLX)),
                Row("LeftThumbAxisY", Src(PadLY)),

                // Gyro and stick on the same axis row. Yaw is the horizontal
                // sweep, pitch the vertical.
                Row("RightThumbAxisX", GyroSource(GyroYaw), Src(PadRX)),
                Row("RightThumbAxisY", GyroSource(GyroPitch), Src(PadRY)),

                Row("RightTrigger", Src(PadRT)),
                Row("LeftTrigger", Src(PadLT)),
                Row("ButtonA", Src(PadA)),
                Row("ButtonB", Src(PadB)),
                Row("ButtonX", Src(PadX)),
                Row("ButtonY", Src(PadY)),
                Row("LeftShoulder", Src(PadLB)),
                Row("RightShoulder", Src(PadRB)),
                Row("ButtonBack", Src(PadBack)),
                Row("ButtonStart", Src(PadStart)),
                Row("ButtonGuide", Src(PadGuide)),
                Row("LeftThumbButton", Src(PadLS)),
                Row("RightThumbButton", Src(PadRS)),
                Row("DPadUp", Src(PadUp)),
                Row("DPadDown", Src(PadDown)),
                Row("DPadLeft", Src(PadLeft)),
                Row("DPadRight", Src(PadRight)),
            });
            // Slot-level engage gate: motion only steers while the left
            // trigger is held.
            set.WorkshopGyroEngageDescriptor = PadLT;

            AddQuietLayer(set);
            return set;
        }

        /// <summary>A trigger read as a BUTTON: floor and ceiling pulled in so
        /// a partial pull reads as a full press. Fighting games treat the
        /// shoulder triggers as digital buttons, and a partial pull that
        /// registers as a partial input is a dropped move.</summary>
        private static MappingSource DigitalTrigger(string descriptor)
        {
            var src = Src(descriptor);
            src.ParamRangeOuter = 0.35;   // full output by a third of the pull
            return src;
        }

        /// <summary><para>The brake trigger's inside guard. Forza ships the
        /// brake at 2 percent inside and the throttle at 0, so a finger
        /// resting on the brake does not drag it.</para>
        /// <para>The shape must be nonzero or the value never moves:
        /// WorkshopTuningApplier.FoldSourceShaping routes an inner radius
        /// through FoldStickGeometry ONLY when a shape is stamped, and the
        /// trigger's own read (SourceCoercion.ReadAsUnipolar) never calls
        /// ApplyStickDeadZoneShape at all. With the shape set, assignment
        /// folds this onto the device's own Dead Zone card
        /// (ShapingCardFor("LeftTrigger") has a live SetDeadZone, and a null
        /// SetShape it skips), which is where a trigger deadzone belongs and
        /// where the user can see and edit it.</para></summary>
        private static MappingSource BrakeTrigger(string descriptor)
        {
            var src = Src(descriptor);
            src.ParamStickDeadZoneShape = 1;   // axial: required for the fold
            src.ParamStickDeadZoneInner = 0.02;
            return src;
        }

        /// <summary><para>Jibb Smart's canonical pair: sensitivity ramps from
        /// 1 to 2, starting at a threshold of zero.</para>
        /// <para>The ramp is ParamAccel, not GyroSensitivity. GyroSensitivity
        /// is a FLAT multiplier folded into the rate itself
        /// (SourceCoercion.ReadTunedGyroRate), so 2.0 there would double slow
        /// movements too and lose the whole point. ApplyPerSourceAccel
        /// computes v * (1 + accel * |v|), so accel 1.0 is a gain of exactly
        /// 1 at rest rising to exactly 2 at full scale, and both gyro legs
        /// (bipolar and trigger) apply it. The ramp begins immediately, which
        /// is the lower threshold of zero; the upper end saturates at the
        /// lane's full scale rather than at a stated degrees-per-second, and
        /// that is the honest limit of the per-source channel.</para></summary>
        private static MappingSource GyroSource(string descriptor)
        {
            var src = Src(descriptor);
            src.ParamAccel = 1.0;
            return src;
        }

        // ── Radial menus ────────────────────────────────────────────────

        /// <summary>A radial menu hosted on a stick and gated behind a held
        /// button, which is the "hold RB, then flick the right stick" shape
        /// the proposal specifies for the strategy and CRPG hotbars.
        ///
        /// <para>The menu's own <see cref="MenuDefinitionEntry.LayerMask"/>
        /// is what gates it: "anything else engages the menu only while that
        /// layer is held". So the layer needs an activator, and the opener
        /// button's own Base binding is blocked while it is held, the same
        /// way a bank blocks what it consumes.</para></summary>
        private static void AddRadial(MappingSet set, int menuId, string layer,
            string opener, string host, string displayName,
            params (string Label, byte Vk)[] cells)
        {
            var menu = new MenuDefinitionEntry
            {
                DeviceGuid = "",
                MenuId = menuId,
                Name = displayName,
                Kind = MenuKind.Radial,
                HostDescriptor = host,
                LayerMask = layer,
                // Fires when the hosting layer ends, i.e. when the held
                // opener is released. The configurator's own wording:
                // "when the trackpad is no longer touched or when the
                // mode shift button is released".
                FireType = MenuFireType.TouchRelease,
                CellCount = cells.Length,
                HasCenter = false,
                ShowLabels = true,
            };
            for (int i = 0; i < cells.Length; i++)
            {
                menu.Items.Add(new MenuItemDefinition
                {
                    Index = i,
                    Label = cells[i].Label,
                    VirtualKey = cells[i].Vk,
                });
            }
            set.Menus.Add(menu);

            set.ShiftActivators.Add(new ShiftActivator
            {
                DeviceGuid = "",
                Descriptor = opener,
                Mode = "Hold",
                LayerMask = layer,
                LayerName = displayName,
                InheritUnmapped = true,
            });
            // The opener must not also fire its Base binding while held.
            BlockInheritedTargets(set, layer, new HashSet<string>(StringComparer.Ordinal) { opener });
        }

        // ── Macros: the keys the row engine cannot reach ────────────────

        /// <summary>
        /// Builds a device-free macro that taps one or more virtual keys when
        /// an abstract pad input fires.
        ///
        /// <para>This exists because the KbM row engine's key set is CLOSED.
        /// The media transport (play/pause, stop, next/previous track), the
        /// volume trio, the browser keys and the Windows key are all outside
        /// it, so a ROW naming them is silently dead. Macros are the lane that
        /// does reach them, via SendInput, and a starter profile can carry
        /// them because <see cref="ProfileData.Macros"/> rides the profile.</para>
        ///
        /// <para>The trigger is DEVICE-FREE, the same way the rows are: the
        /// descriptor is an abstract <c>Gamepad *</c> alias and the choice's
        /// DeviceGuid is empty, so it resolves onto whichever pad is assigned.
        /// The spec comes from <c>TriggerInputEntry.Spec</c> on a DESCRIPTOR
        /// entry rather than being hand-forged, so it cannot drift from the
        /// engine's own grammar. The comment in the body says why the
        /// raw-button builder is the wrong one here.</para>
        ///
        /// <para>Returns null when the descriptor cannot be resolved, and the
        /// caller drops it. A macro that cannot bind is never shipped.</para>
        /// </summary>
        private static MacroData KeyMacro(string name, string descriptor,
            MacroTriggerMode mode, params byte[] keys)
        {
            // DESCRIPTOR entry, not a raw-button one. TryBuildTriggerEntry
            // deliberately folds an abstract alias to its canonical
            // "Button N" so picker entries convert like raw ones, which
            // stores RawButton = 0 for "Gamepad ButtonA". That still fires
            // on a gamepad, because index 0 is A in the normalized array,
            // but it throws away the abstraction: the macro editor then
            // shows "Button 0" where every mapping row shows "Gamepad A",
            // and a force-raw or non-gamepad device would read ITS index 0.
            //
            // The descriptor form is what abstract spellings are for. Per
            // SourceDescriptor's own contract, those "have no raw-entry
            // form: the readers canonicalize abstract 'Gamepad ...'
            // spellings and evaluate ... with the same per-(device, slot)
            // tuning a mapping row gets". Spec then writes "sd:{descriptor}"
            // and the editor renders the friendly pad name.
            var entry = new MacroItem.TriggerInputEntry
            {
                DeviceGuid = Guid.Empty,
                SourceDescriptor = descriptor,
            };
            string spec = entry.Spec;
            if (string.IsNullOrEmpty(spec)) return null;

            // Press every key in order, then release in reverse, so a chord
            // (Win+Ctrl+O) holds its modifiers across the final key and a
            // single key is a plain tap.
            var actions = new List<ActionData>(keys.Length * 2);
            foreach (var vk in keys)
                actions.Add(new ActionData { Type = MacroActionType.KeyPress, KeyCode = vk });
            for (int i = keys.Length - 1; i >= 0; i--)
                actions.Add(new ActionData { Type = MacroActionType.KeyRelease, KeyCode = keys[i] });

            return new MacroData
            {
                PadIndex = 0,
                Name = name,
                IsEnabled = true,
                TriggerSource = MacroTriggerSource.InputDevice,
                TriggerInputs = spec,
                TriggerButtons = 0,
                TriggerAxisTargets = null,
                // The row lane may also bind this button; consuming the
                // trigger here would suppress it.
                ConsumeTriggerButtons = false,
                TriggerMode = mode,
                Actions = actions.ToArray(),
            };
        }

        /// <summary><para>A gyro recenter bound to a held button. "Always have
        /// a calibrate button": drifted gyro is unusable, and a recenter you
        /// can only reach by opening the app is not a recenter.</para>
        /// <para>Held rather than tapped, and non-consuming, so the button
        /// keeps its ordinary press. GyroRecenter zeroes every accumulated
        /// aim reference the slot holds, which is exactly what a calibrate
        /// button is for.</para></summary>
        private static MacroData RecenterMacro(string name, string descriptor, int holdMs)
        {
            var entry = new MacroItem.TriggerInputEntry
            {
                DeviceGuid = Guid.Empty,
                SourceDescriptor = descriptor,
            };
            string spec = entry.Spec;
            if (string.IsNullOrEmpty(spec)) return null;
            return new MacroData
            {
                PadIndex = 0, Name = name, IsEnabled = true,
                TriggerSource = MacroTriggerSource.InputDevice,
                TriggerInputs = spec, TriggerButtons = 0, TriggerAxisTargets = null,
                ConsumeTriggerButtons = false,
                TriggerMode = MacroTriggerMode.HoldForMs, TriggerHoldMs = holdMs,
                Actions = new[] { new ActionData { Type = MacroActionType.GyroRecenter } },
            };
        }

        /// <summary>A plain tap: fires the moment the button goes down.</summary>
        private static MacroData Tap(string name, string descriptor, params byte[] keys)
            => KeyMacro(name, descriptor, MacroTriggerMode.OnPress, keys);

        // Media transport, volume, browser and system keys. Every one of these
        // is OUTSIDE the KbM row engine's closed VK set, which is exactly why
        // they ride macros.
        private const byte VkMediaPlayPause = 0xB3, VkMediaStop = 0xB2;
        private const byte VkMediaNextTrack = 0xB0, VkMediaPrevTrack = 0xB1;
        private const byte VkVolumeMute = 0xAD, VkVolumeDown = 0xAE, VkVolumeUp = 0xAF;
        private const byte VkBrowserBack = 0xA6;
        private const byte VkLWin = 0x5B, VkO = 0x4F;

        // ── Shared structure ────────────────────────────────────────────

        private static MappingSet NewKbmSet() => new() { Authoritative = true };

        private static MappingSet NewPadSet() => new() { Authoritative = true };

        /// <summary>SOCD pair string for a keyboard slot: pipe-separated
        /// "vkA:vkB" using the same decimal virtual-key numbers the KbM
        /// cleaner parses.</summary>
        private static string SocdKeyPairs(byte a, byte b, byte c, byte d)
            => $"{a}:{b}|{c}:{d}";

        /// <summary>Stops an inheriting layer from ALSO firing the Base
        /// bindings of the buttons it consumes.
        ///
        /// <para>The resolver's rule, verbatim: "a zero-source layer row still
        /// BLOCKS Base fallthrough when it's an explicit NoInherit
        /// declaration". So for every Base target one of the consumed inputs
        /// drives, and which this layer does not already remap with sources,
        /// add a sourceless NoInherit row.</para>
        ///
        /// <para>The set is COMPUTED from the set's own Base rows rather than
        /// hand-listed, so a profile that later rebinds a button cannot drift
        /// out of sync with the layer that borrows it.</para></summary>
        private static void BlockInheritedTargets(MappingSet set, string layer,
            HashSet<string> consumed)
        {
            var remapped = new HashSet<string>(
                set.Rows.Where(r => r.LayerMask == layer && r.Sources.Count > 0)
                        .Select(r => r.Target),
                StringComparer.Ordinal);

            foreach (var target in set.Rows
                         .Where(r => (r.LayerMask ?? "Base") == "Base"
                                  && r.Sources.Any(x => consumed.Contains(x.Descriptor)))
                         .Select(r => r.Target)
                         .Distinct(StringComparer.Ordinal)
                         .ToList())
            {
                if (remapped.Contains(target)) continue;
                set.Rows.Add(new MappingRow
                {
                    Target = target, LayerMask = layer, NoInherit = true,
                });
            }
        }

        /// <summary>Adds one held ability bank on the given trigger: four
        /// D-pad slots then four face slots, which is the Cross Hotbar's own
        /// arrangement.
        ///
        /// <para><b>InheritUnmapped is TRUE, and that alone would double-fire.</b>
        /// The layer must inherit, or holding the bank would kill the cursor,
        /// the movement keys and everything else the profile maps. But
        /// inheriting means a target the bank does NOT remap still falls
        /// through to Base, and the bank's buttons usually DO drive a Base
        /// target: on Hotbar, A is "5" on the layer and Space on Base, so one
        /// press emitted both.</para>
        ///
        /// <para>The fix is per-target, and the resolver states it exactly:
        /// "a zero-source layer row still BLOCKS Base fallthrough when it's an
        /// explicit NoInherit declaration". So for every Base target one of
        /// this bank's buttons drives, and for the activator's own button, add
        /// a sourceless NoInherit row on the layer. The bank keys fire, the
        /// inherited base bindings for those buttons do not, and everything
        /// else still inherits.</para>
        ///
        /// <para>The block set is COMPUTED from the set's own Base rows rather
        /// than hand-listed per profile, so a profile that later rebinds a face
        /// button cannot drift out of sync with its bank.</para></summary>
        private static void AddBank(MappingSet set, string layer, string displayName,
            string activator, int doublePressMs, byte[] keys)
        {
            string[] buttons = { PadUp, PadDown, PadLeft, PadRight, PadA, PadB, PadX, PadY };
            int n = Math.Min(buttons.Length, keys.Length);

            for (int i = 0; i < n; i++)
                set.Rows.Add(LayerRow(layer, Key(keys[i]), Src(buttons[i])));

            // Every physical input this bank consumes: its eight slots plus the
            // trigger that opens it (holding LT to reach the bank must not also
            // hold the left click LT drives on Base).
            var consumed = new HashSet<string>(StringComparer.Ordinal) { activator };
            for (int i = 0; i < n; i++) consumed.Add(buttons[i]);

            BlockInheritedTargets(set, layer, consumed);

            set.ShiftActivators.Add(new ShiftActivator
            {
                DeviceGuid = "",
                Descriptor = activator,
                Mode = "Hold",
                LayerMask = layer,
                // The MASK is the internal id; the NAME is what the shift-layer
                // flyout renders (InputService resolves it into
                // ShiftLayerFlyout's LayerNameText), so it is localized like
                // every other string the user reads.
                LayerName = displayName,
                InheritUnmapped = true,
                DoublePressMs = doublePressMs,
            });
        }

        /// <summary>Every starter carries a silent layer on a long-press of
        /// Start, so the pad can be muted without unassigning it.
        ///
        /// <para><b>THE LAYER IS EMPTY ON PURPOSE. The emptiness IS the
        /// mechanism.</b> With <see cref="ShiftActivator.InheritUnmapped"/>
        /// false the layer REPLACES Base outright, and every target with no
        /// row on it outputs zero. A layer that maps nothing therefore maps
        /// EVERYTHING to nothing, which is exactly "the controller stops
        /// sending". Adding rows here would defeat it.</para>
        ///
        /// <para>Steam ships <c>empty.vdf</c> ("Empty Bindings, Use as Base")
        /// for the same reason, and AntiMicroX's Desktop and Civilization
        /// profiles both reserve a deliberately blank set. The layer NAME
        /// says so out loud, because an empty layer is otherwise
        /// indistinguishable from an unfinished one.</para></summary>
        private static void AddQuietLayer(MappingSet set)
        {
            set.ShiftActivators.Add(new ShiftActivator
            {
                DeviceGuid = "",
                Descriptor = PadStart,
                Mode = "Toggle",
                LayerMask = "Quiet",
                LayerName = Strings.Instance.Starter_QuietLayerName,
                InheritUnmapped = false,
                DelayMs = 600,
            });
        }

        /// <summary>Wraps one mapping set as a complete single-slot profile.
        /// Mirrors WorkshopProfileMaterializer: one claimed slot, the default
        /// per-type profile id, and an EMPTY (never null) macro array, since
        /// null Macros is the legacy sentinel meaning "leave the live set
        /// alone" and a starter profile owns its state outright.
        /// ExecutableNames stays empty: an archetype has no executable, so it
        /// never auto-switches and is always chosen by hand.</summary>
        private static ProfileData Wrap(string name, VirtualControllerType type, MappingSet set)
            => Wrap(name, null, (type, set));

        /// <summary>Wraps a single-slot profile that also carries macros. Any
        /// macro that failed to bind its trigger comes through as null and is
        /// dropped, so a profile never ships a macro that cannot fire.</summary>
        private static ProfileData Wrap(string name, VirtualControllerType type,
            MappingSet set, IEnumerable<MacroData> macros)
            => Wrap(name, macros, (type, set));

        /// <summary>Wraps one or more slots as a profile. A SPLIT config (a pad
        /// slot plus a keyboard slot) is the shape the Workshop importer already
        /// produces whenever a config needs both output kinds, and Emulation
        /// needs it because its hotkey verbs are keyboard keys that a gamepad
        /// slot cannot send. Slots are claimed in argument order.</summary>
        private static ProfileData Wrap(string name, IEnumerable<MacroData> macros,
            params (VirtualControllerType Type, MappingSet Set)[] slots)
        {
            int maxPads = InputManager.MaxPads;
            var created = new bool[maxPads];
            var enabled = new bool[maxPads];
            var types = new int[maxPads];
            var ids = new string[maxPads];
            var sets = new MappingSet[maxPads];

            for (int i = 0; i < slots.Length && i < maxPads; i++)
            {
                created[i] = true;
                enabled[i] = true;
                types[i] = (int)slots[i].Type;
                ids[i] = InputManager.GetDefaultProfileId(slots[i].Type);
                sets[i] = slots[i].Set;
            }

            return new ProfileData
            {
                Name = name,
                SlotCreated = created,
                SlotEnabled = enabled,
                SlotControllerTypes = types,
                SlotProfileIds = ids,
                SlotMappingSets = sets,
                Macros = macros?.Where(m => m != null).ToArray() ?? Array.Empty<MacroData>(),
                ExecutableNames = string.Empty,
            };
        }

        // ── Catalog ─────────────────────────────────────────────────────

        private static List<StarterProfileInfo> _all;

        /// <summary>The shipped starter profiles, in display order.</summary>
        public static IReadOnlyList<StarterProfileInfo> All => _all ??= new List<StarterProfileInfo>
        {
            new("desktop", VirtualControllerType.KeyboardMouse,
                () => Wrap(Strings.Instance.Starter_Desktop_Name,
                    VirtualControllerType.KeyboardMouse, BuildDesktop(), DesktopMacros()),
                s => s.Starter_Desktop_Name, s => s.Starter_Desktop_Description),

            new("wasd", VirtualControllerType.KeyboardMouse,
                () => Wrap(Strings.Instance.Starter_Wasd_Name,
                    VirtualControllerType.KeyboardMouse, BuildWasdAndMouse()),
                s => s.Starter_Wasd_Name, s => s.Starter_Wasd_Description),

            new("hotbar", VirtualControllerType.KeyboardMouse,
                () => Wrap(Strings.Instance.Starter_Hotbar_Name,
                    VirtualControllerType.KeyboardMouse, BuildHotbar()),
                s => s.Starter_Hotbar_Name, s => s.Starter_Hotbar_Description),

            new("pointclick", VirtualControllerType.KeyboardMouse,
                () => Wrap(Strings.Instance.Starter_PointClick_Name,
                    VirtualControllerType.KeyboardMouse, BuildPointAndClick(), PointAndClickMacros()),
                s => s.Starter_PointClick_Name, s => s.Starter_PointClick_Description),

            new("strategy", VirtualControllerType.KeyboardMouse,
                () => Wrap(Strings.Instance.Starter_Strategy_Name,
                    VirtualControllerType.KeyboardMouse, BuildStrategy(), StrategyMacros()),
                s => s.Starter_Strategy_Name, s => s.Starter_Strategy_Description),

            new("isometric", VirtualControllerType.KeyboardMouse,
                () => Wrap(Strings.Instance.Starter_Isometric_Name,
                    VirtualControllerType.KeyboardMouse, BuildIsometricRpg()),
                s => s.Starter_Isometric_Name, s => s.Starter_Isometric_Description),

            new("twinstick", VirtualControllerType.KeyboardMouse,
                () => Wrap(Strings.Instance.Starter_TwinStick_Name,
                    VirtualControllerType.KeyboardMouse, BuildTwinStick()),
                s => s.Starter_TwinStick_Name, s => s.Starter_TwinStick_Description),

            new("media", VirtualControllerType.KeyboardMouse,
                () => Wrap(Strings.Instance.Starter_Media_Name,
                    VirtualControllerType.KeyboardMouse, BuildMediaRemote(), MediaRemoteMacros()),
                s => s.Starter_Media_Name, s => s.Starter_Media_Description),

            new("emulation", VirtualControllerType.Xbox,
                () => Wrap(Strings.Instance.Starter_Emulation_Name, null,
                    (VirtualControllerType.Xbox, BuildEmulation()),
                    (VirtualControllerType.KeyboardMouse, BuildEmulationHotkeys())),
                s => s.Starter_Emulation_Name, s => s.Starter_Emulation_Description),

            new("fighting", VirtualControllerType.Xbox,
                () => Wrap(Strings.Instance.Starter_Fighting_Name,
                    VirtualControllerType.Xbox, BuildFightingGames()),
                s => s.Starter_Fighting_Name, s => s.Starter_Fighting_Description),

            new("racing", VirtualControllerType.Xbox,
                () => Wrap(Strings.Instance.Starter_Racing_Name,
                    VirtualControllerType.Xbox, BuildRacing()),
                s => s.Starter_Racing_Name, s => s.Starter_Racing_Description),

            new("spacesim", VirtualControllerType.Xbox,
                () => Wrap(Strings.Instance.Starter_SpaceSim_Name,
                    VirtualControllerType.Xbox, BuildSpaceSim()),
                s => s.Starter_SpaceSim_Name, s => s.Starter_SpaceSim_Description),

            new("gyroaim", VirtualControllerType.Xbox,
                () => Wrap(Strings.Instance.Starter_GyroAim_Name,
                    VirtualControllerType.Xbox, BuildGyroAim(), GyroAimMacros()),
                s => s.Starter_GyroAim_Name, s => s.Starter_GyroAim_Description),
        };

        /// <summary>Looks a starter profile up by its stable key.</summary>
        public static StarterProfileInfo Find(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            foreach (var p in All)
                if (string.Equals(p.Key, key, StringComparison.Ordinal)) return p;
            return null;
        }
    }

    /// <summary>One entry in the starter gallery. Name and description read
    /// through the live <see cref="Strings"/> instance so a language change
    /// re-reads them without rebuilding the catalog.</summary>
    public sealed class StarterProfileInfo
    {
        private readonly Func<Strings, string> _name;
        private readonly Func<Strings, string> _description;
        private readonly Func<ProfileData> _build;

        internal StarterProfileInfo(string key, VirtualControllerType outputType,
            Func<ProfileData> build, Func<Strings, string> name, Func<Strings, string> description)
        {
            Key = key;
            OutputType = outputType;
            _build = build;
            _name = name;
            _description = description;
        }

        /// <summary>Stable identifier, independent of display language.</summary>
        public string Key { get; }

        /// <summary>Virtual controller type the profile's single slot creates.</summary>
        public VirtualControllerType OutputType { get; }

        /// <summary>Localized display name.</summary>
        public string Name => _name(Strings.Instance);

        /// <summary>Localized one-line description.</summary>
        public string Description => _description(Strings.Instance);

        /// <summary>Localized output-type label for the gallery card.</summary>
        public string OutputLabel => OutputType == VirtualControllerType.KeyboardMouse
            ? Strings.Instance.Starter_OutputKeyboardMouse
            : Strings.Instance.Starter_OutputGamepad;

        /// <summary>Builds a fresh <see cref="ProfileData"/>. Called once per
        /// save so two saves never share mutable state.</summary>
        public ProfileData Build() => _build();
    }
}
