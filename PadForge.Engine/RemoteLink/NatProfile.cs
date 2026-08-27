using System;
using System.Collections.Generic;
using System.Net;

namespace PadForge.Engine.RemoteLink
{
    /// <summary>
    /// A socket's NAT behavior, learned by probing several STUN servers from
    /// the same socket (#294 symmetric-NAT support). The mapping KIND decides
    /// how a peer punches to us:
    ///
    /// - <see cref="NatKind.Cone"/>: endpoint-independent mapping. Every
    ///   destination sees the same external port, so a peer punches straight to
    ///   that one port. This is most home broadband.
    /// - <see cref="NatKind.SymmetricSequential"/>: endpoint-DEPENDENT, but the
    ///   external port advances by a small, consistent step per new destination
    ///   (measured: Verizon 5G/CGNAT increments by 2). Unpunchable by a fixed
    ///   port, but the peer can PREDICT the next ports and spray a range to hit
    ///   the real one. This is the case that makes fixed-wireless home internet
    ///   work.
    /// - <see cref="NatKind.SymmetricRandom"/>: endpoint-dependent with an
    ///   unpredictable port. Prediction degrades to a low-odds birthday spray;
    ///   this is the residual that truly needs a relay.
    /// </summary>
    public enum NatKind : byte
    {
        Unknown = 0,
        Cone = 1,
        SymmetricSequential = 2,
        SymmetricRandom = 3,
    }

    public sealed class NatProfile
    {
        public NatKind Kind { get; init; }
        public IPAddress PublicAddress { get; init; }
        /// <summary>The most recent external port observed. Prediction bases
        /// the ramp on this.</summary>
        public int LastPort { get; init; }
        /// <summary>External-port step per new destination for
        /// <see cref="NatKind.SymmetricSequential"/> (0 otherwise).</summary>
        public int Delta { get; init; }

        public bool IsSymmetric => Kind is NatKind.SymmetricSequential or NatKind.SymmetricRandom;
        public bool IsPunchable => Kind is NatKind.Cone or NatKind.SymmetricSequential;

        /// <summary>Classifies from the external ports observed across several
        /// STUN servers, IN PROBE ORDER, plus the public address. Fewer than two
        /// observations is <see cref="NatKind.Unknown"/> (can't tell).</summary>
        public static NatProfile Classify(IPAddress publicAddress, IReadOnlyList<int> portsInProbeOrder)
        {
            if (portsInProbeOrder == null || portsInProbeOrder.Count == 0)
                return new NatProfile { Kind = NatKind.Unknown, PublicAddress = publicAddress };

            int last = portsInProbeOrder[^1];
            if (portsInProbeOrder.Count == 1)
                return new NatProfile { Kind = NatKind.Unknown, PublicAddress = publicAddress, LastPort = last };

            // All equal across distinct servers => endpoint-independent (cone).
            bool allEqual = true;
            for (int i = 1; i < portsInProbeOrder.Count; i++)
                if (portsInProbeOrder[i] != portsInProbeOrder[0]) { allEqual = false; break; }
            if (allEqual)
                return new NatProfile { Kind = NatKind.Cone, PublicAddress = publicAddress, LastPort = portsInProbeOrder[0], Delta = 0 };

            // Endpoint-dependent. Are the per-destination steps small and
            // consistent (sequential allocator) or erratic (random)?
            var deltas = new List<int>(portsInProbeOrder.Count - 1);
            for (int i = 1; i < portsInProbeOrder.Count; i++)
                deltas.Add(portsInProbeOrder[i] - portsInProbeOrder[i - 1]);

            bool sequential = true;
            int minD = int.MaxValue, maxD = int.MinValue;
            foreach (int d in deltas)
            {
                // A CGNAT sequential allocator steps forward by a small amount.
                // Other subscribers share the pool, so allow some spread, but a
                // huge or negative step is "random" for our purposes.
                if (d <= 0 || d > 256) { sequential = false; break; }
                if (d < minD) minD = d;
                if (d > maxD) maxD = d;
            }
            if (sequential && (maxD - minD) <= 16)
            {
                int delta = Median(deltas);
                if (delta < 1) delta = 1;
                return new NatProfile { Kind = NatKind.SymmetricSequential, PublicAddress = publicAddress, LastPort = last, Delta = delta };
            }
            return new NatProfile { Kind = NatKind.SymmetricRandom, PublicAddress = publicAddress, LastPort = last, Delta = 0 };
        }

        private static int Median(List<int> xs)
        {
            var s = new List<int>(xs);
            s.Sort();
            return s[s.Count / 2];
        }
    }
}
