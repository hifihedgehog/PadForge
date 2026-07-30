using System;
using System.Collections.Generic;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Resources.Strings;

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
        private const byte VkBackspace = 0x08, VkDelete = 0x2E;
        private const byte VkShift = 0xA0;    // VK_LSHIFT
        private const byte VkControl = 0xA2;  // VK_LCONTROL
        private const byte VkMenu = 0xA4;     // VK_LMENU (Alt)
        private const byte VkLeft = 0x25, VkUp = 0x26, VkRight = 0x27, VkDown = 0x28;
        private const byte VkPageUp = 0x21, VkPageDown = 0x22;
        private const byte Vk0 = 0x30, Vk1 = 0x31, Vk2 = 0x32, Vk3 = 0x33, Vk4 = 0x34;
        private const byte Vk5 = 0x35, Vk6 = 0x36, Vk7 = 0x37, Vk8 = 0x38, Vk9 = 0x39;
        private const byte VkA = 0x41, VkC = 0x43, VkD = 0x44, VkE = 0x45, VkF = 0x46;
        private const byte VkG = 0x47, VkI = 0x49, VkM = 0x4D, VkQ = 0x51, VkR = 0x52;
        private const byte VkS = 0x53, VkV = 0x56, VkW = 0x57;
        private const byte VkF1 = 0x70, VkF2 = 0x71, VkF3 = 0x72, VkF4 = 0x73, VkF5 = 0x74;
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
        private const string MLeft = "KbmMBtn0", MRight = "KbmMBtn1", MMiddle = "KbmMBtn2";

        /// <summary>The cursor pair every mouse-driving starter profile
        /// shares: both touchpads when the hardware has them, with the stick
        /// alongside as a rate cursor. While no finger is down the pointer
        /// contributes nothing and the stick drives; the moment a finger
        /// lands the row routes absolute.</summary>
        private static IEnumerable<MappingRow> CursorRows() => new[]
        {
            Row(MouseX, Src(Pad1PtrX), Src(Pad0PtrX), Src(PadRX)),
            Row(MouseY, Src(Pad1PtrY), Src(Pad0PtrY), Src(PadRY)),
        };

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
                // Scroll on the left stick. Step 3 negates the scroll axis
                // (KbmScroll positive = up after that negation), so the
                // stick's raw sign is inverted here to make pushing up
                // scroll up.
                Row(Scroll, Src(PadLY)),
                Row(ScrollH, Src(PadLX)),

                Row(MLeft, Src(PadRT)),
                Row(MRight, Src(PadLT)),
                Row(MMiddle, Src(PadLS)),

                Row(Key(VkReturn), Src(PadA)),
                Row(Key(VkEscape), Src(PadB)),
                Row(Key(VkSpace), Src(PadY)),
                Row(Key(VkBackspace), Src(PadX)),

                Row(Key(VkUp), Src(PadUp)),
                Row(Key(VkDown), Src(PadDown)),
                Row(Key(VkLeft), Src(PadLeft)),
                Row(Key(VkRight), Src(PadRight)),

                Row(Key(VkControl), Src(PadLB)),
                Row(Key(VkMenu), Src(PadRB)),
                Row(Key(VkTab), Src(PadBack)),
            });
            AddQuietLayer(set);
            return set;
        }

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

                Row(MLeft, Src(PadRT)),
                Row(MRight, Src(PadLT)),
                Row(MMiddle, Src(PadRS)),

                Row(Key(VkSpace), Src(PadA)),   // Jump
                Row(Key(VkE), Src(PadB)),       // Use
                Row(Key(VkR), Src(PadX)),       // Reload
                Row(Key(VkF), Src(PadY)),       // Flashlight
                Row(Key(VkQ), Src(PadLB)),
                Row(Key(VkG), Src(PadRB)),
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

            // Bank 1: hold LT. Four D-pad slots plus four face slots.
            AddBank(set, "Bank1", PadLT, doublePressMs: 0,
                new[] { Vk1, Vk2, Vk3, Vk4, Vk5, Vk6, Vk7, Vk8 });
            // Bank 2: hold RT.
            AddBank(set, "Bank2", PadRT, doublePressMs: 0,
                new[] { Vk9, Vk0, VkOemMinus, VkOemPlus, VkF1, VkF2, VkF3, VkF4 });

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

            // Hotkey layer: hold Back. InheritUnmapped keeps every unlisted
            // target falling through to Base, which is what RetroArch's
            // input_enable_hotkey does (the modifier gates the hotkeys and
            // leaves everything else alone).
            set.ShiftActivators.Add(new ShiftActivator
            {
                DeviceGuid = "",
                Descriptor = PadBack,
                Mode = "Hold",
                LayerMask = "Hotkey",
                LayerName = "Hotkey",
                InheritUnmapped = true,
            });
            return set;
        }

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
                Row("RightTrigger", Src(PadRT)),

                Row("LeftShoulder", Src(PadLB)),
                Row("LeftTrigger", Src(PadLT)),
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
            set.Rows.AddRange(CursorRows());
            set.Rows.AddRange(new[]
            {
                Row(MLeft, Src(PadRT), Src(PadA)),
                Row(MRight, Src(PadLT)),

                Row(Key(VkEscape), Src(PadB), Src(PadStart)),
                Row(Key(VkI), Src(PadX)),
                // Hold-to-reveal-hotspots is widespread but never
                // standardised; implementations split between Tab and Space.
                // Tab here, Space on Back for the skip-dialogue half.
                Row(Key(VkTab), Src(PadY)),
                Row(Key(VkSpace), Src(PadBack)),

                Row(Key(VkPageUp), Src(PadLB)),
                Row(Key(VkPageDown), Src(PadRB)),

                Row(Key(VkUp), Src(PadUp)),
                Row(Key(VkDown), Src(PadDown)),
                Row(Key(VkLeft), Src(PadLeft)),
                Row(Key(VkRight), Src(PadRight)),
            });
            AddQuietLayer(set);
            return set;
        }

        /// <summary>Strategy: RTS, 4X, grand strategy, city builders.
        ///
        /// <para>The camera goes on the LEFT stick and the cursor on the
        /// right, which is what both verified references do (the AntiMicroX
        /// Civilization V profile and a fully specified Command and Conquer
        /// layout), and what Valve's own strategy guidance implies by putting
        /// the camera on a directional surface. Clicks stay on the triggers so
        /// the pointing device and the click button are physically
        /// independent, which is the only way box-select works.</para></summary>
        private static MappingSet BuildStrategy()
        {
            var set = NewKbmSet();
            set.Rows.AddRange(CursorRows());
            set.Rows.AddRange(new[]
            {
                // Camera pan on the left stick, as arrow keys.
                Row(Key(VkUp), Up(PadLY), Src(PadUp)),
                Row(Key(VkDown), Down(PadLY), Src(PadDown)),
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
                Row(Key(VkOemMinus), Src(PadLS)),
            });
            // The hotbar bank is why this profile exists: ten number keys on a
            // held modifier is the difference between the genre being playable
            // on a pad and not.
            AddBank(set, "Hotbar", PadBack, doublePressMs: 0,
                new[] { Vk1, Vk2, Vk3, Vk4, Vk5, Vk6, Vk7, Vk8 });
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
            set.Rows.AddRange(CursorRows());
            set.Rows.AddRange(new[]
            {
                // Camera pan on the left stick; the cursor is always live
                // because this drives a mouse-only game.
                Row(Key(VkUp), Up(PadLY)),
                Row(Key(VkDown), Down(PadLY)),
                Row(Key(VkLeft), Left(PadLX)),
                Row(Key(VkRight), Right(PadLX)),

                Row(MLeft, Src(PadRT)),
                Row(MRight, Src(PadLT)),

                Row(Key(VkSpace), Src(PadA)),   // pause, the genre's most-pressed key
                Row(Key(VkEscape), Src(PadB)),
                Row(Key(VkI), Src(PadX)),       // inventory
                Row(Key(VkReturn), Src(PadY)),  // Larian's End Turn
                Row(Key(VkTab), Src(PadRS)),    // highlight interactables
                Row(Key(VkC), Src(PadLB)),      // character
                Row(Key(VkM), Src(PadRB)),      // map

                Row(Key(VkF5), Src(PadStart)),   // quicksave
            });
            AddBank(set, "Hotbar", PadBack, doublePressMs: 0,
                new[] { Vk1, Vk2, Vk3, Vk4, Vk5, Vk6, Vk7, Vk8 });
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
        /// fullscreen-video context and Valve ships videoplayer.vdf; they
        /// agree on the shape and differ only on which player keys they emit.
        ///
        /// <para>Deliberately built from keys the KbM row engine actually
        /// carries. Space, the arrows, F and M are what every web and desktop
        /// player listens for. The SYSTEM media keys (volume, next / previous
        /// track, browser back) sit outside the row engine's closed VK set and
        /// would each need a paired hold macro, so they are left out rather
        /// than shipped as rows that silently never fire.</para></summary>
        private static MappingSet BuildMediaRemote()
        {
            var set = NewKbmSet();
            set.Rows.AddRange(CursorRows());
            set.Rows.AddRange(new[]
            {
                Row(Key(VkSpace), Src(PadA)),      // play / pause, near-universal
                Row(Key(VkBackspace), Src(PadB)),  // back / up
                Row(Key(VkM), Src(PadX)),          // mute
                Row(Key(VkF), Src(PadY)),          // fullscreen

                // Seek on left/right, volume on up/down, which is what every
                // in-page player binds the arrows to.
                Row(Key(VkLeft), Src(PadLeft), Left(PadLX)),
                Row(Key(VkRight), Src(PadRight), Right(PadLX)),
                Row(Key(VkUp), Src(PadUp), Up(PadLY)),
                Row(Key(VkDown), Src(PadDown), Down(PadLY)),

                Row(Key(VkPageUp), Src(PadLB)),
                Row(Key(VkPageDown), Src(PadRB)),

                Row(MLeft, Src(PadRT)),
                Row(MRight, Src(PadLT)),
                Row(Key(VkEscape), Src(PadStart)),
                Row(Key(VkTab), Src(PadBack)),
            });
            AddQuietLayer(set);
            return set;
        }

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

                // Throttle and brake pass straight through. The pedal feel
                // that matters is the game's own trigger curve, and a second
                // shaping layer here would fight it.
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
        private static MappingSet BuildGyroAim()
        {
            var set = NewPadSet();
            set.Rows.AddRange(new[]
            {
                Row("LeftThumbAxisX", Src(PadLX)),
                Row("LeftThumbAxisY", Src(PadLY)),

                // Gyro and stick on the same axis row. Yaw is the horizontal
                // sweep, pitch the vertical.
                Row("RightThumbAxisX", Src(GyroYaw), Src(PadRX)),
                Row("RightThumbAxisY", Src(GyroPitch), Src(PadRY)),

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
            return set;
        }

        // ── Shared structure ────────────────────────────────────────────

        private static MappingSet NewKbmSet() => new() { Authoritative = true };

        private static MappingSet NewPadSet() => new() { Authoritative = true };

        /// <summary>SOCD pair string for a keyboard slot: pipe-separated
        /// "vkA:vkB" using the same decimal virtual-key numbers the KbM
        /// cleaner parses.</summary>
        private static string SocdKeyPairs(byte a, byte b, byte c, byte d)
            => $"{a}:{b}|{c}:{d}";

        /// <summary>Adds one held ability bank on the given trigger: four
        /// D-pad slots then four face slots, which is the Cross Hotbar's own
        /// arrangement. InheritUnmapped is FALSE so the bank replaces the
        /// base bindings on those eight buttons while held, instead of
        /// firing both.</summary>
        private static void AddBank(MappingSet set, string layer, string activator,
            int doublePressMs, byte[] keys)
        {
            string[] buttons = { PadUp, PadDown, PadLeft, PadRight, PadA, PadB, PadX, PadY };
            for (int i = 0; i < buttons.Length && i < keys.Length; i++)
                set.Rows.Add(LayerRow(layer, Key(keys[i]), Src(buttons[i])));

            set.ShiftActivators.Add(new ShiftActivator
            {
                DeviceGuid = "",
                Descriptor = activator,
                Mode = "Hold",
                LayerMask = layer,
                LayerName = layer,
                InheritUnmapped = true,
                DoublePressMs = doublePressMs,
            });
        }

        /// <summary>Every keyboard-and-mouse starter carries a silent layer on a
        /// long-press of Start, so the pad can be muted without unassigning it.
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
        {
            int maxPads = InputManager.MaxPads;
            var created = new bool[maxPads];
            var enabled = new bool[maxPads];
            var types = new int[maxPads];
            var ids = new string[maxPads];
            var sets = new MappingSet[maxPads];

            created[0] = true;
            enabled[0] = true;
            types[0] = (int)type;
            ids[0] = InputManager.GetDefaultProfileId(type);
            sets[0] = set;

            return new ProfileData
            {
                Name = name,
                SlotCreated = created,
                SlotEnabled = enabled,
                SlotControllerTypes = types,
                SlotProfileIds = ids,
                SlotMappingSets = sets,
                Macros = Array.Empty<MacroData>(),
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
                    VirtualControllerType.KeyboardMouse, BuildDesktop()),
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
                    VirtualControllerType.KeyboardMouse, BuildPointAndClick()),
                s => s.Starter_PointClick_Name, s => s.Starter_PointClick_Description),

            new("strategy", VirtualControllerType.KeyboardMouse,
                () => Wrap(Strings.Instance.Starter_Strategy_Name,
                    VirtualControllerType.KeyboardMouse, BuildStrategy()),
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
                    VirtualControllerType.KeyboardMouse, BuildMediaRemote()),
                s => s.Starter_Media_Name, s => s.Starter_Media_Description),

            new("emulation", VirtualControllerType.Xbox,
                () => Wrap(Strings.Instance.Starter_Emulation_Name,
                    VirtualControllerType.Xbox, BuildEmulation()),
                s => s.Starter_Emulation_Name, s => s.Starter_Emulation_Description),

            new("fighting", VirtualControllerType.Xbox,
                () => Wrap(Strings.Instance.Starter_Fighting_Name,
                    VirtualControllerType.Xbox, BuildFightingGames()),
                s => s.Starter_Fighting_Name, s => s.Starter_Fighting_Description),

            new("racing", VirtualControllerType.Xbox,
                () => Wrap(Strings.Instance.Starter_Racing_Name,
                    VirtualControllerType.Xbox, BuildRacing()),
                s => s.Starter_Racing_Name, s => s.Starter_Racing_Description),

            new("gyroaim", VirtualControllerType.Xbox,
                () => Wrap(Strings.Instance.Starter_GyroAim_Name,
                    VirtualControllerType.Xbox, BuildGyroAim()),
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
