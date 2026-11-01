using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HIDMaestro;
using PadForge.Common.Input;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// A DualShock 4's speaker over USB (#546): feature report 0xE0 routes a
    /// wired second model's or an adapter pad's USB audio, and output report
    /// 0x05 carries the speaker volume while the route plays the speaker.
    /// The report's bytes per the CUH-ZCT2E descriptor (DJm00n/ControllersInfo),
    /// duaLib's ReportFeatureInDongleSetAudio and its AudioOutput values, and
    /// mihaits's test on a wired CUH-ZCT2.
    /// </summary>
    public class Ds4UsbSpeakerTests
    {
        // duaLib's AudioOutput: 0 left and right to the headset, 1 left to the
        // headset, 2 left to the headset and right to the speaker, 3 right to
        // the speaker. Output Path 0 is Automatic, 4 Speaker Only.
        [Theory]
        [InlineData(0, 3)]
        [InlineData(1, 0)]
        [InlineData(2, 1)]
        [InlineData(3, 2)]
        [InlineData(4, 3)]
        public void EachOutputPath_TakesItsRoute(int resolvedPath, int route)
            => Assert.Equal(route, Ds4UsbAudio.RouteFor(resolvedPath));

        [Fact]
        public void TheRouteReport_IsE0_00_Route()
        {
            Assert.Equal(new byte[] { 0xE0, 0x00, 0x03 }, Ds4UsbAudio.BuildRouteReport(Ds4UsbAudio.RouteSpeaker));
            Assert.Equal(new byte[] { 0xE0, 0x00, 0x00 }, Ds4UsbAudio.BuildRouteReport(Ds4UsbAudio.RouteStereoHeadset));
            Assert.True(Ds4UsbAudio.RouteHasSpeaker(Ds4UsbAudio.RouteSpeaker));
            Assert.True(Ds4UsbAudio.RouteHasSpeaker(Ds4UsbAudio.RouteHeadsetAndSpeaker));
            Assert.False(Ds4UsbAudio.RouteHasSpeaker(Ds4UsbAudio.RouteStereoHeadset));
            Assert.False(Ds4UsbAudio.RouteHasSpeaker(Ds4UsbAudio.RouteMonoHeadset));
            Assert.False(Ds4UsbAudio.RouteHasSpeaker(-1));
        }

        /// <summary>An explicit Output Path holds its route. Automatic puts the
        /// speaker on while PadForge plays to the pad and gives the headset
        /// back once, and a pad PadForge never routed is left alone. Nothing
        /// goes out twice.</summary>
        [Theory]
        //           path, plays, last, write
        [InlineData(0, false, -1, -1)]  // never routed, nothing playing: left alone
        [InlineData(0, true, -1, 3)]    // Automatic while PadForge plays: the speaker
        [InlineData(0, true, 3, -1)]    // already there
        [InlineData(0, false, 3, 0)]    // stopped: the headset back
        [InlineData(0, false, 0, -1)]   // already back
        [InlineData(1, false, -1, 0)]   // Headphones (Stereo)
        [InlineData(1, true, 3, 0)]     // an explicit path wins over playback
        [InlineData(3, false, 0, 2)]    // Headphones + Speaker
        [InlineData(4, false, 2, 3)]    // Speaker Only
        [InlineData(4, true, 3, -1)]
        [InlineData(2, false, 2, 1)]    // Headphones (Mono)
        public void TheDispatcher_WritesARouteOnlyWhenItChanges(int path, bool plays, int last, int write)
            => Assert.Equal(write, Ds4UsbAudio.RouteToWrite(path, plays, last));

        [Theory]
        [InlineData(1, true)]
        [InlineData(2, true)]
        [InlineData(0, false)]
        [InlineData(3, false)]
        [InlineData(4, false)]
        public void OnlyTheJackPaths_AreHeadphoneOnly(int path, bool headphoneOnly)
            => Assert.Equal(headphoneOnly, Ds4UsbAudio.IsHeadphoneOnlyPath(path));

        private static Dictionary<string, object> Fields(int usbRoute, bool assertVolumes = false)
            => Ds4EffectSynthesizer.BuildFields(new DeviceSlotConfig(), 0f, 0, 0, 0, 0f, 0, 0,
                assertVolumes: assertVolumes, usbRoute: usbRoute);

        /// <summary>A route with the speaker turns the speaker volume on at its
        /// ceiling and carries byte 23. A headset route carries the route
        /// alone, and the headphone bytes stay off on USB.</summary>
        [Fact]
        public void ASpeakerRoute_CarriesTheSpeakerVolume()
        {
            var speaker = Fields(Ds4UsbAudio.RouteSpeaker);
            Assert.Equal((byte)3, speaker["ds4UsbRoute"]);
            Assert.Equal((byte)(0x07 | 0x80), speaker["validFlag0"]);
            Assert.Equal(Ds4BtAudio.VolumeMax, speaker["speakerVolume"]);
            Assert.Equal((byte)0x85, speaker["ds4UsbAudioConfig"]);

            var both = Fields(Ds4UsbAudio.RouteHeadsetAndSpeaker);
            Assert.Equal((byte)(0x07 | 0x80), both["validFlag0"]);

            var headset = Fields(Ds4UsbAudio.RouteStereoHeadset);
            Assert.Equal((byte)0, headset["ds4UsbRoute"]);
            Assert.Equal((byte)0x07, headset["validFlag0"]);
            Assert.False(headset.ContainsKey("ds4UsbAudioConfig"));

            var none = Fields(-1);
            Assert.False(none.ContainsKey("ds4UsbRoute"));
            Assert.False(none.ContainsKey("ds4UsbAudioConfig"));
            Assert.Equal((byte)0x07, none["validFlag0"]);

            // Bluetooth keeps its own volume assert (#528).
            var bt = Fields(-1, assertVolumes: true);
            Assert.Equal((byte)(0x07 | 0x10 | 0x20 | 0x80), bt["validFlag0"]);
            Assert.False(bt.ContainsKey("ds4UsbAudioConfig"));
        }

        /// <summary>The bytes as they leave for the pad: bit 0x80 of byte 1,
        /// byte 22 at 0x4F and byte 23 at 0x85, the values #546 sent. Byte 23
        /// is not in HIDMaestro's profile, so the writer places it.</summary>
        [Fact]
        public void TheUsbReport_CarriesBytes22And23()
        {
            var profile = HMaestroProfileCatalog.GetProfileById("dualshock-4-v2");
            Assert.NotNull(profile);
            var fields = Fields(Ds4UsbAudio.RouteSpeaker);
            byte[] packet = HMOutputEncoder.Encode(profile, fields);
            PlayStationEffectWriter.ApplyDs4UsbAudioConfig(packet, fields);
            Assert.Equal(0x05, packet[0]);
            Assert.Equal(0x87, packet[1]);
            Assert.Equal(0x4F, packet[22]);
            Assert.Equal(0x85, packet[23]);

            var headset = Fields(Ds4UsbAudio.RouteStereoHeadset);
            packet = HMOutputEncoder.Encode(profile, headset);
            PlayStationEffectWriter.ApplyDs4UsbAudioConfig(packet, headset);
            Assert.Equal(0x07, packet[1]);
            Assert.Equal(0, packet[23]);

            // The Bluetooth report never takes the USB byte.
            var bt = HMaestroProfileCatalog.GetProfileById("dualshock-4-v2-bt");
            Assert.NotNull(bt);
            packet = HMOutputEncoder.Encode(bt, fields);
            byte before = packet[23];
            PlayStationEffectWriter.ApplyDs4UsbAudioConfig(packet, fields);
            Assert.Equal(before, packet[23]);
        }

        [Fact]
        public void TheWriter_SendsTheRouteAfterTheOutputReport_OnUsbAlone()
        {
            string writer = RepoFile("PadForge.App", "Common", "Input", "PlayStationEffectWriter.cs");
            Assert.Contains("ApplyDs4UsbAudioConfig(packet, fields);", writer);
            Assert.Contains("return WriteRaw(devicePath, packet) && WriteDs4UsbRoute(devicePath, packet, fields);", writer);
            Assert.Contains("return written && WriteDs4UsbRoute(devicePath, packet, fields);", writer);
            Assert.Contains("if (packet[0] != 0x05) return true;", writer);
            Assert.Contains("RawHidOutput.SetFeature(devicePath, Ds4UsbAudio.BuildRouteReport(route), stampSonyBtCrc: false)", writer);
        }

        /// <summary>The dispatcher routes a USB pad with audio, never a
        /// Bluetooth one, a first model or a peer, remembers the route only once
        /// the frame lands, and forgets it when the pad comes back, so the pad
        /// gets its route again.</summary>
        [Fact]
        public void TheDispatcher_OwnsTheRoute()
        {
            string src = RepoFile("PadForge.App", "Common", "Input", "UserEffectsDispatcher.cs").Replace("\r\n", "\n");
            Assert.Contains("bool ds4Usb = !isDs5 && !isBluetooth\n"
                + "                        && AudioPassthroughService.Ds4HasAudio((ushort)ud.ProdId, false)\n"
                + "                        && !RemoteLinkOutputRouter.IsPeerPath(ud.DevicePath);", src);
            Assert.Contains("int ds4UsbRoute = ds4Usb && !s_audioRoutesReleased\n"
                + "                        ? Ds4UsbAudio.RouteToWrite(pathVal, PlaysToSpeaker(ud.InstanceGuid), lastUsbRoute,\n"
                + "                            s_routedOffHeadset.ContainsKey(ud.InstanceGuid))", src);
            Assert.Contains("assertDs4Mic, ds4UsbRoute);", src);
            Assert.Contains("(hpVol, pathVal, ds4MicArmed, ds4UsbRoute)));", src);
            Assert.Contains("_prevDs4UsbRoute.Remove(device);", src);
            Assert.Contains("if (audio.Ds4UsbRoute >= 0) _prevDs4UsbRoute[device] = audio.Ds4UsbRoute;", src);
        }

        private static string RepoFile(params string[] parts)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PadForge.sln")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return File.ReadAllText(Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray()));
        }
    }
}
