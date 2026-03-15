namespace PadForge.Engine.Data
{
    /// <summary>
    /// Deadzone algorithm for thumbstick axes.
    /// See https://minimuino.github.io/thumbstick-deadzones/ for visual reference.
    /// </summary>
    public enum DeadZoneShape
    {
        /// <summary>Independent per-axis deadzone (square/cross shape). Legacy behavior.</summary>
        Axial = 0,

        /// <summary>Circular/elliptical magnitude check, no output rescaling.</summary>
        Radial = 1,

        /// <summary>Circular/elliptical magnitude check with output rescaling (industry standard).</summary>
        ScaledRadial = 2,

        /// <summary>Axis-dependent thresholds: DZ grows on one axis as the other increases.</summary>
        SlopedAxial = 3,

        /// <summary>Sloped axis-dependent thresholds with output rescaling.</summary>
        SlopedScaledAxial = 4,

        /// <summary>Scaled Radial followed by Sloped Scaled Axial (best hybrid).</summary>
        Hybrid = 5,
    }
}
