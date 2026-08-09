// DualSense Edge: its own model family. Edge profiles must always
// render the Edge mesh (removable stick modules, back function
// buttons), never a plain DualSense, so it does not ride the
// DualSense appearance list.

using System.Windows.Media.Media3D;

namespace PadForge.Models3D
{
    /// <summary>DualSense Edge. Reuses the DualSense model body (same
    /// touchpad, riders and stick-module material handling) against the
    /// DualSenseEdge asset folder.</summary>
    public sealed class ControllerModelDualSenseEdge : ControllerModelDualSense
    {
        // `new` is deliberate, not an oversight: the Edge is its own family
        // with its own one-entry colorway list, and these shadow the
        // DualSense arrays rather than override them (statics cannot be
        // virtual). Nothing resolves them through a base-typed expression:
        // ControllerModelView's family switch names this type explicitly,
        // and the Edge reaches the body through the protected family-scoped
        // constructor, so the base's private Validate (which binds to the
        // DualSense list at compile time) is never on the Edge's path.
        public static new readonly string[] AppearanceIds = { "Edge" };
        public static new readonly string[] AppearanceNames = { "DualSense Edge" };

        public ControllerModelDualSenseEdge() : base("Edge", "DualSenseEdge")
        {
            // The Edge's trigger mesh sits ~0.8 mm higher than the
            // standard DualSense's, so the shared hinge lands in the
            // wrong place on it. Same rule, its own bounds: at the top
            // edge 49 of 400 sampled vertices went 1.90 mm inside the
            // bumper at full pull; here it clears by 0.56 mm.
            ShoulderTriggerRotationPointCenterLeftMillimeter  = new Vector3D(-49.4f, 5.08f, 41.8f);
            ShoulderTriggerRotationPointCenterRightMillimeter = new Vector3D( 49.4f, 5.08f, 41.8f);
        }
    }
}
