using System;
using System.Collections.Generic;
using System.Linq;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Common.Mapping;
using PadForge.Engine.Data;
using PadForge.Engine.Menus;
using PadForge.Services;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The bundled starter profiles (#256). These tests exist because a
    /// starter profile is authored once and then shipped to everyone, so a
    /// typo in a descriptor is not a bug one user hits, it is a bug every
    /// user hits with no way to tell what went wrong.
    ///
    /// <para>Every test drives the real catalog through the real
    /// <see cref="SourceCoercion"/> parsers. Nothing here re-implements the
    /// grammar it is checking, which is the failure mode that makes a green
    /// suite meaningless.</para>
    /// </summary>
    // Serialized against the other classes that drive InputManager's per-slot
    // statics. HoldingOneChordLeg drives the real layer resolver, which writes
    // the slot-0 suppression set, and those are process-global: run in parallel
    // with the macro tests it perturbs their exact-equality asserts.
    [Collection("SettingsManagerStatics")]
    public class StarterProfileCatalogTests
    {
        private static IEnumerable<(StarterProfileInfo Info, ProfileData Profile)> Built()
            => StarterProfileCatalog.All.Select(i => (i, i.Build()));

        private static IEnumerable<(string Key, MappingSet Set)> Sets()
            => Built().SelectMany(b => b.Profile.SlotMappingSets
                .Where(s => s != null)
                .Select(s => (b.Info.Key, s)));

        /// <summary>The primary gyro channels a device-agnostic profile may
        /// name. The aux pair ("Gyro L Pitch" and friends) is left out on
        /// purpose: it exists only on a Joy-Con pair, so a starter profile
        /// must not assume it.</summary>
        private static readonly HashSet<string> GyroChannels =
            new(StringComparer.Ordinal) { "Gyro Pitch", "Gyro Yaw", "Gyro Roll" };

        /// <summary>Ordinal comparer for the (layer, target) grouping key.
        /// Declared rather than relying on the default so the grouping is
        /// case-sensitive, matching how the engine compares layer masks.</summary>
        private sealed class StringTupleComparer : IEqualityComparer<(string, string)>
        {
            public static readonly StringTupleComparer Instance = new();
            public bool Equals((string, string) a, (string, string) b)
                => string.Equals(a.Item1, b.Item1, StringComparison.Ordinal)
                && string.Equals(a.Item2, b.Item2, StringComparison.Ordinal);
            public int GetHashCode((string, string) v)
                => HashCode.Combine(v.Item1, v.Item2);
        }

        private static IEnumerable<(string Key, MappingRow Row, MappingSource Source)> Sources()
            => Sets().SelectMany(t => t.Set.Rows.SelectMany(r =>
                r.Sources.Select(s => (t.Key, r, s))));

        [Fact]
        public void Catalog_IsNotEmpty_AndKeysAreUnique()
        {
            var keys = StarterProfileCatalog.All.Select(p => p.Key).ToList();
            Assert.NotEmpty(keys);
            Assert.Equal(keys.Count, keys.Distinct(StringComparer.Ordinal).Count());
            Assert.All(keys, k => Assert.False(string.IsNullOrWhiteSpace(k)));
        }

        /// <summary>
        /// THE load-bearing one. Without Authoritative the legacy automap
        /// merge injects the assigned device's own descriptors on top of the
        /// authored rows and every single input fires twice. It is invisible
        /// until a device is assigned, which is exactly when a user first
        /// tries the profile.
        /// </summary>
        [Fact]
        public void EverySet_IsAuthoritative()
        {
            foreach (var (key, set) in Sets())
                Assert.True(set.Authoritative,
                    $"starter '{key}' has a MappingSet that is not Authoritative, so every input would double-fire");
        }

        /// <summary>A starter profile must never name hardware. An empty
        /// DeviceGuid is the "(Any device)" choice, which is what lets one
        /// profile serve whichever controller the user assigns.</summary>
        [Fact]
        public void EverySource_TargetsAnyDevice()
        {
            foreach (var (key, row, src) in Sources())
                Assert.True(string.IsNullOrEmpty(src.DeviceGuid),
                    $"starter '{key}' row '{row.Target}' pins a device GUID ('{src.DeviceGuid}')");
        }

        /// <summary>Every source descriptor must be one the engine actually
        /// resolves: an abstract gamepad alias, or an absolute touchpad
        /// pointer. Anything else is a typo that silently reads zero.</summary>
        [Fact]
        public void EverySource_UsesAResolvableDescriptor()
        {
            var aliases = SourceCoercion.GamepadAliasTable
                .Select(a => "Gamepad " + a.Member)
                .ToHashSet(StringComparer.Ordinal);

            foreach (var (key, row, src) in Sources())
            {
                bool ok = aliases.Contains(src.Descriptor)
                       || SourceCoercion.IsTouchpadPointerDescriptor(src.Descriptor)
                       || GyroChannels.Contains(src.Descriptor);
                Assert.True(ok,
                    $"starter '{key}' row '{row.Target}' uses unresolvable descriptor '{src.Descriptor}'");
            }
        }

        /// <summary>
        /// Every keyboard target must be a key the KbM row engine actually
        /// carries. Its VK set is CLOSED, built once in the InputManager
        /// static constructor, and a row naming a key outside it is silently
        /// dead: no error, no warning, the binding simply never fires.
        ///
        /// <para>This caught four real dead rows on arrival. The set holds only
        /// the SIDED modifiers (0xA0..0xA5), so the unsided VK_SHIFT 0x10,
        /// VK_CONTROL 0x11 and VK_MENU 0x12 are all absent, and VK_LWIN 0x5B is
        /// absent entirely. Desktop had authored Ctrl, Alt and the Windows key,
        /// and both WASD and Hotbar had authored Shift.</para>
        ///
        /// <para>Reads the engine's own table by reflection rather than
        /// restating it, because a copied list would drift and then agree with
        /// itself forever.</para>
        /// </summary>
        [Fact]
        public void EveryKeyTarget_IsInTheEnginesClosedVkSet()
        {
            var field = typeof(InputManager).GetField("KbmKeyVkCodes",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            Assert.True(field != null, "InputManager.KbmKeyVkCodes not found; the KbM key table moved");
            var supported = ((byte[])field.GetValue(null)).ToHashSet();
            Assert.True(supported.Count > 60, "the KbM key table looks truncated");

            foreach (var (key, set) in Sets())
            {
                foreach (var row in set.Rows)
                {
                    if (!row.Target.StartsWith("KbmKey", StringComparison.Ordinal)) continue;
                    string hex = row.Target.Substring("KbmKey".Length);
                    Assert.True(byte.TryParse(hex,
                            System.Globalization.NumberStyles.HexNumber,
                            System.Globalization.CultureInfo.InvariantCulture, out byte vk),
                        $"starter '{key}' target '{row.Target}' is not KbmKey + two hex digits");
                    Assert.True(supported.Contains(vk),
                        $"starter '{key}' binds '{row.Target}' (VK 0x{vk:X2}), which the KbM row " +
                        "engine does not carry, so the row is dead");
                }
            }
        }

        /// <summary>Non-key KbM targets must be ones the engine routes.</summary>
        [Fact]
        public void EveryNonKeyKbmTarget_IsRecognised()
        {
            var known = new HashSet<string>(StringComparer.Ordinal)
            {
                "KbmMouseX", "KbmMouseXNeg", "KbmMouseY", "KbmMouseYNeg",
                "KbmScroll", "KbmScrollNeg", "KbmScrollH", "KbmScrollHNeg",
                "KbmMBtn0", "KbmMBtn1", "KbmMBtn2", "KbmMBtn3", "KbmMBtn4",
            };
            foreach (var (key, set) in Sets())
                foreach (var row in set.Rows)
                {
                    if (!row.Target.StartsWith("Kbm", StringComparison.Ordinal)) continue;
                    if (row.Target.StartsWith("KbmKey", StringComparison.Ordinal)) continue;
                    Assert.True(known.Contains(row.Target),
                        $"starter '{key}' uses unrecognised KbM target '{row.Target}'");
                }
        }

        /// <summary>Gamepad-output profiles must name real mapping targets, or
        /// the row goes nowhere. Checked against the migrator's canonical
        /// list, which is the engine's own vocabulary.</summary>
        [Fact]
        public void EveryGamepadTarget_IsAKnownMappingTarget()
        {
            var known = new HashSet<string>(StringComparer.Ordinal)
            {
                "ButtonA", "ButtonB", "ButtonX", "ButtonY",
                "LeftShoulder", "RightShoulder",
                "ButtonBack", "ButtonStart", "ButtonGuide", "ButtonShare",
                "LeftThumbButton", "RightThumbButton",
                "DPadUp", "DPadDown", "DPadLeft", "DPadRight",
                "LeftThumbAxisX", "LeftThumbAxisY",
                "RightThumbAxisX", "RightThumbAxisY",
                "LeftTrigger", "RightTrigger",
            };
            foreach (var (info, p) in Built())
            {
                if (info.OutputType == VirtualControllerType.KeyboardMouse) continue;
                foreach (var row in p.SlotMappingSets[0].Rows)
                    Assert.True(known.Contains(row.Target),
                        $"starter '{info.Key}' uses unknown gamepad target '{row.Target}'");
            }
        }

        /// <summary>Shift activators name real inputs too, and every one must
        /// carry the layer it engages.</summary>
        [Fact]
        public void EveryActivator_IsWellFormed()
        {
            var aliases = SourceCoercion.GamepadAliasTable
                .Select(a => "Gamepad " + a.Member)
                .ToHashSet(StringComparer.Ordinal);

            foreach (var (key, set) in Sets())
            {
                foreach (var act in set.ShiftActivators)
                {
                    Assert.True(aliases.Contains(act.Descriptor),
                        $"starter '{key}' activator uses unresolvable descriptor '{act.Descriptor}'");
                    Assert.True(string.IsNullOrEmpty(act.DeviceGuid),
                        $"starter '{key}' activator pins a device GUID");
                    Assert.False(string.IsNullOrWhiteSpace(act.LayerMask),
                        $"starter '{key}' activator has no LayerMask");
                }
            }
        }

        /// <summary>A row on a named layer is dead unless an activator
        /// engages that layer, and an activator with no rows is a layer that
        /// does nothing. The quiet layer is the deliberate exception: it is
        /// empty ON PURPOSE, because a layer that maps nothing while engaged
        /// is how the pad goes silent.</summary>
        [Fact]
        public void EveryNonBaseLayer_HasAnActivator()
        {
            foreach (var (key, set) in Sets())
            {
                var engaged = set.ShiftActivators
                    .Select(a => a.LayerMask)
                    .ToHashSet(StringComparer.Ordinal);

                foreach (var layer in set.Rows.Select(r => r.LayerMask ?? "Base").Distinct())
                {
                    if (string.Equals(layer, "Base", StringComparison.Ordinal)) continue;
                    Assert.True(engaged.Contains(layer),
                        $"starter '{key}' has rows on layer '{layer}' that no activator engages");
                }
            }
        }

        /// <summary>Two rows for the same target on the same layer is
        /// ambiguous: which one wins is an implementation detail nobody
        /// authoring a profile should be relying on.</summary>
        [Fact]
        public void NoLayer_BindsTheSameTargetTwice()
        {
            foreach (var (key, set) in Sets())
            {
                var dupes = set.Rows
                    .GroupBy(r => ((r.LayerMask ?? "Base"), r.Target), StringTupleComparer.Instance)
                    .Where(g => g.Count() > 1)
                    .Select(g => $"{g.Key.Item1}/{g.Key.Item2}")
                    .ToList();
                Assert.True(dupes.Count == 0,
                    $"starter '{key}' binds these targets twice on one layer: {string.Join(", ", dupes)}");
            }
        }

        /// <summary>The quiet layer is the deliberate empty one: an activator
        /// that engages a layer with NO rows and InheritUnmapped false, so
        /// every target reads zero while it is held. If a row ever lands on
        /// it, the pad stops going silent and the escape hatch is gone.
        /// </summary>
        [Fact]
        public void QuietLayer_StaysEmptyAndReplacesBase()
        {
            foreach (var (key, set) in Sets())
            {
                var quiet = set.ShiftActivators.SingleOrDefault(a => a.LayerMask == "Quiet");
                if (quiet == null) continue;

                Assert.False(quiet.InheritUnmapped,
                    $"starter '{key}' quiet layer inherits Base, so it would not silence anything");
                Assert.DoesNotContain(set.Rows, r => (r.LayerMask ?? "Base") == "Quiet");
                Assert.True(quiet.DelayMs > 0,
                    $"starter '{key}' quiet layer has no long-press delay, so a tap would mute the pad");
            }
        }

        /// <summary>Exactly one slot is claimed, it is enabled, and it carries
        /// the mapping set. A starter that claims no slot produces nothing;
        /// one that claims several is not the cookie-cutter contract.</summary>
        [Fact]
        public void EveryProfile_ClaimsExactlyOneEnabledSlot()
        {
            foreach (var (info, p) in Built())
            {
                Assert.Equal(InputManager.MaxPads, p.SlotCreated.Length);
                Assert.Equal(InputManager.MaxPads, p.SlotEnabled.Length);
                Assert.Equal(InputManager.MaxPads, p.SlotControllerTypes.Length);
                Assert.Equal(InputManager.MaxPads, p.SlotMappingSets.Length);

                // Most starters claim exactly one slot. A SPLIT config (a pad
                // slot plus a keyboard slot) is legitimate and is the shape the
                // Workshop importer produces when a config needs both output
                // kinds; Emulation needs it because its hotkey verbs are
                // keyboard keys a gamepad slot cannot send. Whatever the count,
                // the claimed slots must be contiguous from 0, all enabled, and
                // all carrying a set.
                int claimed = p.SlotCreated.Count(c => c);
                Assert.InRange(claimed, 1, 2);
                for (int i = 0; i < claimed; i++)
                {
                    Assert.True(p.SlotCreated[i], $"starter '{info.Key}' slot {i} not claimed");
                    Assert.True(p.SlotEnabled[i], $"starter '{info.Key}' slot {i} is disabled");
                    Assert.NotNull(p.SlotMappingSets[i]);
                }
                for (int i = claimed; i < p.SlotCreated.Length; i++)
                    Assert.False(p.SlotCreated[i], $"starter '{info.Key}' claims a non-contiguous slot {i}");

                Assert.Equal((int)info.OutputType, p.SlotControllerTypes[0]);

                // The slot's HIDMaestro profile id must match what the engine
                // itself would pick for that VC type. It is deliberately NULL
                // for keyboard/mouse and MIDI, which do not route through
                // HIDMaestro at all, so asserting "non-empty" here would be
                // asserting the opposite of the contract.
                for (int i = 0; i < claimed; i++)
                    Assert.Equal(
                        InputManager.GetDefaultProfileId((VirtualControllerType)p.SlotControllerTypes[i]),
                        p.SlotProfileIds[i]);
            }
        }

        /// <summary>Null Macros is the legacy sentinel meaning "leave the live
        /// macro set alone". A starter profile owns its state outright, so it
        /// must carry a non-null array, EMPTY when it authors no macros.
        ///
        /// <para>Profiles legitimately DO carry macros now: the media
        /// transport, the Windows key and Shift+Tab are all outside the row
        /// engine's closed VK set, so they can only ride this lane. What must
        /// hold is that every shipped macro is well formed and device-free,
        /// since one that cannot bind is a silent dead binding.</para></summary>
        [Fact]
        public void EveryProfile_CarriesWellFormedMacros()
        {
            foreach (var (info, p) in Built())
            {
                Assert.NotNull(p.Macros);
                foreach (var m in p.Macros)
                {
                    Assert.True(m.IsEnabled, $"starter '{info.Key}' macro '{m.Name}' is disabled");
                    Assert.False(string.IsNullOrWhiteSpace(m.Name),
                        $"starter '{info.Key}' has an unnamed macro");
                    Assert.Equal(MacroTriggerSource.InputDevice, m.TriggerSource);
                    Assert.False(string.IsNullOrEmpty(m.TriggerInputs),
                        $"starter '{info.Key}' macro '{m.Name}' has no trigger, so it can never fire");
                    Assert.NotNull(m.Actions);
                    Assert.NotEmpty(m.Actions);
                    // Press/release pairs: every key pressed is released.
                    var pressed = m.Actions.Where(a => a.Type == MacroActionType.KeyPress)
                                           .Select(a => a.KeyCode).OrderBy(k => k).ToList();
                    var released = m.Actions.Where(a => a.Type == MacroActionType.KeyRelease)
                                            .Select(a => a.KeyCode).OrderBy(k => k).ToList();
                    Assert.Equal(pressed, released);
                    Assert.False(m.ConsumeTriggerButtons,
                        $"starter '{info.Key}' macro '{m.Name}' consumes its trigger, suppressing the row lane");
                }
            }
        }

        /// <summary>An archetype has no executable, so it must never
        /// auto-switch. Chosen by hand, always.</summary>
        [Fact]
        public void EveryProfile_HasNoExecutableRule()
        {
            foreach (var (info, p) in Built())
                Assert.True(string.IsNullOrEmpty(p.ExecutableNames),
                    $"starter '{info.Key}' would auto-switch on an executable");
        }

        /// <summary>Two saves must not share mutable state, or editing the
        /// second profile would silently edit the first.</summary>
        [Fact]
        public void Build_ReturnsAFreshInstanceEveryCall()
        {
            foreach (var info in StarterProfileCatalog.All)
            {
                var a = info.Build();
                var b = info.Build();
                Assert.NotSame(a, b);
                Assert.NotSame(a.SlotMappingSets, b.SlotMappingSets);
                Assert.NotSame(a.SlotMappingSets[0], b.SlotMappingSets[0]);
                Assert.NotEqual(a.Id, b.Id);
            }
        }

        /// <summary>SOCD config is only live when BOTH the mode and the pairs
        /// are set, and the pair grammar differs by slot type: gamepad slots
        /// pair mapping TARGET names, keyboard slots pair decimal virtual-key
        /// numbers. A malformed pair is dropped silently by the parser, so an
        /// authored-but-unparseable pair would leave SOCD quietly off.</summary>
        [Fact]
        public void SocdConfig_IsCompleteAndParseable()
        {
            foreach (var (info, p) in Built())
            {
                var set = p.SlotMappingSets[0];
                bool hasMode = !string.IsNullOrEmpty(set.SocdMode);
                bool hasPairs = !string.IsNullOrEmpty(set.SocdPairs);
                Assert.Equal(hasMode, hasPairs);
                if (!hasMode) continue;

                Assert.Contains(set.SocdMode, new[] { "Off", "LastWins", "Neutral", "FirstWins" });

                bool gamepad = info.OutputType != VirtualControllerType.KeyboardMouse;
                foreach (var token in set.SocdPairs.Split('|', StringSplitOptions.RemoveEmptyEntries))
                {
                    var halves = token.Split(':');
                    Assert.True(halves.Length == 2,
                        $"starter '{info.Key}' SOCD pair '{token}' is not 'a:b'");
                    Assert.NotEqual(halves[0], halves[1]);

                    if (gamepad)
                    {
                        // Target names, which must be real mapping targets.
                        Assert.All(halves, h => Assert.False(string.IsNullOrWhiteSpace(h)));
                    }
                    else
                    {
                        // Decimal virtual-key numbers in 1..255.
                        foreach (var h in halves)
                        {
                            Assert.True(int.TryParse(h, out int vk),
                                $"starter '{info.Key}' SOCD key '{h}' is not decimal");
                            Assert.InRange(vk, 1, 255);
                        }
                    }
                }
            }
        }

        /// <summary>Fighting Games ships tournament-legal or it does not ship.
        /// Capcom's rules require simultaneous opposing directions to produce
        /// no movement on BOTH axes, which only Neutral satisfies, and cap
        /// movement at one input system per direction, so the left stick must
        /// stay unbound while the D-pad drives.</summary>
        [Fact]
        public void FightingGames_IsTournamentLegal()
        {
            var info = StarterProfileCatalog.Find("fighting");
            Assert.NotNull(info);
            var set = info.Build().SlotMappingSets[0];

            Assert.Equal("Neutral", set.SocdMode);
            var pairs = set.SocdPairs.Split('|', StringSplitOptions.RemoveEmptyEntries)
                .Select(t => t.Trim()).ToList();
            Assert.Contains("DPadLeft:DPadRight", pairs);
            Assert.Contains("DPadUp:DPadDown", pairs);

            // Exactly one directional surface. The stick axes must not appear
            // as sources anywhere in this profile.
            foreach (var row in set.Rows)
                foreach (var src in row.Sources)
                    Assert.False(src.Descriptor.Contains("StickX", StringComparison.Ordinal)
                              || src.Descriptor.Contains("StickY", StringComparison.Ordinal),
                        $"Fighting Games binds '{src.Descriptor}', a second directional surface");

            // Evo prohibits hardware macros outright.
            Assert.Empty(info.Build().Macros);
        }

        /// <summary>Every mouse-driving starter offers the touchpad and the
        /// stick on the same cursor row, so whichever surface the controller
        /// has (or the user touches) drives it. Pad 1 must be listed before
        /// pad 0 so the right-hand surface wins on two-pad hardware.</summary>
        [Fact]
        public void CursorRows_OfferBothTouchpadAndStick()
        {
            foreach (var (info, p) in Built())
            {
                if (info.OutputType != VirtualControllerType.KeyboardMouse) continue;
                var set = p.SlotMappingSets[0];

                foreach (var target in new[] { "KbmMouseX", "KbmMouseY" })
                {
                    var row = set.Rows.SingleOrDefault(r =>
                        r.Target == target && (r.LayerMask ?? "Base") == "Base");
                    Assert.True(row != null, $"starter '{info.Key}' has no {target} row");

                    var pointers = row.Sources
                        .Where(s => SourceCoercion.IsTouchpadPointerDescriptor(s.Descriptor))
                        .ToList();
                    Assert.True(pointers.Count >= 2,
                        $"starter '{info.Key}' {target} does not offer both touchpads");
                    Assert.Contains(row.Sources, s => s.Descriptor.StartsWith("Gamepad ", StringComparison.Ordinal));

                    SourceCoercion.TryParseTouchpadPointer(pointers[0].Descriptor,
                        out int firstPad, out _, out _);
                    Assert.Equal(1, firstPad);
                }
            }
        }

        /// <summary>Stick directions are half-axis reads, with Invert acting
        /// as the half SELECTOR. A direction authored as a plain bipolar
        /// source would fire on both halves at once.</summary>
        [Fact]
        public void KeyRowsDrivenByAStickAxis_UseHalfAxisReads()
        {
            foreach (var (key, row, src) in Sources())
            {
                if (!row.Target.StartsWith("KbmKey", StringComparison.Ordinal)) continue;
                bool stickAxis = src.Descriptor.EndsWith("StickX", StringComparison.Ordinal)
                              || src.Descriptor.EndsWith("StickY", StringComparison.Ordinal);
                if (!stickAxis) continue;
                Assert.True(src.HalfAxis,
                    $"starter '{key}' drives key row '{row.Target}' from a whole stick axis");
            }
        }

        /// <summary>
        /// A held bank must not double-fire. The bank layer inherits (it has
        /// to, or holding it would kill the cursor and the movement keys), and
        /// inheriting means a target the bank does NOT remap still falls
        /// through to Base. The bank's own buttons usually DO drive a Base
        /// target, so without an explicit block one press emitted both: on
        /// Hotbar, holding LT and pressing A sent "5" AND Space.
        ///
        /// <para>The resolver's rule is that a zero-source layer row blocks the
        /// fallthrough only when it is an explicit NoInherit declaration, so
        /// this asserts one exists for every Base target the bank's buttons and
        /// its own activator drive.</para>
        /// </summary>
        [Fact]
        public void BankLayers_BlockTheBaseBindingsOfEveryButtonTheyConsume()
        {
            int banksChecked = 0;

            foreach (var (key, set) in Sets())
            {
                foreach (var act in set.ShiftActivators)
                {
                    if (act.LayerMask == "Quiet" || act.LayerMask == "Hotkey") continue;
                    banksChecked++;

                    var layerRows = set.Rows.Where(r => r.LayerMask == act.LayerMask).ToList();
                    var consumed = new HashSet<string>(StringComparer.Ordinal) { act.Descriptor };
                    foreach (var s in layerRows.SelectMany(r => r.Sources))
                        consumed.Add(s.Descriptor);

                    foreach (var baseRow in set.Rows.Where(r =>
                                 (r.LayerMask ?? "Base") == "Base"
                                 && r.Sources.Any(s => consumed.Contains(s.Descriptor))))
                    {
                        var block = layerRows.FirstOrDefault(r => r.Target == baseRow.Target);
                        bool blocked = block != null
                            && (block.NoInherit || block.Sources.Count > 0);
                        Assert.True(blocked,
                            $"starter '{key}' layer '{act.LayerMask}' leaves Base target " +
                            $"'{baseRow.Target}' inheriting, so its buttons double-fire");
                    }
                }
            }

            Assert.True(banksChecked >= 4, $"only {banksChecked} banks checked; the sweep is not covering them");
        }

        /// <summary>Emulation's whole differentiator is the hotkey layer. It
        /// shipped with an activator and ZERO rows, so holding Back did
        /// nothing at all while the description promised save states.
        ///
        /// <para>The verbs are keyboard keys, which a gamepad slot cannot
        /// send, so the profile is a split config: the pad half blocks the
        /// gamepad outputs and the keyboard half emits the keys. Both halves
        /// need their own copy of the activator, because a shift layer is per
        /// mapping set.</para></summary>
        [Fact]
        public void Emulation_HotkeyLayer_HasVerbsAndEngagesBothHalves()
        {
            var info = StarterProfileCatalog.Find("emulation");
            Assert.NotNull(info);
            var p = info.Build();

            var sets = p.SlotMappingSets.Where(s => s != null).ToList();
            Assert.Equal(2, sets.Count);

            foreach (var set in sets)
                Assert.Contains(set.ShiftActivators, a => a.LayerMask == "Hotkey" && a.Mode == "Hold");

            var kbm = p.SlotMappingSets[1];
            var verbs = kbm.Rows
                .Where(r => r.LayerMask == "Hotkey" && r.Sources.Count > 0)
                .ToList();
            Assert.True(verbs.Count >= 6,
                $"the hotkey layer carries only {verbs.Count} verbs, so holding Back does almost nothing");
            Assert.All(verbs, r => Assert.StartsWith("KbmKey", r.Target, StringComparison.Ordinal));

            // The keyboard half must be SILENT until Back is held, or it would
            // type into the emulator during play.
            Assert.DoesNotContain(kbm.Rows, r => (r.LayerMask ?? "Base") == "Base");

            // The pad half must stop the gamepad outputs those buttons drive.
            var pad = p.SlotMappingSets[0];
            Assert.Contains(pad.Rows, r => r.LayerMask == "Hotkey" && r.NoInherit);
        }

        /// <summary>Hotbar's headline is thirty-two abilities behind two
        /// triggers, which needs FOUR banks: a plain hold on each trigger and
        /// a double-tap-and-hold on each. It shipped with two, so the shipped
        /// description overstated it by half.</summary>
        [Fact]
        public void Hotbar_HasBothTiers_AndThirtyTwoSlots()
        {
            var set = StarterProfileCatalog.Find("hotbar").Build().SlotMappingSets[0];

            var banks = set.ShiftActivators.Where(a => a.LayerMask != "Quiet").ToList();
            Assert.Equal(4, banks.Count);
            Assert.Equal(2, banks.Count(a => a.DoublePressMs > 0));
            Assert.Equal(2, banks.Count(a => a.DoublePressMs == 0));

            // Both tiers on both triggers.
            foreach (var trigger in new[] { "Gamepad LeftTrigger", "Gamepad RightTrigger" })
            {
                Assert.Contains(banks, a => a.Descriptor == trigger && a.DoublePressMs == 0);
                Assert.Contains(banks, a => a.Descriptor == trigger && a.DoublePressMs > 0);
            }

            int slots = banks.Sum(a => set.Rows.Count(r =>
                r.LayerMask == a.LayerMask && r.Sources.Count > 0));
            Assert.Equal(32, slots);
        }

        /// <summary>Every keyboard-and-mouse starter carries the quiet layer.
        /// Hotbar was the one exception, against a docs claim that says
        /// "every". A missing escape hatch means the pad keeps typing into
        /// whatever the user alt-tabs to.</summary>
        [Fact]
        public void EveryKeyboardMouseProfile_HasAQuietLayer()
        {
            int checkedProfiles = 0;
            foreach (var (info, p) in Built())
            {
                if (info.OutputType != VirtualControllerType.KeyboardMouse) continue;
                checkedProfiles++;
                var set = p.SlotMappingSets[0];
                var quiet = set.ShiftActivators.SingleOrDefault(a => a.LayerMask == "Quiet");
                Assert.True(quiet != null, $"starter '{info.Key}' has no quiet layer");
            }
            Assert.True(checkedProfiles >= 8, $"only {checkedProfiles} KBM profiles checked");
        }

        /// <summary>Returns every virtual key a profile's macros press.</summary>
        private static HashSet<int> MacroKeys(ProfileData p)
            => p.Macros.SelectMany(m => m.Actions)
                       .Where(a => a.Type == MacroActionType.KeyPress)
                       .Select(a => a.KeyCode)
                       .ToHashSet();

        /// <summary>
        /// Media Remote must carry the SYSTEM transport, which is the whole
        /// difference between a media remote and a desktop profile that
        /// happens to press Space.
        ///
        /// <para>It first shipped with none of these: the profile was Desktop
        /// with different letters, measured at 41% identical bindings by
        /// physical input, and the omission was documented in a comment rather
        /// than fixed. These keys are outside the row engine's closed VK set,
        /// so the macro lane is the only way to reach them.</para></summary>
        [Fact]
        public void MediaRemote_CarriesTheSystemTransport()
        {
            var p = StarterProfileCatalog.Find("media").Build();
            var keys = MacroKeys(p);

            var required = new (int Vk, string What)[]
            {
                (0xB3, "play/pause"), (0xB2, "stop"),
                (0xB0, "next track"), (0xB1, "previous track"),
                (0xAF, "volume up"), (0xAE, "volume down"), (0xAD, "mute"),
            };
            foreach (var (vk, what) in required)
                Assert.True(keys.Contains(vk),
                    $"Media Remote has no {what} (VK 0x{vk:X2}), so it is not a media remote");

            // And they must be reachable from real buttons, not orphaned.
            Assert.All(p.Macros, m => Assert.False(string.IsNullOrEmpty(m.TriggerInputs)));
        }

        /// <summary>Desktop must reach the two system keys no row target can:
        /// Valve's Show Keyboard on X (Win+Ctrl+O) and the Windows key. Both
        /// were dropped as "not reachable" when the profile first shipped.
        /// </summary>
        [Fact]
        public void Desktop_ReachesShowKeyboardAndTheWindowsKey()
        {
            var p = StarterProfileCatalog.Find("desktop").Build();
            Assert.NotEmpty(p.Macros);

            var chord = p.Macros.FirstOrDefault(m =>
                m.Actions.Count(a => a.Type == MacroActionType.KeyPress) == 3);
            Assert.True(chord != null, "Desktop has no Show Keyboard chord");
            var pressed = chord.Actions.Where(a => a.Type == MacroActionType.KeyPress)
                                       .Select(a => a.KeyCode).ToList();
            Assert.Contains(0x5B, pressed);   // Win
            Assert.Contains(0xA2, pressed);   // Ctrl
            Assert.Contains(0x4F, pressed);   // O

            Assert.Contains(p.Macros, m =>
                m.Actions.Count(a => a.Type == MacroActionType.KeyPress) == 1
                && m.Actions.Any(a => a.KeyCode == 0x5B));
        }

        /// <summary>Point and Click cycles hotspots BACKWARDS with Shift+Tab,
        /// a chord no single row target can express.</summary>
        [Fact]
        public void PointAndClick_CarriesTheBackwardsCycleChord()
        {
            var p = StarterProfileCatalog.Find("pointclick").Build();
            Assert.Contains(p.Macros, m =>
            {
                var k = m.Actions.Where(a => a.Type == MacroActionType.KeyPress)
                                 .Select(a => a.KeyCode).ToList();
                return k.Count == 2 && k.Contains(0xA0) && k.Contains(0x09);
            });
        }

        /// <summary>
        /// Strategy and Isometric RPG were specified with RADIAL MENUS, and
        /// shipped with shift-layer banks standing in for them. Larian ships
        /// exactly the radial shape: "radial menus (accessed with the
        /// triggers) give you all your skills, items, and actions without
        /// needing a hotbar".
        ///
        /// <para>A radial gated behind a held opener needs three things
        /// together: the menu, its layer, and an activator engaging that
        /// layer. Any one missing and it never opens.</para></summary>
        [Theory]
        [InlineData("strategy", 1, 10)]
        [InlineData("isometric", 2, 6)]
        public void RadialProfiles_CarryTheirMenus(string key, int menuCount, int firstCellCount)
        {
            var set = StarterProfileCatalog.Find(key).Build().SlotMappingSets[0];

            Assert.Equal(menuCount, set.Menus.Count);
            foreach (var menu in set.Menus)
            {
                Assert.Equal(MenuKind.Radial, menu.Kind);
                Assert.NotEmpty(menu.Items);
                Assert.Equal(menu.CellCount, menu.Items.Count);
                Assert.All(menu.Items, i => Assert.True(i.VirtualKey > 0,
                    $"'{key}' radial cell '{i.Label}' emits no key"));
                Assert.All(menu.Items, i => Assert.False(string.IsNullOrWhiteSpace(i.Label)));

                // The menu is gated behind a held opener, so its layer needs
                // an activator or it can never open.
                Assert.False(string.IsNullOrEmpty(menu.LayerMask),
                    $"'{key}' radial '{menu.Name}' is not gated behind a layer");
                Assert.Contains(set.ShiftActivators,
                    a => a.LayerMask == menu.LayerMask && a.Mode == "Hold");
                // Firing on release is what makes hold-flick-release work.
                Assert.Equal(MenuFireType.TouchRelease, menu.FireType);
            }
            Assert.Equal(firstCellCount, set.Menus[0].CellCount);
        }

        /// <summary>
        /// Macro triggers must carry the ABSTRACT descriptor, not a folded raw
        /// button index.
        ///
        /// <para>The owner spotted this in the UI: the macro editor listed
        /// "Button 0, Button 1, Button 2" while every mapping-row dropdown
        /// showed "Gamepad A". The cause was routing through
        /// TryBuildTriggerEntry, which deliberately folds an abstract alias to
        /// its canonical "Button N" so picker entries convert like raw ones.
        /// It still fired on a gamepad, since index 0 is A in the normalized
        /// array, but it discarded the abstraction: a force-raw or non-gamepad
        /// device would read ITS index 0 instead.</para>
        ///
        /// <para>The spec grammar is the assertion: "sd:" is a descriptor
        /// entry and "btn:" is a raw index. A starter macro must never emit
        /// the latter.</para></summary>
        [Fact]
        public void MacroTriggers_CarryTheAbstractDescriptor_NotARawButtonIndex()
        {
            int checkedMacros = 0;
            foreach (var (info, p) in Built())
            {
                foreach (var m in p.Macros)
                {
                    checkedMacros++;
                    foreach (var spec in m.TriggerInputs.Split('|', StringSplitOptions.RemoveEmptyEntries))
                    {
                        Assert.DoesNotContain(":btn:", spec, StringComparison.Ordinal);
                        Assert.Contains(":sd:", spec, StringComparison.Ordinal);
                        // And the descriptor it carries is an abstract alias,
                        // resolvable exactly like a mapping row's source.
                        string descriptor = spec.Substring(spec.IndexOf(":sd:", StringComparison.Ordinal) + 4);
                        Assert.StartsWith("Gamepad ", descriptor, StringComparison.Ordinal);
                    }
                }
            }
            Assert.True(checkedMacros >= 12,
                $"only {checkedMacros} macros checked; the sweep is not covering the catalog");
        }

        /// <summary>Names and descriptions must be real localized strings, not
        /// missing-resource placeholders, in every shipped language.</summary>
        [Theory]
        [InlineData("en")]
        [InlineData("de")]
        [InlineData("es")]
        [InlineData("fr")]
        [InlineData("it")]
        [InlineData("ja")]
        [InlineData("ko")]
        [InlineData("nl")]
        [InlineData("pt-BR")]
        [InlineData("zh-Hans")]
        public void NamesAndDescriptions_AreLocalized(string culture)
        {
            var previous = System.Globalization.CultureInfo.CurrentUICulture;
            try
            {
                System.Globalization.CultureInfo.CurrentUICulture =
                    System.Globalization.CultureInfo.GetCultureInfo(culture);

                foreach (var info in StarterProfileCatalog.All)
                {
                    Assert.False(string.IsNullOrWhiteSpace(info.Name),
                        $"starter '{info.Key}' has no name in {culture}");
                    Assert.False(string.IsNullOrWhiteSpace(info.Description),
                        $"starter '{info.Key}' has no description in {culture}");
                    Assert.False(string.IsNullOrWhiteSpace(info.OutputLabel),
                        $"starter '{info.Key}' has no output label in {culture}");
                    Assert.DoesNotContain("Starter_", info.Name, StringComparison.Ordinal);
                    Assert.DoesNotContain("Starter_", info.Description, StringComparison.Ordinal);
                }
            }
            finally
            {
                System.Globalization.CultureInfo.CurrentUICulture = previous;
            }
        }

        // ── Artifact conformance ────────────────────────────────────────
        // The specification these profiles were commissioned against names
        // each binding individually. One test per promise, because "the
        // profile exists and loads" was already true of every binding these
        // caught as wrong.

        private static MappingSet SetOf(string key)
            => StarterProfileCatalog.Find(key).Build().SlotMappingSets.First(s => s != null);

        private static MappingRow RowOf(string key, string target)
            => SetOf(key).Rows.Single(r => r.Target == target
                && (r.LayerMask ?? "Base") == "Base");

        private static string[] DescriptorsOf(string key, string target)
            => RowOf(key, target).Sources.Select(s => s.Descriptor).ToArray();

        /// <summary>WASD follows Valve's own wasd.vdf: LEFT click sits on the
        /// right-stick CLICK beside the trigger, never middle click, and the
        /// weapon swap is the scroll wheel rather than letter keys, which is
        /// what makes it work in games that number their slots
        /// differently.</summary>
        [Fact]
        public void Wasd_PutsLeftClickOnTheStickClick_AndSwapsWeaponsOnTheWheel()
        {
            // "Gamepad RightStick" is the CLICK: GamepadAliasTable maps it to
            // Button 9. The axes are spelled RightStickX / RightStickY.
            Assert.Contains("Gamepad RightStick", DescriptorsOf("wasd", "KbmMBtn0"));  // LMB
            // True whether middle click is bound elsewhere or not bound at all.
            Assert.DoesNotContain(SetOf("wasd").Rows, r => r.Target == "KbmMBtn2"      // MMB
                && r.Sources.Any(s => s.Descriptor == "Gamepad RightStick"));
            Assert.Contains("Gamepad LeftShoulder", DescriptorsOf("wasd", "KbmScrollNeg"));
            Assert.Contains("Gamepad RightShoulder", DescriptorsOf("wasd", "KbmScroll"));
        }

        /// <summary>The cursor-driven genres host the cursor on the LEFT
        /// stick and keep the right stick for the camera. Getting this
        /// backwards makes both unusable at once, since the hand steering the
        /// pointer is also the hand panning the view.</summary>
        [Theory]
        [InlineData("pointclick")]
        [InlineData("isometric")]
        public void CursorGenres_DriveTheCursorFromTheLeftStick(string key)
        {
            Assert.Contains("Gamepad LeftStickX", DescriptorsOf(key, "KbmMouseX"));
            Assert.Contains("Gamepad LeftStickY", DescriptorsOf(key, "KbmMouseY"));
            Assert.DoesNotContain("Gamepad RightStickX", DescriptorsOf(key, "KbmMouseX"));
        }

        /// <summary>Isometric's camera pan rides the stick the cursor does
        /// NOT use, so the two never fight for the same thumb.</summary>
        [Fact]
        public void Isometric_PansTheCameraWithTheRightStick()
        {
            foreach (var (target, axis) in new[]
                     { ("KbmKey26", "Gamepad RightStickY"), ("KbmKey28", "Gamepad RightStickY"),
                       ("KbmKey25", "Gamepad RightStickX"), ("KbmKey27", "Gamepad RightStickX") })
                Assert.Contains(axis, DescriptorsOf("isometric", target));
        }

        /// <summary>Point and Click: Y skips dialogue (Space), and hotspots
        /// cycle FORWARD on LB with the backward chord on RB. The chord
        /// cannot be a row, since no single row target is a chord.</summary>
        [Fact]
        public void PointClick_SkipsOnY_AndCyclesHotspotsInTheRightOrder()
        {
            Assert.Contains("Gamepad ButtonY", DescriptorsOf("pointclick", "KbmKey20"));       // Space
            Assert.Contains("Gamepad LeftShoulder", DescriptorsOf("pointclick", "KbmKey09"));  // Tab
            Assert.DoesNotContain("Gamepad RightShoulder", DescriptorsOf("pointclick", "KbmKey09"));

            var p = StarterProfileCatalog.Find("pointclick").Build();
            var back = p.Macros.Single(m => m.Actions.Any(a => a.KeyCode == 0xA0));   // Shift+Tab
            Assert.Contains("RightShoulder", back.TriggerInputs);
            Assert.Contains(back.Actions, a => a.Type == MacroActionType.KeyPress && a.KeyCode == 0x09);
        }

        /// <summary>Strategy reaches Pause/Break. It is outside the KbM row
        /// engine's closed VK set, so it MUST ride the macro lane: a row
        /// would be silently dead.</summary>
        [Fact]
        public void Strategy_ReachesPauseBreak_ThroughTheMacroLane()
        {
            var p = StarterProfileCatalog.Find("strategy").Build();
            Assert.Contains(0x13, MacroKeys(p));
            Assert.DoesNotContain(SetOf("strategy").Rows, r => r.Target == "KbmKey13");
        }

        /// <summary>Fighting Games reads both triggers DIGITALLY: a partial
        /// pull is a full press. A partial pull that registers as a partial
        /// input is a dropped move.</summary>
        [Fact]
        public void Fighting_ReadsBothTriggersAsButtons()
        {
            foreach (var target in new[] { "LeftTrigger", "RightTrigger" })
            {
                var src = RowOf("fighting", target).Sources.Single();
                Assert.InRange(src.ParamRangeOuter, 0.01, 0.5);
                // Live on the trigger read itself: ReadAsUnipolar's tail calls
                // ApplyCurveRangeShaping, whose outer leg needs shape 0.
                Assert.Equal(0, src.ParamStickDeadZoneShape);
            }
        }

        /// <summary>Racing's trigger deadzones are ASYMMETRIC on purpose:
        /// throttle at 0 inside, brake guarded so a resting finger does not
        /// drag it. The shape must be nonzero or the assignment-time fold
        /// never moves the radius onto the device's Dead Zone card.</summary>
        [Fact]
        public void Racing_GuardsTheBrakeButNotTheThrottle()
        {
            var brake = RowOf("racing", "LeftTrigger").Sources.Single();
            Assert.InRange(brake.ParamStickDeadZoneInner, 0.005, 0.1);
            Assert.NotEqual(0, brake.ParamStickDeadZoneShape);

            var throttle = RowOf("racing", "RightTrigger").Sources.Single();
            Assert.Equal(0.0, throttle.ParamStickDeadZoneInner);
        }

        /// <summary>Gyro Aim ramps sensitivity 1 to 2 with rate. The ramp is
        /// ParamAccel; GyroSensitivity is a FLAT multiplier folded into the
        /// rate, so setting it instead would double slow movement too and lose
        /// the entire point of a ramp.</summary>
        [Fact]
        public void GyroAim_RampsWithRate_RatherThanMultiplyingFlat()
        {
            foreach (var target in new[] { "RightThumbAxisX", "RightThumbAxisY" })
            {
                var gyro = RowOf("gyroaim", target).Sources
                    .Single(s => s.Descriptor.StartsWith("Gyro ", StringComparison.Ordinal));
                Assert.Equal(1.0, gyro.ParamAccel);          // gain 1 at rest, 2 at full scale
                Assert.Equal(1.0, gyro.GyroSensitivity);     // the flat lane stays at unity
            }
        }

        /// <summary><para>THE OVERLAP CONTRACT. A shift activator with
        /// PostponeMapping false (the default) CONSUMES its own input: the
        /// suppression set is built from the raw, pre-delay read
        /// (WasDown = inputDown, before delayMet is applied), and the row
        /// evaluators skip any source in it. So a single-button activator
        /// kills that button's ordinary binding on EVERY press, not only on
        /// the long one that engages the layer.</para>
        /// <para>The quiet layer used to sit on Start alone, which meant Start
        /// was a dead button in all thirteen profiles: Escape never fired on
        /// WASD or Twin-Stick, and ButtonStart never reached the game on any of
        /// the five gamepad profiles. The fix is a CHORD, so suppression only
        /// applies while both buttons are held and neither is disturbed
        /// alone.</para></summary>
        [Fact]
        public void TheSilenceGesture_DoesNotEatAnyButtonsOrdinaryBinding()
        {
            foreach (var (key, set) in Sets())
            {
                var quiet = set.ShiftActivators.FirstOrDefault(a => a.LayerMask == "Quiet");
                if (quiet == null) continue;

                Assert.Equal("Chord", quiet.Kind);
                Assert.False(string.IsNullOrEmpty(quiet.ChordSecondDescriptor),
                    $"starter '{key}' declares a chord with no second button");

                // BOTH legs, against BOTH lanes. The first draft of this test
                // checked only the primary leg against other ACTIVATORS, and
                // that blind spot is exactly what let Media Remote ship a
                // plain-tap Stop macro on the chord's Guide leg.
                foreach (var leg in new[] { quiet.Descriptor, quiet.ChordSecondDescriptor })
                {
                    foreach (var other in set.ShiftActivators.Where(a => a != quiet))
                    {
                        Assert.False(other.Kind != "Chord" && other.Descriptor == leg,
                            $"starter '{key}' also uses '{leg}' as a lone activator");
                    }
                }
            }
        }

        /// <summary>The macro lane is NOT covered by an activator's postpone
        /// suppression: Step4b carries no IsSourceSuppressedPostpone call at
        /// all, while the row evaluator has ten. So a macro sitting on either
        /// leg of the silence chord fires when the user reaches for silence,
        /// and a macro is a discrete ACTION (stop playback, recenter) rather
        /// than a transient key. Any such macro must be a ShortPress, which
        /// fires on the falling edge only under TriggerHoldMs.</summary>
        [Fact]
        public void NoMacroOnTheSilenceLegs_FiresOnAPlainPress()
        {
            int checkedProfiles = 0;
            foreach (var (info, profile) in Built())
            {
                var quiet = profile.SlotMappingSets.Where(s => s != null)
                    .SelectMany(s => s.ShiftActivators)
                    .FirstOrDefault(a => a.LayerMask == "Quiet");
                if (quiet == null) continue;
                checkedProfiles++;

                var legs = new HashSet<string>(StringComparer.Ordinal)
                    { quiet.Descriptor, quiet.ChordSecondDescriptor };

                foreach (var m in profile.Macros)
                {
                    foreach (var spec in m.TriggerInputs.Split('|', StringSplitOptions.RemoveEmptyEntries))
                    {
                        int i = spec.IndexOf(":sd:", StringComparison.Ordinal);
                        if (i < 0) continue;
                        string descriptor = spec.Substring(i + 4);
                        if (!legs.Contains(descriptor)) continue;

                        Assert.True(m.TriggerMode == MacroTriggerMode.ShortPress,
                            $"starter '{info.Key}' macro '{m.Name}' sits on the silence leg " +
                            $"'{descriptor}' with mode {m.TriggerMode}, so it fires every time " +
                            "the user reaches for silence");
                        // And the short-press window must stay UNDER the hold
                        // that engages the layer, or the gesture fires it anyway.
                        Assert.True(m.TriggerHoldMs < quiet.DelayMs,
                            $"starter '{info.Key}' macro '{m.Name}' has TriggerHoldMs " +
                            $"{m.TriggerHoldMs} >= the quiet delay {quiet.DelayMs}");
                    }
                }
            }
            Assert.True(checkedProfiles >= 13, $"only {checkedProfiles} profiles checked");
        }

        /// <summary>The mechanism above, driven through the REAL resolver
        /// rather than asserted: hold one leg of the chord and the other
        /// button's binding must survive. A same-window positive control
        /// proves the harness can see suppression at all, so a green result
        /// is not vacuous.</summary>
        [Fact]
        public void HoldingOneChordLeg_LeavesTheOtherButtonsBindingAlive()
        {
            var ProbeGuid = new Guid("13ea3b23-bb17-802d-f268-c194414535f8");
            var set = SetOf("wasd");
            var quiet = set.ShiftActivators.Single(a => a.LayerMask == "Quiet");

            var state = new PadForge.Engine.CustomInputState();
            // Start alone: the gesture's second leg, held by itself.
            state.Buttons[7] = true;   // Gamepad ButtonStart -> Button 7
            InputManager.ResolveActiveLayerMask(0, set, state, ProbeGuid.ToString());
            Assert.False(
                InputManager.IsSourceSuppressedPostpone(0, "", "Gamepad ButtonStart"),
                "Start alone is swallowed, so its ordinary binding is dead");

            // Positive control, same window: both legs held IS the gesture,
            // and then the engine does consume them.
            state.Buttons[10] = true;  // Gamepad ButtonGuide -> Button 10
            InputManager.ResolveActiveLayerMask(0, set, state, ProbeGuid.ToString());
            Assert.True(
                InputManager.IsSourceSuppressedPostpone(0, "", quiet.Descriptor),
                "the harness cannot observe suppression at all, so the check above proves nothing");
        }

        /// <summary>Layer names are rendered verbatim by the shift-layer flyout
        /// (InputService resolves the engaged activator's LayerName into
        /// ShiftLayerFlyout's LayerNameText), so they are user-facing strings
        /// and must be localized. Four ability banks shipped announcing
        /// themselves as "Bank1" through "Bank4": an internal identifier, in
        /// English, on screen.</summary>
        [Fact]
        public void EveryShiftLayerName_IsLocalized_NotAnInternalIdentifier()
        {
            var previous = System.Globalization.CultureInfo.CurrentUICulture;
            try
            {
                var english = new List<string>();
                var owner = new List<string>();
                foreach (var (key, set) in Sets())
                {
                    foreach (var act in set.ShiftActivators)
                    {
                        owner.Add($"{key}/{act.LayerMask}");
                        Assert.False(string.IsNullOrWhiteSpace(act.LayerName),
                            $"starter '{key}' has a nameless layer '{act.LayerMask}'");
                        // An internal id on screen. Not "equals the mask":
                        // Strategy's localized English name legitimately IS
                        // "Hotbar", same as its mask.
                        Assert.False(
                            System.Text.RegularExpressions.Regex.IsMatch(act.LayerName, @"^Bank\d+$"),
                            $"starter '{key}' shows the internal layer id '{act.LayerName}' " +
                            "to the user instead of a localized name");
                        english.Add(act.LayerName);
                    }
                }
                Assert.NotEmpty(english);

                // Resource-backed, not a hardcoded literal that merely differs
                // from the mask: the same names must move under another locale.
                System.Globalization.CultureInfo.CurrentUICulture =
                    System.Globalization.CultureInfo.GetCultureInfo("ja");
                var japanese = Sets()
                    .SelectMany(t => t.Set.ShiftActivators.Select(a => a.LayerName))
                    .ToList();
                Assert.Equal(english.Count, japanese.Count);
                // EVERY name, not a threshold. A count-based bar let a single
                // reverted name survive mutation testing, which is the
                // "tests that cannot fail" trap in miniature.
                for (int i = 0; i < english.Count; i++)
                {
                    Assert.False(english[i] == japanese[i],
                        $"layer name '{english[i]}' ({owner[i]}) is identical under a " +
                        "Japanese locale, so it is a hardcoded English literal rather " +
                        "than resource-backed");
                }
            }
            finally
            {
                System.Globalization.CultureInfo.CurrentUICulture = previous;
            }
        }

        /// <summary><para>The gesture actually SILENCES. Suppression and
        /// engagement are different code paths, and Chord + DelayMs + Toggle
        /// is a combination nothing else in the catalog uses, so proving the
        /// chord consumes correctly proves nothing about whether it fires.
        /// </para>
        /// <para>Drives the real resolver over a real 600 ms hold. Negative
        /// control first: one leg held for the same duration must NOT engage,
        /// or "it engaged" would just mean "any press engages".</para>
        /// </summary>
        [Fact]
        public void TheSilenceGesture_EngagesTheQuietLayer_AndOneLegAlone_DoesNot()
        {
            var probe = new Guid("13ea3b23-bb17-802d-f268-c194414535f8").ToString();
            var set = SetOf("wasd");
            var quiet = set.ShiftActivators.Single(a => a.LayerMask == "Quiet");
            int holdMs = quiet.DelayMs + 250;

            // Negative control: Start alone, held past the delay.
            var lone = new PadForge.Engine.CustomInputState();
            lone.Buttons[7] = true;                        // Start
            InputManager.ResolveActiveLayerMask(0, set, lone, probe);
            System.Threading.Thread.Sleep(holdMs);
            Assert.Equal("Base", InputManager.ResolveActiveLayerMask(0, set, lone, probe));

            // Release, so the toggle sees a clean rising edge for the chord.
            var idle = new PadForge.Engine.CustomInputState();
            InputManager.ResolveActiveLayerMask(0, set, idle, probe);

            var chord = new PadForge.Engine.CustomInputState();
            chord.Buttons[7] = true;                       // Start
            chord.Buttons[10] = true;                      // Guide
            try
            {
                // The gesture: both legs, held past the delay.
                InputManager.ResolveActiveLayerMask(0, set, chord, probe);
                System.Threading.Thread.Sleep(holdMs);
                Assert.Equal("Quiet", InputManager.ResolveActiveLayerMask(0, set, chord, probe));

                // It is a TOGGLE: the layer outlives the release.
                InputManager.ResolveActiveLayerMask(0, set, idle, probe);
                Assert.Equal("Quiet", InputManager.ResolveActiveLayerMask(0, set, idle, probe));
            }
            finally
            {
                // Toggle OFF again, or slot 0 stays latched on Quiet for every
                // test that runs after this one. The activator runtime and the
                // published per-slot mask are process-global, so a test that
                // engages a layer owns putting it back.
                InputManager.ResolveActiveLayerMask(0, set, chord, probe);
                System.Threading.Thread.Sleep(holdMs);
                InputManager.ResolveActiveLayerMask(0, set, chord, probe);
                InputManager.ResolveActiveLayerMask(0, set, idle, probe);
                // And drop the slot's published sets entirely.
                InputManager.ResolveActiveLayerMask(0, new MappingSet(), idle, probe);
            }
        }

        /// <summary>THE ROSTER. The proposal specifies thirteen archetypes by
        /// name, and this shipped twelve for a full release cycle because
        /// nothing compared the list to the list. A missing profile is not a
        /// binding defect any other test in this file can see.</summary>
        [Fact]
        public void Roster_CarriesEveryArchetypeTheProposalNames()
        {
            string[] expected =
            {
                "desktop", "wasd", "pointclick", "strategy", "isometric",
                "twinstick", "media", "hotbar",
                "fighting", "emulation", "racing", "spacesim", "gyroaim",
            };
            var shipped = StarterProfileCatalog.All.Select(p => p.Key).ToList();
            foreach (var key in expected)
                Assert.Contains(key, shipped);
            Assert.Equal(13, shipped.Count);
        }

        /// <summary>Every profile can be silenced without unassigning the pad,
        /// which the proposal asks for on ALL of them and not only on the
        /// keyboard ones. Steam ships empty.vdf for the same reason.</summary>
        [Fact]
        public void EveryProfile_CanBeSilenced()
        {
            foreach (var (info, profile) in Built())
            {
                var sets = profile.SlotMappingSets.Where(s => s != null).ToList();
                Assert.True(
                    sets.Any(s => s.ShiftActivators.Any(a => a.LayerMask == "Quiet")),
                    $"starter '{info.Key}' has no quiet layer, so the pad cannot be silenced");
            }
        }

        /// <summary>Space Sim's axes are Frontier's, read from the shipped
        /// Elite Dangerous presets: roll on left X, pitch on left Y, vertical
        /// thrust on right Y, yaw on right X. Those three are unanimous across
        /// every stock gamepad preset, and right-stick X is the only one the
        /// set disagrees on.</summary>
        [Fact]
        public void SpaceSim_UsesFrontiersAxisAssignment()
        {
            Assert.Contains("Gamepad LeftStickX", DescriptorsOf("spacesim", "LeftThumbAxisX"));
            Assert.Contains("Gamepad LeftStickY", DescriptorsOf("spacesim", "LeftThumbAxisY"));
            Assert.Contains("Gamepad RightStickX", DescriptorsOf("spacesim", "RightThumbAxisX"));
            Assert.Contains("Gamepad RightStickY", DescriptorsOf("spacesim", "RightThumbAxisY"));

            // Pitch ships Inverted="0", so the raw read is already
            // pull-back-to-climb and an inversion here would break it.
            Assert.All(RowOf("spacesim", "LeftThumbAxisY").Sources, s => Assert.False(s.Invert));

            // Throttle on the bumpers, guns on the triggers. Frontier never
            // puts an absolute throttle on a self-centering stick.
            Assert.Contains("Gamepad LeftShoulder", DescriptorsOf("spacesim", "LeftShoulder"));
            Assert.Contains("Gamepad RightShoulder", DescriptorsOf("spacesim", "RightShoulder"));
            Assert.Contains("Gamepad RightTrigger", DescriptorsOf("spacesim", "RightTrigger"));
        }

        /// <summary>Space Sim's flight axes are shaped, which is the whole
        /// reason the profile beats a bare pad: docking happens in the first
        /// tenth of stick travel. The Precision layer softens them further and
        /// must not leave the stick click double-firing.</summary>
        [Fact]
        public void SpaceSim_ShapesTheFlightAxes_AndHasAPrecisionLayer()
        {
            foreach (var target in new[] { "LeftThumbAxisX", "LeftThumbAxisY",
                                           "RightThumbAxisX", "RightThumbAxisY" })
            {
                var src = RowOf("spacesim", target).Sources.Single();
                Assert.True(src.ParamCurveExponent > 1.0,
                    $"spacesim '{target}' is unshaped, so it flies like a bare pad");
            }

            var set = SetOf("spacesim");
            var act = set.ShiftActivators.Single(a => a.LayerMask == "Precision");
            Assert.Equal("Gamepad RightStick", act.Descriptor);
            Assert.Equal("Toggle", act.Mode);

            var fine = set.Rows.Where(r => r.LayerMask == "Precision" && r.Sources.Count > 0).ToList();
            Assert.Equal(4, fine.Count);
            Assert.All(fine, r => Assert.True(
                r.Sources.Single().ParamCurveExponent
                    > RowOf("spacesim", r.Target).Sources.Single().ParamCurveExponent,
                $"Precision '{r.Target}' is not softer than Base, so the layer does nothing"));

            Assert.Contains(set.Rows, r => r.LayerMask == "Precision"
                && r.Target == "RightThumbButton" && r.NoInherit);
        }

        /// <summary>Gyro Aim carries a calibrate button. Drifted gyro is
        /// unusable, and a recenter reachable only by opening the app is not a
        /// recenter. It must be a real GyroRecenter action: a shift layer with
        /// no rows recenters nothing.</summary>
        [Fact]
        public void GyroAim_CarriesARealCalibrateBinding()
        {
            var p = StarterProfileCatalog.Find("gyroaim").Build();
            var cal = p.Macros.Single(m =>
                m.Actions.Any(a => a.Type == MacroActionType.GyroRecenter));
            Assert.False(string.IsNullOrEmpty(cal.TriggerInputs));
            Assert.False(cal.ConsumeTriggerButtons);   // Back keeps its ordinary press
            Assert.Equal(MacroTriggerMode.HoldForMs, cal.TriggerMode);
        }
    }
}
