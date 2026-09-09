using CommunityToolkit.Mvvm.Input;

namespace PadForge.ViewModels
{
    public partial class MacroAction
    {
        private RelayCommand _resetButtonSelectionCommand;
        public RelayCommand ResetButtonSelectionCommand => _resetButtonSelectionCommand ??= new RelayCommand(() =>
        {
            ButtonFlags = 0;
            CustomButtons = null;
            if (_buttonOptions != null)
                foreach (var option in _buttonOptions) option.Refresh();
            OnPropertyChanged(nameof(CustomButtons));
            OnPropertyChanged(nameof(HasCustomButtons));
            OnPropertyChanged(nameof(DisplayText));
        });

        private RelayCommand _resetLightbarCycleModesCommand;
        public RelayCommand ResetLightbarCycleModesCommand => _resetLightbarCycleModesCommand ??= new RelayCommand(() =>
        {
            LightbarCycleModesCsv = "1,2,3,4,11,12,13";
            if (_cycleModeOptions != null)
                foreach (var option in _cycleModeOptions) option.RefreshCheckedState();
        });

        private RelayCommand _resetPointerCycleModesCommand;
        public RelayCommand ResetPointerCycleModesCommand => _resetPointerCycleModesCommand ??= new RelayCommand(() =>
        {
            PointerCycleModesCsv = "Mouse,FpsMouse,Mouse43,Mouse169";
            foreach (var option in PointerCycleModeOptions) option.RefreshCheckedState();
        });
    }
}
