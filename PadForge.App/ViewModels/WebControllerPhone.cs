namespace PadForge.ViewModels
{
    /// <summary>A phone or browser connected to the web controller now, as the Web Controller page's
    /// Phones card lists it (#574). It keeps the session's device, so the session that ends is the one
    /// that leaves the list.</summary>
    public sealed class WebControllerPhone
    {
        internal WebControllerPhone(PadForge.Engine.WebControllerDevice device)
        {
            Device = device;
            Id = device.InstanceGuid;
            Name = device.Name;
        }

        internal PadForge.Engine.WebControllerDevice Device { get; }
        public System.Guid Id { get; }
        public string Name { get; }

        /// <summary>A screen reader reads a plain list's items by their text.</summary>
        public override string ToString() => Name;
    }
}
