using System;
using PadForge.Engine.Common;

namespace PadForge.Common.Input
{
    /// <summary>
    /// The mirror of the Head Tracking page's settings the engine reads (issue #355),
    /// the shape of <see cref="HandheldButtonRegistry.FeatureEnabled"/>:
    /// static, written by DashboardViewModel on the UI thread, read by
    /// the poll thread's device sweep and by the device on every read.
    ///
    /// <para><see cref="Version"/> bumps on a change that needs the row
    /// reopened (either input toggle or an active UDP port). The two ranges are
    /// read live on every poll, so a range edit takes effect at once.</para>
    /// </summary>
    internal static class HeadTrackingRuntime
    {
        public const int DefaultUdpPort = 4242;
        public const int DefaultRotationRangeDeg = 90;
        public const int DefaultTranslationRangeCm = 30;

        private static volatile bool _enabled;
        private static volatile int _udpPort = DefaultUdpPort;
        private static volatile bool _freeTrackEnabled;
        private static volatile bool _openXrEnabled;
        private static volatile string _openXrRuntimeManifest = string.Empty;
        private static volatile int _rotationRangeDeg = DefaultRotationRangeDeg;
        private static volatile int _translationRangeCm = DefaultTranslationRangeCm;
        private static volatile int _version;

        /// <summary>The UDP input toggle. FreeTrack is enabled independently.</summary>
        public static bool Enabled
        {
            get => _enabled;
            set
            {
                if (_enabled == value) return;
                _enabled = value;
                _version++;
            }
        }

        /// <summary>Whether the headset is read through OpenXR (issue #403).
        ///
        /// <para>This is the path for a user whose headset runs on Virtual
        /// Desktop's runtime with no SteamVR at all, which the SteamVR
        /// consumer cannot serve: it loads its native library out of a
        /// SteamVR install, so no SteamVR means no head input.</para></summary>
        public static bool OpenXrEnabled
        {
            get => _openXrEnabled;
            set
            {
                if (_openXrEnabled == value) return;
                _openXrEnabled = value;
                _version++;
            }
        }

        /// <summary>Manifest of the OpenXR runtime to read the headset from,
        /// or empty for the machine's default.
        ///
        /// <para>Naming one matters when a game uses Virtual Desktop's
        /// runtime while the system default is another, which is the ordinary
        /// arrangement for someone who also has SteamVR installed. Choosing
        /// here changes only this process.</para></summary>
        public static string OpenXrRuntimeManifest
        {
            get => _openXrRuntimeManifest ?? string.Empty;
            set
            {
                string v = value ?? string.Empty;
                if (string.Equals(_openXrRuntimeManifest, v, StringComparison.OrdinalIgnoreCase)) return;
                _openXrRuntimeManifest = v;
                if (_openXrEnabled) _version++;
            }
        }

        public static bool AnyEnabled => _enabled || _freeTrackEnabled || _openXrEnabled;

        /// <summary>UDP port OpenTrack's "UDP over network" output sends to.</summary>
        public static int UdpPort
        {
            get => _udpPort;
            set
            {
                int v = Math.Clamp(value, 1, 65535);
                if (_udpPort == v) return;
                _udpPort = v;
                if (_enabled) _version++;
            }
        }

        /// <summary>Whether FreeTrack 2.0 shared memory input is enabled.</summary>
        public static bool FreeTrackEnabled
        {
            get => _freeTrackEnabled;
            set
            {
                if (_freeTrackEnabled == value) return;
                _freeTrackEnabled = value;
                _version++;
            }
        }

        /// <summary>Head rotation, in degrees, that moves a rotation axis
        /// to full deflection. The starting point for all three rotation
        /// axes, each of which can then be set on its own.</summary>
        public static int RotationRangeDeg
        {
            get => _rotationRangeDeg;
            // Axes that follow the family read this live, so moving it moves
            // them without touching any axis a user has pinned.
            set => _rotationRangeDeg = Math.Clamp(value, 1, 180);
        }

        /// <summary>Head travel, in centimeters, that moves a translation
        /// axis to full deflection. The starting point for all three
        /// translation axes.</summary>
        public static int TranslationRangeCm
        {
            get => _translationRangeCm;
            set => _translationRangeCm = Math.Clamp(value, 1, 500);
        }

        // Per-axis overrides in HeadPose's order: yaw, pitch, roll, X, Y, Z.
        // Zero means "follow the family range", which is what every axis does
        // until a user pins one, so an existing setup keeps behaving the way
        // it did.
        private static readonly int[] _axisRange = new int[HeadPose.AxisCount];

        /// <summary>
        /// The range for one axis, in that axis's own unit: degrees for yaw,
        /// pitch and roll, centimeters for X, Y and Z.
        ///
        /// <para>Per axis because the three translations are not one setting.
        /// Head elevation on a bike wants a span of a few centimeters while
        /// leaning wants twenty, and one shared number cannot be both. Issue
        /// #403 asked for exactly this, to place where a tuck, a neutral and
        /// a sit up each trigger.</para>
        /// </summary>
        public static int GetAxisRange(int axis)
        {
            if (axis < 0 || axis >= HeadPose.AxisCount) return 0;
            int own = System.Threading.Volatile.Read(ref _axisRange[axis]);
            if (own > 0) return own;
            return axis <= HeadPose.AxisRoll ? _rotationRangeDeg : _translationRangeCm;
        }

        /// <summary>Pins one axis's range, or zero to follow its family.</summary>
        public static void SetAxisRange(int axis, int value)
        {
            if (axis < 0 || axis >= HeadPose.AxisCount) return;
            int limit = axis <= HeadPose.AxisRoll ? 180 : 500;
            int v = value <= 0 ? 0 : Math.Clamp(value, 1, limit);
            System.Threading.Volatile.Write(ref _axisRange[axis], v);
        }

        /// <summary>The pinned value for this axis, or zero when it
        /// follows its family. This is what gets saved, because saving the
        /// resolved range would silently pin every axis the first time a
        /// user's settings were written.</summary>
        public static int GetAxisRangeOverride(int axis)
            => axis >= 0 && axis < HeadPose.AxisCount
               ? System.Threading.Volatile.Read(ref _axisRange[axis]) : 0;

        /// <summary>True when this axis has its own range rather than the
        /// family's.</summary>
        public static bool AxisRangeIsPinned(int axis)
            => axis >= 0 && axis < HeadPose.AxisCount
               && System.Threading.Volatile.Read(ref _axisRange[axis]) > 0;

        private static int _recenterRequests;

        /// <summary>
        /// Counts recenter requests. The OpenXR source watches it and drops
        /// its captured neutral, so the next sample becomes the new one.
        ///
        /// <para>It is a counter rather than a flag because two requests in a
        /// row must both take effect, and a flag the source clears would race
        /// with the click that set it.</para>
        ///
        /// <para>Only OpenXR has a neutral to move. OpenTrack and FreeTrack
        /// carry whatever zero their own application was centered on.</para>
        /// </summary>
        public static int RecenterRequests => System.Threading.Volatile.Read(ref _recenterRequests);

        /// <summary>Makes wherever the user is now the neutral.</summary>
        public static void Recenter() => System.Threading.Interlocked.Increment(ref _recenterRequests);

        /// <summary>Bumps when the row must reopen to pick up a change.</summary>
        public static int Version => _version;
    }
}
