using System;
using System.Threading;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Models3D;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Which preview an Extended slot draws.
    ///
    /// <para>Every Extended slot used to get the generic schematic, without
    /// exception. That is right for most of the category, which is wheels,
    /// flight sticks and arcade panels, and drawing a gamepad under those
    /// would be a lie. It was wrong for Valve's pads: they live in Extended
    /// too, PadForge ships their bodies, and the schematic they got instead
    /// was empty, because those profiles declare no axes or buttons for it
    /// to size itself from.</para>
    ///
    /// <para>The rule is now "draw the body when we have the right one".
    /// <see cref="HMaestroProfileCatalog.HasDedicatedArt"/> is the question,
    /// and it has to distinguish real art from the fallback, since an
    /// unrecognized profile still resolves to a perfectly renderable Xbox
    /// 360 body.</para>
    /// </summary>
    public class ExtendedPreviewRoutingTests
    {
        [Theory]
        [InlineData("steam-deck")]
        [InlineData("steam-deck-composite")]
        [InlineData("steam-controller")]
        [InlineData("steam-controller-composite")]
        [InlineData("steam-controller-2")]
        public void ValveProfilesOnAnExtendedSlotDrawTheirOwnBody(string id)
        {
            Assert.True(HMaestroProfileCatalog.HasDedicatedArt(
                id, VirtualControllerType.Extended));
        }

        /// <summary>The rest of Extended keeps the schematic. A wheel drawn
        /// as an Xbox pad is worse than a generic diagram, which is the
        /// whole reason the fallback must not read as dedicated art.</summary>
        [Theory]
        [InlineData("padforge-custom")]
        [InlineData("logitech-g25")]
        [InlineData("")]
        [InlineData(null)]
        public void EverythingElseOnExtendedKeepsTheSchematic(string id)
        {
            Assert.False(HMaestroProfileCatalog.HasDedicatedArt(
                id, VirtualControllerType.Extended));
        }

        /// <summary>Reporting dedicated art must not change what the
        /// resolver returns. Both overloads answer with the same folders,
        /// so no caller can drift from the other.</summary>
        [Theory]
        [InlineData("steam-controller-2")]
        [InlineData("dualsense-composite")]
        [InlineData("padforge-custom")]
        public void TheTwoOverloadsAgree(string id)
        {
            var plain = HMaestroProfileCatalog.ResolveAssetFolders(
                id, VirtualControllerType.Extended);
            var withFlag = HMaestroProfileCatalog.ResolveAssetFolders(
                id, VirtualControllerType.Extended, out _);
            Assert.Equal(plain, withFlag);
        }

        /// <summary>Every body an Extended slot can now be routed to must
        /// actually construct. This path was unreachable before, so a mesh
        /// that throws would have surfaced as a dead preview pane rather
        /// than as a build error.</summary>
        [Fact]
        public void EveryValveModelConstructs()
        {
            string failures = null;
            var t = new Thread(() =>
            {
                foreach (var (name, make) in new (string, Func<ControllerModelBase>)[]
                {
                    ("SteamDeck", () => new ControllerModelSteamDeck()),
                    ("SteamController", () => new ControllerModelSteamController()),
                    ("SteamController2", () => new ControllerModelSteamController2()),
                })
                {
                    try
                    {
                        using var m = make();
                        if (m.model3DGroup.Children.Count == 0)
                            failures += $"{name}: built with no geometry\n";
                        if (m.ButtonMap.Count == 0)
                            failures += $"{name}: built with no mappable targets\n";
                    }
                    catch (Exception e)
                    {
                        failures += $"{name}: {e.GetType().Name}: {e.Message}\n";
                    }
                }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            t.Join();
            Assert.True(failures == null, failures);
        }
    }
}
