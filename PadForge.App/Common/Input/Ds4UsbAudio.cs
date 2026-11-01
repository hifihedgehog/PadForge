namespace PadForge.Common.Input
{
    /// <summary>
    /// Where a DualShock 4's USB audio plays (#546): the second model on a
    /// cable (0x09CC) and a pad on Sony's wireless adapter (0x0BA0). Feature
    /// report 0xE0 picks the route for the pad's USB audio endpoint, the way
    /// the DualSense's output path register does for its own.
    ///
    /// <para>The report is three bytes, <c>E0 00 route</c>: the CUH-ZCT2E
    /// descriptor declares 0xE0 with a 2-byte payload (usage 0xFF800057, the
    /// descriptor dump in DJm00n/ControllersInfo), the Controllers Wiki lists
    /// it as Set Audio Output for a USB pad and the adapter and gives its
    /// routes (Sony DualShock 4/Data Structures), and duaLib builds it as
    /// ReportFeatureInDongleSetAudio (dataStructures.h) from its DualShock 4
    /// output path (readDualshock4.cpp) and its reset path for a pad on USB
    /// (duaLibUtils.cpp). mihaits confirmed the routes on a wired CUH-ZCT2
    /// (#546), and they are the DualSense's output path values, so the USB
    /// sink maps channels for both through
    /// <see cref="AudioPassthroughService.MapMirrorChannels"/>.</para>
    ///
    /// <para>A route with the speaker also needs the speaker volume in output
    /// report 0x05: bit 0x80 of byte 1 (ds4mac docs/04-DS4-USB-Protocol.md
    /// 3.2) and byte 22 from 0x00 to 0x4F. #546 also sent byte 23 as 0x85.
    /// The wiki and duaLib's BTSetStateData say its two fields (UNK_AUDIO1
    /// and UNK_AUDIO2) appear to be 5 and 1 for audio, and duaLib never sends
    /// them. The byte goes out with the volume, once per route change, the
    /// way duaLib sends the volume once per change.</para>
    /// </summary>
    internal static class Ds4UsbAudio
    {
        public const byte RouteReportId = 0xE0;

        /// <summary>Left and right to the headset: the route the pad starts
        /// on, which is why its USB audio reached only the jack before #546,
        /// and the one written to give the headset back.</summary>
        public const byte RouteStereoHeadset = 0;
        /// <summary>Left to the headset.</summary>
        public const byte RouteMonoHeadset = 1;
        /// <summary>Left to the headset, right to the speaker.</summary>
        public const byte RouteHeadsetAndSpeaker = 2;
        /// <summary>Right to the speaker.</summary>
        public const byte RouteSpeaker = 3;

        /// <summary>Byte 23 of output report 0x05 while the speaker plays.
        /// HIDMaestro's dualshock-4-v2 profile declares the volumes up to byte
        /// 22 and not this one, so the writer places it after encoding.</summary>
        public const byte SpeakerAudioConfig = 0x85;
        public const int SpeakerAudioConfigOffset = 23;

        /// <summary>The route for a resolved Output Path: Headphones (Stereo)
        /// and (Mono) to the headset, Headphones + Speaker to both, and
        /// Speaker Only and Automatic to the speaker, as Automatic plays a
        /// DualSense and a Bluetooth DualShock 4 on their speakers while
        /// PadForge plays to them. Follow Headphone Jack is resolved before it
        /// gets here
        /// (<see cref="AudioPassthroughService.ResolveOutputPath"/>).</summary>
        public static byte RouteFor(int resolvedPath) => resolvedPath switch
        {
            1 => RouteStereoHeadset,
            2 => RouteMonoHeadset,
            3 => RouteHeadsetAndSpeaker,
            _ => RouteSpeaker,
        };

        public static bool RouteHasSpeaker(int route)
            => route == RouteHeadsetAndSpeaker || route == RouteSpeaker;

        /// <summary>A resolved Output Path that plays only through the jack.</summary>
        public static bool IsHeadphoneOnlyPath(int resolvedPath) => resolvedPath is 1 or 2;

        public static byte[] BuildRouteReport(byte route) => new byte[] { RouteReportId, 0x00, route };

        /// <summary>The route the effects dispatcher writes this tick, or -1.
        /// An explicit Output Path holds its route. Automatic puts the speaker
        /// on while PadForge plays to the pad and gives the headset back once
        /// it stops, as the DualSense's speaker path does, and otherwise
        /// leaves the pad alone. A route goes out only when it differs from
        /// the last one the pad took (<paramref name="lastRoute"/>, -1 for
        /// none), so a returning pad, whose re-enumeration reset its route,
        /// gets it again once the dispatcher forgets what it wrote.</summary>
        public static int RouteToWrite(int resolvedPath, bool padForgePlays, int lastRoute)
        {
            int route;
            if (resolvedPath > 0) route = RouteFor(resolvedPath);
            else if (padForgePlays) route = RouteSpeaker;
            else if (lastRoute > RouteStereoHeadset) route = RouteStereoHeadset;
            else return -1;
            return route != lastRoute ? route : -1;
        }
    }
}
