using System;
using PadForge.Engine;

namespace PadForge.Common.Input
{
    public partial class InputManager
    {
        // ─────────────────────────────────────────────
        //  Step 6: RetrieveXiStates
        //  Copies the combined gamepad states directly from Step 4 output
        //  for UI display. This shows exactly what was submitted to the
        //  virtual controllers and works for all controller types
        //  (Xbox 360, DualShock 4, etc.).
        //
        //  Previously used XInput P/Invoke readback, but that only worked
        //  for Xbox 360 virtual controllers. DS4 controllers don't appear
        //  in the XInput stack, so direct copy is both more universal and
        //  more accurate.
        // ─────────────────────────────────────────────

        /// <summary>
        /// Step 6: For each of the 4 controller slots, copies the combined
        /// gamepad state to <see cref="RetrievedXiStates"/> for UI display.
        /// Only populates slots that have an active virtual controller.
        /// </summary>
        private void RetrieveXiStates()
        {
            for (int padIndex = 0; padIndex < MaxPads; padIndex++)
            {
                try
                {
                    var vc = _virtualControllers[padIndex];
                    if (vc != null && vc.IsConnected)
                    {
                        RetrievedXiStates[padIndex] = CombinedXiStates[padIndex];
                    }
                    else
                    {
                        RetrievedXiStates[padIndex].Clear();
                    }
                }
                catch (Exception ex)
                {
                    RaiseError($"Error retrieving state for pad {padIndex}", ex);
                    RetrievedXiStates[padIndex].Clear();
                }
            }
        }
    }
}
