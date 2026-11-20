using System;
using System.Collections.Generic;
using System.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PadForge.Engine.Common.Mapping;
using PadForge.Resources.Strings;

namespace PadForge.ViewModels
{
    /// <summary>
    /// An added source as a line of the open row
    /// (<see cref="IMappingSourceLine"/>), and what the row's first source
    /// shares with it: the mode list, the mode's name and the one Record
    /// that takes a mode's keys in turn.
    /// </summary>
    public partial class MappingSourceItem : IMappingSourceLine
    {
        /// <summary>The mark of a button that pushes a stick's axis its
        /// positive way, and the opposite way.</summary>
        public const string DirectionPositive = "→ +";
        public const string DirectionNegative = "← −";

        /// <summary>Whether an input is pressed or not, as against moved: a
        /// button, a hat's direction, a touchpad's press, an NFC tag or a
        /// voice phrase. Such an input pushes a stick's axis one way, so it
        /// carries a direction there. The input is given with no flag
        /// before it.</summary>
        internal static bool IsButtonClass(string descriptor)
        {
            string d = descriptor?.Trim() ?? string.Empty;
            if (d.Length == 0) return false;
            // An abstract "Gamepad ..." name folds to what it reads first.
            d = SourceCoercion.ResolveGamepadAlias(d) ?? d;
            return d.StartsWith("Button ", StringComparison.Ordinal)
                || d.StartsWith("POV ", StringComparison.Ordinal)
                || d.StartsWith("Touchpad ", StringComparison.Ordinal)
                || SourceCoercion.IsNfcTagDescriptor(d)
                || SourceCoercion.IsVoicePhraseDescriptor(d);
        }

        public MappingItem Row => ParentMappingItem;

        public MappingSourceItem ModeHolder => this;

        /// <summary>This is the object a row keeps its first source's mode
        /// on, and no source of its own.</summary>
        private bool HoldsTheFirstSourcesMode
            => ParentMappingItem != null && ReferenceEquals(ParentMappingItem.PrimaryKindSource, this);

        /// <summary>The modes the source's picker offers. The first source
        /// leaves out Invert On Hold while no other source is there for it
        /// to turn around (<see cref="MappingItem.PrimaryKindOptions"/>).
        /// Both leave out Rapid Trigger where it cannot act.</summary>
        public IReadOnlyList<KindChoice> ModeChoices
            => HoldsTheFirstSourcesMode ? ParentMappingItem.PrimaryKindOptions : KindChoices;

        internal void RaiseModeChoices() => OnPropertyChanged(nameof(ModeChoices));

        /// <summary>The mode's name as its picker shows it.</summary>
        public string KindLabel
        {
            get
            {
                foreach (var option in KindOptions)
                    if (string.Equals(option.Value, _kind, StringComparison.Ordinal))
                        return option.Name;
                return _kind ?? string.Empty;
            }
        }

        public bool IsLineKeyMode => IsKindDescriptorless;

        public bool ShowsLineDeadZone => !IsKindDescriptorless && IsDeadZoneApplicable;

        public string LineDeviceGuid => _deviceGuid ?? string.Empty;

        /// <summary>The device's name. A source whose keys read two
        /// devices is on neither, and names both.</summary>
        public string LineDeviceLabel
        {
            get
            {
                if (!IsKindDescriptorless) return DisplayDeviceLabel ?? string.Empty;
                if (string.IsNullOrEmpty(_deviceGuid) && UsesUpDownKeys)
                {
                    string up = ParamUpDeviceLabel, down = ParamDownDeviceLabel;
                    if (up.Length > 0 && down.Length > 0 && !string.Equals(up, down, StringComparison.Ordinal))
                        return up + " + " + down;
                }
                return _deviceLabel ?? string.Empty;
            }
        }

        public PadViewModel.MappedDeviceInfo LineDevice => ParentMappingItem?.Owner?.MappedDeviceOf(LineDeviceGuid);

        public bool IsLineAway => PadViewModel.IsAway(ParentMappingItem?.Owner, LineDeviceGuid);

        public bool IsLineUnassigned => !HasAnyBoundFeed && string.IsNullOrEmpty(_deviceGuid);

        private string _lineLetter = string.Empty;

        public string LineLetter
        {
            get => _lineLetter;
            internal set => SetProperty(ref _lineLetter, value ?? string.Empty);
        }

        /// <summary>Which way the source pushes a stick's axis, on a row
        /// with two directions.</summary>
        public string LineDirection
            => ParentMappingItem != null && ParentMappingItem.IsBipolarAxisTarget ? DirectionBadge : string.Empty;

        private bool _isTrimStick;

        public bool IsTrimStick
        {
            get => _isTrimStick;
            internal set => SetProperty(ref _isTrimStick, value);
        }

        public bool LineInvert
        {
            get => Invert;
            set => Invert = value;
        }

        public bool LineHalf
        {
            get => HalfAxis;
            set => HalfAxis = value;
        }

        public bool LineBidirectional
        {
            get => Bidirectional;
            set => Bidirectional = value;
        }

        public int LineDeadZone
        {
            get => DeadZone;
            set => DeadZone = value;
        }

        // ─────────────────────────────────────────────
        //  Record and Clear beside the source's input
        // ─────────────────────────────────────────────

        /// <summary>Raised when Record should take the keys of the source's
        /// mode: Up and then Down, or the modifier.</summary>
        public event EventHandler StartKeysRecordingRequested;

        private RelayCommand _lineRecordCommand;

        /// <summary>Record on the source's line. A mode that reads its own
        /// input records that input. A mode that reads keys records them in
        /// turn, as the first source's Record does. A second click stops
        /// either.</summary>
        public RelayCommand LineRecordCommand => _lineRecordCommand ??= new RelayCommand(() =>
        {
            if (IsRecording) StopRecordingRequested?.Invoke(this, EventArgs.Empty);
            else if (IsKindDescriptorless) StartKeysRecordingRequested?.Invoke(this, EventArgs.Empty);
            else StartRecordingRequested?.Invoke(this, EventArgs.Empty);
        });

        private RelayCommand _lineClearCommand;

        /// <summary>Clear on the source's line: empties the input, or the
        /// keys of a mode that reads keys, and keeps the source.</summary>
        public RelayCommand LineClearCommand => _lineClearCommand ??= new RelayCommand(() =>
        {
            if (!IsKindDescriptorless)
            {
                ClearCommand.Execute(null);
                return;
            }
            SetParamKey(ParamRecordTarget.Up, string.Empty, null);
            SetParamKey(ParamRecordTarget.Down, string.Empty, null);
            SetParamKey(ParamRecordTarget.Modifier, string.Empty, null);
        });

        private RelayCommand _lineRemoveCommand;

        /// <summary>Remove at the line's end: the row drops the source.</summary>
        public RelayCommand LineRemoveCommand => _lineRemoveCommand ??= new RelayCommand(
            () => ParentMappingItem?.RemoveExtraSourceCommand.Execute(this));

        public string LineRecordIcon => RecordButtonIcon;

        public string LineRecordText => RecordButtonText;

        /// <summary>Record is offered: the source's device is connected, or
        /// the source names none.</summary>
        public bool CanLineRecord => !IsLineAway;

        /// <summary>Raises what a change of the source means to its line,
        /// after the change itself.</summary>
        protected override void OnPropertyChanged(PropertyChangedEventArgs e)
        {
            base.OnPropertyChanged(e);
            switch (e.PropertyName)
            {
                case nameof(Invert):
                    Raise(nameof(LineInvert));
                    Raise(nameof(LineDirection));
                    break;
                case nameof(HalfAxis):
                    Raise(nameof(LineHalf));
                    break;
                case nameof(Bidirectional):
                    Raise(nameof(LineBidirectional));
                    break;
                case nameof(DeadZone):
                    Raise(nameof(LineDeadZone));
                    break;
                case nameof(IsDeadZoneApplicable):
                    Raise(nameof(ShowsLineDeadZone));
                    break;
                case nameof(Kind):
                    Raise(nameof(KindLabel));
                    Raise(nameof(IsLineKeyMode));
                    Raise(nameof(ShowsLineDeadZone));
                    RaiseLineFacts();
                    break;
                case nameof(KindChoices):
                    Raise(nameof(ModeChoices));
                    break;
                case nameof(RecordButtonIcon):
                    Raise(nameof(LineRecordIcon));
                    break;
                case nameof(RecordButtonText):
                    Raise(nameof(LineRecordText));
                    break;
                case nameof(Descriptor):
                case nameof(DeviceGuid):
                case nameof(DeviceLabel):
                case nameof(SelectedInput):
                case nameof(ParamUp):
                case nameof(ParamDown):
                case nameof(ParamModifier):
                case nameof(ParamUpDeviceLabel):
                case nameof(ParamDownDeviceLabel):
                    RaiseLineFacts();
                    break;
            }
        }

        private void Raise(string name) => base.OnPropertyChanged(new PropertyChangedEventArgs(name));

        /// <summary>What the source's line shows, read again: its device
        /// may be another, or have connected or gone.</summary>
        internal void RaiseLineFacts()
        {
            Raise(nameof(Row));
            Raise(nameof(LineDeviceGuid));
            Raise(nameof(LineDeviceLabel));
            Raise(nameof(LineDevice));
            Raise(nameof(IsLineAway));
            Raise(nameof(CanLineRecord));
            Raise(nameof(IsLineUnassigned));
            Raise(nameof(LineDirection));
            Raise(nameof(ModeChoices));
        }
    }
}
