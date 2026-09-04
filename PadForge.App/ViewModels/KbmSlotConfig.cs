using System.Collections.ObjectModel;
using System.Xml.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace PadForge.ViewModels
{
    /// <summary>Localized entry for the SOCD mode dropdown. Same shape as
    /// MappingItem.CombineModeOption: engine-stable Value plus a layman
    /// Name and one-line Description.</summary>
    public sealed class SocdModeOption
    {
        public string Value { get; set; } = "";
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
    }

    /// <summary>Which half of a Keyboard + Mouse slot a mapping row drives
    /// (#408). The classifier lives on PadViewModel as KbmSurfaceOf, reading
    /// the same descriptor vocabulary the engine dispatches on.</summary>
    public enum KbmSurfaceKind
    {
        Keyboard,
        Mouse,
    }

    /// <summary>One entry in the Preset chip's surface dropdown, the
    /// Keyboard + Mouse counterpart of the HIDMaestro profile a gamepad slot
    /// picks there. No Description: a chip shows one short label, which is
    /// why the profile combo beside it is a flat DisplayMemberPath too.</summary>
    public sealed class KbmSurfaceOption
    {
        public string Value { get; set; } = "";
        public string Name { get; set; } = "";
    }

    /// <summary>Pickable keyboard key for the SOCD pair editor. The list
    /// mirrors the KBM mapping targets built by
    /// PadViewModel.InitializeKeyboardMouseMappings (same keys, same
    /// localized labels), minus mouse buttons and axes.</summary>
    public sealed class SocdKeyOption
    {
        public int Vk { get; set; }
        public string Label { get; set; } = "";
    }

    /// <summary>One editable SOCD key pair. VK edits reserialize the
    /// owning config's SocdPairs string.</summary>
    public sealed class SocdPairItem : ObservableObject
    {
        private readonly KbmSlotConfig _owner;

        public SocdPairItem(KbmSlotConfig owner, int vkA, int vkB)
        {
            _owner = owner;
            _vkA = vkA;
            _vkB = vkB;
        }

        private int _vkA;
        public int VkA
        {
            get => _vkA;
            set
            {
                if (SetProperty(ref _vkA, value))
                    _owner?.OnPairItemChanged();
            }
        }

        private int _vkB;
        public int VkB
        {
            get => _vkB;
            set
            {
                if (SetProperty(ref _vkB, value))
                    _owner?.OnPairItemChanged();
            }
        }

        /// <summary>Items source for both key ComboBoxes.</summary>
        public System.Collections.Generic.IReadOnlyList<SocdKeyOption> KeyOptions
            => KbmSlotConfig.GetKeyOptions();
    }

    /// <summary>
    /// Per-slot keyboard + mouse output configuration (discussion #205,
    /// SOCD / Snap Tap). Same per-slot lane as MidiSlotConfig: lives on
    /// PadViewModel, referenced into the engine by InputService, persisted
    /// through AppSettings / profiles as KbmSlotConfigData.
    /// </summary>
    public class KbmSlotConfig : ObservableObject
    {
        /// <summary>W/S, A/D, Up/Down, Left/Right as decimal VK pairs.</summary>
        public const string DefaultSocdPairs = "87:83|65:68|38:40|37:39";

        public KbmSlotConfig()
        {
            RebuildPairItems();
        }

        /// <summary>The surface mode's default and the value every file
        /// written before #408 reads as: the slot drives both halves, which
        /// is what Keyboard + Mouse has always meant.</summary>
        public const string DefaultSurfaces = "Both";

        private string _surfaces = DefaultSurfaces;
        /// <summary>Which halves of the slot are live: "Both",
        /// "KeyboardOnly" or "MouseOnly" (#408, @Xaklse on Discord).
        /// Stored locale-stable like <see cref="SocdMode"/>, and the
        /// dropdown maps it through <see cref="KbmSurfaceOption"/>.
        ///
        /// <para>Turning a half off HIDES its rows and stops the slot from
        /// dispatching them. It does NOT delete the mappings: they stay on the
        /// PadSetting so turning the half back on restores the user's work,
        /// which is the same parked-state contract the settings load honors
        /// for a device a profile does not assign.</para>
        ///
        /// <para>The gate covers the slot's own keyboard and mouse output, the
        /// KbmRawState the Keyboard + Mouse virtual controller submits. It does
        /// NOT cover macros, which reach the OS through SendInput on every slot
        /// type including gamepads, and are therefore not this slot's
        /// output.</para></summary>
        public string Surfaces
        {
            get => _surfaces;
            set
            {
                string v = value;
                if (v != "KeyboardOnly" && v != "MouseOnly") v = DefaultSurfaces;
                if (!SetProperty(ref _surfaces, v)) return;
                OnPropertyChanged(nameof(KeyboardEnabled));
                OnPropertyChanged(nameof(MouseEnabled));
            }
        }

        /// <summary>Whether the keyboard half is live. Read by the mapping
        /// table and by the engine's Keyboard + Mouse dispatch.</summary>
        public bool KeyboardEnabled => _surfaces != "MouseOnly";

        /// <summary>Whether the mouse half is live.</summary>
        public bool MouseEnabled => _surfaces != "KeyboardOnly";

        /// <summary>Whether a row of the given half should be shown and
        /// dispatched under the current mode.</summary>
        public bool Allows(KbmSurfaceKind kind)
            => kind switch
            {
                KbmSurfaceKind.Keyboard => KeyboardEnabled,
                KbmSurfaceKind.Mouse => MouseEnabled,
                // A member added without a case here would otherwise take the
                // mouse branch in silence. Fail loud instead: KbmSurfaceOf and
                // RowMatchesSurface both need the same edit.
                _ => throw new System.ArgumentOutOfRangeException(nameof(kind)),
            };

        private string _socdMode = "Off";
        /// <summary>SOCD mode name: "Off", "LastWins", "Neutral", "FirstWins".
        /// Stored locale-stable; the dropdown maps it through SocdModeOption.</summary>
        public string SocdMode
        {
            get => _socdMode;
            set => SetProperty(ref _socdMode, string.IsNullOrEmpty(value) ? "Off" : value);
        }

        private string _socdPairs = DefaultSocdPairs;
        /// <summary>Pipe-separated "vkA:vkB" decimal pairs. The persisted
        /// form; SocdPairItems is its editable projection.</summary>
        public string SocdPairs
        {
            get => _socdPairs;
            set
            {
                if (!SetProperty(ref _socdPairs, value ?? string.Empty)) return;
                if (!_syncingPairs) RebuildPairItems();
            }
        }

        /// <summary>Editable projection of <see cref="SocdPairs"/> for the
        /// pair editor's ItemsControl.</summary>
        public ObservableCollection<SocdPairItem> SocdPairItems { get; } = new();

        private bool _syncingPairs;

        /// <summary>Reserializes the pair items into SocdPairs. Called by
        /// item VK setters and the add / remove commands.</summary>
        internal void OnPairItemChanged()
        {
            _syncingPairs = true;
            var sb = new System.Text.StringBuilder();
            foreach (var p in SocdPairItems)
            {
                if (sb.Length > 0) sb.Append('|');
                sb.Append(p.VkA).Append(':').Append(p.VkB);
            }
            SocdPairs = sb.ToString();
            _syncingPairs = false;
        }

        private void RebuildPairItems()
        {
            SocdPairItems.Clear();
            if (string.IsNullOrEmpty(_socdPairs)) return;
            foreach (var token in _socdPairs.Split('|', System.StringSplitOptions.RemoveEmptyEntries))
            {
                int colon = token.IndexOf(':');
                if (colon <= 0 || colon >= token.Length - 1) continue;
                if (!int.TryParse(token.AsSpan(0, colon), out int vkA)) continue;
                if (!int.TryParse(token.AsSpan(colon + 1), out int vkB)) continue;
                SocdPairItems.Add(new SocdPairItem(this, vkA, vkB));
            }
        }

        private RelayCommand _addSocdPairCommand;
        /// <summary>Appends a fresh W/S pair for the user to retarget.</summary>
        public RelayCommand AddSocdPairCommand =>
            _addSocdPairCommand ??= new RelayCommand(() =>
            {
                SocdPairItems.Add(new SocdPairItem(this, 0x57, 0x53));
                OnPairItemChanged();
            });

        private RelayCommand<SocdPairItem> _removeSocdPairCommand;
        public RelayCommand<SocdPairItem> RemoveSocdPairCommand =>
            _removeSocdPairCommand ??= new RelayCommand<SocdPairItem>(item =>
            {
                if (item == null) return;
                SocdPairItems.Remove(item);
                OnPairItemChanged();
            });

        private RelayCommand _resetSocdCommand;
        /// <summary>The SOCD card's Reset All. It resets the SOCD card, and
        /// nothing else. It used to call ResetToDefaults, which also rewrote
        /// the surface mode that lives in the Preset chip a tab away, so a
        /// click meant to clear key pairs silently turned a Mouse Only slot
        /// back into a Keyboard + Mouse one (#408).</summary>
        public RelayCommand ResetSocdCommand =>
            _resetSocdCommand ??= new RelayCommand(ResetSocdToDefaults);

        /// <summary>SOCD mode and pairs only. ResetToDefaults is the whole-slot
        /// reset and still includes the surface mode.</summary>
        public void ResetSocdToDefaults()
        {
            SocdMode = "Off";
            SocdPairs = DefaultSocdPairs;
        }

        private RelayCommand _resetSocdModeCommand;
        /// <summary>Mode-only reset for the card's mode row. The card's
        /// Reset All (ResetSocdCommand) resets the pairs too.</summary>
        public RelayCommand ResetSocdModeCommand =>
            _resetSocdModeCommand ??= new RelayCommand(() => SocdMode = "Off");

        /// <summary>Resets every field to its fresh-install default IN
        /// PLACE, preserving the instance (external PropertyChanged
        /// subscribers survive; same invariant as MidiSlotConfig).</summary>
        public void ResetToDefaults()
        {
            Surfaces = DefaultSurfaces;
            ResetSocdToDefaults();
        }

        private static KbmSurfaceOption[] _surfaceOptionsCache;
        private static int _surfaceOptionsCacheCulture;

        public System.Collections.Generic.IReadOnlyList<KbmSurfaceOption> AvailableKbmSurfaces
            => GetSurfaceOptions();

        private static KbmSurfaceOption[] GetSurfaceOptions()
        {
            int culture = System.Globalization.CultureInfo.CurrentUICulture.LCID;
            var cached = _surfaceOptionsCache;
            if (cached != null && _surfaceOptionsCacheCulture == culture)
                return cached;

            var s = PadForge.Resources.Strings.Strings.Instance;
            var arr = new[]
            {
                new KbmSurfaceOption { Value = "Both",         Name = s.Pad_Kbm_Surfaces_Both_Name },
                new KbmSurfaceOption { Value = "MouseOnly",    Name = s.Pad_Kbm_Surfaces_MouseOnly_Name },
                new KbmSurfaceOption { Value = "KeyboardOnly", Name = s.Pad_Kbm_Surfaces_KeyboardOnly_Name },
            };
            _surfaceOptionsCache = arr;
            _surfaceOptionsCacheCulture = culture;
            return arr;
        }

        // ── Dropdown item sources ──
        // Culture-stamped caches, same idiom as
        // MappingItem.GetAvailableCombineModes: the WPF bindings re-read
        // these properties freely, so the arrays are built once per culture.

        private static SocdModeOption[] _modeOptionsCache;
        private static int _modeOptionsCacheCulture;

        public System.Collections.Generic.IReadOnlyList<SocdModeOption> AvailableSocdModes
            => GetModeOptions();

        private static SocdModeOption[] GetModeOptions()
        {
            int culture = System.Globalization.CultureInfo.CurrentUICulture.LCID;
            var cached = _modeOptionsCache;
            if (cached != null && _modeOptionsCacheCulture == culture)
                return cached;

            var s = PadForge.Resources.Strings.Strings.Instance;
            var arr = new[]
            {
                new SocdModeOption { Value = "Off",       Name = s.Pad_Kbm_Socd_Mode_Off_Name,       Description = s.Pad_Kbm_Socd_Mode_Off_Description },
                new SocdModeOption { Value = "LastWins",  Name = s.Pad_Kbm_Socd_Mode_LastWins_Name,  Description = s.Pad_Kbm_Socd_Mode_LastWins_Description },
                new SocdModeOption { Value = "Neutral",   Name = s.Pad_Kbm_Socd_Mode_Neutral_Name,   Description = s.Pad_Kbm_Socd_Mode_Neutral_Description },
                new SocdModeOption { Value = "FirstWins", Name = s.Pad_Kbm_Socd_Mode_FirstWins_Name, Description = s.Pad_Kbm_Socd_Mode_FirstWins_Description },
            };
            _modeOptionsCache = arr;
            _modeOptionsCacheCulture = culture;
            return arr;
        }

        private static SocdKeyOption[] _keyOptionsCache;
        private static int _keyOptionsCacheCulture;

        /// <summary>Keyboard keys pickable in a SOCD pair. Same key set and
        /// localized labels as PadViewModel.InitializeKeyboardMouseMappings
        /// builds for the KBM mapping targets.</summary>
        public static System.Collections.Generic.IReadOnlyList<SocdKeyOption> GetKeyOptions()
        {
            int culture = System.Globalization.CultureInfo.CurrentUICulture.LCID;
            var cached = _keyOptionsCache;
            if (cached != null && _keyOptionsCacheCulture == culture)
                return cached;

            var s = PadForge.Resources.Strings.Strings.Instance;
            var list = new System.Collections.Generic.List<SocdKeyOption>(110);
            void Add(string label, int vk) => list.Add(new SocdKeyOption { Vk = vk, Label = label });

            for (int i = 0; i < 26; i++)
                Add(((char)('A' + i)).ToString(), 0x41 + i);
            for (int i = 0; i <= 9; i++)
                Add(i.ToString(), 0x30 + i);
            for (int i = 1; i <= 12; i++)
                Add($"F{i}", 0x6F + i);

            Add(s.Key_LeftShift, 0xA0);
            Add(s.Key_RightShift, 0xA1);
            Add(s.Key_LeftCtrl, 0xA2);
            Add(s.Key_RightCtrl, 0xA3);
            Add(s.Key_LeftAlt, 0xA4);
            Add(s.Key_RightAlt, 0xA5);

            Add(s.Key_Space, 0x20);
            Add(s.Key_Enter, 0x0D);
            Add(s.Key_Escape, 0x1B);
            Add(s.Key_Tab, 0x09);
            Add(s.Key_Backspace, 0x08);
            Add(s.Key_CapsLock, 0x14);
            Add(s.Key_NumLock, 0x90);
            Add(s.Key_ScrollLock, 0x91);
            Add(s.Key_PrintScreen, 0x2C);
            Add(s.Key_Pause, 0x13);

            Add(s.Key_Up, 0x26);
            Add(s.Key_Down, 0x28);
            Add(s.Key_Left, 0x25);
            Add(s.Key_Right, 0x27);
            Add(s.Key_Home, 0x24);
            Add(s.Key_End, 0x23);
            Add(s.Key_PageUp, 0x21);
            Add(s.Key_PageDown, 0x22);
            Add(s.Key_Insert, 0x2D);
            Add(s.Key_Delete, 0x2E);

            Add(";", 0xBA);
            Add("=", 0xBB);
            Add(",", 0xBC);
            Add("-", 0xBD);
            Add(".", 0xBE);
            Add("/", 0xBF);
            Add("`", 0xC0);
            Add("[", 0xDB);
            Add("\\", 0xDC);
            Add("]", 0xDD);
            Add("'", 0xDE);

            for (int i = 0; i <= 9; i++)
                Add($"Num {i}", 0x60 + i);
            Add("Num *", 0x6A);
            Add("Num +", 0x6B);
            Add("Num -", 0x6D);
            Add("Num .", 0x6E);
            Add("Num /", 0x6F);

            var arr = list.ToArray();
            _keyOptionsCache = arr;
            _keyOptionsCacheCulture = culture;
            return arr;
        }
    }

    /// <summary>XML-serializable snapshot of a KBM slot's configuration.</summary>
    public class KbmSlotConfigData
    {
        [XmlAttribute] public int SlotIndex { get; set; }
        /// <summary>#408. An absent attribute leaves this initializer, so
        /// every file and profile written before the surface mode existed
        /// reads as Both and behaves exactly as it did.</summary>
        [XmlAttribute] public string Surfaces { get; set; } = KbmSlotConfig.DefaultSurfaces;
        [XmlAttribute] public string SocdMode { get; set; } = "Off";
        [XmlAttribute] public string SocdPairs { get; set; } = KbmSlotConfig.DefaultSocdPairs;
    }
}
