using System;
using System.Collections.Generic;
using PadForge.Engine.Common.Mapping;
using PadForge.Engine.Data;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The mouse steer checks every source read asks (IsMouseTravel,
    /// MouseSteerOf, ReadsOwnGate) answer before the kind's string tests for
    /// a descriptor that cannot be one of theirs. The answers equal those of
    /// the checks as they were, kept here as the reference, for every
    /// source of a grid of descriptors, kinds and settings.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class MouseSteerFastRejectTests
    {
        private static bool ReferenceIsMouseTravel(MappingSource source)
            => source != null && SourceEvaluator.IsDescriptorKind(source.Kind)
               && (SourceCoercion.IsMouseTravelDescriptor(source.Descriptor)
                   || (source.ParamAxisRelative && SourceCoercion.TryParseRelativeAxis(source.Descriptor, out _, out _)));

        private static SourceEvaluator.MouseSteerFamily ReferenceMouseSteerOf(MappingSource src, string evaluatedDeviceGuid, out bool y)
        {
            y = false;
            if (src == null || !SourceEvaluator.IsDescriptorKind(src.Kind) || src.ParamAxisRelative) return SourceEvaluator.MouseSteerFamily.None;
            string d = src.Descriptor;
            SourceEvaluator.MouseSteerFamily family;
            if (d == "Mouse Position X" || d == "Mouse Position Y")
            {
                family = SourceEvaluator.MouseSteerFamily.Position;
                y = d[d.Length - 1] == 'Y';
            }
            else if (d == "Axis 0" || d == "Axis 1")
            {
                family = SourceEvaluator.MouseSteerFamily.Speed;
                y = d == "Axis 1";
            }
            else return SourceEvaluator.MouseSteerFamily.None;

            bool waits = !string.IsNullOrEmpty(src.GateDescriptor) || !string.IsNullOrEmpty(src.Gate2Descriptor);
            string steer = MappingSource.EffectiveMouseSteer(src.ParamTravelSteer, null, waits, holdsPosition: false);
            bool own = steer is MappingSource.TravelSteerHeldStay or MappingSource.TravelSteerToggle
                || MappingSource.EffectiveTravelSmoothingMs(src.ParamTravelSmoothingMs) > 0;
            if (family == SourceEvaluator.MouseSteerFamily.Speed)
                own |= MappingSource.EffectiveMouseSpeedSensitivity(src.ParamMouseSpeedSensitivity) != MappingSource.DefaultMouseSpeedSensitivity
                    || MappingSource.EffectiveMouseBackToCenter(src.ParamMouseBackToCenter) > 0;
            else
                own |= src.ParamMousePositionAnchor && waits;
            if (!own) return SourceEvaluator.MouseSteerFamily.None;
            if (family == SourceEvaluator.MouseSteerFamily.Speed)
            {
                var isMouse = SourceCoercion.IsMouseDeviceProvider;
                if (isMouse == null || !isMouse(string.IsNullOrEmpty(src.DeviceGuid) ? evaluatedDeviceGuid : src.DeviceGuid))
                    return SourceEvaluator.MouseSteerFamily.None;
            }
            return family;
        }

        private static readonly string[] Descriptors =
        {
            null, "", "M", "W", "A", "Axis 0", "Axis 1", "Axis 2", "Axis 10", "axis 0", "Axis 0 ", "Slider 0", "Slider 3",
            "Mouse Travel X", "Mouse Travel Y", "Mouse Travel ", "Wheel Travel", "Wheel Travel Sideways", "Wheel",
            "Mouse Position X", "Mouse Position Y", "Mouse Position Z", "Mouse Speed X", "Button 0", "Button 16",
            "POV 0 Up", "Gamepad A", "Touchpad 1 X", "Wheel Up", "Mouse Button 1", "Axis X",
        };

        private static readonly string[] Kinds = { null, "", "Direct", "Toggle", "RapidTrigger", "Incremental", "Ramped", "Macro", "direct" };

        private static IEnumerable<MappingSource> Sources()
        {
            string mouse = Guid.NewGuid().ToString(), pad = Guid.NewGuid().ToString();
            foreach (var d in Descriptors)
                foreach (var k in Kinds)
                    foreach (bool relative in new[] { false, true })
                        for (int settings = 0; settings < 8; settings++)
                            foreach (string device in new[] { "", mouse, pad })
                                yield return new MappingSource
                                {
                                    Descriptor = d, Kind = k, ParamAxisRelative = relative, DeviceGuid = device,
                                    ParamTravelSmoothingMs = (settings & 1) != 0 ? 5 : 0,
                                    ParamMouseSpeedSensitivity = (settings & 2) != 0 ? 2.0 : MappingSource.DefaultMouseSpeedSensitivity,
                                    ParamMouseBackToCenter = (settings & 4) != 0 ? 1.0 : 0,
                                    ParamTravelSteer = settings == 3 ? MappingSource.TravelSteerToggle
                                        : settings == 5 ? MappingSource.TravelSteerHeldStay : MappingSource.TravelSteerAlways,
                                    GateDescriptor = settings >= 6 ? "Button 1" : "",
                                    ParamMousePositionAnchor = settings == 7,
                                };
        }

        [Fact]
        public void TheChecksAnswerAsTheyDid_ForEverySourceOfTheGrid()
        {
            var provider = SourceCoercion.IsMouseDeviceProvider;
            try
            {
                int compared = 0;
                foreach (var withProvider in new[] { false, true })
                {
                    string mouseGuid = null;
                    SourceCoercion.IsMouseDeviceProvider = withProvider ? g => g == mouseGuid : null;
                    foreach (var src in Sources())
                    {
                        mouseGuid ??= src.DeviceGuid.Length > 0 ? src.DeviceGuid : null;
                        foreach (string evaluated in new[] { mouseGuid ?? "x", "y" })
                        {
                            Assert.Equal(ReferenceIsMouseTravel(src), SourceEvaluator.IsMouseTravel(src));
                            var expected = ReferenceMouseSteerOf(src, evaluated, out bool expectedY);
                            var actual = SourceEvaluator.MouseSteerOf(src, evaluated, out bool actualY);
                            Assert.Equal(expected, actual);
                            Assert.Equal(expectedY, actualY);
                            Assert.Equal(ReferenceIsMouseTravel(src) || expected != SourceEvaluator.MouseSteerFamily.None,
                                SourceEvaluator.ReadsOwnGate(src, evaluated));
                            compared++;
                        }
                    }
                }
                Assert.False(SourceEvaluator.IsMouseTravel(null));
                Assert.Equal(SourceEvaluator.MouseSteerFamily.None, SourceEvaluator.MouseSteerOf(null, "x", out _));
                Assert.True(compared > 50_000, $"compared {compared}");
            }
            finally { SourceCoercion.IsMouseDeviceProvider = provider; }
        }

        /// <summary>The grid holds sources each answer of each check comes
        /// from, so a fast reject that rejected too much would fail.</summary>
        [Fact]
        public void TheGridReachesEveryAnswer()
        {
            var provider = SourceCoercion.IsMouseDeviceProvider;
            try
            {
                SourceCoercion.IsMouseDeviceProvider = _ => true;
                bool travel = false, relative = false, speed = false, position = false;
                foreach (var src in Sources())
                {
                    if (ReferenceIsMouseTravel(src)) { travel = true; relative |= src.ParamAxisRelative && !SourceCoercion.IsMouseTravelDescriptor(src.Descriptor); }
                    var family = ReferenceMouseSteerOf(src, "x", out _);
                    speed |= family == SourceEvaluator.MouseSteerFamily.Speed;
                    position |= family == SourceEvaluator.MouseSteerFamily.Position;
                }
                Assert.True(travel && relative && speed && position);
            }
            finally { SourceCoercion.IsMouseDeviceProvider = provider; }
        }
    }
}
