namespace PadForge.Common.Input
{
    /// <summary>
    /// Every byte value, boxed once. The effect builders hand the encoder a
    /// dictionary of objects, and boxing each byte again made a heap object
    /// per field, thirteen for each DualSense frame at 30 frames a second
    /// (measured 2026-11-16). A boxed value cannot change, so one box serves
    /// every frame.
    /// </summary>
    internal static class BoxedBytes
    {
        private static readonly object[] s_boxes = Create();

        private static object[] Create()
        {
            var boxes = new object[256];
            for (int i = 0; i < boxes.Length; i++) boxes[i] = (byte)i;
            return boxes;
        }

        /// <summary>The one box of <paramref name="value"/>.</summary>
        internal static object Of(byte value) => s_boxes[value];
    }
}
