using System;

namespace PadForge.Common.Input
{
    public partial class InputManager
    {
        // ─────────────────────────────────────────────
        //  Step 6: PublishSteeringAngles
        //  Publishes each slot's steering angle frame for the steering
        //  rumble and the page that shows it.
        //
        //  This step once also copied each slot's combined output, keyboard
        //  and mouse, and touchpad state into arrays for the interface. The
        //  interface reads the combined states themselves
        //  (CombinedOutputStates), so nothing read the copies, and they
        //  went in 6.0.0.
        // ─────────────────────────────────────────────

        /// <summary>Step 6: publishes each slot's steering angle frame
        /// (<see cref="PublishSteeringAngleFrame"/>). A slot whose publish
        /// throws reads no angle until its next good pass.</summary>
        private void PublishSteeringAngles()
        {
            for (int padIndex = 0; padIndex < MaxPads; padIndex++)
            {
                try
                {
                    PublishSteeringAngleFrame(padIndex);
                }
                catch (Exception ex)
                {
                    System.Threading.Volatile.Write(ref _steeringAngleFrames[padIndex], 0);
                    RaiseError($"Error publishing the steering angle for pad {padIndex}", ex);
                }
            }
        }
    }
}
