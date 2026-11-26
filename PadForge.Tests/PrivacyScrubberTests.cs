using System;
using System.Collections.Generic;
using System.Linq;
using PadForge.Engine.Privacy;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The scrubber that takes private values out of a problem recording and
    /// out of what a program reads (#597). Every kind is planted in a typed
    /// field and in free text, alone and glued inside a longer run, and the
    /// result is searched for it.
    /// </summary>
    public class PrivacyScrubberTests
    {
        private const string User = "marisol";
        private const string Pc = "DESKTOP-Q7KX2M";
        private const string AppFolder = @"D:\Games\PadForge";

        private static PrivacyScrubber New() => new(new PrivacyEnvironment
        {
            UserName = User,
            DomainName = Pc,
            PcName = Pc,
            ProfileFolder = @"C:\Users\" + User,
            AppFolder = AppFolder + @"\",
        });

        private static void Gone(string scrubbed, string value)
            => Assert.True(scrubbed.IndexOf(value, StringComparison.OrdinalIgnoreCase) < 0,
                "the value is still in: " + scrubbed);

        // ── Typed fields ──

        [Fact]
        public void Devices_AreNamedInTheOrderTheyAreFirstNamed_AndControllersByTheirSlot()
        {
            var s = New();
            Assert.Equal("D1", s.DeviceAlias("{a}"));
            Assert.Equal("D2", s.DeviceAlias("{b}"));
            Assert.Equal("D1", s.DeviceAlias("{A}"));
            Assert.Equal("C1", PrivacyScrubber.ControllerAlias(0));
            Assert.Equal("C16", PrivacyScrubber.ControllerAlias(15));
        }

        [Theory]
        [InlineData(@"\\?\hid#vid_054c&pid_0ce6&mi_03#8&1a2b3c4d&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}",
            @"HID\VID_054C&PID_0CE6&MI_03\<i 1>", "8&1a2b3c4d&0&0000")]
        [InlineData(@"HID\VID_045E&PID_02FF&IG_00\9&2F3A1B&0&0000",
            @"HID\VID_045E&PID_02FF&IG_00\<i 1>", "9&2F3A1B&0&0000")]
        [InlineData(@"USB\VID_057E&PID_2009\000000000001",
            @"USB\VID_057E&PID_2009\<i 1>", "000000000001")]
        [InlineData(@"BTHENUM\{00001124-0000-1000-8000-00805f9b34fb}_VID&0002054c_PID&0ce6\9&2a4f3b1&0&A0AB51C32FCB_C00000000",
            @"BTHENUM\VID_054C&PID_0CE6\<i 1>", "A0AB51C32FCB")]
        [InlineData(@"HID\VID_046D&PID_C52B&MI_01&COL02\7&3b9c1e2f&0&0001",
            @"HID\VID_046D&PID_C52B&MI_01&COL02\<i 1>", "7&3b9c1e2f")]
        public void ADevicePath_KeepsWhatSaysItsKind_AndLosesItsInstance(string path, string expected, string instancePart)
        {
            var s = New();
            string scrubbed = s.DevicePath(path);
            Assert.Equal(expected, scrubbed);
            Gone(scrubbed, instancePart);
            // The same path in a line of free text gets the same stand-in.
            Assert.Equal("opened " + expected + " ok", s.Text("opened " + path + " ok"));
        }

        [Fact]
        public void TheInstancePart_IsWhatFollowsTheHardwareId()
        {
            Assert.Equal("8&1a2b3c4d&0&0000", PrivacyScrubber.InstancePartOf(
                @"\\?\hid#vid_054c&pid_0ce6&mi_03#8&1a2b3c4d&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}"));
            Assert.Null(PrivacyScrubber.InstancePartOf(@"HID\VID_054C&PID_0CE6"));
            Assert.Null(PrivacyScrubber.InstancePartOf(""));
        }

        [Fact]
        public void ASerialNumber_IsNamedAfterItsDevice_InTypedFieldsAndInFreeText()
        {
            var s = New();
            string alias = s.DeviceAlias("{pad}");
            Assert.Equal("<serial of D1>", s.Serial("SN-88213-QRT", alias));
            Assert.Equal("serial <serial of D1> read", s.Text("serial SN-88213-QRT read"));
            Assert.Equal("glued x<serial of D1>y", s.Text("glued xSN-88213-QRTy"));
        }

        [Theory]
        [InlineData("A0AB51C32FCB")]
        [InlineData("A0:AB:51:C3:2F:CB")]
        [InlineData("a0-ab-51-c3-2f-cb")]
        public void ABluetoothAddress_IsOneStandIn_HoweverItIsWritten(string written)
        {
            var s = New();
            Assert.Equal("<bt 1>", s.Alias(PrivateKind.BluetoothAddress, written));
            Assert.Equal("a <bt 1> b <bt 1> c <bt 1> d",
                s.Text("a A0AB51C32FCB b a0:ab:51:c3:2f:cb c A0-AB-51-C3-2F-CB d"));
        }

        [Fact]
        public void ABluetoothAddressNobodyNamed_IsFoundByItsShape()
        {
            var s = New();
            string scrubbed = s.Text("peer 5C:BA:37:D1:0E:44 and bare 5CBA37D10E45 seen");
            Assert.Equal("peer <bt 1> and bare <bt 2> seen", scrubbed);
        }

        [Fact]
        public void TwelveDigits_AreANumber_AndStay()
            => Assert.Equal("stamp 202611231516 len=123456789012", New().Text("stamp 202611231516 len=123456789012"));

        [Fact]
        public void TheUserName_TheProfileFolder_AndThePcName_AreReplacedWhereverTheyStand()
        {
            var s = New();
            Assert.Equal("user <user> on <pc>", s.Text("user " + User + " on " + Pc));
            Assert.Equal("glued ab<user>cd and <pc>-2", s.Text("glued ab" + User.ToUpperInvariant() + "cd and " + Pc.ToLowerInvariant() + "-2"));
            string path = s.Text(@"loading C:\Users\" + User + @"\AppData\Local\Temp\dump file.bin now");
            Gone(path, User);
            Gone(path, "AppData");
            Assert.Contains(@"<folder>\dump", path);
        }

        [Fact]
        public void APath_KeepsItsFileName_AndPadForgesFolderBecomesAStandIn()
        {
            var s = New();
            Assert.Equal(@"<padforge>\PadForge.xml", s.FilePath(AppFolder + @"\PadForge.xml"));
            Assert.Equal(@"<folder>\notes.txt", s.FilePath(@"C:\Users\someone else\Documents\notes.txt"));
            Assert.Equal(@"read <padforge>\crash.log and <folder>\game.exe ok",
                s.Text("read " + AppFolder + @"\crash.log and C:\Program Files\Some Studio\Game\game.exe ok"));
            Assert.Equal(@"share <folder>\setup.ini", s.Text(@"share \\nas-box\media\cfg\setup.ini"));
            Assert.Equal(@"slashes <folder>\a.txt", s.Text("slashes C:/Users/else/a.txt"));
            Assert.Equal(@"long \\?\<folder>\b.txt", s.Text(@"long \\?\C:\Users\Jane Doe\b.txt"));
        }

        [Fact]
        public void AFolderNameWithASpace_IsNotLeftBehind()
        {
            string scrubbed = New().Text(@"open C:\Users\Jane Doe\Saved Games\profile one.sav failed");
            Gone(scrubbed, "Jane");
            Gone(scrubbed, "Doe");
            Gone(scrubbed, "Saved Games");
        }

        [Theory]
        [InlineData("192.168.1.42", "<lan-ip 1>")]
        [InlineData("10.0.0.7", "<lan-ip 1>")]
        [InlineData("172.20.5.9", "<lan-ip 1>")]
        [InlineData("169.254.3.3", "<lan-ip 1>")]
        [InlineData("100.72.1.1", "<lan-ip 1>")]
        [InlineData("203.0.113.77", "<public-ip 1>")]
        [InlineData("fe80::1c2b:3aff:fe4d:5e6f", "<lan-ip 1>")]
        [InlineData("fd12:3456:789a::1", "<lan-ip 1>")]
        [InlineData("2001:db8:85a3::8a2e:370:7334", "<public-ip 1>")]
        public void AnAddress_SaysOnlyWhetherItIsLocalOrPublic(string address, string standIn)
        {
            var s = New();
            Assert.Equal(standIn, s.Address(address));
            Assert.Equal("at " + standIn + " now", s.Text("at " + address + " now"));
        }

        [Fact]
        public void Loopback_AndPorts_Stay()
        {
            var s = New();
            Assert.Equal("127.0.0.1", s.Address("127.0.0.1"));
            Assert.Equal("::1", s.Address("::1"));
            Assert.Equal("listen 127.0.0.1:26760 and [::1]:8080 and <lan-ip 1>:26760 and [<public-ip 1>]:443",
                s.Text("listen 127.0.0.1:26760 and [::1]:8080 and 192.168.0.9:26760 and [2001:db8::5]:443"));
        }

        [Fact]
        public void TwoAddressesThatShareTheirStart_AreTwoStandIns()
        {
            var s = New();
            s.Know(PrivateKind.LanAddress, "192.168.1.5");
            Assert.Equal("a <lan-ip 1> b <lan-ip 2>", s.Text("a 192.168.1.5 b 192.168.1.50"));
        }

        [Fact]
        public void AHostFromPadForgesOwnList_Stays_AndAnyOtherBecomesAStandIn()
        {
            var s = New();
            Assert.Equal("github.com", s.Host("github.com"));
            Assert.Equal("<host 1>", s.Host("my-nas.lan"));
            Assert.Equal("get https://api.github.com/... and http://<host 1>:8080/... and ws://<lan-ip 1>:9000",
                s.Text("get https://api.github.com/repos/x/y?token=abc and http://my-nas.lan:8080/pad?code=991 and ws://192.168.4.4:9000"));
        }

        [Fact]
        public void Secrets_BecomeStandIns_AloneAndGlued()
        {
            var s = New();
            Assert.Equal("<access-code 1>", s.Alias(PrivateKind.AccessCode, "742916"));
            Assert.Equal("<key 1>", s.Alias(PrivateKind.Key, "Zm9vYmFyQmF6UXV4MTIzNDU2Nzg5MA=="));
            Assert.Equal("<peer 1>", s.Alias(PrivateKind.Peer, "9F86D081884C7D659A2FEAA0C55AD015A3BF4F1B2B0B822CD15D6C15B0F00A08"));
            Assert.Equal("<pairing-code 1>", s.Alias(PrivateKind.PairingCode, "7KQ2M-9XWTR"));
            Assert.Equal("<channel>", s.Alias(PrivateKind.Channel, "nightowl_plays"));

            string text = s.Text("code=742916 key Zm9vYmFyQmF6UXV4MTIzNDU2Nzg5MA== peer 9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08 "
                + "pair 7KQ2M-9XWTR join #nightowl_plays glued qqnightowl_playsqq");
            Assert.Equal("code=<access-code 1> key <key 1> peer <peer 1> pair <pairing-code 1> join #<channel> glued qq<channel>qq", text);
        }

        [Fact]
        public void ACodeOfDigits_IsNotCutOutOfALongerNumber()
        {
            var s = New();
            s.Know(PrivateKind.AccessCode, "742916");
            Assert.Equal("len=17429163 code <access-code 1>", s.Text("len=17429163 code 742916"));
        }

        [Fact]
        public void ALongRunNobodyNamed_IsAKey_AndWordsAreNot()
        {
            var s = New();
            Assert.Equal("relay as <key 1> chan=0A1B2C3D",
                s.Text("relay as 9F86D081884C7D659A2FEAA0C55AD015A3BF4F1B2B0B822CD15D6C15B0F00A08 chan=0A1B2C3D"));
            Assert.Equal("token <key 2>", s.Text("token dGhpcyBpcyBhIDMyIGJ5dGUga2V5IDEyMzQ1Njc4OTA"));
            Assert.Equal("pair <pairing-code 1> done", s.Text("pair 7KQ2M-9XWTR-4HJNP-AB3DE done"));
            // A function's name, a file's name and a hex dump with spaces
            // are none of these.
            string plain = "SDL HIDAPI_DriverPS5_UpdateEffects in InputManagerStep5VirtualDevices bytes=00 08 7F 00 02 00 FF 10";
            Assert.Equal(plain, s.Text(plain));
        }

        [Fact]
        public void AnSdlGuid_NamesAModel_AndStays()
        {
            var s = New();
            const string sdlGuid = "030000004c050000e60c000000006800";
            Assert.True(PrivacyScrubber.IsSdlGuid(sdlGuid));
            Assert.Equal("guid " + sdlGuid, s.Text("guid " + sdlGuid));
            Assert.False(PrivacyScrubber.IsSdlGuid("9f86d081884c7d659a2feaa0c55ad015"));
        }

        [Fact]
        public void ADeviceTypeGuid_Stays_AndAnyOtherBecomesAStandIn()
        {
            var s = New();
            foreach (string kept in new[]
            {
                "{4d1e55b2-f16f-11cf-88cb-001111000030}",   // the HID interface class
                "0ce6054c-0000-0000-0000-504944564944",     // a product GUID
                "A36D02E0-C9F3-11CF-BFC7-444553540000",     // a DirectInput axis
                "00001812-0000-1000-8000-00805f9b34fb",     // a Bluetooth service
                "00000000-0000-0000-0000-000000000000",
            })
            {
                Assert.Equal(kept, s.Identifier(kept));
                Assert.Equal("class " + kept, s.Text("class " + kept));
            }
            Assert.Equal("<id 1>", s.Identifier("3f2504e0-4f89-41d3-9a0c-0305e82c3301"));
            Assert.Equal("device {<id 1>} and <id 2> and <id 3>",
                s.Text("device {3F2504E0-4F89-41D3-9A0C-0305E82C3301} and 6ba7b810-9dad-11d1-80b4-00c04fd430c8 and {7c9e6679-7425-40de-944b-e07fc1f90ae7}"));
        }

        [Fact]
        public void AnEmailAddress_BecomesAStandIn()
            => Assert.Equal("mail <email 1> twice <email 1>", New().Text("mail jo.do+pad@example.org twice JO.DO+PAD@EXAMPLE.ORG"));

        [Theory]
        [InlineData(PrivateKind.DeviceName, "Living Room Pad", "<device-name 1>")]
        [InlineData(PrivateKind.ProfileName, "Halo with Sam", "<profile 1>")]
        [InlineData(PrivateKind.MacroName, "Quick Reload", "<macro 1>")]
        [InlineData(PrivateKind.LayerName, "Driving", "<layer 1>")]
        [InlineData(PrivateKind.NfcTagName, "Link amiibo", "<tag 1>")]
        [InlineData(PrivateKind.VoicePhrase, "open the pod bay doors", "<phrase 1>")]
        [InlineData(PrivateKind.ChatWord, "jumpscare", "<word 1>")]
        [InlineData(PrivateKind.AudioDevice, "Sam's Headset", "<audio 1>")]
        [InlineData(PrivateKind.Phone, "Pixel of Sam", "<phone 1>")]
        [InlineData(PrivateKind.Program, "secretgame.exe", "<program 1>")]
        public void ANameTheUserTyped_BecomesAStandIn_InTypedFieldsAndInFreeText(PrivateKind kind, string name, string standIn)
        {
            var s = New();
            Assert.Equal(standIn, s.Alias(kind, name));
            Assert.Equal("switch to " + standIn + " done", s.Text("switch to " + name + " done"));
            Assert.Equal("glued zz" + standIn + "zz", s.Text("glued zz" + name + "zz"));
        }

        [Fact]
        public void AShortName_IsReplacedOnlyAsAWordOfItsOwn()
        {
            var s = New();
            Assert.Equal("<macro 1>", s.Alias(PrivateKind.MacroName, "Dash"));
            Assert.Equal("macro <macro 1> on the Dashboard", s.Text("macro Dash on the Dashboard"));
        }

        [Fact]
        public void AValueOfOneCharacter_IsNotSearchedFor_AndIsCounted()
        {
            var s = New();
            Assert.Equal("<macro 1>", s.Alias(PrivateKind.MacroName, "A"));
            Assert.Equal("Button A is down", s.Text("Button A is down"));
            Assert.Equal(1, s.TooShort);
        }

        [Fact]
        public void ATextMacro_IsItsLength_AndProgramArguments_AreAStandIn()
        {
            Assert.Equal("<text of 14 characters>", PrivacyScrubber.TextLength("my password 12"));
            Assert.Equal("<text of 0 characters>", PrivacyScrubber.TextLength(null));
            Assert.Equal("<args>", PrivacyScrubber.ArgumentsStandIn);
            Assert.True(PrivacyScrubber.IsStandIn(PrivacyScrubber.TextLength("abc")));
        }

        // ── One file, one stand-in ──

        [Fact]
        public void AValue_AlwaysBecomesTheSameStandIn_AndTwoValuesBecomeTwo()
        {
            var s = New();
            string first = s.Text("peer 5C:BA:37:D1:0E:44 at 192.168.1.20");
            string second = s.Text("again 5cba37d10e44 at 192.168.1.20 and 192.168.1.21 and 5C-BA-37-D1-0E-99");
            Assert.Equal("peer <bt 1> at <lan-ip 1>", first);
            Assert.Equal("again <bt 1> at <lan-ip 1> and <lan-ip 2> and <bt 2>", second);
            Assert.Equal("<lan-ip 2>", s.Address("192.168.1.21"));
            // Another file starts its numbers again.
            Assert.Equal("at <lan-ip 1>", New().Text("at 192.168.1.21"));
        }

        [Fact]
        public void AStandIn_IsNeverReadAgain()
        {
            var s = New();
            // A PC named after part of a stand-in cannot break one.
            s.Know(PrivateKind.Key, "public");
            s.Know(PrivateKind.ProfileName, "serial");
            string once = s.Text("a 203.0.113.5 b");
            Assert.Equal("a <public-ip 1> b", once);
            Assert.Equal(once, s.Text(once));
            Assert.Equal(once, s.SearchFinished(once));
            Assert.Equal("<serial of D1> stays", s.Text("<serial of D1> stays"));
        }

        [Fact]
        public void TheCounts_SayHowManyOfEachKind_AndHoldNoValue()
        {
            var s = New();
            s.Text("user " + User + " and " + User + " at 192.168.1.20 and 192.168.1.21");
            var counts = s.Counts();
            var user = counts.Single(c => c.Kind == PrivateKind.UserName);
            Assert.Equal(2, user.Places);
            var lan = counts.Single(c => c.Kind == PrivateKind.LanAddress);
            Assert.Equal(2, lan.Values);
            Assert.Equal(2, lan.Places);
            Assert.DoesNotContain(counts, c => c.Kind == PrivateKind.Email);
            foreach (PrivateKind kind in Enum.GetValues<PrivateKind>())
                Assert.False(string.IsNullOrWhiteSpace(PrivacyScrubber.KindLabel(kind)));
        }

        // ── What must not be mistaken for a private value ──

        [Theory]
        [InlineData("15:16:02.123 HEARTBEAT poll hz=1000 idle=0 cycle=0ms")]
        [InlineData("STALL poll sdl=31ms enum=0ms cycle=52ms")]
        [InlineData("DEV + SDL#7 054C:0CE6 DualSense Wireless Controller backend=hidapi type=1")]
        [InlineData("FFBANY idx=0 src=XInput rid=0x00 len=5 bytes=00 08 7F 00 02")]
        [InlineData("version 6.0.1 build 20261123-151600")]
        public void AnOrdinaryDiagnosticsLine_ComesThroughUnchanged(string line)
            => Assert.Equal(line, New().Text(line));

        // ── The self-test and the search of the finished file ──

        private static List<(PrivateKind, string)> Live() => new()
        {
            (PrivateKind.UserName, User),
            (PrivateKind.PcName, Pc),
            (PrivateKind.Folder, @"C:\Users\" + User),
            (PrivateKind.SerialNumber, "SN-88213-QRT"),
            (PrivateKind.SerialNumber, "00112233"),
            (PrivateKind.DeviceInstance, "8&1a2b3c4d&0&0000"),
            (PrivateKind.BluetoothAddress, "A0:AB:51:C3:2F:CB"),
            (PrivateKind.AccessCode, "742916"),
            (PrivateKind.Key, "Zm9vYmFyQmF6UXV4MTIzNDU2Nzg5MA=="),
            (PrivateKind.Peer, "9F86D081884C7D659A2FEAA0C55AD015A3BF4F1B2B0B822CD15D6C15B0F00A08"),
            (PrivateKind.Channel, "nightowl_plays"),
            (PrivateKind.LanAddress, "192.168.1.42"),
            (PrivateKind.LanAddress, "fe80::1c2b:3aff:fe4d:5e6f"),
        };

        [Fact]
        public void TheCanary_HoldsEveryLiveValue_AndNoneComesThrough()
        {
            var s = New();
            foreach (var (kind, value) in Live()) s.Know(kind, value);
            Assert.Empty(PrivacyScrubber.Survivors(Live(), s.Text));
        }

        [Fact]
        public void TheCanary_NamesTheKindOfAValueThatComesThrough_AndNeverTheValue()
        {
            // A scrubber that was never told the access code, standing in for
            // a rule that misses one.
            var s = New();
            foreach (var (kind, value) in Live().Where(v => v.Item1 != PrivateKind.AccessCode)) s.Know(kind, value);
            var survivors = PrivacyScrubber.Survivors(Live(), s.Text);
            Assert.Equal(new[] { PrivateKind.AccessCode }, survivors);

            // And one that lets everything through names every kind once.
            var all = PrivacyScrubber.Survivors(Live(), line => line);
            Assert.Equal(Live().Select(v => v.Item1).Distinct().OrderBy(k => k), all.OrderBy(k => k));
        }

        [Fact]
        public void TheSearchOfTheFinishedFile_FindsAValueLearnedAfterItsLineWasWritten()
        {
            var s = New();
            // The line was written before the device's serial was known, and
            // nothing about a serial's shape gives it away.
            string written = s.Text("SDL opened pad serial QX7-LATE-0042 over USB");
            Assert.Contains("QX7-LATE-0042", written);

            s.Serial("QX7-LATE-0042", s.DeviceAlias("{late}"));
            string searched = s.SearchFinished(written);
            Assert.Equal("SDL opened pad serial <serial of D1> over USB", searched);

            var count = s.Counts().Single(c => c.Kind == PrivateKind.SerialNumber);
            Assert.Equal(1, count.FoundLate);
            Assert.Equal(1, count.Places);
            // A line with nothing to find comes back as it is.
            Assert.Equal("nothing here", s.SearchFinished("nothing here"));
        }

        // ── Microphones, phones and what was said ──

        private const string Headset = "Headset Microphone (Priya Raman's AirPods Pro)";
        private const string Phone = "Dmitri Volkov's Pixel 8";
        private const string Said = "call me back at nine tonight";

        [Fact]
        public void AMicrophoneNamedAfterAPerson_IsAnAudioStandIn_TypedAndWhereTheVoiceLinesNameIt()
        {
            var s = New();
            Assert.Equal("<audio 1>", s.Alias(PrivateKind.AudioDevice, Headset));
            Assert.Equal("VOICE [<audio 1>] listening (vosk, 3 phrases, en-US)", s.Text("VOICE [" + Headset + "] listening (vosk, 3 phrases, en-US)"));
            Assert.Equal("VOICE capture alive: <audio 1> fmt=48000Hz/1ch/f32", s.Text("VOICE capture alive: " + Headset + " fmt=48000Hz/1ch/f32"));
            Assert.Equal("VOICE capture stats: <audio 1> callbacks=500 bytesTo16k=32000 peak16k=812/32767",
                s.Text("VOICE capture stats: " + Headset + " callbacks=500 bytesTo16k=32000 peak16k=812/32767"));

            // One the scrubber was never told of is found by where the voice
            // lines put it, and is known from then on.
            var fresh = New();
            Assert.Equal("VOICE [<audio 1>] session down (stopped)", fresh.Text("VOICE [Speakers (Tomas Lind's Echo)] session down (stopped)"));
            Assert.Equal("PERSONA mic <audio 1> opened", fresh.Text("PERSONA mic Speakers (Tomas Lind's Echo) opened"));
        }

        [Fact]
        public void APhoneNamedAfterAPerson_IsAPhoneStandIn_WhereAWebControllersNameCarriesIt()
        {
            var s = New();
            Assert.Equal("WEBGP deadline DualShock 4 Web Controller: <phone 1> idle=1200ms neutralized",
                s.Text("WEBGP deadline DualShock 4 Web Controller: " + Phone + " idle=1200ms neutralized"));
            Assert.Equal("DEV - Browser Gamepad: <phone 1>", s.Text("DEV - Browser Gamepad: " + Phone));
            Assert.Equal("seen <phone 1> again", s.Text("seen " + Phone + " again"));
            Assert.Equal(Phone, PrivacyScrubber.WebViewerOf("Xbox 360 Web Controller: " + Phone));
            Assert.Null(PrivacyScrubber.WebViewerOf("Xbox 360 Web Controller 3"));
        }

        [Fact]
        public void WhatTheRecognizerHeard_IsItsLength_AndARegisteredPhraseIsItsStandIn()
        {
            var s = New();
            s.Know(PrivateKind.VoicePhrase, "open the pod bay doors");
            Assert.Equal("VOICE [<audio 1>] heard \"<speech of 28 characters>\" conf=0.91 gate=1 fires=0",
                s.Text("VOICE [" + Headset + "] heard \"" + Said + "\" conf=0.91 gate=1 fires=0"));
            Assert.Equal("VOICE [<audio 1>] heard \"<phrase 1>\" conf=0.88 gate=1 fires=1",
                s.Text("VOICE [" + Headset + "] heard \"open the pod bay doors\" conf=0.88 gate=1 fires=1"));
            Assert.Equal("VOICE [<audio 1>] alternates: \"<speech of 25 characters>\" 0.41 | \"<speech of 26 characters>\" 0.22",
                s.Text("VOICE [" + Headset + "] alternates: \"meet me by the north gate\" 0.41 | \"meat me buy the north gait\" 0.22"));
            Assert.Equal("VOICE [<audio 1>] rejected (nearest \"<speech of 13 characters>\" conf=0.31)",
                s.Text("VOICE [" + Headset + "] rejected (nearest \"pass the salt\" conf=0.31)"));
            Assert.Equal("VOICE dialog row NO MATCH for \"<speech of 13 characters>\" (rows=3)",
                s.Text("VOICE dialog row NO MATCH for \"turn left now\" (rows=3)"));
            // Quoted text outside the voice lines is no speech.
            Assert.Equal("BINDERR \"Path\" not found on \"PadViewModel\"", s.Text("BINDERR \"Path\" not found on \"PadViewModel\""));
            Assert.Contains(s.Counts(), c => c.Kind == PrivateKind.Speech && c.Places == 5);
        }

        [Fact]
        public void TheCanary_TriesTheVoiceAndWebLines_AndNamesTheKindsThatCameThrough()
        {
            var s = New();
            Assert.Empty(PrivacyScrubber.ShapeSurvivors(s.Text));
            Assert.Equal(new[] { PrivateKind.AudioDevice, PrivateKind.Phone, PrivateKind.Speech, PrivateKind.Key }, PrivacyScrubber.ShapeSurvivors(line => line));
        }

        [Fact]
        public void TheSearchAtTheStop_TakesMicrophonesPhonesAndSpeechOutOfALineWrittenRaw()
        {
            var s = New();
            string raw = "VOICE [" + Headset + "] heard \"" + Said + "\" conf=0.80 gate=1 fires=0";
            string searched = s.SearchFinished(raw);
            Assert.Equal("VOICE [<audio 1>] heard \"<speech of 28 characters>\" conf=0.80 gate=1 fires=0", searched);
            Assert.Equal("WEBGP deadline Browser Gamepad: <phone 1> idle=900ms neutralized",
                s.SearchFinished("WEBGP deadline Browser Gamepad: " + Phone + " idle=900ms neutralized"));
            var counts = s.Counts();
            Assert.Equal(1, counts.Single(c => c.Kind == PrivateKind.AudioDevice).FoundLate);
            Assert.Equal(1, counts.Single(c => c.Kind == PrivateKind.Speech).FoundLate);
            Assert.Equal(1, counts.Single(c => c.Kind == PrivateKind.Phone).FoundLate);
            Assert.Equal(searched, s.SearchFinished(searched, count: false));
        }

        [Fact]
        public void TheFirstDigitsOfAKey_AreTheKeysStandIn_WhenTheyAreKnown()
        {
            var s = New();
            s.Know(PrivateKind.Key, "9F86D081884C7D65");
            s.Know(PrivateKind.Peer, "3A7BD3E2");
            Assert.Equal("RELAY dial: relay host-key=<key 1> chan=0A1B2C3D", s.Text("RELAY dial: relay host-key=9F86D081884C7D65 chan=0A1B2C3D"));
            Assert.Equal("PATH upgraded for peer <peer 1>", s.Text("PATH upgraded for peer 3A7BD3E2"));
        }

        [Fact]
        public void AKnownPhoneNameWithACommaInIt_IsReplacedWhole_BeforeTheWebControllerRuleCanCutIt()
        {
            var s = New();
            s.Know(PrivateKind.Phone, "Smith, Jane");
            string line = s.Text("DEV - Browser Gamepad: Smith, Jane");
            Gone(line, "Jane");
            Assert.Equal("DEV - Browser Gamepad: <phone 1>", line);
            var late = New();
            late.Know(PrivateKind.Phone, "Smith, Jane");
            Assert.Equal("DEV - Browser Gamepad: <phone 1>", late.SearchFinished("DEV - Browser Gamepad: Smith, Jane"));
            // A name known first as another kind is still the phone's.
            var both = New();
            both.Know(PrivateKind.DeviceName, "Smith, Jane");
            both.Know(PrivateKind.Phone, "Smith, Jane");
            Gone(both.Text("DEV - Browser Gamepad: Smith, Jane"), "Jane");
            // A microphone whose name holds the user's keeps the one stand-in
            // its typed name gets.
            var typed = New();
            string mic = "Headset (" + User + "'s Buds)";
            Assert.Equal("VOICE [<audio 1>] listening", typed.Text("VOICE [" + mic + "] listening"));
            Assert.Equal("<audio 1>", typed.Alias(PrivateKind.AudioDevice, mic));
        }

        [Fact]
        public void TheKeyARelayLineNamesByItsFirstDigits_IsAStandIn_WhenNobodyNamedIt()
        {
            var s = New();
            string connected = s.Text("RELAY identity-connected: relay.example.net as 9F86D081884C7D65");
            Gone(connected, "9F86D081884C7D65");
            Assert.EndsWith(" as <key 1>", connected);
            // One key, one stand-in, wherever a relay line names it.
            Assert.Equal("RELAY listen: caller <key 1>", s.Text("RELAY listen: caller 9F86D081884C7D65"));
            Assert.Equal("RELAY identity dial: relay peer=<key 2> chan=0A1B2C3D",
                s.Text("RELAY identity dial: relay peer=3A7BD3E29F86D081 chan=0A1B2C3D"));
            // The search at the stop takes one out of a line written raw.
            var late = New();
            Assert.Equal("RELAY connect: HELLO from <key 1>", late.SearchFinished("RELAY connect: HELLO from 3a7bd3e29f86d081"));
            // Sixteen hex digits outside a relay line are no key.
            Assert.Equal("SDL pad 0123456789ABCDEF opened", New().Text("SDL pad 0123456789ABCDEF opened"));
        }

        [Fact]
        public void TheMarkOfALineLeftOut_IsAStandIn_AndAPlainScrubberLeavesNoLineOut()
        {
            Assert.True(PrivacyScrubber.IsStandIn(PrivacyScrubber.LineLeftOut));
            Assert.Equal(0, New().LinesLeftOut);
        }
    }
}
