using System;
using System.Collections.Generic;
using System.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PadForge.Engine;
using PadForge.Engine.Common.Mapping;
using PadForge.Engine.Data;
using PadForge.Resources.Strings;

namespace PadForge.ViewModels
{
    /// <summary>
    /// The row in the mappings list: its cells under the devices' columns,
    /// and its first source as a line of the open row. The first source is
    /// kept on the row itself (its input in <see cref="SourceDescriptor"/>,
    /// its mode on <see cref="PrimaryKindSource"/>) and every other one is a
    /// <see cref="MappingSourceItem"/>. Both are an
    /// <see cref="IMappingSourceLine"/>, so one template draws either.
    /// </summary>
    public partial class MappingItem : IMappingSourceLine
    {
        /// <summary>The controller the row belongs to, which knows the
        /// devices and holds the list's columns. Null for a row that is in
        /// no list.</summary>
        internal PadViewModel Owner { get; set; }

        // ─────────────────────────────────────────────
        //  The first source's line
        // ─────────────────────────────────────────────

        public MappingItem Row => this;

        public MappingSourceItem ModeHolder => PrimaryKindSource;

        /// <summary>The first source reads keys of its mode's own
        /// (Incremental, Ramp, Invert On Hold), so it has no input to pick
        /// and none of the three switches.</summary>
        public bool IsLineKeyMode => !IsPrimaryDescriptor;

        public bool ShowsLineDeadZone => IsPrimaryDescriptor && IsDeadZoneApplicable;

        public string LineDeviceGuid => IsPrimaryDescriptor
            ? PrimarySourceDeviceGuid ?? string.Empty
            : PrimaryKindSource?.LineDeviceGuid ?? string.Empty;

        public string LineDeviceLabel => IsPrimaryDescriptor
            ? PrimarySourceDeviceLabel ?? string.Empty
            : PrimaryKindSource?.LineDeviceLabel ?? string.Empty;

        public PadViewModel.MappedDeviceInfo LineDevice => Owner?.MappedDeviceOf(LineDeviceGuid);

        public bool IsLineAway => PadViewModel.IsAway(Owner, LineDeviceGuid);

        public bool IsLineUnassigned => IsPrimaryDescriptor
            ? string.IsNullOrEmpty(SourceDescriptor) && string.IsNullOrEmpty(PrimarySourceDeviceGuid)
            : PrimaryKindSource?.IsLineUnassigned ?? true;

        private string _lineLetter = string.Empty;

        public string LineLetter
        {
            get => _lineLetter;
            private set => SetProperty(ref _lineLetter, value ?? string.Empty);
        }

        /// <summary>Which way the first source pushes a stick's axis: a
        /// button, a hat's direction or a touchpad's press on a row with
        /// two directions. The same mark an added source carries.</summary>
        public string LineDirection
        {
            get
            {
                if (!IsBipolarAxisTarget || !IsPrimaryDescriptor) return string.Empty;
                if (!MappingSourceItem.IsButtonClass(StripLegacyPrefix(SourceDescriptor))) return string.Empty;
                return IsInverted ? MappingSourceItem.DirectionNegative : MappingSourceItem.DirectionPositive;
            }
        }

        /// <summary>The trim stick is the row's last source, and a row that
        /// trims has two at least, so the first is never it.</summary>
        public bool IsTrimStick => false;

        public bool LineInvert
        {
            get => IsInverted;
            set => IsInverted = value;
        }

        public bool LineHalf
        {
            get => IsHalfAxis;
            set => IsHalfAxis = value;
        }

        public bool LineBidirectional
        {
            get => IsBidirectional;
            set => IsBidirectional = value;
        }

        public int LineDeadZone
        {
            get => MappingDeadZone;
            set => MappingDeadZone = value;
        }

        /// <summary>Record beside the first source's input is the row's
        /// own Record, which takes a mode's keys in turn.</summary>
        public RelayCommand LineRecordCommand => ToggleRecordCommand;

        public RelayCommand LineClearCommand => ClearCommand;

        public RelayCommand LineRemoveCommand => RemoveFirstSourceCommand;

        public string LineRecordIcon => RecordButtonIcon;

        public string LineRecordText => RecordButtonText;

        public bool CanLineRecord => IsRecordable && !IsLineAway;

        /// <summary>Raises what a change of the row means to its line and
        /// to the list, after the change itself.</summary>
        protected override void OnPropertyChanged(PropertyChangedEventArgs e)
        {
            base.OnPropertyChanged(e);
            switch (e.PropertyName)
            {
                case nameof(IsInverted):
                    base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(LineInvert)));
                    base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(LineDirection)));
                    MarkListDirty();
                    break;
                case nameof(IsHalfAxis):
                    base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(LineHalf)));
                    break;
                case nameof(IsBidirectional):
                    base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(LineBidirectional)));
                    break;
                case nameof(MappingDeadZone):
                    base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(LineDeadZone)));
                    break;
                case nameof(IsDeadZoneApplicable):
                    base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(ShowsLineDeadZone)));
                    break;
                case nameof(IsRecording):
                    base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(IsRowRecording)));
                    break;
                case nameof(LiveLevel):
                    base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(LiveBarLevel)));
                    break;
                case nameof(RecordButtonIcon):
                    base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(LineRecordIcon)));
                    break;
                case nameof(RecordButtonText):
                    base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(LineRecordText)));
                    break;
                case nameof(SourceDescriptor):
                case nameof(NegSourceDescriptor):
                case nameof(SourceDisplayText):
                case nameof(SelectedInput):
                case nameof(PrimarySourceDeviceGuid):
                case nameof(PrimarySourceDeviceLabel):
                case nameof(IsPrimaryDescriptor):
                case nameof(PrimaryKindLabel):
                    RaiseLineFacts();
                    MarkListDirty();
                    break;
                case nameof(CombineMode):
                case nameof(IsMultiSource):
                    RefreshLineMarks();
                    base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(ShowsCombine)));
                    MarkListDirty();
                    break;
            }
        }

        /// <summary>What the first source's line shows, read again: its
        /// device may be another, or have connected or gone.</summary>
        internal void RaiseLineFacts()
        {
            base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(IsLineKeyMode)));
            base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(ShapingHolder)));
            base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(ShowsLineDeadZone)));
            base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(LineDeviceGuid)));
            base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(LineDeviceLabel)));
            base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(LineDevice)));
            base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(IsLineAway)));
            base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(CanLineRecord)));
            base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(IsLineUnassigned)));
            base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(LineDirection)));
        }

        /// <summary>A source of the row changed: one was added or removed,
        /// or one of them reads another input, device or mode.</summary>
        private void OnSourcesChangedForList()
        {
            RaiseLineFacts();
            RefreshLineMarks();
            MarkListDirty();
        }

        private void MarkListDirty() => Owner?.MarkListDirty();

        /// <summary>The marks that depend on a source's place among the
        /// row's sources: the trim stick, which is the last source of a row
        /// that trims, and each source's letter in a Custom formula.</summary>
        internal void RefreshLineMarks()
        {
            bool trims = ShouldShowTrimSettings;
            int last = ExtraSources.Count - 1;
            for (int i = 0; i <= last; i++)
            {
                var extra = ExtraSources[i];
                if (extra != null) extra.IsTrimStick = trims && i == last;
            }
            RefreshLineLetters();
        }

        /// <summary>Gives each source the letter a Custom formula knows it
        /// by, as the engine counts them (<see cref="WalkPositionalSlots"/>):
        /// a modifier takes no letter, and the opposite direction of a
        /// stick's axis shares the letter of the source it pairs with. The
        /// letters show only while the formula does.</summary>
        private void RefreshLineLetters()
        {
            bool show = ShouldShowCustomExpression;
            bool firstIsModifier = string.Equals(PrimaryKindSource?.Kind ?? "Direct", "InvertOnHold", StringComparison.Ordinal);
            bool firstExists = !string.IsNullOrEmpty(SourceDescriptor) || !IsPrimaryDescriptor
                || (IsCustomCombine && PrimarySourceExists);
            int count = 0;
            string first = string.Empty;
            if (firstExists && !firstIsModifier)
            {
                first = LetterAt(count);
                count++;
            }
            LineLetter = show ? first : string.Empty;

            int paired = FoldedPairIndex(firstExists, firstIsModifier);
            for (int i = 0; i < ExtraSources.Count; i++)
            {
                var extra = ExtraSources[i];
                if (extra == null) continue;
                string letter;
                if (i == paired) letter = first;
                else if (string.Equals(extra.Kind ?? "Direct", "InvertOnHold", StringComparison.Ordinal)) letter = string.Empty;
                else
                {
                    letter = LetterAt(count);
                    count++;
                }
                extra.LineLetter = show ? letter : string.Empty;
            }
        }

        private static string LetterAt(int index)
            => index >= 0 && index < 26 ? ((char)('a' + index)).ToString() : string.Empty;

        // ─────────────────────────────────────────────
        //  Loading and removing the first source
        // ─────────────────────────────────────────────

        /// <summary>The input with its Invert and Half flags written before
        /// it ("IH"), the form the row keeps its first source's input in.</summary>
        internal static string WithFlagPrefix(string descriptor, bool invert, bool halfAxis)
        {
            if (string.IsNullOrEmpty(descriptor)) return string.Empty;
            return (invert ? "I" : string.Empty) + (halfAxis ? "H" : string.Empty) + descriptor;
        }

        /// <summary>Takes a stored source as the row's first, or empties the
        /// first source for none. A source whose mode reads its own input
        /// (Direct, Toggle, Rapid Trigger) lands in the row's own fields.
        /// One whose mode reads keys lands on <see cref="PrimaryKindSource"/>
        /// and leaves the row's own fields empty. A load and the removal of
        /// the first source both come through here, so the source that
        /// moves up reads exactly as it would after a save and a load.</summary>
        internal void LoadFirstSource(MappingSource source, Func<string, string> deviceLabelOf)
        {
            bool readsKeys = source != null && !SourceEvaluator.IsDescriptorKind(source.Kind);
            if (source != null && !readsKeys)
            {
                LoadDescriptor(WithFlagPrefix(source.Descriptor, source.Invert, source.HalfAxis));
                // 50 for a stored 0: the engine reads the two alike, as
                // "use the global threshold", and the editor starts at 1.
                MappingDeadZone = source.DeadZone > 0 ? source.DeadZone : 50;
                IsBidirectional = source.Bidirectional;
                InvertOutput = source.InvertOutput;
                ParamAccel = source.ParamAccel;
                GyroSensitivity = source.GyroSensitivity > 0 ? source.GyroSensitivity : 1.0;
                MouseCursorSensitivity = source.MouseCursorSensitivity > 0 ? source.MouseCursorSensitivity : 1.0;
                IrPointerSensitivity = source.IrPointerSensitivity > 0 ? source.IrPointerSensitivity : 1.0;
                Sensitivity = source.Sensitivity > 0 ? source.Sensitivity : 1.0;
                PrimarySourceDeviceGuid = source.DeviceGuid ?? string.Empty;
                PrimarySourceDeviceLabel = deviceLabelOf?.Invoke(source.DeviceGuid) ?? string.Empty;
                LoadShapingAndGates(source);
                LoadPrimaryKind(source);
                return;
            }
            LoadDescriptor(string.Empty);
            PrimarySourceDeviceGuid = string.Empty;
            PrimarySourceDeviceLabel = string.Empty;
            MappingDeadZone = 50;
            IsBidirectional = false;
            InvertOutput = false;
            ParamAccel = 0;
            GyroSensitivity = 1.0;
            MouseCursorSensitivity = 1.0;
            IrPointerSensitivity = 1.0;
            Sensitivity = 1.0;
            // A mode that reads keys keeps these on its own holder.
            LoadShapingAndGates(null);
            LoadPrimaryKind(readsKeys ? source : null);
            // The mode's holder carries the source's device, and its line
            // names it, as an added source's line names its own.
            if (readsKeys && PrimaryKindSource != null)
                PrimaryKindSource.DeviceLabel = deviceLabelOf?.Invoke(source.DeviceGuid) ?? string.Empty;
        }

        private RelayCommand _removeFirstSourceCommand;

        /// <summary>Remove on the first source's line. The next source
        /// becomes the first, and with no next source the row is cleared.</summary>
        public RelayCommand RemoveFirstSourceCommand =>
            _removeFirstSourceCommand ??= new RelayCommand(RemoveFirstSource);

        private void RemoveFirstSource()
        {
            if (ExtraSources.Count == 0 || ExtraSources[0] == null)
            {
                if (ExtraSources.Count > 0) ExtraSources.RemoveAt(0);
                else ClearCommand.Execute(null);
                return;
            }
            var next = ExtraSources[0];
            var source = next.ToDomain();
            string label = next.DeviceLabel;
            // The row is between two states while the source moves. The
            // bracket a load uses keeps the mode gate from judging it then.
            BeginLoadRow();
            try
            {
                ExtraSources.RemoveAt(0);
                LoadFirstSource(source, _ => label);
                PrimarySourceExists = true;
            }
            finally { EndLoadRow(); }
            Owner?.ResolveFirstSourceName(this);
            OnSourcesChangedForList();
        }

        // ─────────────────────────────────────────────
        //  The row's cells
        // ─────────────────────────────────────────────

        private IReadOnlyList<RowCell> _cells = Array.Empty<RowCell>();

        /// <summary>The row's cells, one under each column of the list, in
        /// the columns' order.</summary>
        public IReadOnlyList<RowCell> Cells
        {
            get
            {
                Owner?.FlushList();
                return _cells;
            }
        }

        private IReadOnlyList<RowTag> _liveTags = Array.Empty<RowTag>();

        /// <summary>The tags whose level the live refresh reads.</summary>
        internal IReadOnlyList<RowTag> LiveTags => _liveTags;

        /// <summary>The closed row's end names how its sources combine:
        /// where the open row offers the Combine choice, and one is set.</summary>
        public bool ShowsCombine => IsMultiSource && !string.IsNullOrEmpty(CombineMode);

        /// <summary>A tag for each input that drives the row, in the order
        /// of its sources: the first source's, then each added source's. A
        /// source read through its own input is one tag. A source whose
        /// mode reads keys is a tag for each key that is set.</summary>
        internal List<RowTag> BuildTags()
        {
            var tags = new List<RowTag>();
            var s = Strings.Instance;
            if (IsPrimaryDescriptor)
            {
                string clean = StripLegacyPrefix(SourceDescriptor);
                if (clean.Length > 0)
                {
                    tags.Add(NewInputTag(this, PrimarySourceDeviceGuid,
                        _selectedInput?.DisplayName ?? _resolvedBaseName ?? clean, clean, LineDirection, PrimaryKindLabel,
                        PrimaryKindSource?.Kind));
                }
                string opposite = StripLegacyPrefix(NegSourceDescriptor);
                if (opposite.Length > 0)
                {
                    tags.Add(NewInputTag(this, PrimarySourceDeviceGuid, _resolvedNegText ?? opposite, opposite,
                        MappingSourceItem.DirectionNegative, PrimaryKindLabel, PrimaryKindSource?.Kind));
                }
            }
            else if (PrimaryKindSource != null)
            {
                AddKeyTags(tags, this, PrimaryKindSource, s);
            }
            foreach (var extra in ExtraSources)
            {
                if (extra == null) continue;
                if (extra.IsKindDescriptorless)
                {
                    AddKeyTags(tags, extra, extra, s);
                    continue;
                }
                if (string.IsNullOrEmpty(extra.Descriptor)) continue;
                tags.Add(NewInputTag(extra, extra.DeviceGuid, extra.SelectedInput?.DisplayName ?? extra.Descriptor,
                    extra.Descriptor, extra.LineDirection, extra.KindLabel, extra.Kind));
            }
            return tags;
        }

        private static RowTag NewInputTag(IMappingSourceLine line, string deviceGuid, string text, string descriptor,
            string direction, string modeName, string kind)
        {
            deviceGuid = (deviceGuid ?? string.Empty).ToLowerInvariant();
            var type = SourceCoercion.ClassifyDescriptor(descriptor);
            return new RowTag
            {
                Line = line,
                Text = text ?? string.Empty,
                Direction = direction ?? string.Empty,
                Descriptor = descriptor ?? string.Empty,
                DeviceGuid = deviceGuid,
                HasLevel = deviceGuid.Length > 0
                    && (type == SourceCoercion.SourceType.Axis || type == SourceCoercion.SourceType.Slider),
                PendingMode = string.Equals(kind ?? "Direct", "Direct", StringComparison.Ordinal) ? string.Empty : modeName ?? string.Empty,
            };
        }

        private static void AddKeyTags(List<RowTag> tags, IMappingSourceLine line, MappingSourceItem holder, Strings s)
        {
            string mode = holder.KindLabel;
            if (holder.UsesUpDownKeys)
            {
                AddKeyTag(tags, line, MappingSourceItem.ParamRecordTarget.Up, s.Pad_Row_Up, holder.ParamUp,
                    holder.ParamUpInputChoice?.DisplayName, holder.ParamUpReadDevice, mode);
                AddKeyTag(tags, line, MappingSourceItem.ParamRecordTarget.Down, s.Pad_Row_Down, holder.ParamDown,
                    holder.ParamDownInputChoice?.DisplayName, holder.ParamDownReadDevice, mode);
            }
            else if (holder.IsInvertOnHoldKind)
            {
                AddKeyTag(tags, line, MappingSourceItem.ParamRecordTarget.Modifier, s.Pad_Row_Modifier, holder.ParamModifier,
                    holder.ParamModifierInputChoice?.DisplayName, holder.ParamModifierReadDevice, mode);
            }
        }

        private static void AddKeyTag(List<RowTag> tags, IMappingSourceLine line, MappingSourceItem.ParamRecordTarget key,
            string word, string descriptor, string name, string deviceGuid, string mode)
        {
            if (string.IsNullOrEmpty(descriptor)) return;
            tags.Add(new RowTag
            {
                Line = line,
                Key = key,
                Word = word ?? string.Empty,
                Text = string.IsNullOrEmpty(name) ? descriptor : name,
                Descriptor = descriptor,
                DeviceGuid = (deviceGuid ?? string.Empty).ToLowerInvariant(),
                PendingMode = mode ?? string.Empty,
            });
        }

        /// <summary>Lays the row's tags under the list's columns. Each tag
        /// goes under the column of the device it is read on. Under a
        /// column, the last tag of a source names the source's mode when it
        /// is not Direct. The fold's cell counts the tags on the devices
        /// that are not connected.</summary>
        internal void SetCells(IReadOnlyList<ListColumn> columns, List<RowTag> tags)
        {
            var perColumn = new List<RowTag>[columns.Count];
            int away = 0;
            foreach (var tag in tags)
            {
                int at = -1;
                for (int i = 0; i < columns.Count; i++)
                {
                    if (columns[i].IsFold || !string.Equals(columns[i].Guid, tag.DeviceGuid, StringComparison.Ordinal)) continue;
                    at = i;
                    break;
                }
                if (at < 0) continue;
                (perColumn[at] ??= new List<RowTag>()).Add(tag);
                if (columns[at].IsAway) away++;
            }

            var cells = new RowCell[columns.Count];
            var live = new List<RowTag>();
            for (int i = 0; i < columns.Count; i++)
            {
                var own = perColumn[i];
                if (own != null)
                {
                    for (int t = 0; t < own.Count; t++)
                    {
                        bool lastOfItsSource = t == own.Count - 1 || !ReferenceEquals(own[t + 1].Line, own[t].Line);
                        own[t].Mode = lastOfItsSource ? own[t].PendingMode : string.Empty;
                        own[t].ShowsLevel = own[t].HasLevel && !columns[i].IsAway;
                        if (own[t].ShowsLevel) live.Add(own[t]);
                    }
                }
                cells[i] = new RowCell(this, columns[i], own, columns[i].IsFold ? away : 0);
            }
            _cells = cells;
            _liveTags = live;
            base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(Cells)));
        }

        private bool _isRecordTarget;

        /// <summary>The controller is recording for this row: its first
        /// source, one of its added sources, or a new one. Set by the
        /// controller's view model from the target it records.</summary>
        internal bool IsRecordTarget
        {
            get => _isRecordTarget;
            set
            {
                if (_isRecordTarget == value) return;
                _isRecordTarget = value;
                base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(IsRowRecording)));
            }
        }

        /// <summary>Something records for the row. Its line flashes while
        /// it does, whichever of its sources the input will land on.</summary>
        public bool IsRowRecording => IsRecording || _isRecordTarget;

        // ─────────────────────────────────────────────
        //  The value's bar
        // ─────────────────────────────────────────────

        /// <summary>The row's value rests in the middle of its range and
        /// moves either way: a stick's axis, a motion axis, or a custom
        /// Extended slot's stick axis.</summary>
        public bool IsCenteredValue
        {
            get
            {
                if (IsBipolarAxisTarget) return true;
                string t = TargetSettingName ?? string.Empty;
                if (t.StartsWith("RawAxis", StringComparison.Ordinal)) return Category != MappingCategory.Triggers;
                return t.StartsWith("Vr", StringComparison.Ordinal)
                    && (t.EndsWith("StickX", StringComparison.Ordinal) || t.EndsWith("StickY", StringComparison.Ordinal));
            }
        }

        private int _liveSign;

        /// <summary>Which way the row's live value stands from rest: -1, 0
        /// or 1. Set by the live refresh with <see cref="LiveLevel"/>.</summary>
        internal int LiveSign
        {
            get => _liveSign;
            set
            {
                if (_liveSign == value) return;
                _liveSign = value;
                base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(LiveBarLevel)));
            }
        }

        /// <summary>Where the row's live value stands on its bar, 0 to 1. A
        /// centered value rests in the middle of the bar and moves either
        /// way from it. Any other fills from the start, as
        /// <see cref="LiveLevel"/> does.</summary>
        public double LiveBarLevel
            => IsCenteredValue ? Math.Clamp(0.5 + _liveSign * LiveLevel / 2, 0, 1) : LiveLevel;
    }
}
