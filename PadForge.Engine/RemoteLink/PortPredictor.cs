using System;
using System.Collections.Generic;
using System.Net;

namespace PadForge.Engine.RemoteLink
{
    /// <summary>
    /// Turns a peer's NAT profile into the set of endpoints to spray at during a
    /// punch (#294 symmetric-NAT support). The hard case is a
    /// <see cref="NatKind.SymmetricSequential"/> peer (Verizon 5G / T-Mobile
    /// home internet / most CGNAT): it allocates a NEW external port for each
    /// destination, so a fixed port never works, but the allocation ADVANCES
    /// predictably. When the peer sends its first probe to us, its NAT allocates
    /// a port near its last-seen one; we spray a forward window of predicted
    /// ports so one of ours lands on the port it actually allocated, and because
    /// the peer's outbound probe already opened that mapping toward us, our
    /// probe is let in. Both sides do this; the path opens with no relay.
    ///
    /// Prediction is grounded in the peer's own measurement: its last external
    /// port P and its per-destination step D (from probing several STUN servers
    /// in order). The window ramps P+D, P+2D, … and, because other subscribers
    /// share a sequential CGNAT pool and perturb the exact next value, also
    /// covers the immediate neighborhood around each predicted port.
    /// </summary>
    public static class PortPredictor
    {
        /// <summary>Default forward window (number of D-steps) sprayed for a
        /// sequential-symmetric peer. 512 steps at the measured delta covers a
        /// wide allocation drift while staying a ~1-2 s spray. Tunable per call.</summary>
        public const int DefaultWindowSteps = 512;

        /// <summary>Neighborhood radius sprayed around each predicted port to
        /// absorb the jitter from other subscribers allocating concurrently on
        /// the shared CGNAT pool.</summary>
        public const int DefaultNeighborhood = 2;

        /// <summary>
        /// Builds the ordered endpoint list to spray at a peer, given the peer's
        /// public address and NAT profile plus any raw candidate endpoints it
        /// advertised (public-from-STUN, private-LAN). Order matters: the most
        /// likely targets go first so a hit lands fast.
        ///
        /// - The raw advertised endpoints first (a cone peer, or the LAN path,
        ///   connects immediately with no prediction).
        /// - For a sequential-symmetric peer, the predicted forward window on
        ///   its public IP: P+D, P-neighborhood..P+neighborhood around each,
        ///   ramping outward.
        /// De-duplicated, port-bounded to 1..65535.
        /// </summary>
        public static IReadOnlyList<IPEndPoint> BuildSprayTargets(
            IPAddress peerPublicAddress, NatProfile peerProfile,
            IReadOnlyList<IPEndPoint> rawCandidates,
            int windowSteps = DefaultWindowSteps, int neighborhood = DefaultNeighborhood)
        {
            var result = new List<IPEndPoint>();
            var seen = new HashSet<(string, int)>();

            void Add(IPAddress ip, int port)
            {
                if (ip == null || port < 1 || port > 65535) return;
                // The whole address, not its low 32 bits. MapToIPv4 on a real
                // IPv6 address keeps only the last four bytes, so two distinct
                // v6 candidates that happen to share that suffix collided and
                // the second was silently dropped from the spray. The v6 lane
                // is the no-NAT-to-punch path, so losing a candidate there
                // costs the easiest win available.
                string ipKey = ip.ToString();
                if (!seen.Add((ipKey, port))) return;
                result.Add(new IPEndPoint(ip, port));
            }

            // 1. Everything the peer directly advertised comes first: a cone
            //    peer or a shared-LAN path is an immediate hit, no prediction.
            if (rawCandidates != null)
                foreach (var ep in rawCandidates) Add(ep.Address, ep.Port);

            // 2. Predicted window for a sequential-symmetric peer.
            if (peerProfile is { Kind: NatKind.SymmetricSequential }
                && peerPublicAddress != null && peerProfile.LastPort > 0)
            {
                int d = Math.Max(1, peerProfile.Delta);
                int basePort = peerProfile.LastPort;
                for (int k = 1; k <= windowSteps; k++)
                {
                    int predicted = basePort + k * d;
                    if (predicted > 65535) break;
                    Add(peerPublicAddress, predicted);
                    for (int n = 1; n <= neighborhood; n++)
                    {
                        Add(peerPublicAddress, predicted - n);
                        Add(peerPublicAddress, predicted + n);
                    }
                }
            }

            return result;
        }

        /// <summary>How many datagrams a spray of the given size sends, so the
        /// caller can bound the burst. One per target.</summary>
        public static int SprayCount(IReadOnlyList<IPEndPoint> targets) => targets?.Count ?? 0;
    }
}
