using CommunityToolkit.Mvvm.ComponentModel;

namespace PadForge.ViewModels
{
    /// <summary>
    /// One source row within a multi-source <see cref="MappingItem"/>.
    /// Phase 2C — represents a single <c>Engine.Data.MappingSource</c> in
    /// the Mappings UI. Bound by the (forthcoming) RowDetailsTemplate
    /// inside the Mappings DataGrid.
    /// </summary>
    public class MappingSourceItem : ObservableObject
    {
        private string _kind = "Direct";
        private string _deviceGuid = "";
        private string _descriptor = "";
        private bool _invert;
        private bool _halfAxis;
        private int _deadZone = 50;
        private string _paramUp = "";
        private string _paramDown = "";
        private double _paramRate = 0.5;
        private bool _paramSticky = true;
        private double _paramMin;
        private double _paramMax = 1;
        private string _paramModifier = "";

        public string Kind { get => _kind; set => SetProperty(ref _kind, value ?? "Direct"); }
        public string DeviceGuid { get => _deviceGuid; set => SetProperty(ref _deviceGuid, value ?? ""); }
        public string Descriptor { get => _descriptor; set => SetProperty(ref _descriptor, value ?? ""); }
        public bool Invert { get => _invert; set => SetProperty(ref _invert, value); }
        public bool HalfAxis { get => _halfAxis; set => SetProperty(ref _halfAxis, value); }
        public int DeadZone { get => _deadZone; set => SetProperty(ref _deadZone, value); }

        public string ParamUp { get => _paramUp; set => SetProperty(ref _paramUp, value ?? ""); }
        public string ParamDown { get => _paramDown; set => SetProperty(ref _paramDown, value ?? ""); }
        public double ParamRate { get => _paramRate; set => SetProperty(ref _paramRate, value); }
        public bool ParamSticky { get => _paramSticky; set => SetProperty(ref _paramSticky, value); }
        public double ParamMin { get => _paramMin; set => SetProperty(ref _paramMin, value); }
        public double ParamMax { get => _paramMax; set => SetProperty(ref _paramMax, value); }
        public string ParamModifier { get => _paramModifier; set => SetProperty(ref _paramModifier, value ?? ""); }

        /// <summary>Builds a domain <see cref="Engine.Data.MappingSource"/>
        /// from this VM's current values. Used by the Save pipeline.</summary>
        public Engine.Data.MappingSource ToDomain() => new()
        {
            Kind = _kind ?? "Direct",
            DeviceGuid = _deviceGuid ?? "",
            Descriptor = _descriptor ?? "",
            Invert = _invert,
            HalfAxis = _halfAxis,
            DeadZone = _deadZone,
            ParamUp = _paramUp ?? "",
            ParamDown = _paramDown ?? "",
            ParamRate = _paramRate,
            ParamSticky = _paramSticky,
            ParamMin = _paramMin,
            ParamMax = _paramMax,
            ParamModifier = _paramModifier ?? "",
        };

        /// <summary>Populates this VM from a domain
        /// <see cref="Engine.Data.MappingSource"/>.</summary>
        public static MappingSourceItem FromDomain(Engine.Data.MappingSource src)
        {
            if (src == null) return new MappingSourceItem();
            return new MappingSourceItem
            {
                Kind = src.Kind ?? "Direct",
                DeviceGuid = src.DeviceGuid ?? "",
                Descriptor = src.Descriptor ?? "",
                Invert = src.Invert,
                HalfAxis = src.HalfAxis,
                DeadZone = src.DeadZone,
                ParamUp = src.ParamUp ?? "",
                ParamDown = src.ParamDown ?? "",
                ParamRate = src.ParamRate,
                ParamSticky = src.ParamSticky,
                ParamMin = src.ParamMin,
                ParamMax = src.ParamMax,
                ParamModifier = src.ParamModifier ?? "",
            };
        }
    }
}
