using System.Numerics;

namespace PadForge.Engine.Touchpad
{
    /// <summary>
    /// Pre-processed shape template for the touchpad <see cref="ShapeRecognizer"/>.
    /// In-box templates (Circle / CircleCCW / Square / Triangle / Z /
    /// Checkmark) are constructed at app startup from procedural point
    /// generators in <see cref="InBoxShapeTemplates"/>; custom user
    /// templates are constructed at profile-load time from
    /// <see cref="TouchpadCustomGesture"/> after multi-sample averaging.
    ///
    /// <para>The template carries both the normalized point cloud and a
    /// pre-computed lookup table (LUT). The LUT is a small 2D grid
    /// (<see cref="LookupTableSize"/> × <see cref="LookupTableSize"/>)
    /// whose cells store the index of the nearest template point for
    /// the grid-cell's center. Matching uses the LUT to answer
    /// "what's the nearest template point to this candidate point?"
    /// in O(1) instead of $P's linear scan over the template, which
    /// is what makes the $Q recognizer fast.</para>
    ///
    /// <para>The matcher is otherwise the $P / $Q family's point-cloud
    /// approach: scale, position, and rotation invariant by
    /// construction (resample → centroid-translate → scale-uniform).
    /// Multi-finger gestures concatenate each finger's normalized
    /// path into one cloud; finger correspondence is not tracked, so
    /// "I drew this with two fingers" matches whichever finger drew
    /// which stroke.</para>
    /// </summary>
    public sealed class ShapeTemplate
    {
        /// <summary>Gesture name (e.g. <c>Circle</c>, <c>CircleCCW</c>,
        /// <c>Custom_ZoomToFit</c>). Used as the suffix in the
        /// <c>Touchpad N {Name}</c> descriptor that the source-coercion
        /// layer reads.</summary>
        public string Name;

        /// <summary>Number of fingers this template represents. Single-
        /// stroke single-finger = 1; two-finger pinch-out-circle = 2;
        /// up to 5 for PTP-max scenarios.</summary>
        public int FingerCount;

        /// <summary>Normalized point cloud,
        /// <c>FingerCount × ShapeRecognizer.DefaultResampleCount</c>
        /// points long. Pre-built (resampled + centroid-translated +
        /// scaled so the diagonal of the combined bounding box equals 1)
        /// so the matcher's hot path skips the prep work.</summary>
        public Vector2[] PointCloud;

        /// <summary>Pre-computed lookup table for the $Q recognizer.
        /// Flat byte array of length
        /// <see cref="LookupTableSize"/><c>²</c>; each entry is the
        /// index into <see cref="PointCloud"/> of the template point
        /// nearest the center of that grid cell. Storage is ushort
        /// so multi-finger templates with cloud sizes above 255
        /// (e.g. 5 fingers × 32 = 160 — still fits in byte, but
        /// ushort future-proofs us against larger resample counts).</summary>
        public ushort[] LookupTable;

        /// <summary>Edge count of the square lookup grid. Default 64
        /// per the $Q paper's reference; cell width in normalized
        /// units is therefore <c>2.0 / LookupTableSize</c> across the
        /// <c>[-1, +1]</c> indexing range the recognizer uses.</summary>
        public int LookupTableSize;

        /// <summary>Per-template threshold override. 0 = use the
        /// caller's default. Custom gestures with strict / loose feel
        /// can dial this independently of the global default.</summary>
        public float ThresholdOverride;

        /// <summary>Per-template enable toggle. The Touchpad tab UI
        /// surfaces this so users can disable individual gestures
        /// without deleting them.</summary>
        public bool Enabled = true;

        /// <summary>True for user-recorded templates, false for in-box
        /// shapes. Drives the per-finger-count gating in
        /// <see cref="GestureRecognizer.MaybeFireShape"/> — custom
        /// templates always evaluate; in-box shapes only when the
        /// per-pad EnableShapeGestures toggle is on.</summary>
        public bool IsCustom;

        /// <summary>Single-finger angle signature for the angular-margin
        /// recognizer that runs alongside the shape recognizer on
        /// single-finger shapes. Null for multi-finger templates
        /// (angular-margin doesn't naturally extend to a permutation-
        /// invariant cloud across strokes the user can't be relied on
        /// to draw in the same order each time). Built once at
        /// template-load time.</summary>
        public double[] AngularSignature;

        /// <summary>Closed-shape flag — angular-margin matching tries
        /// every cyclic starting-point so a user beginning the trace
        /// from any corner / vertex matches the same template. Open
        /// shapes leave this false. Carried over to
        /// <see cref="AngularTemplate.IsClosed"/> during single-finger
        /// matching.</summary>
        public bool AngularIsClosed;

        /// <summary>Direction-agnostic flag — angular-margin matching
        /// also tries the reverse-direction signature so a shape drawn
        /// CW or CCW both match. Carried over to
        /// <see cref="AngularTemplate.IsDirectionAgnostic"/> during
        /// single-finger matching. Stays false on Circle CW / CCW
        /// (intentionally separate directional gestures).</summary>
        public bool AngularIsDirectionAgnostic;
    }
}
