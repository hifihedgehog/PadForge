using System;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using PadForge.Engine;
using PadForge.Engine.Common;
using Windows.Devices.Sensors;

namespace PadForge.Common.Input
{
    /// <summary>
    /// The machine's own gyroscope and accelerometer through the Windows
    /// sensor stack, as a motion-only <see cref="ISdlInputDevice"/>
    /// (issue #343). Handheld PCs put the IMU in the tablet, not in the
    /// controller halves, so no gamepad SDL opens carries it. Same shape as
    /// <see cref="SonyHeadsetMotionDevice"/>: the sensor's own callback
    /// publishes under a lock, the poll reads a pooled clone, and silence
    /// past a stale window reads as offline.
    ///
    /// <para>Frame. Windows sensors: X toward the screen's right edge, Y
    /// toward its top edge, Z out of the screen (learn.microsoft.com,
    /// sensor-orientation). A handheld held to play has its screen facing
    /// the player, so in SDL's controller frame (X right, Y up, Z toward
    /// the player) SDL X = Windows X, SDL Y = Windows Y, SDL Z = Windows Z,
    /// the identity map. Gyrometer reports degrees per second, converted to
    /// rad/s. Accelerometer reports g-force in the gravity direction (a
    /// face-up device at rest reads -1 g on Z), where SDL reports the
    /// reaction (a device at rest reads +9.8 on its up axis), so the
    /// accelerometer is negated and scaled to m/s².</para>
    /// </summary>
    internal sealed class SystemMotionDevice : ISdlInputDevice
    {
        internal const int StaleWindowMs = 5000;
        private const float DegToRad = (float)(Math.PI / 180.0);
        private const float StandardGravity = 9.80665f;

        private readonly object _stateLock = new();
        private readonly CustomInputState _state = new();
        private readonly Func<long> _nowTicks;
        private readonly long _staleTicks;
        private long _lastSampleTicks;
        private bool _everReceived;
        private volatile bool _attached;
        private volatile bool _disposed;

        private Gyrometer _gyro;
        private Accelerometer _accel;
        private long _samples;

        public SystemMotionDevice(MachineIdentity machine, Func<long> nowTicksProvider = null)
        {
            _nowTicks = nowTicksProvider ?? Stopwatch.GetTimestamp;
            _staleTicks = Stopwatch.Frequency * StaleWindowMs / 1000;
            string key = machine?.Key ?? string.Empty;
            Name = (machine?.DisplayName ?? "This PC") + " Motion";
            DevicePath = "sensor://motion";
            InstanceGuid = Md5Guid("pfsysmotion:" + key);
            ProductGuid = Md5Guid("pfsysmotion-product");
            SdlInstanceId = SyntheticInstanceId.From(DevicePath);
            _lastSampleTicks = _nowTicks();
        }

        /// <summary>True when the machine exposes a gyrometer. A WinRT
        /// query: worker thread only.</summary>
        internal static bool IsAvailable()
        {
            try { return Gyrometer.GetDefault() != null; }
            catch { return false; }
        }

        public uint SdlInstanceId { get; }
        public string Name { get; }
        public int NumAxes => 0;
        public int NumButtons => 0;
        public int RawButtonCount => 0;
        public int NumHats => 0;
        public int[] SupportedButtonIndices => Array.Empty<int>();
        public IntPtr GamepadHandle => IntPtr.Zero;
        public bool HasRumble => false;
        public bool HasRumbleTriggers => false;
        public bool HasHaptic => false;
        public bool HasGyro => true;
        public bool HasAccel { get; private set; }
        public bool HasTouchpad => false;
        public HapticEffectStrategy HapticStrategy => HapticEffectStrategy.None;
        public IntPtr HapticHandle => IntPtr.Zero;
        public uint HapticFeatures => 0;
        public int NumHapticAxes => 0;
        public bool IsAttached => _attached && !_disposed;
        public ushort VendorId => 0x5359; // "SY"
        public ushort ProductId => 0x4D4F; // "MO"
        public Guid InstanceGuid { get; }
        public Guid ProductGuid { get; }
        public string DevicePath { get; }
        public string SerialNumber => string.Empty;
        public string SdlGuid => string.Empty;

        public int GetInputDeviceType() => InputDeviceType.SystemMotion;
        public bool SetRumble(ushort low, ushort high, uint durationMs = uint.MaxValue) => false;
        public bool StopRumble() => false;
        public DeviceObjectItem[] GetDeviceObjects() => Array.Empty<DeviceObjectItem>();

        /// <summary>Subscribes to the sensors at their fastest interval.
        /// WinRT calls: worker thread only.</summary>
        public bool Open()
        {
            if (_disposed) return false;
            try
            {
                _gyro = Gyrometer.GetDefault();
                if (_gyro == null) return false;
                uint min = _gyro.MinimumReportInterval;
                _gyro.ReportInterval = min > 0 ? min : 0;
                _gyro.ReadingChanged += OnGyro;

                _accel = Accelerometer.GetDefault();
                if (_accel != null)
                {
                    uint amin = _accel.MinimumReportInterval;
                    _accel.ReportInterval = amin > 0 ? amin : 0;
                    _accel.ReadingChanged += OnAccel;
                    HasAccel = true;
                }
                _attached = true;
                PadForge.Engine.SdlDiagLog.WriteLine(
                    $"System motion: gyrometer at {_gyro.ReportInterval} ms, accelerometer {(HasAccel ? "present" : "absent")}");
                return true;
            }
            catch (Exception ex)
            {
                PadForge.Engine.SdlDiagLog.WriteLine("System motion: open failed " + ex.Message);
                Unsubscribe();
                return false;
            }
        }

        private void OnGyro(Gyrometer sender, GyrometerReadingChangedEventArgs args)
        {
            var r = args.Reading;
            if (r == null) return;
            Publish((float)r.AngularVelocityX * DegToRad, (float)r.AngularVelocityY * DegToRad,
                (float)r.AngularVelocityZ * DegToRad, null);
        }

        private void OnAccel(Accelerometer sender, AccelerometerReadingChangedEventArgs args)
        {
            var r = args.Reading;
            if (r == null) return;
            Publish(null, null, null, new[]
            {
                (float)(-r.AccelerationX * StandardGravity),
                (float)(-r.AccelerationY * StandardGravity),
                (float)(-r.AccelerationZ * StandardGravity),
            });
        }

        private void Publish(float? gx, float? gy, float? gz, float[] accel)
        {
            long now = _nowTicks();
            lock (_stateLock)
            {
                if (gx.HasValue)
                {
                    _state.Gyro[0] = gx.Value;
                    _state.Gyro[1] = gy.Value;
                    _state.Gyro[2] = gz.Value;
                }
                if (accel != null)
                {
                    _state.Accel[0] = accel[0];
                    _state.Accel[1] = accel[1];
                    _state.Accel[2] = accel[2];
                }
                _lastSampleTicks = now;
                _everReceived = true;
            }
            long n = ++_samples;
            if ((n <= 64 && (n & (n - 1)) == 0) || (n & 4095) == 0)
                PadForge.Engine.SdlDiagLog.WriteLine(
                    $"System motion: sample #{n} gyro=({_state.Gyro[0]:F3},{_state.Gyro[1]:F3},{_state.Gyro[2]:F3}) accel=({_state.Accel[0]:F2},{_state.Accel[1]:F2},{_state.Accel[2]:F2})");
        }

        /// <summary>Test seam: a gyro (deg/s) and accel (g) sample as the
        /// sensors would deliver them.</summary>
        internal void InjectSample(double[] gyroDegPerSec, double[] accelG)
        {
            if (gyroDegPerSec != null)
                Publish((float)gyroDegPerSec[0] * DegToRad, (float)gyroDegPerSec[1] * DegToRad,
                    (float)gyroDegPerSec[2] * DegToRad, null);
            if (accelG != null)
                Publish(null, null, null, new[]
                {
                    (float)(-accelG[0] * StandardGravity),
                    (float)(-accelG[1] * StandardGravity),
                    (float)(-accelG[2] * StandardGravity),
                });
        }

        internal void AttachForTest(bool hasAccel)
        {
            HasAccel = hasAccel;
            _attached = true;
        }

        private PooledInputStatePair _statePool;

        public CustomInputState GetCurrentState(bool forceRaw = false)
        {
            if (_disposed || !_attached) return null;
            lock (_stateLock)
            {
                if (_everReceived && (_nowTicks() - _lastSampleTicks) > _staleTicks)
                    return null;
                var dst = _statePool.Next();
                _state.CopyInto(dst);
                return dst;
            }
        }

        private void Unsubscribe()
        {
            try { if (_gyro != null) _gyro.ReadingChanged -= OnGyro; } catch { }
            try { if (_accel != null) _accel.ReadingChanged -= OnAccel; } catch { }
            _gyro = null;
            _accel = null;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _attached = false;
            Unsubscribe();
        }

        private static Guid Md5Guid(string identifier)
        {
            using var md5 = MD5.Create();
            return new Guid(md5.ComputeHash(Encoding.UTF8.GetBytes(identifier)));
        }
    }
}
