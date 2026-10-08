using System;
using System.IO;
using System.Linq;
using PadForge.Common.Input;
using PadForge.Engine;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The Padix PSX/USB converter rumble path (#440, discussion #435): the
    /// 9-byte motor report PadForge writes to a Buffalo BSGC101 / BSGC201, the
    /// identity gate that selects it, and the dispatch guards that keep SDL
    /// (and through it Buffalo's DirectInput effect plug-in) off those motors.
    /// Byte values come from the vendor plug-in RFVib_C264.dll: report ID 0,
    /// small motor at byte 1 as 0 or 1, big motor at byte 2 as 0x7F + level/2.
    /// </summary>
    public class PadixConverterRumbleTests
    {
        [Fact]
        public void IdentityGateCoversBothBuffaloModelsAndNothingElse()
        {
            Assert.True(PadixConverterIdentity.IsPlayStationConverter(0x0583, 0xB047)); // BSGC101
            Assert.True(PadixConverterIdentity.IsPlayStationConverter(0x0583, 0xB048)); // BSGC201
            Assert.False(PadixConverterIdentity.IsPlayStationConverter(0x0583, 0xB00A)); // BGC-UPS103, 4-byte report
            Assert.False(PadixConverterIdentity.IsPlayStationConverter(0x0583, 0xB00C)); // BGC-UPS203, 4-byte report
            Assert.False(PadixConverterIdentity.IsPlayStationConverter(0x0583, 0x2060)); // iBuffalo SNES pad
            Assert.False(PadixConverterIdentity.IsPlayStationConverter(0x0810, 0xB047)); // right PID, wrong vendor
            Assert.True(PadixConverterRawHidWriter.IsPlayStationConverter(0x0583, 0xB047));
        }

        [Fact]
        public void RestIsTheAllZeroReportThePluginSendsToStop()
        {
            var buf = PadixConverterRawHidWriter.BuildReport(0, 0);
            Assert.Equal(9, buf.Length);
            Assert.All(buf, b => Assert.Equal(0, b));
        }

        [Fact]
        public void FullLeftMotorIsTheBigMotorAtTheVendorsFullLevel()
        {
            var buf = PadixConverterRawHidWriter.BuildReport(65535, 0);
            Assert.Equal(0x00, buf[0]);                 // report ID
            Assert.Equal(0x00, buf[1]);                 // small motor off
            Assert.Equal(0xBE, buf[2]);                 // 0x7F + (127 >> 1)
            Assert.All(buf.Skip(3), b => Assert.Equal(0, b));
        }

        [Fact]
        public void LowestNonZeroLevelIsTheVendorsBase()
        {
            // 512 is the smallest input that reaches level 1 (left16 >> 9).
            var buf = PadixConverterRawHidWriter.BuildReport(512, 0);
            Assert.Equal(0x7F, buf[2]);
            // Below level 1 the big motor stays off rather than sitting at the base.
            Assert.Equal(0x00, PadixConverterRawHidWriter.BuildReport(511, 0)[2]);
        }

        [Fact]
        public void BigMotorByteNeverDecreasesAsTheLeftMotorRises()
        {
            int last = 0;
            for (int v = 0; v <= 65535; v += 257)
            {
                int b2 = PadixConverterRawHidWriter.BuildReport((ushort)v, 0)[2];
                Assert.True(b2 >= last, $"byte 2 fell from {last:X2} to {b2:X2} at {v}");
                Assert.True(b2 == 0 || (b2 >= 0x7F && b2 <= 0xBE), $"byte 2 {b2:X2} outside the vendor's positive range at {v}");
                last = b2;
            }
        }

        [Fact]
        public void RightMotorIsOnOffFromItsHighByte()
        {
            Assert.Equal(0, PadixConverterRawHidWriter.BuildReport(0, 255)[1]);   // high byte zero: off
            Assert.Equal(1, PadixConverterRawHidWriter.BuildReport(0, 256)[1]);   // first nonzero high byte: on
            Assert.Equal(1, PadixConverterRawHidWriter.BuildReport(0, 65535)[1]);
            // The small motor never leaks into the big-motor byte.
            Assert.Equal(0, PadixConverterRawHidWriter.BuildReport(0, 65535)[2]);
        }

        [Fact]
        public void BothMotorsAtOnceFillBothBytes()
        {
            var buf = PadixConverterRawHidWriter.BuildReport(32768, 32768);
            Assert.Equal(1, buf[1]);
            Assert.Equal(0x7F + (64 >> 1), buf[2]); // 32768 >> 9 = 64
        }

        [Fact]
        public void ARefusedWriteRetriesOnceAndThenDedupsAgain()
        {
            // The retry flag once stayed set after its retry, so every later
            // identical frame wrote again at polling cadence.
            var ffb = new ForceFeedbackState();
            Assert.True(ffb.TryRecordMotorSnapshot(40000, 0));
            Assert.False(ffb.TryRecordMotorSnapshot(40000, 0));
            ffb.MarkDirectWriteFailed();
            Assert.True(ffb.TryRecordMotorSnapshot(40000, 0));
            Assert.False(ffb.TryRecordMotorSnapshot(40000, 0));
            Assert.False(ffb.TryRecordMotorSnapshot(40000, 0));
        }

        [Fact]
        public void WriteRefusesAnEmptyPathInsteadOfOpeningNothing()
        {
            Assert.False(PadixConverterRawHidWriter.Write(null, 65535, 65535));
            Assert.False(PadixConverterRawHidWriter.Write(string.Empty, 65535, 65535));
        }

        [Fact]
        public void ApplyForceFeedbackDispatchesTheConverterBeforeSdlAndSkipsTheSdlGate()
        {
            string code = RepoText("PadForge.App", "Common", "Input", "InputManager.Step2.UpdateInputStates.cs")
                .Replace("\r\n", "\n");
            Assert.Contains("bool isPadixConverter = PadForge.Engine.PadixConverterIdentity.IsPlayStationConverter(ud.VendorId, ud.ProdId);", code);
            // A Bliss-Box port (#469) joined the same gate after the converter,
            // and a haptic mouse or keyboard (#494) after it.
            Assert.Contains("if (!isXboxImpulse && !isVendorFfb && !isPadixConverter && !isBlissBox && !isPeripheralHaptic)", code);
            Assert.Contains("else if ((isXboxImpulse || isPadixConverter || isBlissBox || isPeripheralHaptic) && ud.Device == null)", code);
            // The unassign final zero goes through the writer, not StopDeviceForces.
            Assert.Contains("else if (isPadixConverter)\n                            {\n                                PadixConverterRawHidWriter.Write(ud.DevicePath, 0, 0);", code);
            // The dispatch returns before SetDeviceForces. Its levels carry
            // Fold Trigger Rumble into Main Motors (#469 review), which
            // SetDeviceForces applies on SDL's path.
            Assert.Contains("ForceFeedbackState.FoldTriggersForDirectWriter(firstPadSetting, combinedLT, combinedRT, ref padixL, ref padixR);", code);
            int dispatch = code.IndexOf("if (ud.ForceFeedbackState.TryRecordMotorSnapshot(padixL, padixR))", StringComparison.Ordinal);
            int sdl = code.IndexOf("ud.ForceFeedbackState.SetDeviceForces(ud, ud.Device, firstPadSetting, _combinedVibration);", StringComparison.Ordinal);
            Assert.True(dispatch > 0 && sdl > dispatch, "the converter dispatch must precede the SDL SetDeviceForces call");
            // The write is now success-gated so a refused one re-arms the
            // snapshot for a retry, which moved it off the same line as the
            // return that used to follow it.
            Assert.Contains("if (!PadixConverterRawHidWriter.Write(ud.DevicePath, padixL, padixR))\n                        ud.ForceFeedbackState.MarkDirectWriteFailed();", code);
        }

        [Fact]
        public void SdlRumbleIsInertForTheConverterAndTheFlagIsForcedOn()
        {
            string code = RepoText("PadForge.Engine", "Common", "SdlDeviceWrapper.cs").Replace("\r\n", "\n");
            Assert.Contains("|| PadixConverterIdentity.IsPlayStationConverter(VendorId, ProductId);", code);
            int setRumble = code.IndexOf("public bool SetRumble(ushort lowFreq, ushort highFreq, uint durationMs = uint.MaxValue)", StringComparison.Ordinal);
            int guard = code.IndexOf("if (PadixConverterIdentity.IsPlayStationConverter(VendorId, ProductId))\n                return false;", StringComparison.Ordinal);
            int call = code.IndexOf("return SDL_RumbleJoystick(Joystick, lowFreq, highFreq, durationMs);", StringComparison.Ordinal);
            Assert.True(setRumble > 0 && guard > setRumble && call > guard, "SetRumble must refuse the converter before calling SDL");
        }

        [Fact]
        public void ExitSweepAndRemoteApplyUseTheWriter()
        {
            string sweep = RepoText("PadForge.App", "Common", "Input", "InputManager.cs").Replace("\r\n", "\n");
            Assert.Contains("PadixConverterRawHidWriter.Write(ud.DevicePath, 0, 0);", sweep);
            string remote = RepoText("PadForge.App", "Services", "InputService.cs").Replace("\r\n", "\n");
            Assert.Contains("PadForge.Engine.PadixConverterIdentity.IsPlayStationConverter(ud.VendorId, ud.ProdId))", remote);
            Assert.Contains("PadForge.Common.Input.PadixConverterRawHidWriter.Write(", remote);
        }

        private static string RepoText(params string[] parts)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PadForge.sln")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return File.ReadAllText(Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray()));
        }
    }
}
