using System;
using System.IO;
using System.Linq;
using System.Reflection;
using NAudio.Wave;
using PadForge.Common.Input;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// #571: Audio Rumble's Sensitivity reaches 60 and its Bass Cutoff 2000 Hz,
    /// on the Force Feedback tab and on the trigger chain of the Impulse
    /// Triggers tab. The requester drives a direct-drive wheel's force
    /// feedback from game audio, and some games' engine notes sit above the
    /// old 200 Hz top. The detector, the view model and the sliders read one
    /// set of ranges (AudioBassDetector's constants).
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class AudioRumbleRangeTests
    {
        [Fact]
        public void TheRangesAreTheRequestedOnes()
        {
            Assert.Equal(1f, AudioBassDetector.MinSensitivity);
            Assert.Equal(60f, AudioBassDetector.MaxSensitivity);
            Assert.Equal(20f, AudioBassDetector.MinCutoffHz);
            Assert.Equal(2000f, AudioBassDetector.MaxCutoffHz);
        }

        /// <summary>The Pad page's settings take the new tops and stop there,
        /// on both chains. The defaults stay 4 and 80 Hz.</summary>
        [Fact]
        public void ThePadSettings_ReachTheNewTops_AndStopThere()
        {
            var vm = new PadViewModel(0);
            Assert.Equal(4.0, vm.AudioRumbleSensitivity);
            Assert.Equal(80.0, vm.AudioRumbleCutoffHz);
            Assert.Equal(4.0, vm.AudioRumbleTriggersSensitivity);
            Assert.Equal(80.0, vm.AudioRumbleTriggersCutoffHz);

            vm.AudioRumbleSensitivity = 60; Assert.Equal(60.0, vm.AudioRumbleSensitivity);
            vm.AudioRumbleSensitivity = 75; Assert.Equal(60.0, vm.AudioRumbleSensitivity);
            vm.AudioRumbleSensitivity = 0.5; Assert.Equal(1.0, vm.AudioRumbleSensitivity);
            vm.AudioRumbleCutoffHz = 2000; Assert.Equal(2000.0, vm.AudioRumbleCutoffHz);
            vm.AudioRumbleCutoffHz = 2500; Assert.Equal(2000.0, vm.AudioRumbleCutoffHz);
            vm.AudioRumbleCutoffHz = 10; Assert.Equal(20.0, vm.AudioRumbleCutoffHz);

            vm.AudioRumbleTriggersSensitivity = 60; Assert.Equal(60.0, vm.AudioRumbleTriggersSensitivity);
            vm.AudioRumbleTriggersSensitivity = 75; Assert.Equal(60.0, vm.AudioRumbleTriggersSensitivity);
            vm.AudioRumbleTriggersCutoffHz = 2000; Assert.Equal(2000.0, vm.AudioRumbleTriggersCutoffHz);
            vm.AudioRumbleTriggersCutoffHz = 2500; Assert.Equal(2000.0, vm.AudioRumbleTriggersCutoffHz);
        }

        /// <summary>The detector the poll thread hands those settings to takes
        /// the same tops, on both chains.</summary>
        [Fact]
        public void TheDetector_ReachesTheNewTops_AndStopsThere()
        {
            using var detector = new AudioBassDetector();
            detector.Sensitivity = 60; Assert.Equal(60f, detector.Sensitivity);
            detector.Sensitivity = 75; Assert.Equal(60f, detector.Sensitivity);
            detector.CutoffHz = 2000; Assert.Equal(2000f, detector.CutoffHz);
            detector.CutoffHz = 2500; Assert.Equal(2000f, detector.CutoffHz);
            detector.CutoffHz = 10; Assert.Equal(20f, detector.CutoffHz);
            detector.TriggerSensitivity = 60; Assert.Equal(60f, detector.TriggerSensitivity);
            detector.TriggerSensitivity = 75; Assert.Equal(60f, detector.TriggerSensitivity);
            detector.TriggerCutoffHz = 2000; Assert.Equal(2000f, detector.TriggerCutoffHz);
            detector.TriggerCutoffHz = 2500; Assert.Equal(2000f, detector.TriggerCutoffHz);
        }

        /// <summary>A 1 kHz engine note, the kind the requester's games make
        /// above 200 Hz, drives both chains with the cutoff at 2000 Hz and
        /// stays out of them at 200 Hz. Run through the detector's own capture
        /// callback, the way CapturePcmFormatTests feeds it.</summary>
        [Fact]
        public void AnEngineNoteAbove200Hz_DrivesRumble_OnlyWithTheWiderCutoff()
        {
            Assert.True(Level(200f).Body < 0.01f);
            Assert.True(Level(200f).Triggers < 0.01f);
            var wide = Level(2000f);
            Assert.True(wide.Body > 0.3f, "body chain at 2000 Hz: " + wide.Body);
            Assert.True(wide.Triggers > 0.3f, "trigger chain at 2000 Hz: " + wide.Triggers);
        }

        /// <summary>The four sliders reach the same tops the settings do.</summary>
        [Fact]
        public void TheSliders_ReachTheSameTops()
        {
            string xaml = RepoText("PadForge.App", "Views", "PadPage.xaml");
            foreach (string sensitivity in new[] { "AudioRumbleSensitivity", "AudioRumbleTriggersSensitivity" })
            {
                string slider = SliderOf(xaml, sensitivity);
                Assert.Contains("Minimum=\"1\"", slider);
                Assert.Contains("Maximum=\"60\"", slider);
            }
            foreach (string cutoff in new[] { "AudioRumbleCutoffHz", "AudioRumbleTriggersCutoffHz" })
            {
                string slider = SliderOf(xaml, cutoff);
                Assert.Contains("Minimum=\"20\"", slider);
                Assert.Contains("Maximum=\"2000\"", slider);
            }
        }

        // ── Helpers ──

        private static (float Body, float Triggers) Level(float cutoffHz)
        {
            using var detector = new AudioBassDetector
            {
                CutoffHz = cutoffHz,
                TriggerCutoffHz = cutoffHz,
                Sensitivity = 4,
                TriggerSensitivity = 4,
            };
            var capture = new Capture(WaveFormat.CreateIeeeFloatWaveFormat(48000, 2));
            var callback = typeof(AudioBassDetector).GetMethod("OnDataAvailable", BindingFlags.Instance | BindingFlags.NonPublic)!;
            const int frames = 480;
            int n = 0;
            for (int buffer = 0; buffer < 40; buffer++)
            {
                var bytes = new byte[frames * 2 * sizeof(float)];
                for (int i = 0; i < frames; i++, n++)
                {
                    float sample = 0.5f * MathF.Sin(2f * MathF.PI * 1000f * n / 48000f);
                    BitConverter.TryWriteBytes(bytes.AsSpan(i * 8), sample);
                    BitConverter.TryWriteBytes(bytes.AsSpan(i * 8 + 4), sample);
                }
                callback.Invoke(detector, new object[] { capture, new WaveInEventArgs(bytes, bytes.Length) });
            }
            return (detector.BassEnergy, detector.TriggerBassEnergy);
        }

        private sealed class Capture : IWaveIn
        {
            public Capture(WaveFormat format) => WaveFormat = format;
            public WaveFormat WaveFormat { get; set; }
            public event EventHandler<WaveInEventArgs> DataAvailable { add { } remove { } }
            public event EventHandler<StoppedEventArgs> RecordingStopped { add { } remove { } }
            public void StartRecording() { }
            public void StopRecording() { }
            public void Dispose() { }
        }

        private static string SliderOf(string xaml, string property)
        {
            int at = xaml.IndexOf("<Slider Value=\"{Binding " + property + ",", StringComparison.Ordinal);
            Assert.True(at > 0, "no slider for " + property);
            int end = xaml.IndexOf("/>", at, StringComparison.Ordinal);
            return xaml.Substring(at, end - at);
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
