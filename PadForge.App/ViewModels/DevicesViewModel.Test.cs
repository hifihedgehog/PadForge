using System;
using System.Text;
using PadForge.Resources.Strings;

namespace PadForge.ViewModels
{
    /// <summary>
    /// The Test Inputs card's compact test (#574), for a device whose state is
    /// in SDL's gamepad layout: its two sticks, its two triggers and the
    /// buttons held now, as the Devices board draws them. The full readout of
    /// every axis, button and sensor opens under More then.
    /// </summary>
    public partial class DevicesViewModel
    {
        /// <summary>The buttons the gamepad layout names (GamepadObjectNames.Button).</summary>
        private const int GamepadButtons = 22;

        private bool _showGamepadTest;

        /// <summary>Whether the card shows the compact test: the selected
        /// device's state is in the gamepad layout.</summary>
        public bool ShowGamepadTest
        {
            get => _showGamepadTest;
            private set => SetProperty(ref _showGamepadTest, value);
        }

        private double _testLeftX = 0.5, _testLeftY = 0.5, _testRightX = 0.5, _testRightY = 0.5;
        private double _testLeftTrigger, _testRightTrigger;

        /// <summary>The left stick across, 0 to 1, 0.5 at rest.</summary>
        public double TestLeftX { get => _testLeftX; private set => SetProperty(ref _testLeftX, value); }

        /// <summary>The left stick down the page, 0 at the top.</summary>
        public double TestLeftY { get => _testLeftY; private set => SetProperty(ref _testLeftY, value); }

        /// <summary>The right stick across.</summary>
        public double TestRightX { get => _testRightX; private set => SetProperty(ref _testRightX, value); }

        /// <summary>The right stick down the page.</summary>
        public double TestRightY { get => _testRightY; private set => SetProperty(ref _testRightY, value); }

        /// <summary>The left trigger, 0 to 1.</summary>
        public double TestLeftTrigger { get => _testLeftTrigger; private set => SetProperty(ref _testLeftTrigger, value); }

        /// <summary>The right trigger, 0 to 1.</summary>
        public double TestRightTrigger { get => _testRightTrigger; private set => SetProperty(ref _testRightTrigger, value); }

        private string _testPressedNames = string.Empty;

        /// <summary>The buttons held now, by their names in the language,
        /// with commas, or the word None.</summary>
        public string TestPressedNames
        {
            get => _testPressedNames;
            private set => SetProperty(ref _testPressedNames, value ?? string.Empty);
        }

        private string[] _testPressed = Array.Empty<string>();

        /// <summary>The held buttons' names, one for each chip on the
        /// Pressed line, or none.</summary>
        public string[] TestPressed
        {
            get => _testPressed;
            private set
            {
                _testPressed = value ?? Array.Empty<string>();
                OnPropertyChanged();
                OnPropertyChanged(nameof(NothingPressed));
            }
        }

        /// <summary>Whether no button is held: the line says None.</summary>
        public bool NothingPressed => _testPressed.Length == 0;

        /// <summary>What the Pressed line's format holds before its {0}.</summary>
        public string TestPressedLead => PressedPart(true);

        /// <summary>What the Pressed line's format holds after its {0}.</summary>
        public string TestPressedTail => PressedPart(false);

        private static string PressedPart(bool lead)
        {
            string format = Strings.Instance.Devices_Pressed_Format ?? string.Empty;
            int at = format.IndexOf("{0}", StringComparison.Ordinal);
            if (at < 0) return lead ? format + " " : string.Empty;
            return lead ? format.Substring(0, at) : format.Substring(at + 3);
        }

        /// <summary>The held set the names were last built for. MaxValue
        /// stands for none built yet.</summary>
        private ulong _testPressedMask = ulong.MaxValue;

        /// <summary>The test at rest, shown or not, for a device just
        /// selected.</summary>
        private void ResetGamepadTest(bool gamepadLayout)
        {
            ShowGamepadTest = gamepadLayout;
            TestLeftX = TestLeftY = TestRightX = TestRightY = 0.5;
            TestLeftTrigger = TestRightTrigger = 0;
            _testPressedMask = ulong.MaxValue;
            TestPressedNames = Strings.Instance.Common_None;
            TestPressed = Array.Empty<string>();
        }

        /// <summary>The gamepad layout's slots (SdlDeviceWrapper.GetGamepadState):
        /// axis 0 is the left stick across and 1 down (up is 0), 2 the left
        /// trigger, 3 and 4 the right stick, 5 the right trigger, each 0 to
        /// 65535, and buttons 0 to 21 are named by GamepadObjectNames.Button.
        /// The names are built again only when the held set changes.</summary>
        internal void UpdateGamepadTest(PadForge.Engine.CustomInputState state)
        {
            if (state == null) return;
            double Axis(int i, double rest) => state.Axis != null && i < state.Axis.Length
                ? Math.Clamp(state.Axis[i] / 65535.0, 0, 1) : rest;
            TestLeftX = Axis(0, 0.5);
            TestLeftY = Axis(1, 0.5);
            TestLeftTrigger = Axis(2, 0);
            TestRightX = Axis(3, 0.5);
            TestRightY = Axis(4, 0.5);
            TestRightTrigger = Axis(5, 0);
            ulong held = 0;
            if (state.Buttons != null)
                for (int i = 0; i < GamepadButtons && i < state.Buttons.Length; i++)
                    if (state.Buttons[i]) held |= 1UL << i;
            if (held == _testPressedMask) return;
            _testPressedMask = held;
            TestPressedNames = PressedNames(held);
            TestPressed = PressedList(held);
        }

        /// <summary>The language changed: the held buttons' names read again.</summary>
        private void RenamePressed()
        {
            if (_testPressedMask != ulong.MaxValue)
            {
                TestPressedNames = PressedNames(_testPressedMask);
                TestPressed = PressedList(_testPressedMask);
            }
            else if (_showGamepadTest) TestPressedNames = Strings.Instance.Common_None;
        }

        private static string[] PressedList(ulong held)
        {
            if (held == 0) return Array.Empty<string>();
            var names = new System.Collections.Generic.List<string>();
            for (int i = 0; i < GamepadButtons; i++)
                if ((held & (1UL << i)) != 0)
                    names.Add(PadForge.Common.MappingDisplayResolver.LocalizeObjectName(PadForge.Engine.GamepadObjectNames.Button(i)));
            return names.ToArray();
        }

        private static string PressedNames(ulong held)
        {
            if (held == 0) return Strings.Instance.Common_None;
            var names = new StringBuilder();
            for (int i = 0; i < GamepadButtons; i++)
            {
                if ((held & (1UL << i)) == 0) continue;
                if (names.Length > 0) names.Append(", ");
                names.Append(PadForge.Common.MappingDisplayResolver.LocalizeObjectName(PadForge.Engine.GamepadObjectNames.Button(i)));
            }
            return names.ToString();
        }
    }
}
