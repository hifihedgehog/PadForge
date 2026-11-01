using System;
using System.Buffers.Binary;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using PadForge.Common.Input;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// DualShock 4 Bluetooth audio (#528): the mode byte, the headphone jack
    /// route and volume, the headset microphone's reports and decode, and the
    /// steady silence. Byte values follow the hbashton DS4Windows fork
    /// (68a5c64), ds4mac and ds4drv, which the source comments cite.
    /// </summary>
    public class Ds4BtAudioTests
    {
        internal static byte[] Msbc(int index)
        {
            byte[] all = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "TestData", "Sbc", "msbc.msbc"));
            return all.AsSpan(index * SbcDecoder.MsbcFrameLength, SbcDecoder.MsbcFrameLength).ToArray();
        }

        /// <summary>A Bluetooth input report as a 547-byte read returns it:
        /// state when <paramref name="state"/>, audio flagged when
        /// <paramref name="audio"/>, an audio block at
        /// <paramref name="audioAt"/> (0 for none) and a valid CRC.</summary>
        internal static byte[] InputReport(byte id, bool state, bool audio, int audioAt = 0,
                                           byte target = Ds4BtAudio.MicTargetGenuine, ushort sequence = 0,
                                           byte status = 0x08, params byte[][] frames)
        {
            int length = Ds4BtAudio.InputReportLength(id);
            var r = new byte[Ds4BtAudio.MaxInputReportSize];
            r[0] = id;
            r[1] = (byte)(state ? 0xC0 : 0x40);
            r[2] = (byte)(audio ? 0x80 : 0x00);
            if (state) r[Ds4BtAudio.StatusOffset] = status;
            if (audioAt > 0)
            {
                r[audioAt] = (byte)sequence;
                r[audioAt + 1] = (byte)(sequence >> 8);
                r[audioAt + 2] = target;
                int at = audioAt + 3;
                foreach (var f in frames) { f.CopyTo(r, at); at += f.Length; }
            }
            uint crc = Ds4BtAudio.Crc32(0xA1, r.AsSpan(0, length - 4));
            r[length - 4] = (byte)crc;
            r[length - 3] = (byte)(crc >> 8);
            r[length - 2] = (byte)(crc >> 16);
            r[length - 1] = (byte)(crc >> 24);
            return r;
        }

        // The bitwise form of the Sony Bluetooth CRC: the transaction byte,
        // then the report, polynomial 0xEDB88320 (ds4mac's description and
        // DS4AudioStreamer's seed ~0xEADA2D49, the state after 0xA2).
        private static uint ReferenceCrc(byte prefix, ReadOnlySpan<byte> data)
        {
            uint crc = 0xFFFFFFFFu;
            void Feed(byte b)
            {
                crc ^= b;
                for (int i = 0; i < 8; i++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
            Feed(prefix);
            foreach (byte b in data) Feed(b);
            return ~crc;
        }

        [Fact]
        public void Crc32_MatchesTheBitwiseReference()
        {
            var rng = new Random(528);
            var data = new byte[543];
            rng.NextBytes(data);
            Assert.Equal(ReferenceCrc(0xA2, data), Ds4BtAudio.Crc32(0xA2, data));
            Assert.Equal(ReferenceCrc(0xA1, data), Ds4BtAudio.Crc32(0xA1, data));
            // DS4AudioStreamer seeds with ~0xEADA2D49: the CRC state after 0xA2.
            uint afterA2 = 0xFFFFFFFFu ^ 0xA2;
            for (int i = 0; i < 8; i++) afterA2 = (afterA2 & 1) != 0 ? (afterA2 >> 1) ^ 0xEDB88320u : afterA2 >> 1;
            Assert.Equal(~0xEADA2D49u, afterA2);
        }

        [Theory]
        [InlineData(true, Ds4BtAudio.ModeSpeaker, Ds4BtAudio.TargetSpeaker)]
        [InlineData(true, Ds4BtAudio.ModeMicrophone, Ds4BtAudio.TargetHeadset)]
        [InlineData(false, Ds4BtAudio.ModeSpeaker, Ds4BtAudio.TargetHeadset)]
        public void StreamHeader_CarriesModeTargetAndCounter(bool fourFrames, byte mode, byte target)
        {
            var r = new byte[462];
            Ds4BtAudio.WriteStreamHeader(r, fourFrames, 0x1234, mode, target);
            Assert.Equal(fourFrames ? 0x17 : 0x14, r[0]);
            Assert.Equal(0x40, r[1]);
            Assert.Equal(mode, r[2]);
            Assert.NotEqual(0xA2, r[2]);
            Assert.Equal(0x34, r[3]);
            Assert.Equal(0x12, r[4]);
            Assert.Equal(target, r[5]);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void ControlReport_ArmsWithTheModeAndTheVolumeEnablesOnly(bool microphone)
        {
            byte mode = microphone ? Ds4BtAudio.ModeMicrophone : Ds4BtAudio.ModeSpeaker;
            byte[] r = Ds4BtAudio.BuildControlReport(mode, 40, Ds4BtAudio.VolumeMax, microphone);
            Assert.Equal(78, r.Length);
            Assert.Equal(0x11, r[0]);
            Assert.Equal(0xC0, r[1]);
            Assert.Equal(mode, r[2]);
            Assert.Equal(microphone ? 0xF0 : 0xB0, r[3]);
            // Rumble, lightbar and flash belong to the effects writer.
            Assert.Equal(0, r[3] & 0x07);
            for (int i = 4; i <= 20; i++) Assert.Equal(0, r[i]);
            Assert.Equal(40, r[21]);
            Assert.Equal(40, r[22]);
            Assert.Equal(microphone ? Ds4BtAudio.MicGainMax : 0, r[23]);
            Assert.Equal(Ds4BtAudio.VolumeMax, r[24]);
            uint crc = (uint)(r[74] | r[75] << 8 | r[76] << 16 | r[77] << 24);
            Assert.Equal(ReferenceCrc(0xA2, r.AsSpan(0, 74)), crc);
        }

        [Theory]
        [InlineData(0, 0x00)]
        [InlineData(100, 0x4F)]
        [InlineData(50, 40)]
        [InlineData(-5, 0x00)]
        [InlineData(250, 0x4F)]
        public void HeadphoneVolume_MapsOntoTheDs4Range(int percent, int expected)
            => Assert.Equal(expected, Ds4BtAudio.HeadphoneVolumeByte(percent));

        [Theory]
        [InlineData(0, Ds4BtAudio.TargetSpeaker, false)] // Default: the speaker, as before #528
        [InlineData(1, Ds4BtAudio.TargetHeadset, true)]  // Headphones (Stereo)
        [InlineData(2, Ds4BtAudio.TargetHeadset, false)] // Headphones (Mono)
        [InlineData(3, Ds4BtAudio.TargetSpeaker, false)] // Headphones + Speaker: no such route
        [InlineData(4, Ds4BtAudio.TargetSpeaker, false)] // Speaker Only
        public void Route_PicksTargetAndStereo(int resolvedPath, byte target, bool stereo)
        {
            Assert.Equal(target, Ds4BtAudio.TargetFor(resolvedPath));
            Assert.Equal(stereo, Ds4BtAudio.IsStereoRoute(resolvedPath));
        }

        /// <summary>The target byte alone does not isolate the outputs.
        /// DS4-Sound-Link mutes the other output by volume, and the hbashton
        /// fork mutes the speaker for headset-only audio.</summary>
        [Theory]
        [InlineData(0, 40, 0x4F)]  // Default: both on, as before #528
        [InlineData(1, 40, 0)]     // Headphones (Stereo): the speaker is muted
        [InlineData(2, 40, 0)]     // Headphones (Mono): the speaker is muted
        [InlineData(3, 40, 0x4F)]  // Headphones + Speaker resolves as Default
        [InlineData(4, 0, 0x4F)]   // Speaker Only: the jack is muted
        public void Route_SetsBothVolumes(int resolvedPath, int headphone, int speaker)
        {
            var (hp, spk) = Ds4BtAudio.VolumesFor(resolvedPath, 50);
            Assert.Equal(headphone, hp);
            Assert.Equal(speaker, spk);
        }

        [Fact]
        public void FollowHeadphoneJack_ResolvesThroughTheSharedResolver()
        {
            var pad = Guid.NewGuid();
            Assert.Equal(0, AudioPassthroughService.ResolveOutputPath(5, pad));
            AudioPassthroughService.NoteHeadphoneJack(pad, true);
            Assert.Equal(Ds4BtAudio.TargetHeadset, Ds4BtAudio.TargetFor(AudioPassthroughService.ResolveOutputPath(5, pad)));
            AudioPassthroughService.NoteHeadphoneJack(pad, false);
            Assert.Equal(Ds4BtAudio.TargetSpeaker, Ds4BtAudio.TargetFor(AudioPassthroughService.ResolveOutputPath(5, pad)));
        }

        [Fact]
        public void InputReportLengths_FollowTheReportIds()
        {
            Assert.Equal(78, Ds4BtAudio.InputReportLength(0x11));
            Assert.Equal(142, Ds4BtAudio.InputReportLength(0x12));
            Assert.Equal(270, Ds4BtAudio.InputReportLength(0x14));
            Assert.Equal(526, Ds4BtAudio.InputReportLength(0x18));
            Assert.Equal(547, Ds4BtAudio.InputReportLength(0x19));
            Assert.Equal(0, Ds4BtAudio.InputReportLength(0x31));
        }

        [Fact]
        public void Status_IsReadFromValidStateBearingReportsOnly()
        {
            byte status = (byte)(Ds4BtAudio.StatusHeadphones | Ds4BtAudio.StatusMicrophone | 0x07);
            byte[] r = InputReport(0x11, state: true, audio: false, status: status);
            Assert.True(Ds4BtAudio.TryReadStatus(r, 547, out byte read));
            Assert.Equal(status, read);

            Assert.False(Ds4BtAudio.TryReadStatus(InputReport(0x12, state: false, audio: true), 547, out _));
            r[10] ^= 1; // damage the report under its CRC
            Assert.False(Ds4BtAudio.TryReadStatus(r, 547, out _));
            Assert.False(Ds4BtAudio.TryReadStatus(InputReport(0x14, state: true, audio: false), 200, out _));
        }

        private static List<(ushort Seq, byte[] Frame)> Extract(byte[] report, out int count)
        {
            var got = new List<(ushort, byte[])>();
            count = Ds4BtAudio.ExtractMicFrames(report, report.Length, (seq, frame) => got.Add((seq, frame.ToArray())));
            return got;
        }

        [Fact]
        public void MicFrames_FollowTheControllerState()
        {
            byte[] a = Msbc(0), b = Msbc(1);
            byte[] r = InputReport(0x14, state: true, audio: true, audioAt: 78, sequence: 0xFFFF, frames: new[] { a, b });
            var got = Extract(r, out int count);
            Assert.Equal(2, count);
            Assert.Equal((ushort)0xFFFF, got[0].Seq);
            Assert.Equal((ushort)0x0000, got[1].Seq); // the frame number wraps
            Assert.Equal(a, got[0].Frame);
            Assert.Equal(b, got[1].Frame);
        }

        [Fact]
        public void MicFrames_AudioOnly_StaleFlag_AndTheLegacyTarget()
        {
            byte[] a = Msbc(2);
            var audioOnly = Extract(InputReport(0x12, state: false, audio: true, audioAt: 3, sequence: 7, frames: new[] { a }), out int n1);
            Assert.Equal(1, n1);
            Assert.Equal((ushort)7, audioOnly[0].Seq);

            // Firmware that clears the state bit while the state is still there.
            var stale = Extract(InputReport(0x14, state: false, audio: true, audioAt: 78,
                                            target: Ds4BtAudio.MicTargetLegacy, sequence: 9, frames: new[] { a }), out int n2);
            Assert.Equal(1, n2);
            Assert.Equal((ushort)9, stale[0].Seq);
            Assert.Equal(a, stale[0].Frame);
        }

        [Fact]
        public void MicFrames_RejectABadCrc_AndReadNothingWithoutTheAudioBit()
        {
            byte[] r = InputReport(0x14, state: true, audio: true, audioAt: 78, frames: new[] { Msbc(0) });
            r[100] ^= 0x01;
            Extract(r, out int bad);
            Assert.Equal(-1, bad);

            Extract(InputReport(0x14, state: true, audio: false, audioAt: 78, frames: new[] { Msbc(0) }), out int none);
            Assert.Equal(0, none);
        }

        [Fact]
        public void MicFrames_SplitSbcFramesByTheirHeaders()
        {
            // Plain SBC (syncword 0x9C) takes its length from its header: two
            // 64-byte 16 kHz mono frames from the ffmpeg vector.
            byte[] all = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "TestData", "Sbc", "sbc16m.sbc"));
            byte[] a = all[..64], b = all[64..128];
            var got = Extract(InputReport(0x15, state: true, audio: true, audioAt: 78, sequence: 40, frames: new[] { a, b }), out int count);
            Assert.Equal(2, count);
            Assert.Equal(a, got[0].Frame);
            Assert.Equal(b, got[1].Frame);
            Assert.Equal((ushort)41, got[1].Seq);
        }

        [Theory]
        [InlineData(0x11)]
        [InlineData(0x12)]
        [InlineData(0x13)]
        [InlineData(0x14)]
        [InlineData(0x15)]
        [InlineData(0x16)]
        [InlineData(0x17)]
        [InlineData(0x18)]
        [InlineData(0x19)]
        public void Status_ChecksTheCrcAtEachReportLength(byte id)
        {
            byte[] r = InputReport(id, state: true, audio: false, status: Ds4BtAudio.StatusHeadphones);
            Assert.True(Ds4BtAudio.TryReadStatus(r, Ds4BtAudio.MaxInputReportSize, out byte status));
            Assert.Equal(Ds4BtAudio.StatusHeadphones, status);
            r[Ds4BtAudio.InputReportLength(id) - 5] ^= 0x01; // the last byte under the CRC
            Assert.False(Ds4BtAudio.TryReadStatus(r, Ds4BtAudio.MaxInputReportSize, out _));
        }

        [Fact]
        public void MicFrames_TheShortReportHasNoCrcToFail()
        {
            // Report 0x01, which a Bluetooth DualShock 4 sends until a host
            // reads feature report 0x02 (ds4drv backends/hidraw.py), carries
            // no CRC and no audio. The microphone lane counts -1 as a bad CRC.
            var r = new byte[Ds4BtAudio.MaxInputReportSize];
            r[0] = 0x01;
            r[2] = 0x80;
            Extract(r, out int count);
            Assert.Equal(0, count);
        }

        [Fact]
        public void MicStream_ConcealsShortGaps_AndRestartsAfterLongOnes()
        {
            var stream = new Ds4MicStream();
            var chunks = new List<short[]>();
            void Sink(ReadOnlySpan<short> pcm) => chunks.Add(pcm.ToArray());

            stream.Feed(100, Msbc(0), Sink);
            stream.Feed(101, Msbc(1), Sink);
            Assert.Equal(2, chunks.Count);
            Assert.All(chunks, c => Assert.Equal(SbcDecoder.MsbcSamples, c.Length));
            short[] last = chunks[1];

            // Two lost frames: two quieter repeats of the last frame, then the real one.
            stream.Feed(104, Msbc(2), Sink);
            Assert.Equal(5, chunks.Count);
            Assert.Equal(2, stream.FramesConcealed);
            for (int i = 0; i < last.Length; i++)
            {
                Assert.Equal((short)Math.Round(last[i] * Math.Pow(0.82, 1)), chunks[2][i]);
                Assert.Equal((short)Math.Round(last[i] * Math.Pow(0.82, 2)), chunks[3][i]);
            }

            // Five lost frames is past the limit: no repeats, the decoder restarts.
            stream.Feed(110, Msbc(3), Sink);
            Assert.Equal(6, chunks.Count);
            Assert.Equal(1, stream.Discontinuities);
            Assert.Equal(2, stream.FramesConcealed);

            stream.Feed(110, Msbc(3), Sink);  // the same frame again
            stream.Feed(109, Msbc(4), Sink);  // older than the last
            Assert.Equal(6, chunks.Count);
            Assert.Equal(1, stream.OutOfOrder);
            Assert.Equal(0, stream.DecodeFailures);
        }

        [Theory]
        [InlineData(1)]
        [InlineData(4)]
        [InlineData(5)]
        public void MicStream_ConcealsUpToFourLostFrames(int lost)
        {
            var stream = new Ds4MicStream();
            var chunks = new List<short[]>();
            void Sink(ReadOnlySpan<short> pcm) => chunks.Add(pcm.ToArray());

            stream.Feed(10, Msbc(0), Sink);
            short[] last = chunks[0];
            stream.Feed((ushort)(11 + lost), Msbc(1), Sink);

            bool concealed = lost <= Ds4MicStream.MaxConcealedFrames;
            Assert.Equal(concealed ? lost : 0, stream.FramesConcealed);
            Assert.Equal(concealed ? 0 : 1, stream.Discontinuities);
            Assert.Equal(2 + (concealed ? lost : 0), chunks.Count);
            for (int k = 0; concealed && k < lost; k++)
                for (int i = 0; i < last.Length; i++)
                    Assert.Equal((short)Math.Round(last[i] * Math.Pow(0.82, k + 1)), chunks[1 + k][i]);
        }

        private static double Goertzel(short[] x, int from, int count, double freq, double rate)
        {
            double w = 2 * Math.PI * freq / rate, c = 2 * Math.Cos(w), s1 = 0, s2 = 0;
            for (int i = 0; i < count; i++) { double s = x[from + i] + c * s1 - s2; s2 = s1; s1 = s; }
            return s1 * s1 + s2 * s2 - c * s1 * s2;
        }

        [Fact]
        public void Upsampler_HasUnityGain_AndRejectsTheImages()
        {
            var up = new Upsampler3x();
            var dc = Enumerable.Repeat((short)1000, 200).ToArray();
            var outDc = new short[600];
            Assert.Equal(600, up.Process(dc, outDc));
            for (int i = 200; i < 600; i++) Assert.InRange((int)outDc[i], 999, 1001);

            up.Reset();
            var tone = new short[1600];
            for (int i = 0; i < tone.Length; i++) tone[i] = (short)(10000 * Math.Sin(2 * Math.PI * 1000 * i / 16000.0));
            var outTone = new short[4800];
            up.Process(tone, outTone);
            double wanted = Goertzel(outTone, 480, 4320, 1000, 48000);
            double image15 = Goertzel(outTone, 480, 4320, 15000, 48000);
            double image17 = Goertzel(outTone, 480, 4320, 17000, 48000);
            Assert.True(10 * Math.Log10(wanted / image15) > 60, "15 kHz image");
            Assert.True(10 * Math.Log10(wanted / image17) > 60, "17 kHz image");
            double rms = Math.Sqrt(outTone.Skip(480).Average(v => (double)v * v));
            Assert.InRange(20 * Math.Log10(rms / (10000 / Math.Sqrt(2))), -0.2, 0.2);
        }

        [Fact]
        public void MicBlockRate_AveragesDownToTheEndpointRate()
        {
            var pcm = new byte[480 * 2 * 2];
            for (int f = 0; f < 480; f++)
                for (int c = 0; c < 2; c++)
                {
                    short v = (short)(f * 10 + c);
                    pcm[(f * 2 + c) * 2] = (byte)v;
                    pcm[(f * 2 + c) * 2 + 1] = (byte)(v >> 8);
                }
            int frames = AudioPassthroughService.ConvertMicBlockRate(pcm, 480, 2, 16000, new AudioPassthroughService.MicRateState());
            Assert.Equal(160, frames);
            for (int f = 0; f < 160; f++)
                for (int c = 0; c < 2; c++)
                {
                    short got = (short)(pcm[(f * 2 + c) * 2] | pcm[(f * 2 + c) * 2 + 1] << 8);
                    int expected = ((f * 3) * 10 + c + (f * 3 + 1) * 10 + c + (f * 3 + 2) * 10 + c) / 3;
                    Assert.Equal(expected, got);
                }

            // A rate that does not divide 48 kHz goes through the interpolator.
            var odd = new byte[480 * 2];
            int oddFrames = AudioPassthroughService.ConvertMicBlockRate(odd, 480, 1, 44100, new AudioPassthroughService.MicRateState());
            Assert.InRange(oddFrames, 438, 444);

            // The DualSense's own endpoint rate passes through untouched.
            Assert.Equal(480, AudioPassthroughService.ConvertMicBlockRate(odd, 480, 1, 48000, new AudioPassthroughService.MicRateState()));
        }

        [Theory]
        [InlineData(true, true, 9000, 0, true)]      // persona wanted 1 s ago
        [InlineData(true, true, 0, 9500, true)]      // voice wanted 0.5 s ago
        [InlineData(true, true, 7000, 0, false)]     // the want is 3 s old
        [InlineData(true, false, 9000, 0, false)]    // no microphone plugged in
        [InlineData(false, true, 9000, 9000, false)] // the lane switched off
        [InlineData(true, true, 0, 0, false)]        // nobody wants it
        public void ArmedRule_NeedsTheGateAMicrophoneAndARecentWant(bool enabled, bool plugged,
                                                                    long persona, long voice, bool armed)
            => Assert.Equal(armed, AudioPassthroughService.Ds4MicArmedRule(enabled, plugged, persona, voice, 10000));

        [Theory]
        [InlineData("x64")]
        [InlineData("arm64")]
        public void MicLane_IsOn_WithAnSdlBuildThatKeepsInputFlowing(string arch)
        {
            // An armed microphone sends state in reports 0x12 to 0x19, which
            // SDL dropped until fork e0b84e3498 (hifihedgehog/SDL#41) checked
            // each report's CRC at its own length. Its table of those lengths
            // is the fingerprint: no earlier SDL3.dll holds that run of bytes.
            Assert.True(AudioPassthroughService.EnableDs4BtMic);
            int[] values = { 78, 142, 206, 270, 334, 398, 462, 526, 547 };
            var lengths = new byte[values.Length * sizeof(int)];
            for (int i = 0; i < values.Length; i++)
                BinaryPrimitives.WriteInt32LittleEndian(lengths.AsSpan(i * sizeof(int)), values[i]);
            byte[] dll = File.ReadAllBytes(RepoPath("PadForge.App", "Resources", "SDL3", arch, "SDL3.dll"));
            Assert.True(dll.AsSpan().IndexOf(lengths) >= 0, arch + " SDL3.dll predates hifihedgehog/SDL#41");

            // A pad no reader has seen stays in speaker mode.
            Assert.Equal(Ds4BtAudio.ModeSpeaker, AudioPassthroughService.Ds4AudioMode(Guid.NewGuid()));
        }

        [Fact]
        public void EffectsPacket_CarriesTheModeAndOnlyTheChangedLevels()
        {
            var plain = Ds4EffectSynthesizer.BuildFields(new DeviceSlotConfig(), 0f, 0, 0, 0, 0f, 0, 0);
            Assert.Equal((byte)0x07, plain["validFlag0"]);
            Assert.Equal(Ds4BtAudio.ModeSpeaker, plain["btReserved"]);

            var (hp, spk) = Ds4BtAudio.VolumesFor(1, 50); // Headphones (Stereo)
            var changed = Ds4EffectSynthesizer.BuildFields(new DeviceSlotConfig(), 0f, 0, 0, 0, 0f, 0, 0,
                headphoneVolume: hp, speakerVolume: spk, assertVolumes: true,
                audioMode: Ds4BtAudio.ModeMicrophone, assertMicGain: true);
            Assert.Equal((byte)0xF7, changed["validFlag0"]);
            Assert.Equal(Ds4BtAudio.ModeMicrophone, changed["btReserved"]);
            Assert.Equal((byte)40, changed["headphoneVolumeLeft"]);
            Assert.Equal((byte)40, changed["headphoneVolumeRight"]);
            Assert.Equal((byte)0, changed["speakerVolume"]);
            Assert.Equal(Ds4BtAudio.MicGainMax, changed["micVolume"]);
        }

        [Theory]
        [InlineData(0x05C4, @"\\?\HID#{00001124-0000-1000-8000-00805f9b34fb}_VID&0002054c_PID&05c4", true)]
        [InlineData(0x09CC, @"\\?\HID#{00001124-0000-1000-8000-00805f9b34fb}_VID&0002054c_PID&09cc", true)]
        [InlineData(0x09CC, @"\\?\HID#VID_054C&PID_09CC&MI_03#7&1f882a25&0&0000", false)] // wired
        [InlineData(0x0BA0, @"\\?\HID#VID_054C&PID_0BA0&MI_03#7&1f882a25&0&0000", false)] // Sony's USB adapter
        [InlineData(0x0CE6, @"\\?\HID#{00001124-0000-1000-8000-00805f9b34fb}_VID&0002054c_PID&0ce6", false)]
        public void BluetoothDs4_IsRecognizedByModelAndTransport(int pid, string path, bool expected)
            => Assert.Equal(expected, AudioPassthroughService.IsBluetoothDs4(0x054C, (ushort)pid, path));

        // A wired CUH-ZCT2 presents a USB headset, 2-channel 32 kHz out and a
        // 16 kHz microphone (the CUH-ZCT2E descriptor dump in
        // DJm00n/ControllersInfo, HIDMaestro's dualshock-4-v2-composite
        // profile). The sink plays to it as it plays to Sony's adapter. Until
        // 5.2.0 every wired DualShock 4 but the adapter was skipped.
        [Theory]
        [InlineData(0x05C4, true, true)]
        [InlineData(0x09CC, true, true)]
        [InlineData(0x05C4, false, false)] // the first model has no USB audio
        [InlineData(0x09CC, false, true)]
        [InlineData(0x0BA0, false, true)]  // Sony's USB adapter
        public void Ds4HasAudio_FollowsTheModelOnUsb(int pid, bool bluetooth, bool expected)
            => Assert.Equal(expected, AudioPassthroughService.Ds4HasAudio((ushort)pid, bluetooth));

        [Fact]
        public void EveryDs4AudioGate_AsksDs4HasAudio()
        {
            string service = RepoFile("PadForge.App", "Common", "Input", "AudioPassthroughService.cs");
            Assert.DoesNotContain("!= 0x0BA0) continue", service);
            Assert.Equal(3, Count(service, "if (isDs4 && !Ds4HasAudio((ushort)ud.ProdId, isBt)) continue;"));
            string pad = RepoFile("PadForge.App", "ViewModels", "PadViewModel.cs");
            // The speaker and DSP rows, and the USB pad the Output Path row
            // offers itself to (#546).
            Assert.Equal(3, Count(pad, "AudioPassthroughService.Ds4HasAudio((ushort)ud.ProdId,"));
        }

        // A wired CUH-ZCT2 and a pad on Sony's adapter route their USB audio
        // with feature report 0xE0 (#546, Ds4UsbAudio). On a route that plays
        // only through the jack, Headphones (Stereo) or (Mono), such a pad
        // takes macro sounds only while its jack reports headphones. A route
        // with the speaker takes them always, and so does every other pad.
        [Fact]
        public void MacroSounds_SkipAUsbPadOnAJackRouteUntilItsJackReportsHeadphones()
        {
            var states = (ConcurrentDictionary<Guid, bool>)typeof(AudioPassthroughService)
                .GetField("s_padJackState", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
            Guid pad = Guid.NewGuid();
            try
            {
                foreach (int path in new[] { 0, 3, 4 })
                    Assert.True(AudioPassthroughService.TakesMacroSounds(true, path, pad));
                foreach (int path in new[] { 1, 2 })
                {
                    Assert.False(AudioPassthroughService.TakesMacroSounds(true, path, pad)); // never read
                    Assert.True(AudioPassthroughService.TakesMacroSounds(false, path, pad));
                }
                AudioPassthroughService.NoteHeadphoneJack(pad, false);
                Assert.False(AudioPassthroughService.TakesMacroSounds(true, 1, pad));
                Assert.True(AudioPassthroughService.TakesMacroSounds(true, 4, pad));
                AudioPassthroughService.NoteHeadphoneJack(pad, true);
                Assert.True(AudioPassthroughService.TakesMacroSounds(true, 1, pad));
                Assert.True(AudioPassthroughService.TakesMacroSounds(true, 2, pad));
            }
            finally { states.TryRemove(pad, out _); }
        }

        [Fact]
        public void MacroRouting_AsksTakesMacroSounds_AndWatchesEveryUsbPadsJack()
        {
            string service = RepoFile("PadForge.App", "Common", "Input", "AudioPassthroughService.cs");
            Assert.Contains("if (isDs4 && !TakesMacroSounds(!isBt, isBt ? 0 : ResolvedPathOf(slot, padGuid), padGuid)) continue;", service);
            Assert.Contains("(deviceFilter == null ? TakesMacroSounds(s) : s.DeviceGuid == deviceFilter.Value)", service);
            Assert.Contains("if (SinkAlive(s) && TakesMacroSounds(s) && (uint)s.Slot < MaxPads) routed[s.Slot] = true;", service);
            // Under _lock the sink's last resolved path stands in for the
            // provider, which takes settings locks.
            Assert.Contains("private static bool TakesMacroSounds(Sink s) => TakesMacroSounds(s.UsbDs4, s.ResolvedPath, s.DeviceGuid);", service);
            Assert.Contains("_sink.ResolvedPath = outPath;", service);
            Assert.Contains("sk.ResolvedPath = resolved;", service);
            Assert.Equal(2, Count(service, "isDs4 && !isBt));"));
            Assert.Contains("if (d.IsPeer) continue;", service);
            Assert.Contains("if (w.IsDs4 && !w.IsBt) _workSignal.Set();", service);
            Assert.DoesNotContain("HeadsetOnly", service);
        }

        [Fact]
        public void UsbStatus_IsByte30OfReport0x01()
        {
            // Bluetooth report 0x11 puts two more bytes before the same state.
            Assert.Equal(Ds4BtAudio.StatusOffset, Ds4BtAudio.UsbStatusOffset + 2);
            var r = new byte[64];
            r[0] = 0x01;
            r[30] = Ds4BtAudio.StatusHeadphones | 0x0B;
            Assert.True(Ds4BtAudio.TryReadUsbStatus(r, 64, out byte status));
            Assert.Equal(r[30], status);
            Assert.False(Ds4BtAudio.TryReadUsbStatus(r, 63, out _));   // short of a state report
            Assert.False(Ds4BtAudio.TryReadUsbStatus(r.AsSpan(0, 63), 64, out _));
            r[31] = 0x04;                                               // the adapter, no pad linked
            Assert.False(Ds4BtAudio.TryReadUsbStatus(r, 64, out _));
            r[31] = 0x00;
            r[0] = 0x11;
            Assert.False(Ds4BtAudio.TryReadUsbStatus(r, 64, out _));
        }

        private static (Guid Mic, string MicPath, Guid BtMic, string BtMicPath, bool BtMicIsDs4) PickMics(
            bool btDualSense, bool btDs4, params (Guid Guid, string Path, bool IsBt, bool IsDs4)[] pads)
            => AudioPassthroughService.PickPersonaMics(pads.ToList(), btDualSense, btDs4);

        [Fact]
        public void PersonaMic_PrefersADualSenseOnEitherLink_ThenAWiredDs4_AndNeverAPeer()
        {
            var usbDs5 = (Guid.NewGuid(), "usb-ds5", false, false);
            var btDs5 = (Guid.NewGuid(), "bt-ds5", true, false);
            var usbDs4 = (Guid.NewGuid(), "usb-ds4", false, true);
            var btDs4 = (Guid.NewGuid(), "bt-ds4", true, true);
            var peer = (Guid.NewGuid(), "peer://host/pad", true, true);

            var m = PickMics(true, true, usbDs4, usbDs5);
            Assert.Equal((usbDs5.Item1, Guid.Empty), (m.Mic, m.BtMic));

            m = PickMics(true, true, usbDs4, btDs5);
            Assert.Equal((Guid.Empty, btDs5.Item1, false), (m.Mic, m.BtMic, m.BtMicIsDs4));

            m = PickMics(false, true, usbDs4, btDs5);
            Assert.Equal((usbDs4.Item1, "usb-ds4", Guid.Empty), (m.Mic, m.MicPath, m.BtMic));

            m = PickMics(true, true, btDs4, usbDs4);
            Assert.Equal((usbDs4.Item1, Guid.Empty), (m.Mic, m.BtMic));

            m = PickMics(true, true, peer, btDs4);
            Assert.Equal((Guid.Empty, btDs4.Item1, "bt-ds4", true), (m.Mic, m.BtMic, m.BtMicPath, m.BtMicIsDs4));

            m = PickMics(true, true, peer);
            Assert.Equal((Guid.Empty, Guid.Empty), (m.Mic, m.BtMic));

            m = PickMics(true, false, btDs4);
            Assert.Equal((Guid.Empty, Guid.Empty), (m.Mic, m.BtMic));
        }

        // Master Volume multiplies every placement whose sink drives its
        // speaker at one level. A DualSense's firmware speaker byte and a
        // haptic-tone sink apply it themselves.
        [Theory]
        [InlineData(0.8f, false, 50, 0.4f)]
        [InlineData(0.8f, true, 50, 0.8f)]
        [InlineData(1f, false, 100, 1f)]
        [InlineData(1f, false, 0, 0f)]
        [InlineData(0.5f, false, 150, 0.5f)]
        public void PlacementVolume_CarriesMasterWhereTheSinkDoesNot(float action, bool sinkOwnsMaster, int master, float expected)
            => Assert.Equal(expected, SoundMacroService.PlacementVolume(action, sinkOwnsMaster, master), 5);

        [Fact]
        public void MasterVolume_ReachesDs4AndWiiMacroSounds()
        {
            string service = RepoFile("PadForge.App", "Common", "Input", "AudioPassthroughService.cs");
            Assert.Contains(".Select(s => (s.MacroMixer, !s.IsDs4))", service);
            string macros = RepoFile("PadForge.App", "Common", "Input", "SoundMacroService.cs");
            Assert.Contains("foreach (var m in WiiSpeakerService.GetSlotSinkMixers(slot, deviceFilter))\n                    targets.Add((m, false));",
                macros.Replace("\r\n", "\n"));
            Assert.Contains("foreach (var m in HapticToneService.GetSlotSinkMixers(slot, deviceFilter))\n                    targets.Add((m, true));",
                macros.Replace("\r\n", "\n"));
            Assert.Contains("targets = new List<(MixingSampleProvider Mixer, bool OwnsMaster)> { (o.Mixer, false) };", macros);
            Assert.Equal(2, Count(macros, "PlacementVolume(a"));
        }

        // A DualShock 4's USB routes are the DualSense's (#546), so its sink
        // maps channels by the resolved route: Automatic and Speaker Only put
        // a mono mix on channel 1, the right channel the speaker plays, and
        // Headphones (Stereo) passes left and right through.
        [Theory]
        [InlineData(0, 0f, 0.125f)]
        [InlineData(1, 0.5f, -0.25f)]
        [InlineData(2, 0.125f, 0f)]
        [InlineData(3, 0.125f, 0.125f)]
        [InlineData(4, 0f, 0.125f)]
        public void UsbChannels_FollowTheRoute(int path, float ch0, float ch1)
        {
            AudioPassthroughService.MapMirrorChannels(path, 0.5f, -0.25f, out float c0, out float c1);
            Assert.Equal((ch0, ch1), (c0, c1));
            string service = RepoFile("PadForge.App", "Common", "Input", "AudioPassthroughService.cs");
            Assert.Contains("MapMirrorChannels(outPath, _pull[f * 2], _pull[f * 2 + 1],", service);
            Assert.DoesNotContain("MapUsbChannels", service);
        }

        [Fact]
        public void UsbDs4Sink_IsStereoHeadphonesOnHeadphonesStereoAlone()
        {
            string service = RepoFile("PadForge.App", "Common", "Input", "AudioPassthroughService.cs").Replace("\r\n", "\n");
            Assert.Contains("bool stereoHeadphones = sk.IsDs4\n                        ? Ds4BtAudio.IsStereoRoute(resolved)", service);
            Assert.DoesNotContain("bool stereoHeadset = _sink.IsDs4;", service);
        }

        [Fact]
        public void AudioTab_OffersOutputPathToAUsbPad()
        {
            string xaml = RepoFile("PadForge.App", "Views", "PadPage.xaml");
            Assert.DoesNotContain("Pad_Audio_HeadsetOnly", xaml);
            int path = xaml.IndexOf("<TextBlock Text=\"{Binding Pad_Audio_OutputPath, ", StringComparison.Ordinal);
            Assert.True(path > 300);
            Assert.Contains("Visibility=\"{Binding SelectedDeviceHasOutputPath,", xaml.Substring(path - 300, 300));
            string pad = RepoFile("PadForge.App", "ViewModels", "PadViewModel.cs");
            Assert.Equal(3, Count(pad, "OnPropertyChanged(nameof(SelectedDeviceHasOutputPath));"));
            Assert.Equal(3, Count(pad, "OnPropertyChanged(nameof(SelectedDeviceHasNoSpeaker));"));
            Assert.DoesNotContain("SelectedDeviceIsHeadsetOnly", pad);
            // A USB pad offers Headphones + Speaker, which route 2 plays.
            int both = pad.IndexOf("public bool SelectedDeviceHasHeadsetAndSpeakerPath", StringComparison.Ordinal);
            Assert.Contains("|| IsUsbDs4WithAudio(ud);", pad.Substring(both, 700));
        }

        private static string RepoPath(params string[] parts)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PadForge.sln")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
        }

        private static string RepoFile(params string[] parts) => File.ReadAllText(RepoPath(parts));

        private static int Count(string text, string what)
        {
            int n = 0;
            for (int at = text.IndexOf(what, StringComparison.Ordinal); at >= 0;
                 at = text.IndexOf(what, at + what.Length, StringComparison.Ordinal)) n++;
            return n;
        }

        [Fact]
        public void PadPage_ShowsTheDualSenseRowsForABluetoothDs4_AndHidesTheBothRoute()
        {
            string xaml = RepoFile("PadForge.App", "Views", "PadPage.xaml");
            int both = xaml.IndexOf("Pad_Audio_OutputPath_Both", StringComparison.Ordinal);
            Assert.True(both > 0);
            Assert.Contains("SelectedDeviceHasHeadsetAndSpeakerPath", xaml.Substring(both, 300));

            string vm = RepoFile("PadForge.App", "ViewModels", "PadViewModel.cs");
            int jack = vm.IndexOf("public bool SelectedDeviceHasHeadphoneJack", StringComparison.Ordinal);
            Assert.True(jack > 0);
            Assert.Contains("IsBluetoothDs4", vm.Substring(jack, 800));
            int raisedJack = Count(vm, "OnPropertyChanged(nameof(SelectedDeviceHasHeadphoneJack));");
            Assert.True(raisedJack >= 3);
            Assert.Equal(raisedJack, Count(vm, "OnPropertyChanged(nameof(SelectedDeviceHasHeadsetAndSpeakerPath));"));
        }

        [Fact]
        public void Dispatcher_KeepsItsTimerWhileAMicrophoneIsArmed()
        {
            // An armed microphone lapses after about four seconds without an
            // 0xA1 effects report, so it joins the speaker path at both of the
            // dispatcher's timer-demand checks.
            string src = RepoFile("PadForge.App", "Common", "Input", "UserEffectsDispatcher.cs");
            int speaker = Count(src, "|| AudioPassthroughService.SlotWantsSpeakerPath(_padIndex)");
            Assert.Equal(2, speaker);
            Assert.Equal(speaker, Count(src, "|| AudioPassthroughService.SlotHasArmedDs4Mic(_padIndex)"));
            Assert.False(AudioPassthroughService.SlotHasArmedDs4Mic(0)); // no pad has wanted it
        }

        [Fact]
        public void AudioLane_WritesTheModeByte_AndStreamsThroughSilence()
        {
            string src = RepoFile("PadForge.App", "Common", "Input", "AudioPassthroughService.cs");
            Assert.DoesNotContain("report[2] = 0xA2", src);
            Assert.DoesNotContain("vol[2] = 0xA2", src);
            Assert.Contains("Ds4BtAudio.WriteStreamHeader(", src);
            Assert.Contains("Ds4BtAudio.BuildControlReport(", src);

            // The DualShock 4 branch runs before the DualSense idle gate.
            int tick = src.IndexOf("_personaLastAudible = audible;", StringComparison.Ordinal);
            Assert.True(tick > 0);
            int ds4 = src.IndexOf("Ds4BtTick(s, pull, inFrames);", tick, StringComparison.Ordinal);
            int gate = src.IndexOf("if (!audible)", tick, StringComparison.Ordinal);
            Assert.True(ds4 > 0 && gate > 0 && ds4 < gate);
        }
    }

    /// <summary>The voice-only microphone lane against a pipe standing in for
    /// the pad, the way MicrophoneDecoderLifetimeTests drives the DualSense's
    /// persona lane. With no effects dispatcher serving the pad, nothing but
    /// the lane itself can carry the 0xA1 mode byte, and the controller drops
    /// the microphone about four seconds after the last one (DS4Device.cs:2061-2070
    /// in the hbashton fork at 68a5c64).</summary>
    [Collection("AudioProfileLifecycle")]
    public sealed class Ds4VoiceMicKeepaliveTests
    {
        private const BindingFlags Static = BindingFlags.Static | BindingFlags.NonPublic;

        private static void AssertModeReport(byte[] r, bool armed)
        {
            Assert.Equal(0x11, r[0]);
            Assert.Equal(0xC0, r[1]);
            Assert.Equal(armed ? Ds4BtAudio.ModeMicrophone : Ds4BtAudio.ModeSpeaker, r[2]);
            // Only the microphone gain is enabled: rumble, lightbar and both
            // volumes belong to the effects writer and the audio lane.
            Assert.Equal(armed ? Ds4BtAudio.FlagMicrophone : 0, r[3]);
            for (int i = 4; i < 74; i++)
                Assert.Equal(i == 23 && armed ? Ds4BtAudio.MicGainMax : 0, r[i]);
            uint crc = (uint)(r[74] | r[75] << 8 | r[76] << 16 | r[77] << 24);
            Assert.Equal(Ds4BtAudio.Crc32(0xA2, r.AsSpan(0, 74)), crc);
        }

        [Fact]
        public void TheModeReportEnablesOnlyTheMicrophoneGain()
        {
            AssertModeReport(Ds4BtAudio.BuildMicModeReport(true), armed: true);
            AssertModeReport(Ds4BtAudio.BuildMicModeReport(false), armed: false);
        }

        [Fact]
        public async Task AVoiceOnlyPadWithNoDispatcherKeepsItsMicrophoneArmed()
        {
            var type = typeof(AudioPassthroughService);
            Guid pad = Guid.NewGuid();
            int wanted = 1;
            var previous = AudioPassthroughService.VoiceLanePadMicWanted;
            AudioPassthroughService.VoiceLanePadMicWanted = g => g == pad ? Volatile.Read(ref wanted) == 1 : previous(g);
            string name = "PadForge-ds4-voice-mic-" + Guid.NewGuid().ToString("N");
            var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            var laneType = type.GetNestedType("VoiceMicLane", Static)!;
            object lane = Activator.CreateInstance(laneType, nonPublic: true)!;
            laneType.GetField("Pad")!.SetValue(lane, pad);
            laneType.GetField("Path")!.SetValue(lane, @"\\.\pipe\" + name);
            laneType.GetField("IsDs4")!.SetValue(lane, true);
            var connected = server.WaitForConnectionAsync();
            var thread = new Thread(() => type.GetMethod("Ds4VoiceBtMicLoop", Static)!.Invoke(null, [lane])) { IsBackground = true };
            using var stop = new CancellationTokenSource();
            var written = new List<(long At, byte[] Report)>();
            var clock = System.Diagnostics.Stopwatch.StartNew();
            Task feeder = Task.CompletedTask, reader = Task.CompletedTask;
            try
            {
                thread.Start();
                await connected.WaitAsync(TimeSpan.FromSeconds(5));
                // The pad's input: a state-bearing report whose status byte
                // shows a microphone, fifty times a second.
                byte[] status = Ds4BtAudioTests.InputReport(0x11, state: true, audio: false,
                                                            status: Ds4BtAudio.StatusMicrophone)[..78];
                long start = clock.ElapsedMilliseconds;
                feeder = Task.Run(async () =>
                {
                    while (!stop.IsCancellationRequested)
                    {
                        await server.WriteAsync(status, stop.Token);
                        await Task.Delay(20, stop.Token);
                    }
                });
                reader = Task.Run(async () =>
                {
                    var pending = new List<byte>();
                    var buf = new byte[256];
                    while (!stop.IsCancellationRequested)
                    {
                        int n = await server.ReadAsync(buf, stop.Token);
                        if (n <= 0) break;
                        pending.AddRange(buf.AsSpan(0, n).ToArray());
                        while (pending.Count >= Ds4BtAudio.ControlReportSize)
                        {
                            byte[] r = pending.GetRange(0, Ds4BtAudio.ControlReportSize).ToArray();
                            pending.RemoveRange(0, Ds4BtAudio.ControlReportSize);
                            lock (written) written.Add((clock.ElapsedMilliseconds, r));
                        }
                    }
                });

                async Task<(long At, byte[] Report)?> Next(long after, byte mode, long withinMs)
                {
                    long until = clock.ElapsedMilliseconds + withinMs;
                    while (true)
                    {
                        lock (written)
                            foreach (var w in written)
                                if (w.At > after && w.Report[2] == mode) return w;
                        if (clock.ElapsedMilliseconds > until) return null;
                        await Task.Delay(10);
                    }
                }

                var first = await Next(-1, Ds4BtAudio.ModeMicrophone, 1500);
                Assert.True(first.HasValue, "no 0xA1 report within 1.5 s of the first status report");
                Assert.InRange(first.Value.At - start, 0, 1500);
                AssertModeReport(first.Value.Report, armed: true);
                var second = await Next(first.Value.At, Ds4BtAudio.ModeMicrophone, 1500);
                Assert.True(second.HasValue, "no second 0xA1 report within 1.5 s of the first");
                AssertModeReport(second.Value.Report, armed: true);

                // The voice session lets go: once the two-second hold runs
                // out the lane returns the pad to 0xA0 with nothing enabled.
                long released = clock.ElapsedMilliseconds;
                Volatile.Write(ref wanted, 0);
                var off = await Next(released, Ds4BtAudio.ModeSpeaker, 3500);
                Assert.True(off.HasValue, "no 0xA0 report after the microphone was released");
                AssertModeReport(off.Value.Report, armed: false);
            }
            finally
            {
                laneType.GetField("Stop")!.SetValue(lane, true);
                stop.Cancel();
                try { await Task.WhenAll(feeder, reader); } catch (OperationCanceledException) { }
                server.Dispose();
                Assert.True(thread.Join(5000));
                AudioPassthroughService.VoiceLanePadMicWanted = previous;
                ((IDictionary)type.GetField("_ds4Mic", Static)!.GetValue(null)).Remove(pad);
                ((ConcurrentDictionary<Guid, bool>)type.GetField("s_padJackState", Static)!.GetValue(null)).TryRemove(pad, out _);
            }
        }

        [Fact]
        public void TheMicrophoneWakesTheSlotThatWritesThePad()
        {
            var g = Guid.NewGuid();
            var rows = new List<PadForge.Engine.Data.UserSetting>
            {
                new() { InstanceGuid = g, MapTo = 0 },
                new() { InstanceGuid = g, MapTo = 1 },
            };
            // Slot 0 has no virtual controller, so no dispatcher: slot 1 writes.
            Assert.Equal(1, AudioPassthroughService.Ds4MicWriterSlot(rows, g, s => s == 1));
            Assert.Equal(0, AudioPassthroughService.Ds4MicWriterSlot(rows, g, s => true));
            Assert.Equal(-1, AudioPassthroughService.Ds4MicWriterSlot(rows, g, s => false));
            Assert.Equal(-1, AudioPassthroughService.Ds4MicWriterSlot(rows, Guid.NewGuid(), s => true));

            // Both lanes note the armed microphone under that slot.
            string src = File.ReadAllText(Path.Combine(RepoRoot(), "PadForge.App", "Common", "Input",
                                                       "AudioPassthroughService.Ds4Bluetooth.cs"));
            Assert.DoesNotContain("ResolveVoiceMicSlot(", src);
            Assert.DoesNotContain("SetDs4MicWanted(pad, feed.Slot", src);
            Assert.Contains("SetDs4MicWanted(pad, slot, persona: true, wanted);", src);
            Assert.Contains("SetDs4MicWanted(lane.Pad, slot, persona: false, wanted);", src);
            // The definition, then each lane's first lookup and its refresh.
            Assert.Equal(5, src.Split("ResolveDs4MicWriterSlot(").Length - 1);
        }

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PadForge.sln")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return dir.FullName;
        }
    }

    /// <summary>A persona Bluetooth microphone reader whose open fails gives
    /// the pad up until the jack watch's retry deadline, so the jack watch
    /// and the voice lane are not kept off by a lane that is not reading, and
    /// the reconcile starts a fresh reader once the deadline passes.</summary>
    [Collection("AudioProfileLifecycle")]
    public sealed class BtMicOpenRetryTests
    {
        private const BindingFlags Static = BindingFlags.Static | BindingFlags.NonPublic;

        [Theory]
        [InlineData("BtMicLoop")]
        [InlineData("Ds4BtMicLoop")]
        public void AFailedOpenClearsThePadAndSetsARetryDeadline(string loop)
        {
            Guid pad = Guid.NewGuid();
            var feed = new AudioPassthroughService.PersonaFeed
            {
                Owner = new(), Slot = 15, Published = true,
                RouteGeneration = 1, BtMicGen = 1, BtMicPadGuid = pad,
            };
            long before = Environment.TickCount64;
            // A path in the device namespace: the test host refuses the open.
            typeof(AudioPassthroughService).GetMethod(loop, Static)!
                .Invoke(null, [feed, @"\\?\hid#padforge-test-no-such-pad", 1, 1]);
            Assert.Equal(Guid.Empty, feed.BtMicPadGuid);
            Assert.Equal(pad, feed.BtMicRetryPad);
            Assert.InRange(feed.BtMicRetryAfterTick, before + 5000, Environment.TickCount64 + 5000);

            // A lane that was stopped while it opened leaves the feed alone.
            var stale = new AudioPassthroughService.PersonaFeed
            {
                Owner = new(), Slot = 15, Published = true,
                RouteGeneration = 1, BtMicGen = 2, BtMicPadGuid = pad,
            };
            typeof(AudioPassthroughService).GetMethod(loop, Static)!
                .Invoke(null, [stale, @"\\?\hid#padforge-test-no-such-pad", 1, 1]);
            Assert.Equal(pad, stale.BtMicPadGuid);
            Assert.Equal(Guid.Empty, stale.BtMicRetryPad);
        }

        /// <summary>The USB jack reader beside the microphone lane follows
        /// the same rule: a failed open stops claiming the pad, so the sink's
        /// jack watch reads it meanwhile, and the reconcile retries after the
        /// deadline.</summary>
        [Fact]
        public void AFailedUsbJackOpenClearsThePadAndSetsARetryDeadline()
        {
            Guid pad = Guid.NewGuid();
            var feed = new AudioPassthroughService.PersonaFeed
            {
                Owner = new(), Slot = 15, Published = true,
                RouteGeneration = 1, UsbJackGen = 1, UsbJackPadGuid = pad,
            };
            long before = Environment.TickCount64;
            typeof(AudioPassthroughService).GetMethod("UsbJackLoop", Static)!
                .Invoke(null, [feed, @"\\?\hid#padforge-test-no-such-pad", 1, 1]);
            Assert.Equal(Guid.Empty, feed.UsbJackPadGuid);
            Assert.Equal(pad, feed.UsbJackRetryPad);
            Assert.InRange(feed.UsbJackRetryAfterTick, before + 5000, Environment.TickCount64 + 5000);

            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PadForge.sln")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            string src = File.ReadAllText(Path.Combine(dir.FullName, "PadForge.App", "Common", "Input",
                                                       "AudioPassthroughService.cs"));
            Assert.Contains("if (PersonaLaneRestarts(usbJackPad, feed.UsbJackPadGuid, feed.UsbJackRetryPad,", src);
            Assert.DoesNotContain("if (usbJackPad != feed.UsbJackPadGuid)", src);
        }

        [Fact]
        public void TheReconcileRetriesTheFailedPadAfterTheDeadline()
        {
            Guid a = Guid.NewGuid(), b = Guid.NewGuid();
            // Inside the deadline the failed pad waits.
            Assert.False(AudioPassthroughService.PersonaLaneRestarts(a, Guid.Empty, a, 2000, 1000));
            // At the deadline it starts again.
            Assert.True(AudioPassthroughService.PersonaLaneRestarts(a, Guid.Empty, a, 2000, 2000));
            // Another pad, or no pad, never waits on it.
            Assert.True(AudioPassthroughService.PersonaLaneRestarts(b, Guid.Empty, a, 2000, 1000));
            Assert.True(AudioPassthroughService.PersonaLaneRestarts(Guid.Empty, b, a, 2000, 1000));
            // A running lane on the wanted pad stays.
            Assert.False(AudioPassthroughService.PersonaLaneRestarts(a, a, Guid.Empty, 0, 1000));

            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PadForge.sln")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            string src = File.ReadAllText(Path.Combine(dir.FullName, "PadForge.App", "Common", "Input",
                                                       "AudioPassthroughService.cs"));
            Assert.Contains("if (PersonaLaneRestarts(btMicPad, feed.BtMicPadGuid, feed.BtMicRetryPad,", src);
            Assert.DoesNotContain("if (btMicPad != feed.BtMicPadGuid)", src);
        }
    }

    /// <summary>A Remote Link peer's copy of a pad offers the voice phrases its
    /// owner offers. A DualShock 4's answer rests on its Bluetooth path, which
    /// a peer:// copy never has, so the owner's answer crosses in the device
    /// list's v13 tail, the way the v12 tail carries the capacitive sensors
    /// (2ef2be16). An older owner sends none, and the copy keeps today's
    /// answer.</summary>
    public sealed class RemoteVoicePhrasesTests
    {
        private static PadForge.Engine.RemoteLink.RemotePeerDeviceInfo Pad(string id, ushort vid, ushort pid, bool? voice) => new()
        {
            Slot = 0,
            PeerLocalDeviceId = id,
            Name = "pad",
            VendorId = vid,
            ProductId = pid,
            NumAxes = 6,
            NumButtons = 22,
            RawButtonCount = 22,
            SupportedButtonIndices = Enumerable.Range(0, 18).ToArray(),
            SupportedAxisIndices = Enumerable.Range(0, 6).ToArray(),
            InputDeviceType = PadForge.Engine.InputDeviceType.Gamepad,
            HasVoicePhrases = voice,
        };

        private static int TailStart(IEnumerable<PadForge.Engine.RemoteLink.RemotePeerDeviceInfo> devices)
            => PadForge.Engine.RemoteLink.LinkConnection.EncodeDeviceList(devices.Select(d =>
            {
                var bare = d.CloneForSlot(d.Slot);
                bare.HasVoicePhrases = null;
                return bare;
            }).ToList(), "").Length;

        [Fact]
        public void TheOwnersVoicePhrasesCrossTheWire()
        {
            var devices = new[]
            {
                Pad("ds4", 0x054C, 0x09CC, true),
                Pad("switch", 0x057E, 0x2009, false),
                Pad("ds5", 0x054C, 0x0CE6, true),
            };
            var payload = PadForge.Engine.RemoteLink.LinkConnection.EncodeDeviceList(devices, "");
            var list = PadForge.Engine.RemoteLink.LinkConnection.DecodeDeviceList(payload);
            Assert.True(list[0].HasVoicePhrases);
            Assert.Null(list[1].HasVoicePhrases);
            Assert.True(list[2].HasVoicePhrases);

            // The tail is the last thing on the wire: an older owner stops
            // before it, and every row keeps today's answer.
            int start = TailStart(devices);
            Assert.Equal(0xEE, payload[start]);
            var older = PadForge.Engine.RemoteLink.LinkConnection.DecodeDeviceList(payload.Take(start).ToArray());
            Assert.Equal(3, older.Count);
            Assert.All(older, d => Assert.Null(d.HasVoicePhrases));

            // A record naming a row past the list is refused whole.
            var broken = (byte[])payload.Clone();
            broken[start + 2] = 9;
            var refused = PadForge.Engine.RemoteLink.LinkConnection.DecodeDeviceList(broken);
            Assert.Equal(3, refused.Count);
            Assert.All(refused, d => Assert.Null(d.HasVoicePhrases));

            // No row with phrases, no tail: the list is what it was before.
            var none = new[] { Pad("switch", 0x057E, 0x2009, false) };
            Assert.Equal(TailStart(none), PadForge.Engine.RemoteLink.LinkConnection.EncodeDeviceList(none, "").Length);
        }

        [Theory]
        [InlineData(true, true)]   // the owner reads the DualShock 4's microphone
        [InlineData(false, false)] // an older owner: a peer:// path is not Bluetooth
        public void APeersCopyOfADualShock4OffersItsOwnersPhrases(bool fromOwner, bool expected)
        {
            var sent = Pad("ds4", 0x054C, 0x09CC, fromOwner ? true : null);
            var info = PadForge.Engine.RemoteLink.LinkConnection.DecodeDeviceList(
                PadForge.Engine.RemoteLink.LinkConnection.EncodeDeviceList(new[] { sent }, "")).Single();
            info.PeerFingerprintHex = "0123456789abcdef0123456789abcdef";
            using var copy = new PadForge.Engine.RemoteLink.RemotePeerDevice(info);
            var row = new PadForge.Engine.Data.UserDevice();
            row.LoadFromExternalDevice(copy);
            Assert.StartsWith("peer://", row.DevicePath);
            Assert.Equal(expected, row.HasVoicePhrases);
        }

        [Fact]
        public void TheOwnerSendsItsAnswerAndThePeerRefreshesIt()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PadForge.sln")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            string service = File.ReadAllText(Path.Combine(dir.FullName, "PadForge.App", "Services", "InputService.cs"));
            Assert.Contains("HasVoicePhrases = ud.HasVoicePhrases ? true : null,", service);
            string server = File.ReadAllText(Path.Combine(dir.FullName, "PadForge.Engine", "RemoteLink", "LinkServer.cs"));
            int reconcile = server.IndexOf("private void ReconcileRemoteDevices(", StringComparison.Ordinal);
            int end = server.IndexOf("private const string ListTooLarge", reconcile, StringComparison.Ordinal);
            Assert.True(reconcile >= 0 && end > reconcile, "the reconcile moved");
            Assert.Contains("existing.Info.HasVoicePhrases = info.HasVoicePhrases;", server[reconcile..end]);
        }
    }

    /// <summary>The jack watch's DualShock 4 shape, against a file standing
    /// in for the pad, the way JackWatchRetryTests drives the DualSense's.</summary>
    [Collection("AudioProfileLifecycle")]
    public sealed class Ds4JackWatchTests
    {
        [Theory]
        [InlineData(0x20, true)]
        [InlineData(0x40, false)] // a microphone alone is not headphones
        public void ABluetoothDs4ReportSetsTheJackFromBit5(byte status, bool plugged)
        {
            const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
            var type = typeof(AudioPassthroughService);
            var ensure = type.GetMethod("EnsureJackWatch", flags)!;
            var watches = (IDictionary)type.GetField("_jackWatch", flags)!.GetValue(null);
            var gate = type.GetField("_jackLock", flags)!.GetValue(null);
            var states = (ConcurrentDictionary<Guid, bool>)type.GetField("s_padJackState", flags)!.GetValue(null);
            var mics = (IDictionary)type.GetField("_ds4Mic", flags)!.GetValue(null);
            Guid id = Guid.NewGuid();
            string path = Path.Combine(Path.GetTempPath(), "padforge-ds4jack-" + Guid.NewGuid() + ".bin");
            File.WriteAllBytes(path, Ds4BtAudioTests.InputReport(0x11, state: true, audio: false, status: status)[..78]);
            object watch = null;
            try
            {
                ensure.Invoke(null, [id, path, true, true]);
                lock (gate) watch = watches[id];
                Assert.NotNull(watch);
                Assert.True(SpinWait.SpinUntil(() => AudioPassthroughService.TryGetHeadphoneJack(id) == plugged, 2000));
            }
            finally
            {
                if (watch != null)
                {
                    watch.GetType().GetField("Stop")!.SetValue(watch, true);
                    Assert.True(((Thread)watch.GetType().GetField("Thread")!.GetValue(watch)).Join(2000));
                }
                lock (gate) watches.Remove(id);
                states.TryRemove(id, out _);
                mics.Remove(id);
                if (File.Exists(path)) File.Delete(path);
            }
        }

        // A wired second model sends report 0x01, 64 bytes with no CRC, and
        // its microphone bit stays out of the Bluetooth microphone's table.
        [Theory]
        [InlineData(0x20, true)]
        [InlineData(0x6B, true)]
        [InlineData(0x40, false)]
        public void AWiredDs4ReportSetsTheJackFromByte30(byte status, bool plugged)
        {
            const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
            var type = typeof(AudioPassthroughService);
            var ensure = type.GetMethod("EnsureJackWatch", flags)!;
            var watches = (IDictionary)type.GetField("_jackWatch", flags)!.GetValue(null);
            var gate = type.GetField("_jackLock", flags)!.GetValue(null);
            var states = (ConcurrentDictionary<Guid, bool>)type.GetField("s_padJackState", flags)!.GetValue(null);
            var mics = (IDictionary)type.GetField("_ds4Mic", flags)!.GetValue(null);
            Guid id = Guid.NewGuid();
            string path = Path.Combine(Path.GetTempPath(), "padforge-ds4usbjack-" + Guid.NewGuid() + ".bin");
            var report = new byte[64];
            report[0] = 0x01;
            report[Ds4BtAudio.UsbStatusOffset] = status;
            File.WriteAllBytes(path, report);
            object watch = null;
            try
            {
                ensure.Invoke(null, [id, path, false, true]);
                lock (gate) watch = watches[id];
                Assert.NotNull(watch);
                Assert.True(SpinWait.SpinUntil(() => AudioPassthroughService.TryGetHeadphoneJack(id) == plugged, 2000));
                Assert.False(mics.Contains(id));
            }
            finally
            {
                if (watch != null)
                {
                    watch.GetType().GetField("Stop")!.SetValue(watch, true);
                    Assert.True(((Thread)watch.GetType().GetField("Thread")!.GetValue(watch)).Join(2000));
                }
                lock (gate) watches.Remove(id);
                states.TryRemove(id, out _);
                mics.Remove(id);
                if (File.Exists(path)) File.Delete(path);
            }
        }
    }

    /// <summary>The USB frame shaper on a DualShock 4 and on a DualSense,
    /// which play the same routes (#546). In the collection that swaps the
    /// output path provider, which both read.</summary>
    [Collection("AudioProfileLifecycle")]
    public sealed class Ds4UsbShaperTests
    {
        private sealed class ConstantStereo : NAudio.Wave.ISampleProvider
        {
            public NAudio.Wave.WaveFormat WaveFormat { get; } = NAudio.Wave.WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
            public int Read(float[] buffer, int offset, int count)
            {
                for (int i = 0; i < count; i++) buffer[offset + i] = i % 2 == 0 ? 0.5f : -0.25f;
                return count;
            }
        }

        private static (float[] Frames, int ResolvedPath) ShapeOneBlock(bool ds4)
        {
            const BindingFlags any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var aps = typeof(AudioPassthroughService);
            var sinkType = aps.GetNestedType("Sink", BindingFlags.NonPublic)!;
            object sink = Activator.CreateInstance(sinkType, nonPublic: true)!;
            sinkType.GetField("IsDs4")!.SetValue(sink, ds4);
            sinkType.GetField("DeviceGuid")!.SetValue(sink, Guid.NewGuid());
            var shaperType = aps.GetNestedType("UsbFrameProvider", BindingFlags.NonPublic)!;
            var shaper = (NAudio.Wave.IWaveProvider)Activator.CreateInstance(shaperType, any, null,
                new object[] { new ConstantStereo(), NAudio.Wave.WaveFormat.CreateIeeeFloatWaveFormat(48000, 2), sink }, null)!;
            var bytes = new byte[8 * 2 * 4];
            Assert.Equal(bytes.Length, shaper.Read(bytes, 0, bytes.Length));
            var floats = new float[16];
            Buffer.BlockCopy(bytes, 0, floats, 0, bytes.Length);
            return (floats, (int)sinkType.GetField("ResolvedPath")!.GetValue(sink)!);
        }

        // Automatic puts the mono mix on the right channel, the one the
        // speaker plays, and Headphones (Stereo) passes left and right
        // through. The shaper keeps the path it played for macro routing.
        [Theory]
        [InlineData(0, 0f, 0.125f)]
        [InlineData(1, 0.5f, -0.25f)]
        public void TheShaperPlaysADs4AndADualSenseOnTheirRoute(int path, float left, float right)
        {
            var saved = AudioPassthroughService.DeviceAudioOutputPathProvider;
            try
            {
                AudioPassthroughService.DeviceAudioOutputPathProvider = path == 0 ? null : (_, _) => path;
                foreach (bool ds4 in new[] { true, false })
                {
                    var (frames, resolved) = ShapeOneBlock(ds4);
                    for (int f = 0; f < 8; f++) Assert.Equal((left, right), (frames[f * 2], frames[f * 2 + 1]));
                    Assert.Equal(path, resolved);
                }
            }
            finally { AudioPassthroughService.DeviceAudioOutputPathProvider = saved; }
        }
    }
}
