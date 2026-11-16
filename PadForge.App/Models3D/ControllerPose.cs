namespace PadForge.Models3D
{
    /// <summary>How far a 3D model's sticks tilt and its triggers turn for
    /// what is held on the controller. The controller's page and a Home
    /// card's model take their angles from here, so the two move alike.</summary>
    internal static class ControllerPose
    {
        /// <summary>A stick's tilt for its two raw axes, in degrees: first
        /// about the model's up axis (Z), which the X axis drives, then about
        /// its across axis (X), which the Y axis drives, up on the stick
        /// tipping the cap away. Full deflection is
        /// <paramref name="maxAngleDeg"/> each way.</summary>
        internal static (float AboutUp, float AboutAcross) StickAngles(short rawX, short rawY, float maxAngleDeg)
        {
            float normX = rawX / (float)short.MaxValue;
            float normY = rawY / (float)short.MaxValue;
            return (maxAngleDeg * normX, -maxAngleDeg * normY);
        }

        /// <summary>A trigger's turn about the model's across axis for a pull
        /// from 0 to 1, in degrees: <paramref name="maxAngleDeg"/> at full
        /// pull.</summary>
        internal static float TriggerAngle(double pull, float maxAngleDeg) => -maxAngleDeg * (float)pull;
    }
}
