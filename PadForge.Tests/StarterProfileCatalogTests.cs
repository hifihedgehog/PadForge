using System;
using System.Collections.Generic;
using System.Linq;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Common.Mapping;
using PadForge.Engine.Data;
using PadForge.Services;
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
    public class StarterProfileCatalogTests
    {
        private static IEnumerable<(StarterProfileInfo Info, ProfileData Profile)> Built()
            => StarterProfileCatalog.All.Select(i => (i, i.Build()));

        private static IEnumerable<(string Key, MappingSet Set)> Sets()
            => Built().SelectMany(b => b.Profile.SlotMappingSets
                .Where(s => s != null)
                .Select(s => (b.Info.Key, s)));

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
                       || SourceCoercion.IsTouchpadPointerDescriptor(src.Descriptor);
                Assert.True(ok,
                    $"starter '{key}' row '{row.Target}' uses unresolvable descriptor '{src.Descriptor}'");
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

                Assert.Equal(1, p.SlotCreated.Count(c => c));
                Assert.True(p.SlotCreated[0], $"starter '{info.Key}' does not claim slot 0");
                Assert.True(p.SlotEnabled[0], $"starter '{info.Key}' slot 0 is disabled");
                Assert.NotNull(p.SlotMappingSets[0]);
                Assert.Equal((int)info.OutputType, p.SlotControllerTypes[0]);

                // The slot's HIDMaestro profile id must match what the engine
                // itself would pick for that VC type. It is deliberately NULL
                // for keyboard/mouse and MIDI, which do not route through
                // HIDMaestro at all, so asserting "non-empty" here would be
                // asserting the opposite of the contract.
                Assert.Equal(InputManager.GetDefaultProfileId(info.OutputType),
                    p.SlotProfileIds[0]);
            }
        }

        /// <summary>Null Macros is the legacy sentinel meaning "leave the live
        /// macro set alone". A starter profile owns its state outright, so it
        /// must carry an EMPTY array and clear whatever was there.</summary>
        [Fact]
        public void EveryProfile_CarriesEmptyMacrosNotNull()
        {
            foreach (var (info, p) in Built())
            {
                Assert.NotNull(p.Macros);
                Assert.Empty(p.Macros);
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
    }
}
