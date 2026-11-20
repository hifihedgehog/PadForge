using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;

namespace PadForge.ViewModels
{
    /// <summary>What a column of the mappings list stands for.</summary>
    public enum ListColumnKind
    {
        /// <summary>A device assigned to the controller.</summary>
        Device,
        /// <summary>Sources that name no device, so any device drives them.</summary>
        AnyDevice,
        /// <summary>A device a source still names that the controller no
        /// longer has.</summary>
        Gone,
        /// <summary>The devices that are not connected, folded into one
        /// column while the list has no room for a column each.</summary>
        Fold,
    }

    /// <summary>
    /// One column of the mappings list: the devices that drive the
    /// controller stand side by side, named once in the list's head, and a
    /// row holds a cell under each (<see cref="RowCell"/>).
    /// </summary>
    public sealed class ListColumn : ObservableObject
    {
        /// <summary>The least width of a device's column. Under it the
        /// devices that are not connected fold, and past that the cells
        /// scroll sideways.</summary>
        public const double MinWidth = 180;

        /// <summary>The width of the folded column.</summary>
        public const double FoldWidth = 150;

        internal ListColumn(ListColumnKind kind, string guid, PadViewModel.MappedDeviceInfo device)
        {
            Kind = kind;
            Guid = (guid ?? string.Empty).ToLowerInvariant();
            Device = device;
        }

        public ListColumnKind Kind { get; }

        /// <summary>The device's id as sources store it, in lower case.
        /// Empty for the any-device column and for the fold.</summary>
        public string Guid { get; }

        /// <summary>The assigned device, which lights the column's dot
        /// while it sends. Null for every other kind of column.</summary>
        public PadViewModel.MappedDeviceInfo Device { get; }

        public bool IsFold => Kind == ListColumnKind.Fold;

        /// <summary>The width the column keeps whatever the list's: the
        /// fold's. Not a number for a column that shares the room.</summary>
        public double FixedWidth => IsFold ? FoldWidth : double.NaN;

        private string _name = string.Empty;

        /// <summary>The device's name, "(Any device)", or how many devices
        /// the fold holds.</summary>
        public string Name
        {
            get => _name;
            internal set => SetProperty(ref _name, value ?? string.Empty);
        }

        /// <summary>The column's head is an item of the list's head, and a
        /// screen reader reads an item by its ToString()
        /// (ItemAutomationPeer.GetNameCore): the column's name.</summary>
        public override string ToString() => _name;

        private string _typeGlyph = string.Empty;

        public string TypeGlyph
        {
            get => _typeGlyph;
            internal set => SetProperty(ref _typeGlyph, value ?? string.Empty);
        }

        private System.Windows.Media.Geometry _typeShape;

        /// <summary>A wheel's or a joystick's drawing in place of the glyph,
        /// or null.</summary>
        public System.Windows.Media.Geometry TypeShape
        {
            get => _typeShape;
            internal set => SetProperty(ref _typeShape, value);
        }

        private string _typeName = string.Empty;

        /// <summary>The kind of device ("Gamepad", "Wheel"), under its name.</summary>
        public string TypeName
        {
            get => _typeName;
            internal set => SetProperty(ref _typeName, value ?? string.Empty);
        }

        private bool _isAway;

        /// <summary>The device is not connected, or is no longer one of the
        /// controller's. Its column is dimmed and shows no live level.</summary>
        public bool IsAway
        {
            get => _isAway;
            internal set
            {
                if (SetProperty(ref _isAway, value)) OnPropertyChanged(nameof(ShowsType));
            }
        }

        /// <summary>The second line names the kind of device. An away
        /// device's says it is not connected, and the any-device column and
        /// the fold have none.</summary>
        public bool ShowsType => Kind == ListColumnKind.Device && !_isAway;

        private bool _isShown = true;

        /// <summary>Whether the column is drawn: an away device's is not
        /// while the away devices are folded, and the fold is only then.</summary>
        public bool IsShown
        {
            get => _isShown;
            internal set => SetProperty(ref _isShown, value);
        }
    }

    /// <summary>
    /// One input of one device in one row: the tag a cell draws. A source
    /// read through its own input is one tag. A source of a mode that reads
    /// keys is a tag for each key, with the key's word before its name.
    /// </summary>
    public sealed class RowTag : ObservableObject
    {
        /// <summary>"Up", "Down" or "Modifier" for a key of a mode that
        /// reads keys. Empty for a source's own input.</summary>
        public string Word { get; init; } = string.Empty;

        /// <summary>The input's name.</summary>
        public string Text { get; init; } = string.Empty;

        /// <summary>Which way a button pushes a stick's axis. Empty where
        /// that says nothing.</summary>
        public string Direction { get; init; } = string.Empty;

        private string _mode = string.Empty;

        /// <summary>The source's mode, after the last of its tags in the
        /// cell, when it is not Direct.</summary>
        public string Mode
        {
            get => _mode;
            internal set => SetProperty(ref _mode, value ?? string.Empty);
        }

        /// <summary>The source's mode when it is not Direct, for the tag
        /// that turns out to be the source's last under its column.</summary>
        internal string PendingMode { get; init; } = string.Empty;

        /// <summary>The source the tag belongs to: the row for its first
        /// source, else one of its added sources. A click on the tag records
        /// that source again.</summary>
        public IMappingSourceLine Line { get; init; }

        /// <summary>Which key of the source the tag is, or null for the
        /// source's own input.</summary>
        public MappingSourceItem.ParamRecordTarget? Key { get; init; }

        /// <summary>The input as the device's state names it, with no flag
        /// before it. The live level is read by it.</summary>
        internal string Descriptor { get; init; } = string.Empty;

        /// <summary>The device the input is read on.</summary>
        internal string DeviceGuid { get; init; } = string.Empty;

        /// <summary>The input has a level to show: an axis or a slider of a
        /// device, as against a button or a key.</summary>
        public bool HasLevel { get; init; }

        /// <summary>The level is drawn: the input has one and its device is
        /// connected.</summary>
        public bool ShowsLevel { get; internal set; }

        private bool _isCentered;

        /// <summary>The input rests in the middle of its range, as a
        /// stick's axis does, so its level is drawn from the middle.</summary>
        public bool IsCentered
        {
            get => _isCentered;
            internal set => SetProperty(ref _isCentered, value);
        }

        /// <summary>Whether <see cref="IsCentered"/> has been worked out
        /// from the device, which the first live refresh does.</summary>
        internal bool CenterKnown { get; set; }

        /// <summary>Whether the live refresh has found where the tag's
        /// input is in a device's state. It does so once.</summary>
        internal bool InputFound { get; set; }

        /// <summary>The device the level is read on, or null for one the
        /// device list did not hold. Found again whenever the device list
        /// changed (<see cref="DevicesSeen"/>, <see cref="DevicesVersion"/>).</summary>
        internal PadForge.Engine.Data.UserDevice Device { get; set; }

        /// <summary>The device list <see cref="Device"/> was found in.</summary>
        internal PadForge.Common.Input.DeviceList DevicesSeen { get; set; }

        /// <summary>That list's change count when <see cref="Device"/> was
        /// found.</summary>
        internal int DevicesVersion { get; set; }

        /// <summary>The input's index among the state's axes, or among its
        /// sliders when <see cref="InputIsSlider"/> is set. -1 reads 0.</summary>
        internal int InputIndex { get; set; } = -1;

        /// <summary>The input is a slider, as against an axis.</summary>
        internal bool InputIsSlider { get; set; }

        private double _level;

        /// <summary>Where the input stands in its range, 0 to 1. A centered
        /// input rests at one half.</summary>
        public double Level
        {
            get => _level;
            internal set => SetProperty(ref _level, value);
        }

        private string _levelText = string.Empty;

        public string LevelText
        {
            get => _levelText;
            internal set => SetProperty(ref _levelText, value ?? string.Empty);
        }

        /// <summary>What a screen reader says for the tag, in the order the
        /// tag shows it: the key's word, the input, which way it pushes and
        /// the mode. The tag is an item of its cell, and an item is read by
        /// its ToString() (ItemAutomationPeer.GetNameCore). Without this a
        /// key's tag read its first text, the word "Up" with no key. The
        /// live level is left out: it changes as the input moves.</summary>
        public override string ToString()
        {
            var said = new System.Text.StringBuilder();
            foreach (string part in new[] { Word, Text, Direction, _mode })
            {
                if (string.IsNullOrEmpty(part)) continue;
                if (said.Length > 0) said.Append(' ');
                said.Append(part);
            }
            return said.ToString();
        }
    }

    /// <summary>One device's part in one row: a tag for each of that
    /// device's inputs that drives the row's output. The fold's cell counts
    /// the sources on the devices it holds.</summary>
    public sealed class RowCell
    {
        internal RowCell(MappingItem row, ListColumn column, IReadOnlyList<RowTag> tags, int count)
        {
            Row = row;
            Column = column;
            Tags = tags ?? Array.Empty<RowTag>();
            Count = count;
        }

        /// <summary>The row the cell is in. A click on the cell records for
        /// it.</summary>
        public MappingItem Row { get; }

        public ListColumn Column { get; }

        public IReadOnlyList<RowTag> Tags { get; }

        /// <summary>In the fold's cell, how many inputs of the folded
        /// devices drive the row.</summary>
        public int Count { get; }

        public bool HasCount => Column.IsFold && Count > 0;

        /// <summary>What a screen reader says for the cell: the device its
        /// column stands for. The inputs are the items inside it. The cell
        /// is an item of its row, and an item is read by its ToString()
        /// (ItemAutomationPeer.GetNameCore). Without this a cell with an
        /// input read that input with no device, and a cell with none read
        /// its class's name.</summary>
        public override string ToString() => Column.Name;
    }

    /// <summary>
    /// What a source's line in an open row reads and writes. A row's first
    /// source is the row itself and every other one is an added source, and
    /// both offer the same line: its device, its mode, its input with Record
    /// and Clear, Invert, Half and Bidirectional, and Remove. One template
    /// draws either, so the two cannot drift apart.
    /// </summary>
    public interface IMappingSourceLine : System.ComponentModel.INotifyPropertyChanged
    {
        /// <summary>The output the source drives.</summary>
        MappingItem Row { get; }

        /// <summary>Where the source's mode and its keys are kept.</summary>
        MappingSourceItem ModeHolder { get; }

        /// <summary>The id of the device the source reads, empty for any.</summary>
        string LineDeviceGuid { get; }

        /// <summary>That device's name.</summary>
        string LineDeviceLabel { get; }

        /// <summary>The assigned device the source reads, or null.</summary>
        PadViewModel.MappedDeviceInfo LineDevice { get; }

        /// <summary>The source reads a device that is not connected.</summary>
        bool IsLineAway { get; }

        /// <summary>No input is picked and no device is named.</summary>
        bool IsLineUnassigned { get; }

        /// <summary>The letter a Custom formula knows the source by, or
        /// empty for a source the formula does not count.</summary>
        string LineLetter { get; }

        /// <summary>The mark after the device's name: which way a button
        /// pushes a stick's axis.</summary>
        string LineDirection { get; }

        /// <summary>The source is the stick that trims a trigger.</summary>
        bool IsTrimStick { get; }

        InputChoice SelectedInput { get; set; }

        bool LineInvert { get; set; }

        bool LineHalf { get; set; }

        bool LineBidirectional { get; set; }

        int LineDeadZone { get; set; }

        bool IsInvertApplicable { get; }

        bool IsHalfAxisApplicable { get; }

        bool IsDeadZoneApplicable { get; }

        /// <summary>The mode reads keys of its own, so the source has no
        /// input to pick and none of the three switches.</summary>
        bool IsLineKeyMode { get; }

        /// <summary>The line shows the deadzone past which an axis presses a
        /// button: the input presses at one, and the mode reads the input.</summary>
        bool ShowsLineDeadZone { get; }

        /// <summary>Record beside the input: the input itself, or the keys
        /// of a mode that reads keys, in turn.</summary>
        CommunityToolkit.Mvvm.Input.RelayCommand LineRecordCommand { get; }

        /// <summary>Clear beside the input: empties it and keeps the source.</summary>
        CommunityToolkit.Mvvm.Input.RelayCommand LineClearCommand { get; }

        /// <summary>Remove at the line's end: takes the source off the row.</summary>
        CommunityToolkit.Mvvm.Input.RelayCommand LineRemoveCommand { get; }

        string LineRecordIcon { get; }

        string LineRecordText { get; }

        /// <summary>Record is offered: not for a source on a device that is
        /// not connected.</summary>
        bool CanLineRecord { get; }
    }
}
