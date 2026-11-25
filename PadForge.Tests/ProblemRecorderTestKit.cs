using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using PadForge.Common.Recording;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Engine.Privacy;
using PadForge.Engine.Recording;
using PadForge.Services;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// What the problem recorder's tests (#597) share: a host that answers
    /// from lists of its own and writes to a folder of its own, so a
    /// recorder made on it touches neither the engine nor the app's folder,
    /// and the helpers that feed a capture and read a file back.
    /// </summary>
    internal sealed class RecorderTestHost : RecorderHost, IDisposable
    {
        internal const string User = "marisol";
        internal const string Pc = "DESKTOP-Q7KX2M";

        internal readonly string Dir = Path.Combine(Path.GetTempPath(), "PadForge.RecorderTests", Guid.NewGuid().ToString("N"));
        internal readonly List<UserDevice> DeviceList = new();
        internal readonly List<(PrivateKind Kind, string Value)> Live = new();
        internal readonly List<string> System = new() { "PadForge test" };
        internal readonly List<string> Controllers = new();
        internal readonly List<string> SettingsPart = new();
        internal readonly Dictionary<Guid, ulong[]> Read = new();
        internal List<(long Ticks, string Line)> Lines = new();

        internal RecorderTestHost() => Directory.CreateDirectory(Dir);

        public override string Folder => Dir;
        public override bool AttachesToEngine => false;

        public override PrivacyEnvironment Environment() => new()
        {
            UserName = User,
            DomainName = Pc,
            PcName = Pc,
            ProfileFolder = @"C:\Users\" + User,
            AppFolder = Dir,
        };

        public override List<UserDevice> Devices() => new(DeviceList);
        public override void LiveValues(List<(PrivateKind Kind, string Value)> into) => into.AddRange(Live);
        public override void SystemLines(PrivacyScrubber scrubber, List<string> into) => into.AddRange(System);
        public override void ControllerLines(PrivacyScrubber scrubber, List<string> into) => into.AddRange(Controllers);
        public override void SettingsLines(PrivacyScrubber scrubber, List<string> into) => into.AddRange(SettingsPart);
        public override ulong[] KeysRead(UserDevice keyboard) => Read.TryGetValue(keyboard.InstanceGuid, out var bits) ? bits : new ulong[4];
        public override List<(long Ticks, string Line)> History() => Lines;
        public override bool Front(out string path, out bool named, out string profile)
        {
            path = profile = null;
            named = false;
            return false;
        }

        protected override string MappingOf(ISdlInputDevice device) => null;
        protected override int MotorAxes(ISdlInputDevice device) => -1;

        // The watch's questions (#600), answered from the test's own lists,
        // so nothing of the bench is read.
        internal bool EngineRuns;
        internal List<(UserDevice Device, bool Listed)> RawListing;
        internal string Programs;
        internal Dictionary<string, string> Settings;
        /// <summary>The watch reads the app's own settings state, from
        /// <see cref="RecorderHost.Main"/>, instead of <see cref="Settings"/>.</summary>
        internal bool RealSettings;
        internal string Activity = string.Empty;
        public override string ActivityText() => Activity;
        public override bool EngineRunning() => EngineRuns;
        public override List<(UserDevice Device, bool Listed)> RawInputListing() => RawListing;
        public override string ControllerProgramsNow() => Programs;
        public override Dictionary<string, string> SettingsState()
            => RealSettings ? base.SettingsState() : Settings == null ? null : new Dictionary<string, string>(Settings);

        public void Dispose()
        {
            try { Directory.Delete(Dir, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    internal static class RecorderTestKit
    {
        internal static UserDevice Pad(string name = "Test Pad", ushort vid = 0x054C, ushort pid = 0x0CE6, int type = InputDeviceType.Gamepad)
            => new()
            {
                InstanceGuid = Guid.NewGuid(),
                ProductName = name,
                InstanceName = name,
                VendorId = vid,
                ProdId = pid,
                CapType = type,
                CapAxeCount = 6,
                CapButtonCount = 17,
                CapPovCount = 1,
                SdlGuid = "030000004c050000e60c000000006800",
                DevicePath = @"\\?\hid#vid_054c&pid_0ce6&mi_03#8&1a2b3c4d&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}",
                SerialNumber = "A0AB51C32FCB",
                IsOnline = true,
            };

        internal static UserDevice Keyboard(string name = "Test Keyboard")
        {
            var device = Pad(name, 0x046D, 0xC31C, InputDeviceType.Keyboard);
            device.SerialNumber = string.Empty;
            device.DevicePath = @"\\?\hid#vid_046d&pid_c31c&mi_00#7&2b3c4d5e&0&0000#{884b96c3-56ef-11d1-bc8c-00a0c91405dd}";
            return device;
        }

        /// <summary>A state at rest with a pad's two sticks centered.</summary>
        internal static CustomInputState Rest()
        {
            var state = new CustomInputState();
            for (int i = 0; i < 4; i++) state.Axis[i] = 32768;
            return state;
        }

        /// <summary>Moves the capture's clock on, and the recorder's with
        /// it.</summary>
        internal static void Advance(this RecordingCapture capture, double seconds)
            => capture.Shift += (long)(seconds * Stopwatch.Frequency);

        /// <summary>Every line of a file.</summary>
        internal static string[] Lines(string path) => Text(path).Split("\r\n").SkipLast(1).ToArray();

        /// <summary>A recording's whole text, from its zip or a plain file.</summary>
        internal static string Text(string path)
        {
            using var reader = RecordingReader.OpenText(path);
            return reader.ReadToEnd();
        }

        /// <summary>The events of a file as (seconds, layer, subject,
        /// payload), in the order written.</summary>
        internal static List<(double Seconds, char Layer, string Subject, string Payload)> Events(string path)
        {
            var events = new List<(double, char, string, string)>();
            using var reader = RecordingReader.Open(path);
            while (reader.Read())
                events.Add((reader.Seconds, reader.Layer, reader.Subject, reader.Payload.ToString()));
            return events;
        }

        /// <summary>The state each device ends each of its times with: the
        /// last line of every run of lines that share a time, which is one
        /// pass however the recorder's thread cut it.</summary>
        internal static List<RecordedInputFrame> PassFrames(string path, int device)
        {
            List<RecordedInputFrame> all;
            using (var text = RecordingReader.OpenText(path)) all = RecordingReader.ReadInputFrames(text);
            var frames = new List<RecordedInputFrame>();
            foreach (var frame in all.Where(f => f.Device == device))
            {
                if (frames.Count > 0 && frames[^1].Microseconds == frame.Microseconds) frames[^1] = frame;
                else frames.Add(frame);
            }
            return frames;
        }

        /// <summary>Deep equality over a state's public fields, to the bit.
        /// The analog keys are compared as the set they are, and the table
        /// of read stamps is the wrapper's own and no part of a report.</summary>
        internal static void AssertSameState(object expected, object actual, string path = "state")
        {
            if (expected == null || actual == null)
            {
                Assert.True(expected == null && actual == null, path + ": one is null");
                return;
            }
            var type = expected.GetType();
            Assert.Equal(type, actual.GetType());
            if (expected is AnalogKeyInputState keys)
            {
                var other = (AnalogKeyInputState)actual;
                Assert.True(keys.SameAs(other) && other.SameAs(keys), path + ": the analog keys differ");
                return;
            }
            if (type == typeof(float))
            {
                Assert.True(BitConverter.SingleToInt32Bits((float)expected) == BitConverter.SingleToInt32Bits((float)actual),
                    $"{path}: {expected} != {actual}");
                return;
            }
            if (type.IsPrimitive || type == typeof(string))
            {
                Assert.True(Equals(expected, actual), $"{path}: {expected} != {actual}");
                return;
            }
            if (expected is IEnumerable a && actual is IEnumerable b)
            {
                var la = a.Cast<object>().ToList();
                var lb = b.Cast<object>().ToList();
                Assert.True(la.Count == lb.Count, $"{path}: length {la.Count} != {lb.Count}");
                for (int i = 0; i < la.Count; i++) AssertSameState(la[i], lb[i], $"{path}[{i}]");
                return;
            }
            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
                AssertSameState(field.GetValue(expected), field.GetValue(actual), path + "." + field.Name);
        }
    }
}
