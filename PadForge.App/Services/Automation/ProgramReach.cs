using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Serialization;

namespace PadForge.Services.Automation
{
    /// <summary>A device the user picked for a program's reach, by its
    /// instance and the name it had when picked.</summary>
    internal sealed record ReachDevice(Guid Id, string Name);

    /// <summary>
    /// What a program may drive and change (#600 DESIGN-2 N3): all of
    /// PadForge, as every program could before, or only its own devices
    /// and the ones the user picked. It never limits what a program reads:
    /// every device stays readable, keyboards and mice included, at the
    /// level the program has. Kept on the consent record, a web grant and a
    /// network key. Null stands for all of PadForge.
    /// </summary>
    internal sealed record ProgramReach
    {
        /// <summary>Its own devices and these.</summary>
        public IReadOnlyList<ReachDevice> Devices { get; init; } = Array.Empty<ReachDevice>();

        /// <summary>Only its own devices, none picked.</summary>
        internal static readonly ProgramReach OwnDevices = new();

        /// <summary>Whether a device is one of the picked.</summary>
        internal bool Picked(Guid device) => Devices.Any(d => d.Id == device);

        /// <summary>The reach a connection works within: the one the user
        /// kept, or the program's own devices while that is all of PadForge
        /// and its hello asked for them. A hello never widens what the user
        /// kept, and never drops the devices the user picked, which only the
        /// user changes. Either may be null, for all of PadForge.</summary>
        internal static ProgramReach Within(ProgramReach kept, ProgramReach asked) => kept ?? asked;

        /// <summary>The reach as PadForge.xml keeps it: null for all of
        /// PadForge.</summary>
        internal ReachData ToData() => new() { Devices = Devices.Select(d => new ReachDeviceData { Id = d.Id, Name = d.Name }).ToArray() };

        internal static ProgramReach FromData(ReachData data)
            => data == null ? null : new ProgramReach
            {
                Devices = (data.Devices ?? Array.Empty<ReachDeviceData>()).Where(d => d != null && d.Id != Guid.Empty)
                    .GroupBy(d => d.Id).Select(g => new ReachDevice(g.Key, g.First().Name)).Take(MostDevices).ToList(),
            };

        /// <summary>The most devices one reach picks.</summary>
        internal const int MostDevices = 64;

        public bool Equals(ProgramReach other)
            => other != null && Devices.Select(d => d.Id).SequenceEqual(other.Devices.Select(d => d.Id));

        public override int GetHashCode() => Devices.Count;
    }

    /// <summary>A limited reach in PadForge.xml (#600 DESIGN-2 N3): present
    /// only while a program is held to its own devices and these.</summary>
    public sealed class ReachData
    {
        [XmlElement("Device")]
        public ReachDeviceData[] Devices { get; set; }
    }

    /// <summary>One picked device of a reach in PadForge.xml.</summary>
    public sealed class ReachDeviceData
    {
        [XmlAttribute] public Guid Id { get; set; }
        [XmlAttribute] public string Name { get; set; }
    }
}
