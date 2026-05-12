using CommunityToolkit.Mvvm.ComponentModel;

namespace PadForge.ViewModels
{
    /// <summary>
    /// View-model wrapper around a single
    /// <see cref="PadForge.Engine.Data.ShiftActivator"/> from a slot's
    /// MappingSet. Populates the nested tab strip on the Mappings tab and
    /// the right-click context menu's per-layer commands.
    ///
    /// <para>
    /// Treats <c>LayerMask</c> as the engine-side identity (matches
    /// <see cref="PadForge.Engine.Data.MappingRow.LayerMask"/>) and
    /// <c>LayerName</c> as the user-visible display.
    /// </para>
    /// </summary>
    public class ShiftLayerInfo : ObservableObject
    {
        private string _layerMask = "Base";
        public string LayerMask
        {
            get => _layerMask;
            set => SetProperty(ref _layerMask, value ?? "Base");
        }

        private string _layerName = "Base";
        public string LayerName
        {
            get => _layerName;
            set => SetProperty(ref _layerName, value ?? "");
        }

        private string _color = "";
        /// <summary>v2 per-layer color hint, "#AARRGGBB" or empty.</summary>
        public string Color
        {
            get => _color;
            set => SetProperty(ref _color, value ?? "");
        }

        private bool _isActive;
        /// <summary>True when this layer is the one currently being authored
        /// (its tab is selected). Drives RadioButton.IsChecked in the tab
        /// strip and the tab visual highlight.</summary>
        public bool IsActive
        {
            get => _isActive;
            set => SetProperty(ref _isActive, value);
        }

        private bool _isEngagedNow;
        /// <summary>True when the engine reports this layer's activator is
        /// currently engaged on the live device. Surfaces as a runtime
        /// indicator on the tab (e.g. pulsing border). v3 visual-overlay
        /// tier; wired to engine state on a low-frequency tick.</summary>
        public bool IsEngagedNow
        {
            get => _isEngagedNow;
            set => SetProperty(ref _isEngagedNow, value);
        }

        /// <summary>True for the synthetic Base tab; false for shift layers.
        /// Drives whether right-click context menu shows the delete /
        /// configure / rename items (Base never has them).</summary>
        public bool IsBase => string.Equals(_layerMask, "Base", System.StringComparison.Ordinal);
    }
}
