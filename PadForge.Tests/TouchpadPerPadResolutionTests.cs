using System;
using System.Linq;
using System.Reflection;
using PadForge.Engine.Touchpad;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// EVERY touchpad setting resolves per (device, pad).
    ///
    /// <para>The first cut split only the pointer region and left the engine's
    /// providers resolving per device, so swipe haptics kept behaving
    /// device-wide while its card sat on a per-pad tab: the UI stored the
    /// toggle on the selected pad and the engine read whichever entry won
    /// across the device. The gesture snapshot went as far as destructuring
    /// its key as <c>(slotIndex, deviceGuid, _)</c>, discarding the pad
    /// outright.</para>
    ///
    /// <para>These tests are deliberately field-agnostic. A future setting
    /// added to the class is covered without anyone remembering to extend
    /// them.</para>
    /// </summary>
    public class TouchpadPerPadResolutionTests
    {
        private const string Dev = "per-pad-resolution-dev";

        private static TouchpadSettingsEntry Entry(int pad, Action<TouchpadGestureSettings> tune)
        {
            var s = TouchpadGestureSettings.Default();
            tune(s);
            return new TouchpadSettingsEntry { DeviceGuid = Dev, TouchpadIndex = pad, Settings = s };
        }

        [Fact]
        public void SwipeHapticsResolvePerPad()
        {
            // The reported symptom, pinned directly.
            var entries = new[]
            {
                Entry(0, s => { s.EnableSwipeHaptics = false; s.SwipeHapticsIntensity = 0.10f; }),
                Entry(1, s => { s.EnableSwipeHaptics = true;  s.SwipeHapticsIntensity = 0.90f; }),
            };

            var p0 = TouchpadGestureSettings.ResolveForPad(entries, Dev, 0);
            var p1 = TouchpadGestureSettings.ResolveForPad(entries, Dev, 1);

            Assert.False(p0.EnableSwipeHaptics);
            Assert.True(p1.EnableSwipeHaptics);
            Assert.Equal(0.10f, p0.SwipeHapticsIntensity);
            Assert.Equal(0.90f, p1.SwipeHapticsIntensity);
        }

        [Fact]
        public void EverySettingOnTheClassResolvesPerPad()
        {
            // Field-agnostic closure. Perturb one pad's entry field by field
            // and require the OTHER pad to keep its own value, so a setting
            // added later is covered without editing this test.
            var props = typeof(TouchpadGestureSettings)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.CanRead && p.CanWrite)
                .Where(p => p.GetCustomAttributes(typeof(System.Xml.Serialization.XmlAttributeAttribute), false).Any())
                // Deserialize-only legacy shims alias other fields on purpose.
                .Where(p => !p.Name.StartsWith("PointerStretch", StringComparison.Ordinal))
                .ToList();
            Assert.True(props.Count >= 40, $"only {props.Count} persisted settings found");

            foreach (var p in props)
            {
                var e0 = Entry(0, _ => { });
                var e1 = Entry(1, _ => { });
                object baseline = p.GetValue(e0.Settings);
                object perturbed = Perturb(p, baseline);
                if (perturbed == null) continue;      // type we cannot vary
                p.SetValue(e1.Settings, perturbed);

                var entries = new[] { e0, e1 };
                var got0 = p.GetValue(TouchpadGestureSettings.ResolveForPad(entries, Dev, 0));
                var got1 = p.GetValue(TouchpadGestureSettings.ResolveForPad(entries, Dev, 1));

                Assert.True(Equals(got1, perturbed),
                    $"{p.Name}: pad 1 did not read its own value");
                Assert.True(Equals(got0, baseline),
                    $"{p.Name}: pad 0 read pad 1's value, so this setting is NOT per pad");
            }
        }

        private static object Perturb(PropertyInfo p, object baseline)
        {
            if (p.PropertyType == typeof(bool)) return !(bool)baseline;
            if (p.PropertyType == typeof(float)) return (float)baseline + 0.37f;
            if (p.PropertyType == typeof(int)) return (int)baseline + 7;
            if (p.PropertyType == typeof(string)) return (baseline as string ?? "") + "-x";
            return null;
        }

        [Fact]
        public void PositiveControl_TheTwoPadsAreDistinctEntries()
        {
            // Without this the sweep could pass on a resolver that returned a
            // fresh Default() for both pads, where nothing ever matches.
            var entries = new[]
            {
                Entry(0, s => s.CooldownMs = 111),
                Entry(1, s => s.CooldownMs = 222),
            };
            Assert.Equal(111, TouchpadGestureSettings.ResolveForPad(entries, Dev, 0).CooldownMs);
            Assert.Equal(222, TouchpadGestureSettings.ResolveForPad(entries, Dev, 1).CooldownMs);
        }

        [Fact]
        public void AProfileWrittenBeforeTheSplitStillAppliesToEveryPad()
        {
            // Migration: one entry, no pad-specific siblings. It must keep
            // applying everywhere until the user tunes a pad apart, or an
            // upgrade would silently reset every pad but one.
            var entries = new[] { Entry(0, s => s.EnableSwipeHaptics = true) };
            Assert.True(TouchpadGestureSettings.ResolveForPad(entries, Dev, 0).EnableSwipeHaptics);
            Assert.True(TouchpadGestureSettings.ResolveForPad(entries, Dev, 1).EnableSwipeHaptics);
            Assert.True(TouchpadGestureSettings.ResolveForPad(entries, Dev, 2).EnableSwipeHaptics);
        }

        [Fact]
        public void NoProviderStillResolvesPerDevice()
        {
            // Grep-as-a-test. The three engine seams (gesture snapshot, mouse
            // provider, pointer region) all read per pad now; a new one added
            // against ResolveForDevice would reintroduce exactly the swipe
            // haptics bug, silently.
            var svc = System.IO.Path.Combine(RepoRoot(), "PadForge.App", "Services", "InputService.cs");
            var text = System.IO.File.ReadAllText(svc);
            var offending = text.Split('\n')
                .Select((l, i) => (Line: l, No: i + 1))
                .Where(t => (t.Line.Contains("ResolveForDevice") || t.Line.Contains("ResolveEntryForDevice"))
                            && !t.Line.TrimStart().StartsWith("//"))
                .ToList();
            Assert.True(offending.Count == 0,
                "InputService resolves touchpad settings per DEVICE at: "
                + string.Join(", ", offending.Select(t => t.No)));
        }

        private static string RepoRoot()
        {
            var d = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !System.IO.File.Exists(System.IO.Path.Combine(d.FullName, "PadForge.sln")))
                d = d.Parent;
            Assert.NotNull(d);
            return d.FullName;
        }
    }
}
