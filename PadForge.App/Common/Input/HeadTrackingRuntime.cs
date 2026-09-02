using System;

namespace PadForge.Common.Input
{
    /// <summary>
    /// The Dashboard mirror the engine reads for head tracking (issue #355),
    /// the shape of <see cref="HandheldButtonRegistry.FeatureEnabled"/>:
    /// static, written by the Dashboard view model on the UI thread, read by
    /// the poll thread's device sweep and by the device on every read.
    ///
    /// <para><see cref="Version"/> bumps on a change that needs the row
    /// reopened (the UDP port, the FreeTrack toggle). The two ranges are
    /// read live on every poll, so a range edit takes effect at once.</para>
    /// </summary>
    internal static class HeadTrackingRuntime
    {
        public const int DefaultUdpPort = 4242;
        public const int DefaultRotationRangeDeg = 90;
        public const int DefaultTranslationRangeCm = 30;

        private static volatile bool _enabled;
        private static volatile int _udpPort = DefaultUdpPort;
        private static volatile bool _freeTrackEnabled = true;
        private static volatile int _rotationRangeDeg = DefaultRotationRangeDeg;
        private static volatile int _translationRangeCm = DefaultTranslationRangeCm;
        private static volatile int _version;

        /// <summary>The Dashboard toggle. Off by default: no device row, no
        /// socket, no file mapping, no thread.</summary>
        public static bool Enabled
        {
            get => _enabled;
            set => _enabled = value;
        }

        /// <summary>UDP port OpenTrack's "UDP over network" output sends to.</summary>
        public static int UdpPort
        {
            get => _udpPort;
            set
            {
                int v = Math.Clamp(value, 1, 65535);
                if (_udpPort == v) return;
                _udpPort = v;
                _version++;
            }
        }

        /// <summary>Whether the FreeTrack 2.0 shared memory is read as well.</summary>
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
        /// to full deflection.</summary>
        public static int RotationRangeDeg
        {
            get => _rotationRangeDeg;
            set => _rotationRangeDeg = Math.Clamp(value, 1, 180);
        }

        /// <summary>Head travel, in centimeters, that moves a translation
        /// axis to full deflection.</summary>
        public static int TranslationRangeCm
        {
            get => _translationRangeCm;
            set => _translationRangeCm = Math.Clamp(value, 1, 500);
        }

        /// <summary>Bumps when the row must reopen to pick up a change.</summary>
        public static int Version => _version;
    }
}
