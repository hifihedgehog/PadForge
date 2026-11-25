using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;

namespace PadForge.Common.Recording
{
    /// <summary>
    /// The axes a force-feedback device's motors act on, by name (#600), read
    /// as SDL finds them for its haptic device: DirectInput's objects of the
    /// device that are axes with DIDOI_FFACTUATOR set (SDL_dinputhaptic.c,
    /// DI_DeviceObjectCallback). SDL keeps them to itself and gives only a
    /// count (SDL_GetNumHapticAxes). Each axis is named in fixed English by
    /// its type, never in the device's own words.
    ///
    /// <para>The read goes through PadForge's one DirectInput enumerator,
    /// RecorderReaders.EachDevice, which a problem recording's object read
    /// uses too.</para>
    ///
    /// <para>DirectInput's enumeration can take a long time on some PCs (SDL
    /// says so beside its own), so nothing here waits on it: a product asked
    /// for and not yet answered starts one read on a thread of its own, which
    /// takes every attached force-feedback game controller at once. A
    /// product the read did not list is kept as a miss, so DirectInput is
    /// asked again only for a product it has not answered for.</para>
    ///
    /// <para>The test host reads no device of the machine's: with
    /// <see cref="OffLimitsForTest"/> set, the names come from
    /// <see cref="Seam"/> or not at all.</para>
    /// </summary>
    internal static unsafe class ForceActuators
    {
        /// <summary>Set by the test assembly for the life of its process,
        /// never by the app.</summary>
        internal static bool OffLimitsForTest;

        /// <summary>Test seam: the names by vendor and product.</summary>
        internal static Func<ushort, ushort, IReadOnlyList<string>> Seam;

        /// <summary>Test seam: whether <see cref="Kept"/> answers from
        /// <see cref="Seam"/>, as after a read, or finds nothing yet.</summary>
        internal static bool SeamKept = true;

        /// <summary>This process's reads of DirectInput.</summary>
        private static readonly Cache Shared = new(() => ReadAll());

        /// <summary>The names a read already gave, or null, never waiting: a
        /// product no read has answered for is asked for, and
        /// <see cref="ReadSoon"/> reads for it.</summary>
        internal static IReadOnlyList<string> Kept(ushort vendor, ushort product)
        {
            var seam = Seam;
            if (seam != null) return SeamKept ? seam(vendor, product) : null;
            return OffLimitsForTest ? null : Shared.Kept(Product(vendor, product));
        }

        /// <summary>Whether a read found no force-feedback game controller of
        /// this product.</summary>
        internal static bool Missed(ushort vendor, ushort product)
            => Seam == null && !OffLimitsForTest && Shared.Missed(Product(vendor, product));

        /// <summary>Starts a read on a thread of its own for the products
        /// asked for and not yet answered, when no read runs now.</summary>
        internal static void ReadSoon()
        {
            if (Seam != null || OffLimitsForTest) return;
            Shared.ReadSoon();
        }

        /// <summary>
        /// The names DirectInput gave by product, and the products a read
        /// found no force-feedback game controller of. Its lists are replaced
        /// whole after a read and never changed after, so a look takes no
        /// lock but the short one an ask takes.
        /// </summary>
        internal sealed class Cache
        {
            private readonly Func<Dictionary<uint, string[]>> _read;
            private readonly object _sync = new();
            private readonly HashSet<uint> _asked = new();
            private volatile Dictionary<uint, string[]> _known;
            private volatile HashSet<uint> _missed = new();
            private int _reading;

            internal Cache(Func<Dictionary<uint, string[]>> read) => _read = read;

            /// <summary>The names a read gave for a product, or null. A product
            /// neither answered nor missed is asked for.</summary>
            internal IReadOnlyList<string> Kept(uint product)
            {
                var known = _known;
                if (known != null && known.TryGetValue(product, out var names)) return names;
                if (product != 0 && !_missed.Contains(product))
                    lock (_sync) _asked.Add(product);
                return null;
            }

            internal bool Missed(uint product) => _missed.Contains(product);

            /// <summary>Starts a read for the products asked for when no read
            /// runs now, on a thread of its own, or through
            /// <paramref name="start"/> for a test.</summary>
            internal void ReadSoon(Func<Action, System.Threading.Tasks.Task> start = null)
            {
                uint[] asked;
                lock (_sync)
                {
                    if (_asked.Count == 0) return;
                    asked = new uint[_asked.Count];
                    _asked.CopyTo(asked);
                }
                if (System.Threading.Interlocked.Exchange(ref _reading, 1) != 0) return;
                (start ?? System.Threading.Tasks.Task.Run)(() =>
                {
                    try { Read(asked); }
                    finally { System.Threading.Volatile.Write(ref _reading, 0); }
                });
            }

            private void Read(uint[] asked)
            {
                Dictionary<uint, string[]> read = null;
                try { read = _read(); }
                catch (Exception ex)
                {
                    // A diagnostics read never takes PadForge down.
                    PadForge.Engine.SdlDiagLog.WriteLine("RECORDER motor axes " + ex.GetType().Name + ": " + ex.Message);
                }
                // A product asked for and not listed is a miss, and so is
                // every one asked when DirectInput could not be asked.
                var missed = new HashSet<uint>(_missed);
                foreach (uint product in asked)
                    if (read == null || !read.ContainsKey(product)) missed.Add(product);
                if (read != null) _known = read;
                _missed = missed;
                lock (_sync) _asked.ExceptWith(asked);
            }
        }

        /// <summary>A product as DirectInput's product GUID begins for a
        /// HID device: the product ID above the vendor ID.</summary>
        internal static uint Product(ushort vendor, ushort product) => vendor | (uint)product << 16;

        /// <summary>The product a DirectInput product GUID names: for a HID
        /// device the GUID ends in "PIDVID" and begins with the product above
        /// the vendor. Any other GUID names no product, 0.</summary>
        internal static uint ProductOf(Guid product)
        {
            byte* bytes = (byte*)&product;
            // Data4 begins 8 bytes in, and its last six spell PIDVID.
            byte* mark = bytes + 10;
            bool pidVid = mark[0] == (byte)'P' && mark[1] == (byte)'I' && mark[2] == (byte)'D'
                && mark[3] == (byte)'V' && mark[4] == (byte)'I' && mark[5] == (byte)'D';
            return pidVid ? *(uint*)bytes : 0;
        }

        /// <summary>Names as a line says them: X Axis, Y Axis and Z Axis.</summary>
        internal static string Join(IReadOnlyList<string> names)
        {
            if (names == null || names.Count == 0) return string.Empty;
            if (names.Count == 1) return names[0];
            var all = new string[names.Count - 1];
            for (int i = 0; i < all.Length; i++) all[i] = names[i];
            return string.Join(", ", all) + " and " + names[names.Count - 1];
        }

        /// <summary>The fixed English name of an axis of a type.</summary>
        internal static string NameOf(Guid type, uint offset)
        {
            if (type == XAxis) return "X Axis";
            if (type == YAxis) return "Y Axis";
            if (type == ZAxis) return "Z Axis";
            if (type == RxAxis) return "X Rotation";
            if (type == RyAxis) return "Y Rotation";
            if (type == RzAxis) return "Z Rotation";
            if (type == Slider) return "Slider";
            return "the axis at offset " + offset.ToString(CultureInfo.InvariantCulture);
        }

        // ─────────────────────────────────────────────
        //  DirectInput, as dinput.h of the Windows SDK lays it out
        // ─────────────────────────────────────────────

        internal const uint GameControllers = 4;             // DI8DEVCLASS_GAMECTRL
        internal const uint PointingDevices = 2;             // DI8DEVCLASS_POINTER
        internal const uint AttachedOnly = 0x1;              // DIEDFL_ATTACHEDONLY
        internal const uint ForceFeedbackOnly = 0x100;       // DIEDFL_FORCEFEEDBACK
        private const uint Axes = 0x3;                       // DIDFT_AXIS
        private const uint Actuator = 0x1;                   // DIDOI_FFACTUATOR

        private static readonly Guid XAxis = new("A36D02E0-C9F3-11CF-BFC7-444553540000");
        private static readonly Guid YAxis = new("A36D02E1-C9F3-11CF-BFC7-444553540000");
        private static readonly Guid ZAxis = new("A36D02E2-C9F3-11CF-BFC7-444553540000");
        private static readonly Guid RxAxis = new("A36D02F4-C9F3-11CF-BFC7-444553540000");
        private static readonly Guid RyAxis = new("A36D02F5-C9F3-11CF-BFC7-444553540000");
        private static readonly Guid RzAxis = new("A36D02E3-C9F3-11CF-BFC7-444553540000");
        private static readonly Guid Slider = new("A36D02E4-C9F3-11CF-BFC7-444553540000");

        /// <summary>DIDEVICEINSTANCEW.</summary>
        [StructLayout(LayoutKind.Sequential)]
        internal struct DeviceInstance
        {
            public uint Size;
            public Guid Instance;
            public Guid Product;
            public uint DeviceType;
            public fixed char InstanceName[260];
            public fixed char ProductName[260];
            public Guid ForceFeedbackDriver;
            public ushort UsagePage;
            public ushort Usage;
        }

        /// <summary>DIDEVICEOBJECTINSTANCEW.</summary>
        [StructLayout(LayoutKind.Sequential)]
        internal struct ObjectInstance
        {
            public uint Size;
            public Guid Type;
            public uint Offset;
            public uint ObjectType;
            public uint Flags;
            public fixed char Name[260];
            public uint ForceFeedbackMaxForce;
            public uint ForceFeedbackForceResolution;
            public ushort CollectionNumber;
            public ushort DesignatorIndex;
            public ushort UsagePage;
            public ushort Usage;
            public uint Dimension;
            public ushort Exponent;
            public ushort ReportId;
        }

        /// <summary>Every attached force-feedback game controller DirectInput
        /// lists, by product, with the names of its motors' axes. Null when
        /// DirectInput cannot be asked. A live test asks another class of
        /// device, with every axis, to see the same calls answer. A product
        /// already read is not created again.</summary>
        internal static Dictionary<uint, string[]> ReadAll(uint deviceClass = GameControllers,
            uint flags = AttachedOnly | ForceFeedbackOnly, bool actuatorsOnly = true)
        {
            var known = new Dictionary<uint, string[]>();
            bool asked = RecorderReaders.EachDevice(deviceClass, flags, product => !known.ContainsKey(ProductOf(product)), (device, product) =>
            {
                var objects = RecorderReaders.ObjectsOf(device, Axes);
                if (objects == null) return true;
                var names = new List<string>();
                foreach (var item in objects)
                {
                    // SDL's test: an axis object whose flags say it is an actuator.
                    if ((item.ObjectType & Axes) != 0 && (!actuatorsOnly || (item.Flags & Actuator) != 0))
                        names.Add(NameOf(item.Type, item.Offset));
                }
                known[ProductOf(product)] = names.ToArray();
                return true;
            });
            return asked ? known : null;
        }
    }
}
