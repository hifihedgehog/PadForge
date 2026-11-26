using System;
using System.Collections.Generic;
using System.Threading;
using PadForge.Common.Input;
using PadForge.Engine.Privacy;

namespace PadForge.Services
{
    public partial class InputService
    {
        /// <summary>
        /// The problem recorder's share of the UI tick (#600): what Problem
        /// Recording macros asked for since the last tick, taken from the
        /// engine and done here, where a recording's file is made, and a
        /// look at the program in front.
        /// </summary>
        private void TickProblemRecorder()
        {
            var recorder = ProblemRecorder.Instance;
            int asked = Interlocked.Exchange(ref _inputManager.PendingProblemRecording, 0);
            if ((asked & InputManager.PendingRecordingToggle) != 0)
                ProblemRecordingAsked?.Invoke(recorder.Toggle(ProblemRecorder.HowMacro));
            if ((asked & InputManager.PendingRecordingSave) != 0)
                ProblemRecordingAsked?.Invoke(recorder.SaveLastMinute());
            recorder.UiTick();
        }

        /// <summary>Raised on the UI thread with what the recorder answered
        /// a Problem Recording macro, so the window can say why a recording
        /// did not start.</summary>
        internal event Action<RecorderResult> ProblemRecordingAsked;

        /// <summary>Adds Remote Link's private values to a problem
        /// recording's list of what this PC could give away: this PC's key
        /// and fingerprint, and each paired PC's key, name and host. A key is
        /// listed as base64, the form the settings file holds, and as hex,
        /// the form the diagnostics lines write.</summary>
        internal void CollectPrivateValues(List<(PrivateKind Kind, string Value)> into)
        {
            var identity = _remoteLinkIdentity;
            if (identity != null)
            {
                AddKey(into, identity.PublicKey);
                AddFingerprint(into, identity.FingerprintHex);
            }
            var peers = _settingsService?.RemoteLink?.Trust?.Peers;
            if (peers == null) return;
            foreach (var peer in peers)
            {
                if (peer == null) continue;
                into.Add((PrivateKind.Key, peer.PublicKeyBase64));
                try
                {
                    byte[] key = Convert.FromBase64String(peer.PublicKeyBase64 ?? string.Empty);
                    AddKey(into, key);
                    if (key.Length > 0) AddFingerprint(into, Convert.ToHexString(PadForge.Engine.RemoteLink.PeerCrypto.Fingerprint(key)));
                }
                catch (FormatException) { }
                into.Add((PrivateKind.Peer, peer.Name));
                into.Add((PrivateKind.HostName, peer.HostName));
            }
        }

        /// <summary>A key as base64 and as hex, and the first 16 hex digits
        /// the Remote Link lines write of a key.</summary>
        private static void AddKey(List<(PrivateKind Kind, string Value)> into, byte[] key)
        {
            if (key == null || key.Length == 0) return;
            into.Add((PrivateKind.Key, Convert.ToBase64String(key)));
            string hex = Convert.ToHexString(key);
            into.Add((PrivateKind.Key, hex));
            if (hex.Length > 16) into.Add((PrivateKind.Key, hex.Substring(0, 16)));
        }

        /// <summary>A fingerprint whole and as the first 8 hex digits the
        /// Remote Link lines name a peer by.</summary>
        private static void AddFingerprint(List<(PrivateKind Kind, string Value)> into, string hex)
        {
            if (string.IsNullOrEmpty(hex)) return;
            into.Add((PrivateKind.Peer, hex));
            if (hex.Length > 8) into.Add((PrivateKind.Peer, hex.Substring(0, 8)));
        }
    }
}
