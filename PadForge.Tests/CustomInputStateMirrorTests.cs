using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using PadForge.Engine;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Reflection round-trip guards for the CustomInputState mirror family
    /// (lens 1m): CopyInto is the ONE full-field copy (Clone delegates to
    /// it) and ResetForReuse must equal fresh construction. Every public
    /// instance field is populated with a nonzero value via reflection, so
    /// a field added to the class without joining CopyInto/ResetForReuse
    /// fails here the day it lands instead of shipping a stale-data or
    /// dropped-data bug into the pooled read path.
    /// </summary>
    public class CustomInputStateMirrorTests
    {
        /// <summary>Fills every public instance field with distinctive
        /// nonzero data, recursing into arrays and the nested state
        /// classes. Seed varies the values so copy-direction mixups fail.</summary>
        private static void Populate(object obj, int seed)
        {
            foreach (var f in obj.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                object val = f.GetValue(obj);
                var t = f.FieldType;
                if (t == typeof(int)) f.SetValue(obj, seed + 7);
                else if (t == typeof(float)) f.SetValue(obj, seed + 0.5f);
                else if (t == typeof(bool)) f.SetValue(obj, true);
                else if (t == typeof(int[])) f.SetValue(obj, FillInt((int[])val ?? new int[3], seed));
                else if (t == typeof(float[])) f.SetValue(obj, FillFloat((float[])val ?? new float[3], seed));
                else if (t == typeof(bool[])) f.SetValue(obj, FillBool((bool[])val ?? new bool[3]));
                else if (t == typeof(byte[])) f.SetValue(obj, FillByte((byte[])val ?? new byte[3], seed));
                else if (t == typeof(WiiIrState)) f.SetValue(obj, new WiiIrState { X = seed + 0.25f, Y = seed + 0.75f, Detected = true });
                else if (t == typeof(TouchpadInputState[]))
                {
                    var pads = new TouchpadInputState[2];
                    for (int i = 0; i < pads.Length; i++)
                    {
                        pads[i] = new TouchpadInputState(3);
                        Populate(pads[i], seed + i);
                        // Restore the class invariant Populate just stomped:
                        // MaxFingers mirrors the finger arrays' length.
                        pads[i].MaxFingers = 3;
                    }
                    f.SetValue(obj, pads);
                }
                else if (t == typeof(MidiInputState))
                {
                    var midi = new MidiInputState();
                    Populate(midi, seed);
                    f.SetValue(obj, midi);
                }
                else if (t.IsClass && val != null)
                {
                    Populate(val, seed);
                }
                else if (!t.IsValueType)
                {
                    Assert.Fail($"Populate does not know field type {t.Name} ({f.Name}). Teach it so the mirror guard keeps covering every field.");
                }
            }
        }

        private static int[] FillInt(int[] a, int seed) { for (int i = 0; i < a.Length; i++) a[i] = seed + i + 1; return a; }
        private static float[] FillFloat(float[] a, int seed) { for (int i = 0; i < a.Length; i++) a[i] = seed + i + 1.5f; return a; }
        private static bool[] FillBool(bool[] a) { for (int i = 0; i < a.Length; i++) a[i] = true; return a; }
        private static byte[] FillByte(byte[] a, int seed) { for (int i = 0; i < a.Length; i++) a[i] = (byte)(seed + i + 1); return a; }

        /// <summary>Deep structural equality over public instance fields.</summary>
        private static void AssertDeepEqual(object a, object b, string path)
        {
            if (a == null || b == null) { Assert.True(a == null && b == null, $"{path}: null mismatch"); return; }
            var t = a.GetType();
            Assert.Equal(t, b.GetType());
            if (t.IsPrimitive || t == typeof(string)) { Assert.True(Equals(a, b), $"{path}: {a} != {b}"); return; }
            if (a is IEnumerable ea && b is IEnumerable eb && t != typeof(string))
            {
                var la = ea.Cast<object>().ToList();
                var lb = eb.Cast<object>().ToList();
                Assert.True(la.Count == lb.Count, $"{path}: length {la.Count} != {lb.Count}");
                for (int i = 0; i < la.Count; i++) AssertDeepEqual(la[i], lb[i], $"{path}[{i}]");
                return;
            }
            foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.Instance))
                AssertDeepEqual(f.GetValue(a), f.GetValue(b), $"{path}.{f.Name}");
        }

        [Fact]
        public void CopyInto_CarriesEveryField()
        {
            var src = new CustomInputState();
            Populate(src, 10);
            var dst = new CustomInputState();
            src.CopyInto(dst);
            AssertDeepEqual(src, dst, "state");
        }

        [Fact]
        public void CopyInto_ReusesShapes_AndOverwritesStaleData()
        {
            var src = new CustomInputState();
            Populate(src, 10);
            var dst = new CustomInputState();
            Populate(dst, 99);   // stale different-valued data, same shapes
            src.CopyInto(dst);
            AssertDeepEqual(src, dst, "state");
        }

        [Fact]
        public void ResetForReuse_EqualsFreshConstruction()
        {
            var pooled = new CustomInputState();
            Populate(pooled, 10);
            // Fresh + the same open-time nested shapes the pooled one carries,
            // zeroed: what a wrapper-fresh state looks like on a touchpad and
            // MIDI capable device.
            var fresh = new CustomInputState
            {
                Touchpads = new[] { new TouchpadInputState(3), new TouchpadInputState(3) },
                CapSense = new bool[3],
                Midi = new MidiInputState(),
            };
            pooled.ResetForReuse();
            AssertDeepEqual(fresh, pooled, "state");
        }

        [Fact]
        public void Clone_StillDeepAndIndependent()
        {
            var src = new CustomInputState();
            Populate(src, 10);
            var clone = src.Clone();
            AssertDeepEqual(src, clone, "state");
            clone.Axis[0] = 12345;
            clone.Touchpads[0].FingerX[0] = 999f;
            Assert.NotEqual(12345, src.Axis[0]);
            Assert.NotEqual(999f, src.Touchpads[0].FingerX[0]);
        }
    }
}
