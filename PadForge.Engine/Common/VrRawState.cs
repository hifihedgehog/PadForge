namespace PadForge.Engine
{
    /// <summary>
    /// One VR hand's mapped output (issue #49). Button bits mirror
    /// HIDMaestro's HMVRButton flags EXACTLY (System=1, A=2, ATouch=4,
    /// B=8, BTouch=16, TriggerClick=32, GripClick=64, StickClick=128), so
    /// the wrapper's conversion is a cast, never a table. Axis fields use
    /// the pipeline's native short domains: triggers and grips one-sided
    /// 0..32767, stick axes bipolar -32768..32767.
    /// </summary>
    public struct VrHandRaw
    {
        public byte Buttons;
        public short Trigger;
        public short Grip;
        public short StickX;
        public short StickY;
    }

    /// <summary>
    /// The combined VR output for one slot: the left+right hand pair one
    /// virtual VR controller drives. All value fields on purpose, so a
    /// struct assign copies (the KbmRawState discipline) and the MIDI
    /// array-aliasing trap can never apply here.
    /// </summary>
    public struct VrRawState
    {
        public VrHandRaw Left;
        public VrHandRaw Right;

        public void Clear()
        {
            Left = default;
            Right = default;
        }

        /// <summary>Merges another device's contribution into this state:
        /// buttons OR together, axes keep the larger deflection (the
        /// gamepad-merge convention).</summary>
        public void Merge(in VrRawState other)
        {
            Left = MergeHand(in Left, in other.Left);
            Right = MergeHand(in Right, in other.Right);
        }

        private static VrHandRaw MergeHand(in VrHandRaw a, in VrHandRaw b)
        {
            VrHandRaw m;
            m.Buttons = (byte)(a.Buttons | b.Buttons);
            m.Trigger = a.Trigger >= b.Trigger ? a.Trigger : b.Trigger;
            m.Grip = a.Grip >= b.Grip ? a.Grip : b.Grip;
            m.StickX = System.Math.Abs(a.StickX) >= System.Math.Abs(b.StickX) ? a.StickX : b.StickX;
            m.StickY = System.Math.Abs(a.StickY) >= System.Math.Abs(b.StickY) ? a.StickY : b.StickY;
            return m;
        }
    }

    /// <summary>
    /// The VR mapping-key vocabulary, shared by the Step 3 mapper, the
    /// layout translation, and the mapping UI so all three agree on one
    /// table. Keys follow the MIDI/KBM dictionary-lane convention
    /// ("VrLTrigger", "VrRStickXNeg", ...).
    /// </summary>
    public static class VrLayout
    {
        /// <summary>Per-hand button keys, INDEXED BY HMVRButton BIT
        /// POSITION: index i corresponds to button bit 1 &lt;&lt; i.</summary>
        public static readonly string[] LeftButtonKeys =
        {
            "VrLSystem", "VrLA", "VrLATouch", "VrLB",
            "VrLBTouch", "VrLTriggerClick", "VrLGripClick", "VrLStickClick",
        };
        public static readonly string[] RightButtonKeys =
        {
            "VrRSystem", "VrRA", "VrRATouch", "VrRB",
            "VrRBTouch", "VrRTriggerClick", "VrRGripClick", "VrRStickClick",
        };

        public const string LStickX = "VrLStickX";
        public const string LStickY = "VrLStickY";
        public const string LTrigger = "VrLTrigger";
        public const string LGrip = "VrLGrip";
        public const string RStickX = "VrRStickX";
        public const string RStickY = "VrRStickY";
        public const string RTrigger = "VrRTrigger";
        public const string RGrip = "VrRGrip";
    }
}
