using System;
using System.Collections.Generic;

namespace PadForge.Common.Input
{
    /// <summary>The MIDI 1.0 channel voice messages PadForge sends.</summary>
    internal enum Midi1Status : byte
    {
        NoteOff = 0x8,
        NoteOn = 0x9,
        ControlChange = 0xB,
    }

    /// <summary>
    /// One Windows MIDI Services API: the in-box Windows.Devices.Midi2
    /// (<see cref="MidiBackendInBox"/>) or the older App SDK runtime
    /// (<see cref="MidiBackendAppSdk"/>). The two expose the same calls under
    /// different namespaces and class IDs, so everything PadForge does with
    /// MIDI goes through this seam and the bounded lifecycle around it
    /// (<see cref="MidiVirtualController"/>, <see cref="MidiInputDevice"/>,
    /// <see cref="MidiInputRuntime"/>) stays one copy.
    ///
    /// <para>Every member can be service RPC and can hang when the service
    /// is broken. Callers bound each call and never wait on one from the
    /// polling thread.</para>
    /// </summary>
    internal interface IMidiBackend
    {
        MidiApiKind Kind { get; }

        /// <summary>Loads the API and asks the service to be available.
        /// False when it is not.</summary>
        bool Start();

        /// <summary>Releases what <see cref="Start"/> loaded.
        /// <paramref name="skipDispose"/> abandons instead of disposing, for
        /// teardown while the service may already be going away.</summary>
        void Stop(bool skipDispose);

        /// <summary>Creates one virtual device with a single MIDI 1.0
        /// function block, connects to its device side and opens the
        /// connection. On failure it tears down what it built and throws
        /// <see cref="InvalidOperationException"/> naming the failed step.</summary>
        IMidiVirtualEndpoint CreateVirtualEndpoint(string deviceName, string uniqueId, int padIndex);

        /// <summary>A session for input connections, or null when the
        /// service refuses one.</summary>
        IMidiInputSession CreateInputSession(string name);

        /// <summary>The endpoints a client can connect to: normal message
        /// endpoints only, with their names.</summary>
        List<(string Id, string Name)> EnumerateNormalEndpoints();
    }

    /// <summary>An open virtual device.</summary>
    internal interface IMidiVirtualEndpoint
    {
        /// <summary>Sends one MIDI 1.0 channel voice message on group 0.
        /// Throws when the service fails the send.</summary>
        void Send(Midi1Status status, int channel, int data1, int data2);

        /// <summary>Disconnects the device-side connection from the session.</summary>
        void DisconnectConnection();

        /// <summary>Closes the session.</summary>
        void CloseSession();
    }

    /// <summary>The shared session MIDI input connections live on.</summary>
    internal interface IMidiInputSession : IDisposable
    {
        /// <summary>Creates a connection to <paramref name="endpointId"/> and
        /// subscribes <paramref name="onWords"/> before it opens, so no
        /// message is missed. The callback gets each message's first two UMP
        /// words. Null when the service refuses the connection.</summary>
        IMidiInputConnection CreateConnection(string endpointId, Action<uint, uint> onWords);
    }

    /// <summary>One input connection.</summary>
    internal interface IMidiInputConnection
    {
        bool Open();

        /// <summary>Stops delivering messages to the callback.</summary>
        void Detach();

        /// <summary>Removes the connection from its session.</summary>
        void Disconnect();
    }
}
