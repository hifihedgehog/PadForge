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

        /// <summary>True for the synthetic Base tab; false for shift layers.
        /// Consumers: the tab chip's color-dot visibility and the SHIFT
        /// pipeline chip's idle summary, which lists only non-Base layer
        /// names (round eight, R16: round seven's rewrite claimed the dot
        /// was the only consumer, repeating the mistake it corrected). The
        /// context menu's Base behavior lives in the handlers, see
        /// PadPage's tab context-menu comment.</summary>
        public bool IsBase => string.Equals(_layerMask, "Base", System.StringComparison.Ordinal);
    }
}
