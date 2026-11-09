using System.Collections.Generic;
using CommunityToolkit.Mvvm.Input;
using PadForge.Engine.Menus;
using PadForge.Resources.Strings;

namespace PadForge.ViewModels
{
    /// <summary>
    /// The Hotbar style: Steam Input's hotbar as a menu. Four controls run it,
    /// Show or Hide, Previous, Next and Fire, each any button-family input on
    /// the slot's devices. A control left empty reads the opener's direction
    /// (<see cref="MenuHotbar.DefaultControl"/>): Down, Left, Right and Up.
    /// The Click Input and stay-open rows hide, since the bar is shown and
    /// fired by its own controls.
    /// </summary>
    public partial class MenuEditorItem
    {
        public bool IsHotbar => Entry.Kind == MenuKind.Hotbar;

        /// <summary>Applies the hotbar's own defaults when a menu turns into
        /// one: Steam's hotbar reads a stick's directions past 61 percent of
        /// a push. Only a value still at the radial default changes.</summary>
        private void ApplyHotbarDefaults()
        {
            if (Entry.EngageDeadzonePercent == 25) Entry.EngageDeadzonePercent = MenuHotbar.SteamDeadzonePercent;
        }

        /// <summary>The way back when a hotbar turns into a ring or a grid.</summary>
        private void LeaveHotbarDefaults()
        {
            if (Entry.EngageDeadzonePercent == MenuHotbar.SteamDeadzonePercent) Entry.EngageDeadzonePercent = 25;
        }

        private void RaiseHotbarDependents()
        {
            OnPropertyChanged(nameof(IsHotbar));
            OnPropertyChanged(nameof(ShowsClickRow));
            OnPropertyChanged(nameof(ShowsCustomAxesRows));
            OnPropertyChanged(nameof(ShowLayerHold));
            OnPropertyChanged(nameof(EngageDeadzonePercent));
            RaiseHotbarControls();
        }

        private void RaiseHotbarControls()
        {
            RaiseCustomInput(MenuRecordTarget.HotbarShow);
            RaiseCustomInput(MenuRecordTarget.HotbarPrevious);
            RaiseCustomInput(MenuRecordTarget.HotbarNext);
            RaiseCustomInput(MenuRecordTarget.HotbarFire);
        }

        private static bool IsHotbarTarget(MenuRecordTarget t)
            => t is MenuRecordTarget.HotbarShow or MenuRecordTarget.HotbarPrevious
                 or MenuRecordTarget.HotbarNext or MenuRecordTarget.HotbarFire;

        private string HotbarDescriptor(MenuRecordTarget t) => t switch
        {
            MenuRecordTarget.HotbarShow => Entry.HotbarShowDescriptor,
            MenuRecordTarget.HotbarPrevious => Entry.HotbarPreviousDescriptor,
            MenuRecordTarget.HotbarNext => Entry.HotbarNextDescriptor,
            _ => Entry.HotbarFireDescriptor,
        } ?? "";

        /// <summary>Writes one hotbar control. Returns false when it already
        /// held <paramref name="value"/>.</summary>
        private bool WriteHotbarDescriptor(MenuRecordTarget t, string value)
        {
            if (HotbarDescriptor(t) == value) return false;
            switch (t)
            {
                case MenuRecordTarget.HotbarShow: Entry.HotbarShowDescriptor = value; break;
                case MenuRecordTarget.HotbarPrevious: Entry.HotbarPreviousDescriptor = value; break;
                case MenuRecordTarget.HotbarNext: Entry.HotbarNextDescriptor = value; break;
                default: Entry.HotbarFireDescriptor = value; break;
            }
            return true;
        }

        /// <summary>Under an empty control: "(host default)" when the opener
        /// has directions of its own, "(not set)" when it has none.</summary>
        private string HotbarSubtitle(MenuRecordTarget t)
        {
            var control = t switch
            {
                MenuRecordTarget.HotbarShow => HotbarControl.Show,
                MenuRecordTarget.HotbarPrevious => HotbarControl.Previous,
                MenuRecordTarget.HotbarNext => HotbarControl.Next,
                _ => HotbarControl.Fire,
            };
            bool hostHasIt = MenuHotbar.DefaultControl(Entry, control).Length > 0;
            return SubtitleFor(HotbarDescriptor(t),
                hostHasIt ? Strings.Instance.Menu_ClickDefault : Strings.Instance.Menu_NotRecorded);
        }

        public System.ComponentModel.ICollectionView HotbarShowChoicesView
            => BuildChoicesView(BuildInputChoices(analog: false, Entry.HotbarShowDescriptor));
        public System.ComponentModel.ICollectionView HotbarPreviousChoicesView
            => BuildChoicesView(BuildInputChoices(analog: false, Entry.HotbarPreviousDescriptor));
        public System.ComponentModel.ICollectionView HotbarNextChoicesView
            => BuildChoicesView(BuildInputChoices(analog: false, Entry.HotbarNextDescriptor));
        public System.ComponentModel.ICollectionView HotbarFireChoicesView
            => BuildChoicesView(BuildInputChoices(analog: false, Entry.HotbarFireDescriptor));

        public string HotbarShowSelected
        {
            get => Entry.HotbarShowDescriptor ?? "";
            set => SetCustomInput(MenuRecordTarget.HotbarShow, value);
        }
        public string HotbarPreviousSelected
        {
            get => Entry.HotbarPreviousDescriptor ?? "";
            set => SetCustomInput(MenuRecordTarget.HotbarPrevious, value);
        }
        public string HotbarNextSelected
        {
            get => Entry.HotbarNextDescriptor ?? "";
            set => SetCustomInput(MenuRecordTarget.HotbarNext, value);
        }
        public string HotbarFireSelected
        {
            get => Entry.HotbarFireDescriptor ?? "";
            set => SetCustomInput(MenuRecordTarget.HotbarFire, value);
        }

        public string HotbarShowSubtitle => HotbarSubtitle(MenuRecordTarget.HotbarShow);
        public string HotbarPreviousSubtitle => HotbarSubtitle(MenuRecordTarget.HotbarPrevious);
        public string HotbarNextSubtitle => HotbarSubtitle(MenuRecordTarget.HotbarNext);
        public string HotbarFireSubtitle => HotbarSubtitle(MenuRecordTarget.HotbarFire);

        public string HotbarShowRecordIcon => IconFor(MenuRecordTarget.HotbarShow);
        public string HotbarPreviousRecordIcon => IconFor(MenuRecordTarget.HotbarPrevious);
        public string HotbarNextRecordIcon => IconFor(MenuRecordTarget.HotbarNext);
        public string HotbarFireRecordIcon => IconFor(MenuRecordTarget.HotbarFire);

        public string HotbarShowRecordText => TextFor(MenuRecordTarget.HotbarShow);
        public string HotbarPreviousRecordText => TextFor(MenuRecordTarget.HotbarPrevious);
        public string HotbarNextRecordText => TextFor(MenuRecordTarget.HotbarNext);
        public string HotbarFireRecordText => TextFor(MenuRecordTarget.HotbarFire);

        private void RaiseHotbarRecordGlyphs()
        {
            OnPropertyChanged(nameof(HotbarShowRecordIcon));
            OnPropertyChanged(nameof(HotbarPreviousRecordIcon));
            OnPropertyChanged(nameof(HotbarNextRecordIcon));
            OnPropertyChanged(nameof(HotbarFireRecordIcon));
            OnPropertyChanged(nameof(HotbarShowRecordText));
            OnPropertyChanged(nameof(HotbarPreviousRecordText));
            OnPropertyChanged(nameof(HotbarNextRecordText));
            OnPropertyChanged(nameof(HotbarFireRecordText));
        }

        private void RaiseHotbarInput(MenuRecordTarget t)
        {
            switch (t)
            {
                case MenuRecordTarget.HotbarShow:
                    OnPropertyChanged(nameof(HotbarShowChoicesView));
                    OnPropertyChanged(nameof(HotbarShowSelected));
                    OnPropertyChanged(nameof(HotbarShowSubtitle));
                    break;
                case MenuRecordTarget.HotbarPrevious:
                    OnPropertyChanged(nameof(HotbarPreviousChoicesView));
                    OnPropertyChanged(nameof(HotbarPreviousSelected));
                    OnPropertyChanged(nameof(HotbarPreviousSubtitle));
                    break;
                case MenuRecordTarget.HotbarNext:
                    OnPropertyChanged(nameof(HotbarNextChoicesView));
                    OnPropertyChanged(nameof(HotbarNextSelected));
                    OnPropertyChanged(nameof(HotbarNextSubtitle));
                    break;
                default:
                    OnPropertyChanged(nameof(HotbarFireChoicesView));
                    OnPropertyChanged(nameof(HotbarFireSelected));
                    OnPropertyChanged(nameof(HotbarFireSubtitle));
                    break;
            }
        }

        public RelayCommand ResetHotbarShowCommand => _resetHotbarShow ??= new RelayCommand(
            () => SetCustomInput(MenuRecordTarget.HotbarShow, ""));
        private RelayCommand _resetHotbarShow;

        public RelayCommand ResetHotbarPreviousCommand => _resetHotbarPrevious ??= new RelayCommand(
            () => SetCustomInput(MenuRecordTarget.HotbarPrevious, ""));
        private RelayCommand _resetHotbarPrevious;

        public RelayCommand ResetHotbarNextCommand => _resetHotbarNext ??= new RelayCommand(
            () => SetCustomInput(MenuRecordTarget.HotbarNext, ""));
        private RelayCommand _resetHotbarNext;

        public RelayCommand ResetHotbarFireCommand => _resetHotbarFire ??= new RelayCommand(
            () => SetCustomInput(MenuRecordTarget.HotbarFire, ""));
        private RelayCommand _resetHotbarFire;

        public bool HotbarWrap
        {
            get => Entry.HotbarWrap;
            set { if (Entry.HotbarWrap != value) { Entry.HotbarWrap = value; OnPropertyChanged(); OnEdited(); } }
        }

        public bool HotbarDismissOnFire
        {
            get => Entry.HotbarDismissOnFire;
            set
            {
                if (Entry.HotbarDismissOnFire == value) return;
                Entry.HotbarDismissOnFire = value;
                OnPropertyChanged();
                OnEdited();
            }
        }

        public bool HotbarRecenter
        {
            get => Entry.HotbarRecenter;
            set { if (Entry.HotbarRecenter != value) { Entry.HotbarRecenter = value; OnPropertyChanged(); OnEdited(); } }
        }

        // ── Fire mode, as a hotbar runs it ───────────────────────

        private static IReadOnlyList<MenuIntOption> HotbarFireOptionsBacking = BuildHotbarFireOptions();

        /// <summary>The four fire modes as Steam's hotbar runs them
        /// (0x1388c2191 to 0x1388c2530).</summary>
        private static IReadOnlyList<MenuIntOption> BuildHotbarFireOptions() => new[]
        {
            new MenuIntOption { Value = 0, Label = Strings.Instance.Menu_Fire_Click,
                                Description = Strings.Instance.Menu_Fire_Click_Hotbar_Desc },
            new MenuIntOption { Value = 1, Label = Strings.Instance.Menu_Fire_ClickRelease,
                                Description = Strings.Instance.Menu_Fire_ClickRelease_Hotbar_Desc },
            new MenuIntOption { Value = 2, Label = Strings.Instance.Menu_Fire_TouchRelease,
                                Description = Strings.Instance.Menu_Fire_TouchRelease_Hotbar_Desc },
            new MenuIntOption { Value = 3, Label = Strings.Instance.Menu_Fire_Always,
                                Description = Strings.Instance.Menu_Fire_Always_Hotbar_Desc },
        };
    }
}
