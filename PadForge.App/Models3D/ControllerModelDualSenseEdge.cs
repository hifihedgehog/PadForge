// DualSense Edge: its own model family. Edge profiles must always
// render the Edge mesh (removable stick modules, back function
// buttons), never a plain DualSense, so it does not ride the
// DualSense appearance list.

namespace PadForge.Models3D
{
    /// <summary>DualSense Edge. Reuses the DualSense model body (same
    /// touchpad, riders and stick-module material handling) against the
    /// DualSenseEdge asset folder.</summary>
    public sealed class ControllerModelDualSenseEdge : ControllerModelDualSense
    {
        public static readonly string[] AppearanceIds = { "Edge" };
        public static readonly string[] AppearanceNames = { "DualSense Edge" };

        public ControllerModelDualSenseEdge() : base("Edge", "DualSenseEdge") { }
    }
}
