using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PadForge.Resources.Strings;
using PadForge.Services;

namespace PadForge.ViewModels
{
    /// <summary>One paired device under Answer From Another Device.</summary>
    public sealed class AnswerDeviceItem
    {
        internal AnswerDeviceItem(string id, string name, string line, Action<string> remove)
        {
            Id = id;
            Name = name;
            Line = line;
            RemoveCommand = new RelayCommand(() => remove?.Invoke(id));
        }

        public string Id { get; }
        public string Name { get; }
        public string Line { get; }
        public string RemoveId => "Answer_Remove_" + Id;
        public RelayCommand RemoveCommand { get; }

        /// <summary>What a screen reader says for the item in a list.</summary>
        public override string ToString() => Name + ". " + Line;
    }

    /// <summary>
    /// Answer From Another Device (#600), the row under the Assistants
    /// card's More: Pair a Device…, which opens the pairing window, and the
    /// devices paired, each with Remove. It shows only while a web assistant
    /// is allowed or a network key exists (<see cref="WebAnswer.ShowsRow"/>).
    /// Every control is the user's alone.
    /// </summary>
    public sealed class AnswerAccessViewModel : ObservableObject
    {
        private readonly WebAnswer _answers;
        private readonly Func<DateTimeOffset> _now;

        /// <summary>Opens the pairing window. The window gives it, and a
        /// test gives its own.</summary>
        internal Action OpenPairing { get; set; }

        /// <summary>Runs an action on the interface's thread.</summary>
        internal Action<Action> Post { get; set; } = a => a();

        internal AnswerAccessViewModel(WebAnswer answers, Func<DateTimeOffset> now = null)
        {
            _answers = answers;
            _now = now ?? (() => DateTimeOffset.Now);
            PairCommand = new RelayCommand(() => OpenPairing?.Invoke());
            _answers.StateChanged += (_, _) => Post(Refresh);
            Refresh();
        }

        public RelayCommand PairCommand { get; }

        private bool _showsRow;

        /// <summary>Whether the row shows.</summary>
        public bool ShowsRow { get => _showsRow; private set => SetProperty(ref _showsRow, value); }

        public ObservableCollection<AnswerDeviceItem> Devices { get; } = new();

        public bool HasDevices => Devices.Count > 0;

        /// <summary>Reads the row's rule and the devices again: on a change
        /// of the web grants or of the page's state.</summary>
        internal void Refresh()
        {
            ShowsRow = _answers.RowShows();
            var s = Strings.Instance;
            var culture = CultureInfo.CurrentCulture;
            var items = _answers.Devices.Select(d => new AnswerDeviceItem(d.Id, d.Name, Line(d, s, culture), id => _answers.Remove(id))).ToList();
            if (!Devices.Select(d => d.Id + d.Name + d.Line).SequenceEqual(items.Select(d => d.Id + d.Name + d.Line)))
            {
                Devices.Clear();
                foreach (var item in items) Devices.Add(item);
                OnPropertyChanged(nameof(HasDevices));
            }
        }

        /// <summary>"Paired today at 11:40 AM. Last answered at 12:01 PM."</summary>
        private string Line(AnswerDevice d, Strings s, CultureInfo culture)
        {
            string line = string.Format(culture, s.Answer_Device_Paired_Format, When(d.Paired, s, culture, today: s.Answer_When_Today_Format));
            if (d.LastAnswered is { } answered)
                line += " " + string.Format(culture, s.Answer_Device_Answered_Format, When(answered, s, culture, today: s.Answer_When_At_Format));
            return line;
        }

        private string When(DateTimeOffset when, Strings s, CultureInfo culture, string today)
        {
            var local = when.ToLocalTime();
            string time = local.ToString("t", culture);
            return local.Date == _now().ToLocalTime().Date
                ? string.Format(culture, today, time)
                : string.Format(culture, s.Answer_When_Day_Format, local.ToString("d", culture), time);
        }

        /// <summary>The row's controls as catalog entries, each the user's
        /// alone: Search and a screen reader find them, and no program presses
        /// one. None reads a code or a key.</summary>
        internal IReadOnlyList<PadForge.Services.Catalog.LiveItem> CatalogItems()
        {
            var items = new List<PadForge.Services.Catalog.LiveItem>();
            if (!ShowsRow) return items;
            var s = Strings.Instance;
            string where = s.Extras_Automation_Title + ", " + s.Extras_Automation_Card_Assistants;
            const PadForge.Common.ReachClass Own = PadForge.Common.ReachClass.UsersOwn;
            items.Add(new("answer/pair", PadForge.Services.Catalog.LiveKind.Action, s.Answer_Label + ": " + s.Answer_Pair, Own, PadForge.Common.ReachReason.Network)
            { Opens = nameof(Views.AnswerPairDialog), Where = where });
            foreach (var d in Devices)
                items.Add(new("answer/device/" + d.Id, PadForge.Services.Catalog.LiveKind.Action, d.Name + ": " + s.Common_Remove, Own)
                { Read = () => d.Line, Where = where });
            return items;
        }
    }
}
