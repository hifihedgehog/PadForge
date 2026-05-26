using System.Collections.Generic;
using System.Numerics;
using System.Xml.Serialization;

namespace PadForge.Engine.Touchpad
{
    /// <summary>
    /// XML-serializable record of one user-recorded touchpad gesture.
    /// Lives under a profile's <c>TouchpadGestures</c> collection;
    /// gets compiled to a <see cref="ShapeTemplate"/> at profile-load
    /// time and merged into the active shape-template catalog the
    /// gesture engine reads.
    /// </summary>
    public sealed class TouchpadCustomGesture
    {
        /// <summary>User-given name. Becomes the
        /// <c>Touchpad N Custom_{Name}</c> descriptor suffix the
        /// mapping table binds to. Validation at save time: non-empty,
        /// unique within the profile, max 64 chars, no whitespace or
        /// XML-unsafe characters.</summary>
        [XmlAttribute] public string Name { get; set; } = "";

        /// <summary>Device-class filter. <c>any</c> matches every
        /// touchpad-capable device; otherwise a class label like
        /// <c>dualsense</c> / <c>ds4</c> / <c>steamdeck</c> /
        /// <c>steamcontroller</c> / <c>triton</c> / <c>precisiontouchpad</c>
        /// / <c>overlay</c> restricts matching to that device type.</summary>
        [XmlAttribute] public string DeviceClass { get; set; } = "any";

        /// <summary>Touchpad index filter on multi-pad devices. <c>-1</c>
        /// = any pad; otherwise binds to that specific pad index.</summary>
        [XmlAttribute] public int TouchpadIndex { get; set; } = -1;

        /// <summary>Per-template recognition threshold override.
        /// <c>0</c> = use the per-pad <c>GestureMatchThreshold</c>
        /// from settings; otherwise this value supersedes it.</summary>
        [XmlAttribute] public float Threshold { get; set; }

        /// <summary>Per-gesture enable toggle surfaced on the Touchpad
        /// tab. Lets the user keep a gesture configured but temporarily
        /// stop it firing without deleting the recording.</summary>
        [XmlAttribute] public bool Enabled { get; set; } = true;

        /// <summary>The recorded finger paths. One <see cref="FingerPath"/>
        /// per finger that contributed to the gesture; each path's
        /// <c>Points</c> are the per-sample positions in normalized
        /// touchpad space (0..1) with millisecond timestamps for
        /// optional replay / visualization. Multi-finger gestures
        /// (pinch, rotate, multi-finger swipes) have multiple
        /// FingerPath elements; single-finger gestures have one.</summary>
        [XmlElement("FingerPath")]
        public List<FingerPath> FingerPaths { get; set; } = new List<FingerPath>();

        /// <summary>Builds the <see cref="ShapeTemplate"/> the
        /// recognizer evaluates against. Resamples + normalizes the
        /// stored finger paths; called once at profile load. Returns
        /// null when the gesture has no finger paths (corrupt entry).</summary>
        public ShapeTemplate ToTemplate(int resampleCount = ShapeRecognizer.DefaultResampleCount)
        {
            if (FingerPaths == null || FingerPaths.Count == 0) return null;
            var fingers = new List<List<Vector2>>(FingerPaths.Count);
            foreach (var fp in FingerPaths)
            {
                if (fp?.Points == null || fp.Points.Count == 0) continue;
                var pts = new List<Vector2>(fp.Points.Count);
                foreach (var p in fp.Points) pts.Add(new Vector2(p.X, p.Y));
                fingers.Add(pts);
            }
            if (fingers.Count == 0) return null;
            // Single-finger custom gestures also carry an angular
            // signature so the recognizer can run both the point-cloud
            // matcher and the angular-margin matcher and keep the
            // better-scoring match. Multi-finger custom gestures stay
            // point-cloud-only — angular-margin doesn't have a clean
            // per-finger correspondence to compare against.
            double[] angles = fingers.Count == 1
                ? AngularMarginRecognizer.BuildAngleSignature(fingers[0])
                : null;
            var cloud = ShapeRecognizer.BuildCloud(fingers, resampleCount);
            return new ShapeTemplate
            {
                Name = "Custom_" + Name,
                FingerCount = fingers.Count,
                PointCloud = cloud,
                LookupTable = ShapeRecognizer.BuildLookupTable(cloud),
                LookupTableSize = ShapeRecognizer.DefaultLookupTableSize,
                ThresholdOverride = Threshold,
                Enabled = Enabled,
                IsCustom = true,
                AngularSignature = angles,
            };
        }

        public sealed class FingerPath
        {
            [XmlElement("P")] public List<GesturePoint> Points { get; set; } = new List<GesturePoint>();
        }

        public sealed class GesturePoint
        {
            [XmlAttribute("X")] public float X { get; set; }
            [XmlAttribute("Y")] public float Y { get; set; }
            [XmlAttribute("T")] public int T { get; set; }
        }
    }
}
