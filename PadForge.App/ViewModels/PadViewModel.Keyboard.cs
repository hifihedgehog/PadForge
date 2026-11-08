using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.Input;
using PadForge.Engine;

namespace PadForge.ViewModels
{
    /// <summary>
    /// The slot side of keyboard menus (#566): the Keyboard + Mouse slots a
    /// keyboard can type through, the names the slot's lists give an input,
    /// and the record and Map All buttons of the Keyboard Controls card, which
    /// MainWindow runs on the recorder as it runs the menu host's record
    /// button.
    /// </summary>
    public partial class PadViewModel
    {
        /// <summary>Every slot that exists and is Keyboard + Mouse, by pad
        /// index, set by the MainViewModel that holds the slots. A pad holds
        /// it rather than a static, which would keep a main view model alive.</summary>
        internal Func<IReadOnlyList<KbmSlotChoice>> KbmSlotChoicesProvider { get; set; }

        /// <summary>A keyboard row's record button: MainWindow records a press
        /// for it, or stops the recording it has.</summary>
        public event EventHandler<KeyboardInputRow> MenuKeyboardRecordRequested;

        /// <summary>Map All on the Keyboard Controls card: MainWindow records
        /// each control in turn, or stops.</summary>
        public event EventHandler MenuKeyboardMapAllRequested;

        public RelayCommand<KeyboardInputRow> MenuKeyboardRecordCommand =>
            _menuKeyboardRecordCommand ??= new RelayCommand<KeyboardInputRow>(row =>
            {
                if (row == null || SelectedMenu == null) return;
                MenuKeyboardRecordRequested?.Invoke(this, row);
            });
        private RelayCommand<KeyboardInputRow> _menuKeyboardRecordCommand;

        public RelayCommand MenuKeyboardMapAllCommand =>
            _menuKeyboardMapAllCommand ??= new RelayCommand(() =>
            {
                if (SelectedMenu?.IsKeyboard != true) return;
                MenuKeyboardMapAllRequested?.Invoke(this, EventArgs.Empty);
            });
        private RelayCommand _menuKeyboardMapAllCommand;

        /// <summary>The name the slot's input lists give an input on a device:
        /// that device's own entry, else any entry of the input, else the
        /// descriptor. An axis half carries the I/H prefix, which no entry
        /// does, so its base input is looked up and its direction added.</summary>
        internal string DescribeSlotInput(string descriptor, string deviceGuid)
        {
            if (string.IsNullOrEmpty(descriptor)) return descriptor;
            string any = null;
            foreach (var c in SlotAvailableInputs)
            {
                if (c == null || !string.Equals(c.Descriptor, descriptor, StringComparison.Ordinal)) continue;
                if (string.Equals(c.DeviceGuid ?? "", deviceGuid ?? "", StringComparison.OrdinalIgnoreCase)) return c.DisplayName;
                any ??= c.DisplayName;
            }
            if (any != null) return any;
            string clean = PadForge.Engine.Common.Mapping.SourceCoercion.StripLegacyPrefix(descriptor, out bool invert, out bool half);
            if (!half || string.Equals(clean, descriptor, StringComparison.Ordinal)) return descriptor;
            string baseName = DescribeSlotInput(clean, deviceGuid);
            return baseName + " " + (invert ? "-" : "+");
        }

        /// <summary>The Keyboard + Mouse slots a keyboard menu offers.</summary>
        internal IReadOnlyList<KbmSlotChoice> KbmSlotChoices()
            => KbmSlotChoicesProvider?.Invoke() ?? Array.Empty<KbmSlotChoice>();

        /// <summary>True when this slot is a Keyboard + Mouse slot, the kind a
        /// keyboard menu can type through.</summary>
        internal bool IsKeyboardMouseSlot => OutputType == VirtualControllerType.KeyboardMouse;
    }
}
