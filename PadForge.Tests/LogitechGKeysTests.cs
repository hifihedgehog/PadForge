using System;
using System.IO;
using System.Linq;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Common;
using PadForge.Engine.Common.Logitech;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Logitech G-keys through the G-key SDK (issue #454, asked for in
    /// discussion #449).
    ///
    /// <para>No Logitech hardware or software is on the bench, so what is
    /// tested here is everything that does not need the real library: the
    /// event word's layout against the SDK header, the button index map, the
    /// row's press and release behavior, and the wiring that makes the toggle
    /// survive a restart. What needs a rig is the library actually loading and
    /// calling back.</para>
    /// </summary>
    public class LogitechGKeysTests
    {
        private static uint Word(int keyIdx, bool down, int mState, bool mouse,
                                 int reserved1 = 0, int reserved2 = 0)
            => (uint)(keyIdx & 0xFF)
             | (down ? 1u << 8 : 0u)
             | ((uint)(mState & 3) << 9)
             | (mouse ? 1u << 11 : 0u)
             | ((uint)(reserved1 & 0xF) << 12)
             | ((uint)(reserved2 & 0xFFFF) << 16);

        // ─── The event word ───

        [Fact]
        public void TheEventWordUnpacksTheFieldsTheHeaderDeclares()
        {
            // LogitechGkeyLib.h: keyIdx 8, keyDown 1, mState 2, mouse 1,
            // reserved1 4, reserved2 16.
            var code = new LogitechGKeyInterop.GkeyCode { Raw = Word(6, true, 2, false) };
            Assert.Equal(6, code.KeyIndex);
            Assert.True(code.KeyDown);
            Assert.Equal(2, code.MState);
            Assert.False(code.IsMouse);
        }

        [Fact]
        public void AMouseEventSaysSo()
        {
            var code = new LogitechGKeyInterop.GkeyCode { Raw = Word(8, true, 1, mouse: true) };
            Assert.Equal(8, code.KeyIndex);
            Assert.True(code.IsMouse);
        }

        [Fact]
        public void AReleaseReadsAsARelease()
        {
            var code = new LogitechGKeyInterop.GkeyCode { Raw = Word(3, false, 1, false) };
            Assert.False(code.KeyDown);
            Assert.Equal(3, code.KeyIndex);
        }

        /// <summary>
        /// The reserved bits do not leak into the mouse flag.
        ///
        /// <para>This is the defect in Logitech's own C# sample, which reads
        /// <c>mouse</c> as <c>(complete &gt;&gt; 11) &amp; 15</c> when the
        /// header gives the field one bit. With any reserved1 bit set, that
        /// reads a keyboard event as a mouse event and the key lands on the
        /// wrong half of the device.</para>
        /// </summary>
        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(4)]
        [InlineData(8)]
        [InlineData(15)]
        public void ReservedBitsDoNotLeakIntoTheMouseFlag(int reserved1)
        {
            var code = new LogitechGKeyInterop.GkeyCode
            {
                Raw = Word(5, true, 1, mouse: false, reserved1: reserved1),
            };
            Assert.False(code.IsMouse);
            Assert.Equal(5, code.KeyIndex);
            Assert.Equal(1, code.MState);

            var mouseCode = new LogitechGKeyInterop.GkeyCode
            {
                Raw = Word(7, true, 1, mouse: true, reserved1: reserved1),
            };
            Assert.True(mouseCode.IsMouse);
        }

        [Fact]
        public void TheUpperHalfOfTheWordDoesNotDisturbTheFields()
        {
            // The sample's ushort cannot hold these bits at all. Ours must
            // carry them and still read the low fields correctly.
            var code = new LogitechGKeyInterop.GkeyCode
            {
                Raw = Word(29, true, 3, false, reserved1: 15, reserved2: 0xFFFF),
            };
            Assert.Equal(29, code.KeyIndex);
            Assert.True(code.KeyDown);
            Assert.Equal(3, code.MState);
            Assert.False(code.IsMouse);
        }

        [Fact]
        public void TheRangesMatchTheHeader()
        {
            Assert.Equal(29, LogitechGKeyInterop.MaxGKeys);
            Assert.Equal(3, LogitechGKeyInterop.MaxMStates);
            Assert.Equal(6, LogitechGKeyInterop.MinMouseButton);
            Assert.Equal(20, LogitechGKeyInterop.MaxMouseButton);
        }

        // ─── The button map ───

        [Fact]
        public void EveryKeyAndModeGetsItsOwnButton()
        {
            var seen = new System.Collections.Generic.HashSet<int>();
            for (int mode = 1; mode <= LogitechGKeyInterop.MaxMStates; mode++)
                for (int key = 1; key <= LogitechGKeyInterop.MaxGKeys; key++)
                {
                    int b = LogitechGKeyMap.KeyboardButton(key, mode);
                    Assert.InRange(b, 0, LogitechGKeyMap.KeyboardCount - 1);
                    Assert.True(seen.Add(b), $"G{key} M{mode} collided at {b}");
                }
            Assert.Equal(LogitechGKeyMap.KeyboardCount, seen.Count);
        }

        [Fact]
        public void MouseButtonsSitAboveTheKeyboardBlock()
        {
            for (int button = LogitechGKeyInterop.MinMouseButton;
                 button <= LogitechGKeyInterop.MaxMouseButton; button++)
            {
                int b = LogitechGKeyMap.MouseButton(button);
                Assert.InRange(b, LogitechGKeyMap.KeyboardCount, LogitechGKeyMap.TotalCount - 1);
            }
            Assert.Equal(102, LogitechGKeyMap.TotalCount);
        }

        [Fact]
        public void TheWholeLayoutFitsTheStateBuffer()
        {
            Assert.True(LogitechGKeyMap.TotalCount <= CustomInputState.MaxButtons,
                        $"{LogitechGKeyMap.TotalCount} buttons will not fit {CustomInputState.MaxButtons}");
        }

        [Theory]
        [InlineData(0, 1)]
        [InlineData(30, 1)]
        [InlineData(1, 0)]
        [InlineData(1, 4)]
        public void AnOutOfRangeKeyOrModeHasNoButton(int key, int mode)
            => Assert.Equal(-1, LogitechGKeyMap.KeyboardButton(key, mode));

        [Theory]
        [InlineData(5)]
        [InlineData(21)]
        [InlineData(0)]
        public void AnOutOfRangeMouseButtonHasNone(int button)
            => Assert.Equal(-1, LogitechGKeyMap.MouseButton(button));

        [Fact]
        public void EveryButtonReadsBackToTheCoordinatesItCameFrom()
        {
            for (int mode = 1; mode <= LogitechGKeyInterop.MaxMStates; mode++)
                for (int key = 1; key <= LogitechGKeyInterop.MaxGKeys; key++)
                {
                    int b = LogitechGKeyMap.KeyboardButton(key, mode);
                    Assert.True(LogitechGKeyMap.Describe(b, out bool isMouse, out int k, out int m));
                    Assert.False(isMouse);
                    Assert.Equal(key, k);
                    Assert.Equal(mode, m);
                }
            for (int button = LogitechGKeyInterop.MinMouseButton;
                 button <= LogitechGKeyInterop.MaxMouseButton; button++)
            {
                int b = LogitechGKeyMap.MouseButton(button);
                Assert.True(LogitechGKeyMap.Describe(b, out bool isMouse, out int n, out _));
                Assert.True(isMouse);
                Assert.Equal(button, n);
            }
        }

        [Fact]
        public void AnEventRoutesToTheButtonItsCoordinatesName()
        {
            var keyboard = new LogitechGKeyInterop.GkeyCode { Raw = Word(5, true, 2, false) };
            Assert.Equal(LogitechGKeyMap.KeyboardButton(5, 2), LogitechGKeyMap.ButtonFor(keyboard));

            var mouse = new LogitechGKeyInterop.GkeyCode { Raw = Word(9, true, 1, mouse: true) };
            Assert.Equal(LogitechGKeyMap.MouseButton(9), LogitechGKeyMap.ButtonFor(mouse));
        }

        [Fact]
        public void TheFallbackNameSaysWhichKeyInWhichMode()
        {
            Assert.Equal("G1 (M1)", LogitechGKeyMap.FallbackName(LogitechGKeyMap.KeyboardButton(1, 1)));
            Assert.Equal("G5 (M3)", LogitechGKeyMap.FallbackName(LogitechGKeyMap.KeyboardButton(5, 3)));
            Assert.Equal("Mouse Button 6", LogitechGKeyMap.FallbackName(LogitechGKeyMap.MouseButton(6)));
        }

        // ─── The device row ───

        [Fact]
        public void TheRowIsItsOwnDeviceTypeAndNeverAnswersAnyDevice()
        {
            using var dev = new LogitechGKeysDevice();
            Assert.Equal(InputDeviceType.LogitechGKeys, dev.GetInputDeviceType());
            // #431: a row with its own vocabulary must not answer an empty
            // guid source, or a resting key impersonates the gamepad layout.
            Assert.False(InputDeviceType.AnswersAnyDeviceSources(InputDeviceType.LogitechGKeys));
        }

        [Fact]
        public void TheRowListsEveryButtonTheSdkCanReport()
        {
            using var dev = new LogitechGKeysDevice();
            var objects = dev.GetDeviceObjects();
            Assert.Equal(LogitechGKeyMap.TotalCount, objects.Length);
            Assert.Equal(LogitechGKeyMap.TotalCount, dev.NumButtons);
            Assert.Equal(0, dev.NumAxes);
            // Dense, not gated: the SDK cannot be asked how many G-keys a
            // given keyboard has, so claiming a subset would be a guess.
            Assert.Null(dev.SupportedButtonIndices);
            for (int i = 0; i < objects.Length; i++)
                Assert.Equal(i, objects[i].InputIndex);
        }

        [Fact]
        public void APressAssertsItsButtonAndNoOther()
        {
            using var dev = new LogitechGKeysDevice();
            dev.AttachForTest();
            dev.InjectForTest(5, 1, down: true, mouse: false);

            var state = dev.GetCurrentState();
            Assert.NotNull(state);
            Assert.True(state.Buttons[LogitechGKeyMap.KeyboardButton(5, 1)]);
            Assert.False(state.Buttons[LogitechGKeyMap.KeyboardButton(5, 2)]);
            Assert.False(state.Buttons[LogitechGKeyMap.KeyboardButton(6, 1)]);
        }

        [Fact]
        public void ThreeModesOfOneKeyAreThreeDifferentButtons()
        {
            using var dev = new LogitechGKeysDevice();
            dev.AttachForTest();
            dev.InjectForTest(4, 3, down: true, mouse: false);

            var state = dev.GetCurrentState();
            Assert.True(state.Buttons[LogitechGKeyMap.KeyboardButton(4, 3)]);
            Assert.False(state.Buttons[LogitechGKeyMap.KeyboardButton(4, 1)]);
            Assert.False(state.Buttons[LogitechGKeyMap.KeyboardButton(4, 2)]);
        }

        [Fact]
        public void AMousePressLandsOnTheMouseHalf()
        {
            using var dev = new LogitechGKeysDevice();
            dev.AttachForTest();
            dev.InjectForTest(8, 1, down: true, mouse: true);

            var state = dev.GetCurrentState();
            Assert.True(state.Buttons[LogitechGKeyMap.MouseButton(8)]);
            // 8 is also a valid G-key number, so a wrong mouse bit would show
            // up right here.
            Assert.False(state.Buttons[LogitechGKeyMap.KeyboardButton(8, 1)]);
        }

        [Fact]
        public void ATapSurvivesUntilAPollCanSeeIt()
        {
            // A G-key can go down and up between two polls. Without the pulse
            // the macro never sees the edge, which is the handheld row's
            // recorded lesson.
            using var dev = new LogitechGKeysDevice();
            dev.AttachForTest();
            int button = LogitechGKeyMap.KeyboardButton(2, 1);

            dev.InjectForTest(2, 1, down: true, mouse: false);
            dev.InjectForTest(2, 1, down: false, mouse: false);

            Assert.True(dev.GetCurrentState().Buttons[button],
                        "a tap shorter than one poll was dropped");
        }

        [Fact]
        public void AReleasedKeyEventuallyFalls()
        {
            using var dev = new LogitechGKeysDevice();
            dev.AttachForTest();
            int button = LogitechGKeyMap.KeyboardButton(2, 1);

            dev.InjectForTest(2, 1, down: true, mouse: false);
            dev.InjectForTest(2, 1, down: false, mouse: false);
            Assert.True(dev.GetCurrentState().Buttons[button]);

            // The pulse is a floor, not a latch: once it expires the button
            // reads false on every poll, so the falling edge arrives.
            System.Threading.Thread.Sleep(LogitechGKeysDevice.PulseMs + 120);
            Assert.False(dev.GetCurrentState().Buttons[button]);
        }

        [Fact]
        public void AHeldKeyStaysDownPastThePulse()
        {
            using var dev = new LogitechGKeysDevice();
            dev.AttachForTest();
            int button = LogitechGKeyMap.KeyboardButton(9, 2);

            dev.InjectForTest(9, 2, down: true, mouse: false);
            System.Threading.Thread.Sleep(LogitechGKeysDevice.PulseMs + 120);
            Assert.True(dev.GetCurrentState().Buttons[button],
                        "a key still held was released when its pulse ran out");
        }

        [Fact]
        public void AnEventOutsideTheSdkRangesIsIgnored()
        {
            using var dev = new LogitechGKeysDevice();
            dev.AttachForTest();
            // Key 30 does not exist, and mode 0 is not a mode.
            dev.InjectForTest(30, 1, down: true, mouse: false);
            dev.InjectForTest(1, 0, down: true, mouse: false);

            var state = dev.GetCurrentState();
            for (int b = 0; b < LogitechGKeyMap.TotalCount; b++)
                Assert.False(state.Buttons[b], $"button {b} was asserted by an out-of-range event");
        }

        [Fact]
        public void AClosedRowReadsNothing()
        {
            var dev = new LogitechGKeysDevice();
            dev.AttachForTest();
            dev.InjectForTest(1, 1, down: true, mouse: false);
            dev.Dispose();
            Assert.Null(dev.GetCurrentState());
            Assert.False(dev.IsAttached);
        }

        // ─── Discovery ───

        [Fact]
        public void DiscoveryNamesTheClassTheSdkRegisters()
        {
            // The CLSID is how a non-default install is found. Mumble's
            // GKey.cpp has used this same one in production for years.
            Assert.Equal("{7bded654-f278-4977-a20f-6e72a0d07859}", LogitechGKeyCatalog.ClassId);
        }

        [Fact]
        public void DiscoveryReportsARatherThanThrowingWhenNothingIsInstalled()
        {
            // Runs on any machine. Either it finds a real library or it says
            // why, and neither outcome may throw.
            string path = LogitechGKeyCatalog.Find(out var reason);
            if (path == null)
                Assert.NotEqual(LogitechGKeyCatalog.Reason.Found, reason);
            else
                Assert.Equal(LogitechGKeyCatalog.Reason.Found, reason);
        }

        [Fact]
        public void AsourceWithNoSdkStopsWithAReasonRatherThanThrowing()
        {
            using var source = new LogitechGKeySource(_ => { });
            bool started = source.Start();
            if (!started)
                Assert.Contains(source.State, new[]
                {
                    LogitechGKeyState.NoSdk, LogitechGKeyState.SdkPathStale,
                    LogitechGKeyState.LoadFailed, LogitechGKeyState.MissingExports,
                    LogitechGKeyState.InitRefused,
                });
            else
                Assert.Equal(LogitechGKeyState.Running, source.State);
        }

        // ─── Wiring ───

        [Fact]
        public void TheToggleReachesTheRuntimeAndTheDirtyGate()
        {
            // The dirty-gate trap: a setting that works all session and
            // reverts on restart because one of the three sides was missing.
            string mainWindow = RepoFile("PadForge.App", "MainWindow.xaml.cs");
            Assert.Contains("nameof(SettingsViewModel.GKeysEnabled)", mainWindow);

            string settings = RepoFile("PadForge.App", "Services", "SettingsService.cs");
            Assert.Contains("public bool GKeysEnabled", settings);
            Assert.Contains("vm.GKeysEnabled = appSettings.GKeysEnabled", settings);
            Assert.Contains("GKeysEnabled = vm.GKeysEnabled", settings);
        }

        /// <summary>
        /// The toggle lives in the Settings page's Input Engine card, beside
        /// the Flydigi protocol switch, not on the Dashboard.
        ///
        /// <para>It first shipped as its own Dashboard section, which was
        /// wrong: this is a vendor input path the engine can read, the exact
        /// shape of the Flydigi switch that already sits in that card, and
        /// not a live readout the way head tracking is.</para>
        /// </summary>
        [Fact]
        public void TheToggleSitsInTheInputEngineCard()
        {
            string settingsPage = RepoFile("PadForge.App", "Views", "SettingsPage.xaml");
            int engine = settingsPage.IndexOf("Binding Settings_InputEngine,", StringComparison.Ordinal);
            int gkeys = settingsPage.IndexOf("Binding Settings_GKeys,", StringComparison.Ordinal);
            Assert.True(engine > 0 && gkeys > engine, "the G-Keys row is not inside the Input Engine card");
            Assert.Contains("CommandParameter=\"GKeysEnabled\"", settingsPage);

            string dashboard = RepoFile("PadForge.App", "Views", "DashboardPage.xaml");
            Assert.DoesNotContain("GKeys", dashboard);
        }

        /// <summary>
        /// Every checkbox in the Settings window card explains itself.
        ///
        /// <para>Minimize to System Tray shipped with no tooltip while two of
        /// its neighbors had one, and Start Minimized and Start at Login had
        /// the same gap. A checkbox whose label is its only explanation is the
        /// same defect as a reset icon with a generic tooltip.</para>
        /// </summary>
        [Fact]
        public void EveryWindowSettingExplainsItself()
        {
            string page = RepoFile("PadForge.App", "Views", "SettingsPage.xaml");
            foreach (string tip in new[]
                     {
                         "Settings_MinimizeToTrayTip", "Settings_CloseToTrayTip",
                         "Settings_AlwaysShowTrayIconTip", "Settings_StartMinimizedTip",
                         "Settings_StartAtLoginTip",
                     })
                Assert.Contains(tip, page);
        }

        [Fact]
        public void TheRowIsRegisteredAndRetiredBySweepOne()
        {
            string step1 = RepoFile("PadForge.App", "Common", "Input", "InputManager.Step1.UpdateDevices.cs");
            Assert.Contains("UpdateLogitechGKeysDevice()", step1);
            Assert.Contains("RetireLogitechGKeysRow()", step1);
            // Off has to actually stop it, not just hide the row.
            Assert.Contains("ShutdownLogitechGKeysInputs()", step1);
        }

        [Fact]
        public void TheRowsButtonsAreNamedRatherThanNumbered()
        {
            // #150's shape: a row whose every entry has a name must not be
            // renumbered into "Button 0".
            string resolver = RepoFile("PadForge.App", "Common", "MappingDisplayResolver.cs");
            Assert.Contains("InputDeviceType.LogitechGKeys", resolver);
        }

        [Fact]
        public void TheTypeHasAGlyphAndAName()
        {
            string glyph = RepoFile("PadForge.App", "Common", "DeviceTypeGlyph.cs");
            Assert.Contains("InputDeviceType.LogitechGKeys", glyph);

            string svc = RepoFile("PadForge.App", "Services", "InputService.cs");
            Assert.Contains("InputDeviceType.LogitechGKeys => \"LogitechGKeys\"", svc);

            string row = RepoFile("PadForge.App", "ViewModels", "DeviceRowViewModel.cs");
            Assert.Contains("\"LogitechGKeys\" => Strings.Instance.DeviceType_LogitechGKeys", row);
        }

        [Fact]
        public void EveryNewStringReachesEveryLocale()
        {
            string[] keys =
            {
                "Settings_GKeys", "Settings_GKeysTooltip",
                "Settings_GKeysStatus_NoSdk", "Settings_GKeysStatus_PathStale",
                "Settings_GKeysStatus_LoadFailed", "Settings_GKeysStatus_WrongLibrary",
                "Settings_GKeysStatus_InitRefused", "Settings_GKeysStatus_NoKeysYet",
                "Settings_GKeysStatus_Running_Format",
                "Settings_MinimizeToTrayTip", "Settings_StartMinimizedTip", "Settings_StartAtLoginTip",
                "DeviceType_LogitechGKeys", "DeviceType_VrController",
            };
            foreach (string locale in new[]
                     {
                         "Strings.resx", "Strings.de.resx", "Strings.es.resx", "Strings.fr.resx",
                         "Strings.it.resx", "Strings.ja.resx", "Strings.ko.resx", "Strings.nl.resx",
                         "Strings.pt-BR.resx", "Strings.zh-Hans.resx",
                     })
            {
                string text = RepoFile("PadForge.App", "Resources", "Strings", locale);
                foreach (string key in keys)
                    Assert.True(text.Contains("name=\"" + key + "\"", StringComparison.Ordinal),
                                $"{locale} is missing {key}");
            }
        }

        /// <summary>
        /// The setup text names both steps Logitech Gaming Software needs.
        ///
        /// <para>Logitech's G-key SDK manual ("User experience") says a key
        /// reports to an application only after the user drags the "G-key"
        /// command onto it in that application's profile. The hint and the
        /// tooltip named the persistent profile alone, so a user who did
        /// what they said still saw no key.</para>
        /// </summary>
        [Fact]
        public void TheSetupTextNamesTheGKeyCommandInEveryLocale()
        {
            foreach (string locale in new[]
                     {
                         "Strings.resx", "Strings.de.resx", "Strings.es.resx", "Strings.fr.resx",
                         "Strings.it.resx", "Strings.ja.resx", "Strings.ko.resx", "Strings.nl.resx",
                         "Strings.pt-BR.resx", "Strings.zh-Hans.resx",
                     })
            {
                string text = RepoFile("PadForge.App", "Resources", "Strings", locale);

                // The hint has no other reason to say "G-key".
                Assert.True(CommandMentions(ResxValue(text, "Settings_GKeysStatus_NoKeysYet")) >= 1,
                            $"{locale}: the no-key hint leaves out the G-key command");
                // The tooltip already names the G-key SDK, so the command is
                // its second mention.
                Assert.True(CommandMentions(ResxValue(text, "Settings_GKeysTooltip")) >= 2,
                            $"{locale}: the tooltip leaves out the G-key command");
            }
        }


        /// <summary>
        /// A held key whose release never arrives is closed by the resync.
        ///
        /// <para>The SDK only feeds an application whose Logitech profile is
        /// active, so a key held while PadForge loses that profile delivers
        /// no release at all. Events alone left the button asserted for the
        /// life of the session, and the two SDK calls that answer "what is
        /// actually held" were written and never called.</para>
        /// </summary>
        [Fact]
        public void TheResyncClosesAHoldWhoseReleaseNeverCame()
        {
            string device = RepoFile("PadForge.App", "Common", "Input", "LogitechGKeysDevice.cs");
            // The recovery has to run from the state read, which is the only
            // thing that keeps ticking when the events stop.
            Assert.Contains("ResyncHeld", device);
            Assert.Contains("ResyncIntervalMs", device);
            Assert.Contains("src.IsMouseButtonPressed(keyOrButton)", device);
            Assert.Contains("src.IsKeyPressed(keyOrButton, mode)", device);
        }

        /// <summary>The library is never unloaded and shutdown is not
        /// conditional on init having succeeded, which is what the production
        /// reference does and why.</summary>
        [Fact]
        public void TeardownMatchesTheReferenceImplementation()
        {
            string src = RepoFile("PadForge.Engine", "Common", "Logitech", "LogitechGKeySource.cs");
            Assert.DoesNotContain("NativeLibrary.Free", src);
            // Shutdown runs from Teardown unconditionally, not behind an
            // _initialized check.
            int at = src.IndexOf("private void Teardown()", StringComparison.Ordinal);
            Assert.True(at > 0);
            string body = src.Substring(at, Math.Min(600, src.Length - at));
            Assert.Contains("_shutdown?.Invoke()", body);
        }

        /// <summary>Discovery offers every candidate, because a path that
        /// exists is not a path that loads.</summary>
        [Fact]
        public void DiscoveryOffersEveryCandidateNotJustTheFirst()
        {
            var candidates = LogitechGKeyCatalog.Candidates(out var reason);
            Assert.NotNull(candidates);
            if (candidates.Count == 0)
                Assert.NotEqual(LogitechGKeyCatalog.Reason.Found, reason);
            else
                Assert.Equal(LogitechGKeyCatalog.Reason.Found, reason);

            string src = RepoFile("PadForge.Engine", "Common", "Logitech", "LogitechGKeySource.cs");
            Assert.Contains("foreach (string candidate in candidates)", src);
        }
        /// <summary>How often a string says "G-key" as a name. The English
        /// plural "G-keys" names the hardware and does not count.</summary>
        private static int CommandMentions(string value)
        {
            int count = 0;
            for (int at = value.IndexOf("G-key", StringComparison.Ordinal); at >= 0;
                 at = value.IndexOf("G-key", at + 5, StringComparison.Ordinal))
            {
                int after = at + 5;
                if (after >= value.Length || value[after] != 's') count++;
            }
            return count;
        }

        private static string ResxValue(string resx, string key)
        {
            int at = resx.IndexOf("<data name=\"" + key + "\"", StringComparison.Ordinal);
            Assert.True(at >= 0, key + " is missing");
            int open = resx.IndexOf("<value>", at, StringComparison.Ordinal) + "<value>".Length;
            int close = resx.IndexOf("</value>", open, StringComparison.Ordinal);
            return resx.Substring(open, close - open);
        }

        private static string RepoFile(params string[] parts)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PadForge.sln")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return File.ReadAllText(Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray()));
        }
    }
}
