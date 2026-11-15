using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Xml.Serialization;
using PadForge.Common.Input;
using PadForge.Controls;
using PadForge.Engine.Data;
using PadForge.Services;
using PadForge.ViewModels;
using PadForge.Views;
using Xunit;
using Strings = PadForge.Resources.Strings.Strings;
using Step = PadForge.Services.LayerFlyoutDirector.Step;

namespace PadForge.Tests
{
    /// <summary>
    /// How long the Shift Layer Flyout shows a layer (#581): the rules of
    /// <see cref="LayerFlyoutDirector"/>, read with no window and no engine.
    /// A test names the layer each slot is on and reads the step the
    /// director answers with.
    /// </summary>
    public class LayerFlyoutDirectorTests
    {
        private const string Base = LayerFlyoutDirector.Base;

        /// <summary>Three slots, the layer each is on, and the director.</summary>
        private sealed class Stage
        {
            public readonly MappingSet[] Sets = { new MappingSet(), new MappingSet(), new MappingSet() };
            public readonly string[] On = { Base, Base, Base };
            public readonly LayerFlyoutDirector Director = new LayerFlyoutDirector();
            public int Viewed = -1;

            public ShiftActivator Layer(int slot, string mask, string flyout = "")
            {
                var activator = new ShiftActivator { LayerMask = mask, LayerName = mask, Flyout = flyout };
                Sets[slot].ShiftActivators.Add(activator);
                return activator;
            }

            public LayerFlyoutDirector.Decision Tick() => Director.Tick(Sets, (slot, _) => On[slot], Viewed);

            /// <summary>The step of one tick as text: the step, and for a
            /// show or a keep-alive the slot and the layer.</summary>
            public string Step()
            {
                var decision = Tick();
                return decision.Step is LayerFlyoutDirector.Step.None or LayerFlyoutDirector.Step.Hide
                    ? decision.Step.ToString()
                    : $"{decision.Step} {decision.Slot} {decision.Mask}";
            }
        }

        /// <summary>The default: the layer shows when it turns on and is
        /// kept up on every tick while it is on. Base shows once when the
        /// layer ends, and the window's timer takes it away.</summary>
        [Fact]
        public void ALayerOnTheDefault_StaysWhileItIsOn_ThenBaseShowsOnce()
        {
            var stage = new Stage();
            stage.Layer(0, "Aim");
            Assert.Equal("None", stage.Step());

            stage.On[0] = "Aim";
            Assert.Equal("Show 0 Aim", stage.Step());
            Assert.Equal("KeepAlive 0 Aim", stage.Step());
            Assert.Equal("KeepAlive 0 Aim", stage.Step());

            stage.On[0] = Base;
            Assert.Equal("Show 0 Base", stage.Step());
            Assert.Equal("None", stage.Step());
            Assert.Equal("None", stage.Step());
        }

        /// <summary>For 2 Seconds: the layer shows once and nothing keeps it
        /// up, so the window's timer takes it away with the layer still on.
        /// Base still shows when the layer ends.</summary>
        [Fact]
        public void ALayerSetToTwoSeconds_ShowsOnce_AndIsNotKeptUp()
        {
            var stage = new Stage();
            stage.Layer(0, "Sprint", LayerFlyout.Brief);

            stage.On[0] = "Sprint";
            Assert.Equal("Show 0 Sprint", stage.Step());
            Assert.Equal("None", stage.Step());
            Assert.Equal("None", stage.Step());

            stage.On[0] = Base;
            Assert.Equal("Show 0 Base", stage.Step());
            Assert.Equal("None", stage.Step());
        }

        /// <summary>Never: the layer shows nothing when it turns on, and
        /// Base shows nothing when it ends.</summary>
        [Fact]
        public void ALayerSetToNever_IsSilentWhenItTurnsOnAndWhenItEnds()
        {
            var stage = new Stage();
            stage.Layer(0, "Quiet", LayerFlyout.Off);

            stage.On[0] = "Quiet";
            Assert.Equal("None", stage.Step());
            Assert.Equal("None", stage.Step());
            stage.On[0] = Base;
            Assert.Equal("None", stage.Step());
        }

        /// <summary>Base set to Never: a layer that stayed takes its flyout
        /// down at once when it ends, since nothing follows it. A layer that
        /// showed briefly does the same, which does nothing to a flyout the
        /// timer already took.</summary>
        [Fact]
        public void BaseSetToNever_TakesTheEndedLayersFlyoutDown()
        {
            var stage = new Stage();
            stage.Sets[0].BaseFlyout = LayerFlyout.Off;
            stage.Layer(0, "Aim");
            stage.Layer(0, "Sprint", LayerFlyout.Brief);

            stage.On[0] = "Aim";
            Assert.Equal("Show 0 Aim", stage.Step());
            stage.On[0] = Base;
            Assert.Equal("Hide", stage.Step());
            Assert.Equal("None", stage.Step());

            stage.On[0] = "Sprint";
            Assert.Equal("Show 0 Sprint", stage.Step());
            stage.On[0] = Base;
            Assert.Equal("Hide", stage.Step());
            Assert.Equal("None", stage.Step());
        }

        /// <summary>A layer that stays owns the flyout: the viewed slot's
        /// first, else the lowest slot's. When it ends while another such
        /// layer is on, that one shows, and Base does not.</summary>
        [Fact]
        public void ALayerThatStays_OwnsTheFlyout_TheViewedSlotFirstElseTheLowest()
        {
            var stage = new Stage();
            stage.Layer(0, "Aim");
            stage.Layer(2, "Drive");

            stage.On[0] = "Aim";
            stage.On[2] = "Drive";
            Assert.Equal("Show 0 Aim", stage.Step());
            Assert.Equal("KeepAlive 0 Aim", stage.Step());

            stage.Viewed = 2;
            Assert.Equal("Show 2 Drive", stage.Step());
            Assert.Equal("KeepAlive 2 Drive", stage.Step());

            stage.On[2] = Base;
            Assert.Equal("Show 0 Aim", stage.Step());
            stage.On[0] = Base;
            Assert.Equal("Show 0 Base", stage.Step());
        }

        /// <summary>While a layer that stays is on, a brief layer on another
        /// slot and that slot's return to Base do not take the flyout.</summary>
        [Fact]
        public void WhileALayerStays_NothingElseShows()
        {
            var stage = new Stage();
            stage.Layer(0, "Aim");
            stage.Layer(1, "Sprint", LayerFlyout.Brief);

            stage.On[0] = "Aim";
            Assert.Equal("Show 0 Aim", stage.Step());
            stage.On[1] = "Sprint";
            Assert.Equal("KeepAlive 0 Aim", stage.Step());
            stage.On[1] = Base;
            Assert.Equal("KeepAlive 0 Aim", stage.Step());
        }

        /// <summary>A brief layer that is still on holds nothing back: its
        /// flyout is gone after its two seconds, so another slot's layer
        /// shows when it turns on.</summary>
        [Fact]
        public void ABriefLayerThatIsStillOn_DoesNotHoldBackAnotherSlot()
        {
            var stage = new Stage();
            stage.Layer(0, "Sprint", LayerFlyout.Brief);
            stage.Layer(1, "Menu", LayerFlyout.Brief);
            stage.Layer(1, "Drive");

            stage.On[0] = "Sprint";
            Assert.Equal("Show 0 Sprint", stage.Step());
            Assert.Equal("None", stage.Step());

            stage.On[1] = "Menu";
            Assert.Equal("Show 1 Menu", stage.Step());
            Assert.Equal("None", stage.Step());

            stage.On[1] = "Drive";
            Assert.Equal("Show 1 Drive", stage.Step());
            Assert.Equal("KeepAlive 1 Drive", stage.Step());
        }

        /// <summary>The flyout never shows a layer its slot has left. A
        /// layer that gives way to a silent one takes its flyout down at
        /// once, whether it stayed or showed briefly, and so does Base's
        /// flyout when a silent layer turns on under it.</summary>
        [Fact]
        public void TheFlyoutNeverShowsALayerItsSlotHasLeft()
        {
            var stage = new Stage();
            stage.Layer(0, "Aim");
            stage.Layer(0, "Sprint", LayerFlyout.Brief);
            stage.Layer(0, "Quiet", LayerFlyout.Off);

            stage.On[0] = "Aim";
            Assert.Equal("Show 0 Aim", stage.Step());
            stage.On[0] = "Quiet";
            Assert.Equal("Hide", stage.Step());
            Assert.Equal("None", stage.Step());

            stage.On[0] = "Sprint";
            Assert.Equal("Show 0 Sprint", stage.Step());
            stage.On[0] = "Quiet";
            Assert.Equal("Hide", stage.Step());

            // Quiet ends in silence. Sprint ends with Base's flyout, and
            // Quiet turning on under that takes it down.
            stage.On[0] = Base;
            Assert.Equal("None", stage.Step());
            stage.On[0] = "Sprint";
            Assert.Equal("Show 0 Sprint", stage.Step());
            stage.On[0] = Base;
            Assert.Equal("Show 0 Base", stage.Step());
            stage.On[0] = "Quiet";
            Assert.Equal("Hide", stage.Step());
        }

        /// <summary>The choice is read on every tick, so one changed in the
        /// Shift Layer window applies to a layer that is on.</summary>
        [Fact]
        public void AChoiceChangedWhileTheLayerIsOn_Applies()
        {
            var stage = new Stage();
            var aim = stage.Layer(0, "Aim");

            stage.On[0] = "Aim";
            Assert.Equal("Show 0 Aim", stage.Step());
            Assert.Equal("KeepAlive 0 Aim", stage.Step());

            aim.Flyout = LayerFlyout.Brief;
            Assert.Equal("Hide", stage.Step());
            Assert.Equal("None", stage.Step());

            aim.Flyout = "";
            Assert.Equal("Show 0 Aim", stage.Step());
            Assert.Equal("KeepAlive 0 Aim", stage.Step());

            aim.Flyout = LayerFlyout.Off;
            Assert.Equal("Hide", stage.Step());
            Assert.Equal("None", stage.Step());
        }

        /// <summary>Turning the flyout off forgets what was shown and says
        /// whether a flyout may still be up. Turned on again with a layer
        /// on, the layer shows as one that just turned on.</summary>
        [Fact]
        public void Reset_ForgetsAndSaysWhetherAFlyoutMayBeUp()
        {
            var stage = new Stage();
            stage.Layer(0, "Aim");
            Assert.False(stage.Director.Reset());

            stage.On[0] = "Aim";
            Assert.Equal("Show 0 Aim", stage.Step());
            Assert.True(stage.Director.Reset());
            Assert.False(stage.Director.Reset());

            Assert.Equal("Show 0 Aim", stage.Step());
            Assert.Equal("KeepAlive 0 Aim", stage.Step());
        }

        /// <summary>When two slots change in one tick, the slot the flyout
        /// was showing comes first, then the viewed slot, then the lowest.</summary>
        [Fact]
        public void WhenSlotsChangeInOneTick_TheShownSlotThenTheViewedThenTheLowest()
        {
            var lowest = new Stage();
            lowest.Layer(0, "A", LayerFlyout.Brief);
            lowest.Layer(1, "B", LayerFlyout.Brief);
            lowest.On[0] = "A";
            lowest.On[1] = "B";
            Assert.Equal("Show 0 A", lowest.Step());

            var viewed = new Stage { Viewed = 1 };
            viewed.Layer(0, "A", LayerFlyout.Brief);
            viewed.Layer(1, "B", LayerFlyout.Brief);
            viewed.On[0] = "A";
            viewed.On[1] = "B";
            Assert.Equal("Show 1 B", viewed.Step());

            // Slot 0's layer was the one showing. Both end in one tick, with
            // slot 2 the page that is open: slot 0's Base shows.
            var shown = new Stage();
            shown.Layer(0, "A");
            shown.Layer(2, "C");
            shown.On[0] = "A";
            shown.On[2] = "C";
            Assert.Equal("Show 0 A", shown.Step());
            shown.Viewed = 2;
            shown.On[0] = Base;
            shown.On[2] = Base;
            Assert.Equal("Show 0 Base", shown.Step());
        }

        /// <summary>A layer no activator names, as a delete can leave one
        /// on, reads as the default and shows like any other.</summary>
        [Fact]
        public void ALayerNoActivatorNames_ReadsAsTheDefault()
        {
            var stage = new Stage();
            stage.On[1] = "Ghost";
            Assert.Equal("Show 1 Ghost", stage.Step());
            Assert.Equal("KeepAlive 1 Ghost", stage.Step());
            stage.On[1] = Base;
            Assert.Equal("Show 1 Base", stage.Step());
        }

        /// <summary>A slot with no mapping set is on Base and reads as the
        /// defaults, and a change in the number of slots starts over.</summary>
        [Fact]
        public void AnEmptySlot_AndAChangedSlotCount_AreTakenInStride()
        {
            var director = new LayerFlyoutDirector();
            var sets = new MappingSet[] { null, new MappingSet() };
            string[] on = { Base, "Aim" };
            var first = director.Tick(sets, (slot, _) => on[slot], -1);
            Assert.Equal(Step.Show, first.Step);
            Assert.Equal(1, first.Slot);

            var fewer = new MappingSet[] { null };
            Assert.Equal(Step.None, director.Tick(fewer, (_, _) => null, -1).Step);
        }

        /// <summary>The steps the flyout's code took before the choice
        /// existed (InputService.UpdateShiftLayerFlyout as of 3b6666964): one
        /// slot and one layer tracked, the viewed slot first and else the
        /// lowest, a keep-alive while that layer is on, and Base shown once
        /// when every slot is back on it.</summary>
        private sealed class OldFlyout
        {
            private int _lastSlot = -1;
            private string _lastShown = Base;

            public string Step(string[] on, int viewed)
            {
                int slot = -1;
                string mask = Base;
                if (viewed >= 0 && viewed < on.Length && on[viewed] != Base)
                {
                    slot = viewed;
                    mask = on[viewed];
                }
                for (int s = 0; slot < 0 && s < on.Length; s++)
                {
                    if (s == viewed || on[s] == Base) continue;
                    slot = s;
                    mask = on[s];
                }

                int prevSlot = _lastSlot;
                string prevShown = _lastShown;
                if (slot == prevSlot && mask == prevShown)
                    return slot >= 0 ? $"KeepAlive {slot} {mask}" : "None";
                if (slot >= 0)
                {
                    _lastSlot = slot;
                    _lastShown = mask;
                    return $"Show {slot} {mask}";
                }
                _lastSlot = -1;
                _lastShown = Base;
                return prevSlot < 0 || prevShown == Base ? "None" : $"Show {prevSlot} Base";
            }
        }

        /// <summary>With every layer on its default the director takes the
        /// steps the old code took, tick for tick: over three slots whose
        /// layers and whose viewed slot change at random, two or three at a
        /// time as well.</summary>
        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(4)]
        [InlineData(5)]
        public void WithEveryLayerOnItsDefault_TheStepsAreTheOldOnes(int seed)
        {
            var random = new Random(seed);
            var stage = new Stage();
            foreach (int slot in new[] { 0, 1, 2 })
            {
                stage.Layer(slot, "A");
                stage.Layer(slot, "B");
            }
            var old = new OldFlyout();
            string[] layers = { Base, "A", "B" };
            int shows = 0, keeps = 0;

            for (int tick = 0; tick < 4000; tick++)
            {
                // Most ticks change nothing, as most of a real second does.
                if (random.Next(4) == 0)
                {
                    int changes = 1 + random.Next(3);
                    for (int i = 0; i < changes; i++)
                        stage.On[random.Next(3)] = layers[random.Next(3)];
                }
                if (random.Next(40) == 0) stage.Viewed = random.Next(4) - 1;

                string was = old.Step(stage.On, stage.Viewed);
                string now = stage.Step();
                Assert.True(was == now,
                    $"seed {seed} tick {tick}: the old code said '{was}' and the director says '{now}' "
                    + $"with slots on {string.Join(", ", stage.On)} and slot {stage.Viewed} viewed");
                if (now.StartsWith("Show", StringComparison.Ordinal)) shows++;
                if (now.StartsWith("KeepAlive", StringComparison.Ordinal)) keeps++;
            }

            // The run did show layers and keep them up, so the match is not
            // one of nothing against nothing.
            Assert.True(shows > 100, $"only {shows} shows");
            Assert.True(keeps > 100, $"only {keeps} keep-alives");
        }
    }

    /// <summary>
    /// How a layer's Flyout choice (#581) is stored and where it travels:
    /// its two XML attributes, the names a stored value reads as, the copy
    /// of a layer, the content gate, Copy and Paste, Copy From, and an edit
    /// from the Shift Layer window.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public sealed class LayerFlyoutStorageTests : IDisposable
    {
        private readonly MappingSet[] _saved = SettingsManager.SlotMappingSets;

        public LayerFlyoutStorageTests() => SettingsManager.SlotMappingSets = new MappingSet[InputManager.MaxPads];

        public void Dispose() => SettingsManager.SlotMappingSets = _saved;

        /// <summary>A stored value that is empty, or one this build does not
        /// know, reads as the default: a shift layer stays and Base shows
        /// briefly. Base has no choice that stays. The default is stored as
        /// nothing, so a layer nobody set writes what it always wrote.</summary>
        [Fact]
        public void AStoredValue_ReadsAsOneOfTheChoices_AndTheDefaultIsStoredAsNothing()
        {
            foreach (string unset in new[] { null, "", "Stay", "stay", "Sometimes" })
            {
                Assert.Equal(LayerFlyout.Stay, LayerFlyout.OfLayer(unset));
                Assert.Equal(LayerFlyout.Brief, LayerFlyout.OfBase(unset));
            }
            Assert.Equal(LayerFlyout.Brief, LayerFlyout.OfLayer("Brief"));
            Assert.Equal(LayerFlyout.Off, LayerFlyout.OfLayer("Off"));
            Assert.Equal(LayerFlyout.Brief, LayerFlyout.OfBase("Brief"));
            Assert.Equal(LayerFlyout.Off, LayerFlyout.OfBase("Off"));

            Assert.Equal("", LayerFlyout.StoreLayer(LayerFlyout.Stay));
            Assert.Equal("", LayerFlyout.StoreLayer(null));
            Assert.Equal("Brief", LayerFlyout.StoreLayer(LayerFlyout.Brief));
            Assert.Equal("Off", LayerFlyout.StoreLayer(LayerFlyout.Off));
            Assert.Equal("", LayerFlyout.StoreBase(LayerFlyout.Brief));
            Assert.Equal("", LayerFlyout.StoreBase(LayerFlyout.Stay));
            Assert.Equal("Off", LayerFlyout.StoreBase(LayerFlyout.Off));
        }

        /// <summary>The first activator that names a layer speaks for it, as
        /// it does for the layer's name, color and icon. A set that is not
        /// there, and a layer nothing names, read as the defaults.</summary>
        [Fact]
        public void TheFirstActivatorThatNamesALayer_SpeaksForIt()
        {
            var set = new MappingSet { BaseFlyout = "Off" };
            set.ShiftActivators.Add(null);
            set.ShiftActivators.Add(new ShiftActivator { LayerMask = "Aim", Flyout = "Brief" });
            set.ShiftActivators.Add(new ShiftActivator { LayerMask = "Aim", Flyout = "Off" });
            set.ShiftActivators.Add(new ShiftActivator { LayerMask = "Drive" });

            Assert.Equal(LayerFlyout.Brief, LayerFlyout.OfLayer(set, "Aim"));
            Assert.Equal(LayerFlyout.Stay, LayerFlyout.OfLayer(set, "Drive"));
            Assert.Equal(LayerFlyout.Stay, LayerFlyout.OfLayer(set, "Gone"));
            Assert.Equal(LayerFlyout.Stay, LayerFlyout.OfLayer((MappingSet)null, "Aim"));
            Assert.Equal(LayerFlyout.Off, LayerFlyout.OfBase(set));
            Assert.Equal(LayerFlyout.Brief, LayerFlyout.OfBase((MappingSet)null));
            Assert.Equal(LayerFlyout.Brief, LayerFlyout.OfBase(new MappingSet()));
        }

        /// <summary>Both attributes round-trip through the settings file's
        /// XML, and a set written before the choice existed reads as the
        /// defaults.</summary>
        [Fact]
        public void BothAttributes_RoundTripThroughXml_AndAnOldFileReadsAsTheDefaults()
        {
            var set = new MappingSet { BaseFlyout = "Off" };
            set.ShiftActivators.Add(new ShiftActivator { LayerMask = "Aim", Flyout = "Brief" });
            var serializer = new XmlSerializer(typeof(MappingSet));
            using var written = new StringWriter();
            serializer.Serialize(written, set);
            string xml = written.ToString();
            Assert.Contains("BaseFlyout=\"Off\"", xml);
            Assert.Contains("Flyout=\"Brief\"", xml);

            var back = (MappingSet)serializer.Deserialize(new StringReader(xml));
            Assert.Equal("Off", back.BaseFlyout);
            Assert.Equal("Brief", Assert.Single(back.ShiftActivators).Flyout);

            const string old = "<MappingSet BaseLayerName=\"Driving\"><ShiftActivator LayerMask=\"Aim\" Mode=\"Toggle\" /></MappingSet>";
            var before = (MappingSet)serializer.Deserialize(new StringReader(old));
            Assert.Equal("", before.BaseFlyout);
            Assert.Equal("", Assert.Single(before.ShiftActivators).Flyout);
            Assert.Equal(LayerFlyout.Stay, LayerFlyout.OfLayer(before, "Aim"));
            Assert.Equal(LayerFlyout.Brief, LayerFlyout.OfBase(before));
        }

        /// <summary>A slot whose only authoring is silencing Base's flyout
        /// counts as authored, so cold load keeps it. A copy of a layer
        /// carries the layer's choice.</summary>
        [Fact]
        public void TheContentGateAndALayersCopy_CarryTheChoice()
        {
            var set = new MappingSet();
            Assert.False(set.HasAuthoredContent);
            set.BaseFlyout = "Off";
            Assert.True(set.HasAuthoredContent);

            var layer = new ShiftActivator { LayerMask = "Aim", Flyout = "Brief" };
            Assert.Equal("Brief", layer.Clone().Flyout);
        }

        /// <summary>Copy and Paste carry both: the snapshot holds Base's
        /// choice beside Base's name, color and icon, and each layer's
        /// choice rides its activator. A snapshot copied before the choice
        /// existed pastes the defaults.</summary>
        [Fact]
        public void CopyAndPaste_CarryBaseAndLayerChoices()
        {
            var source = new MappingSet { BaseLayerName = "Driving", BaseFlyout = "Off" };
            source.ShiftActivators.Add(new ShiftActivator { LayerMask = "Aim", Mode = "Passive", Flyout = "Brief" });
            SettingsManager.SlotMappingSets[0] = source;
            SettingsManager.SlotMappingSets[1] = new MappingSet { BaseFlyout = "", BaseLayerName = "Old" };

            string json = InputService.BuildShiftLayerSnapshotJson(0);
            Assert.Contains("\"BaseFlyout\":\"Off\"", json);
            InputService.ApplyShiftLayerSnapshotJson(1, json);

            var pasted = SettingsManager.SlotMappingSets[1];
            Assert.Equal("Driving", pasted.BaseLayerName);
            Assert.Equal("Off", pasted.BaseFlyout);
            Assert.Equal("Brief", Assert.Single(pasted.ShiftActivators).Flyout);

            const string old = "{\"Activators\":[{\"LayerMask\":\"Aim\",\"Mode\":\"Passive\"}],\"BaseLayerName\":\"Earlier\",\"BaseColor\":\"\",\"BaseIcon\":\"\"}";
            InputService.ApplyShiftLayerSnapshotJson(1, old);
            pasted = SettingsManager.SlotMappingSets[1];
            Assert.Equal("Earlier", pasted.BaseLayerName);
            Assert.Equal("", pasted.BaseFlyout);
            Assert.Equal("", Assert.Single(pasted.ShiftActivators).Flyout);
        }

        /// <summary>Copy From and a profile's deep copy carry both.</summary>
        [Fact]
        public void ADeepCopyOfASet_CarriesBaseAndLayerChoices()
        {
            var source = new MappingSet { BaseFlyout = "Off" };
            source.ShiftActivators.Add(new ShiftActivator { LayerMask = "Aim", Flyout = "Off" });

            var copy = InputService.CloneMappingSetDeep(source);
            Assert.Equal("Off", copy.BaseFlyout);
            Assert.Equal("Off", Assert.Single(copy.ShiftActivators).Flyout);
        }

        /// <summary>An edit from the Shift Layer window that changes only
        /// the Flyout choice reaches the layer and leaves the layer on: like
        /// the color and the icon, the choice is no reason to clear the
        /// slot's shift runtime.</summary>
        [Fact]
        public void AnEditOfTheChoiceAlone_ReachesTheLayer_AndLeavesItOn()
        {
            using var f = new MenuAuditFixture();
            f.State.Buttons[0] = true;
            f.Frame();
            Assert.Equal("L1", InputManager.GetEngagedLayerMask(0, f.Set));

            PadPage.ApplyShiftActivatorEdit(0, f.Activator, new ShiftActivator
            {
                LayerMask = f.Activator.LayerMask, LayerName = f.Activator.LayerName,
                Mode = f.Activator.Mode, Descriptor = f.Activator.Descriptor,
                DeviceGuid = f.Activator.DeviceGuid, Flyout = LayerFlyout.Brief,
            });

            Assert.Equal(LayerFlyout.Brief, f.Activator.Flyout);
            Assert.Equal("L1", InputManager.GetEngagedLayerMask(0, f.Set));
            f.Frame();
            Assert.Equal("L1", InputManager.GetEngagedLayerMask(0, f.Set));
        }
    }

    /// <summary>
    /// The Flyout row of the Shift Layer window (#581). Each dialog is made
    /// and read, and never shown, so nothing reaches the screen or the focus
    /// of whoever uses the machine.
    /// </summary>
    [Collection("WpfApplicationSingleton")]
    public class LayerFlyoutDialogTests
    {
        private static readonly InputChoice[] NoInputs = Array.Empty<InputChoice>();
        private static readonly ShiftActivator[] NoLayers = Array.Empty<ShiftActivator>();

        private static T Part<T>(Window dialog, string name) where T : class
        {
            var part = dialog.FindName(name) as T;
            Assert.True(part != null, name + " is not a " + typeof(T).Name + " of " + dialog.GetType().Name);
            return part;
        }

        private static string RepoText(params string[] parts)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "PadForge.App")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return File.ReadAllText(Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray()));
        }

        /// <summary>The names the dropdown shows, in order. A choice is read
        /// by its name, which is also what a screen reader says.</summary>
        private static string[] Names(ComboBox combo) => combo.Items.Cast<object>().Select(item => item.ToString()).ToArray();

        private static ShiftActivator Aim(string flyout = "") => new ShiftActivator { LayerName = "Aim", LayerMask = "Aim", Flyout = flyout };

        /// <summary>The row shows only while the Shift Layer Flyout is on,
        /// which the caller says. With it on, More's hint line names the
        /// flyout among what More holds.</summary>
        [Fact]
        public void TheRowShowsOnlyWhileTheFlyoutIsOn()
        {
            DialogTestHost.Run(() =>
            {
                var s = Strings.Instance;
                var without = new ShiftActivatorDialog(NoInputs, Aim(), NoLayers);
                Assert.Equal(Visibility.Collapsed, Part<UIElement>(without, "FlyoutRow").Visibility);
                Assert.Equal(s.Pad_Shift_MoreHint, Part<MoreRow>(without, "ShiftMore").Hint);
                without.Close();

                var with = new ShiftActivatorDialog(NoInputs, Aim(), NoLayers, flyoutEnabled: true);
                Assert.Equal(Visibility.Visible, Part<UIElement>(with, "FlyoutRow").Visibility);
                Assert.Equal(s.Pad_Shift_MoreHint_Flyout, Part<MoreRow>(with, "ShiftMore").Hint);
                Assert.Equal(s.Pad_Shift_Flyout, Part<TextBlock>(with, "FlyoutLabel").Text);
                // The row is one of More's, ahead of the layer's color.
                var more = Part<Panel>(with, "MorePanel");
                var row = Part<UIElement>(with, "FlyoutRow");
                Assert.Same(more, ((FrameworkElement)row).Parent);
                Assert.Equal(more.Children.IndexOf(Part<UIElement>(with, "ColorBlock")) - 1, more.Children.IndexOf(row));
                with.Close();

                var baseWithout = new ShiftActivatorDialog("Base", "", "");
                Assert.Equal(Visibility.Collapsed, Part<UIElement>(baseWithout, "FlyoutRow").Visibility);
                Assert.Equal(s.Pad_Shift_BaseSubtitle, baseWithout.Subtitle);
                baseWithout.Close();
            });
        }

        /// <summary>A shift layer has three choices and starts on While the
        /// Layer Is On. The dropdown shows the layer's stored choice, and one
        /// tooltip says what each choice does.</summary>
        [Fact]
        public void AShiftLayer_HasThreeChoices_AndStartsOnWhileTheLayerIsOn()
        {
            DialogTestHost.Run(() =>
            {
                var s = Strings.Instance;
                var fresh = new ShiftActivatorDialog(NoInputs, null, NoLayers, flyoutEnabled: true);
                var combo = Part<ComboBox>(fresh, "FlyoutCombo");
                Assert.Equal(new[] { s.Pad_Shift_Flyout_Stay, s.Pad_Shift_Flyout_Brief, s.Pad_Shift_Flyout_Off }, Names(combo));
                Assert.Equal(LayerFlyout.Stay, combo.SelectedValue);
                Assert.Equal(s.Pad_Shift_Flyout_Tooltip, combo.ToolTip);
                Assert.Equal(s.Pad_Shift_Flyout_Tooltip, Part<TextBlock>(fresh, "FlyoutLabel").ToolTip);
                Assert.Equal(s.Pad_Shift_Flyout, AutomationProperties.GetName(combo));
                Assert.Equal("", fresh.FlyoutToStore());
                fresh.Close();

                foreach (string stored in new[] { LayerFlyout.Brief, LayerFlyout.Off })
                {
                    var edit = new ShiftActivatorDialog(NoInputs, Aim(stored), NoLayers, flyoutEnabled: true);
                    Assert.Equal(stored, Part<ComboBox>(edit, "FlyoutCombo").SelectedValue);
                    Assert.Equal(stored, edit.FlyoutToStore());
                    edit.Close();
                }
            });
        }

        /// <summary>Base has two choices and starts on For 2 Seconds: it is
        /// on whenever no shift layer is, so it has no time to stay for. Its
        /// row stands under the name and ahead of the color, and the line
        /// under the title names the flyout's time.</summary>
        [Fact]
        public void Base_HasTwoChoices_AndItsRowStandsUnderTheName()
        {
            DialogTestHost.Run(() =>
            {
                var s = Strings.Instance;
                var dialog = new ShiftActivatorDialog("Base", "#7CC7EA", "", "", flyoutEnabled: true);
                var combo = Part<ComboBox>(dialog, "FlyoutCombo");
                Assert.Equal(new[] { s.Pad_Shift_Flyout_Brief, s.Pad_Shift_Flyout_Off }, Names(combo));
                Assert.Equal(LayerFlyout.Brief, combo.SelectedValue);
                Assert.Equal(s.Pad_Shift_Flyout_Base_Tooltip, combo.ToolTip);
                Assert.Equal(s.Pad_Shift_BaseSubtitle_Flyout, dialog.Subtitle);
                Assert.Equal("", dialog.FlyoutToStore());

                var body = Part<Panel>(dialog, "BodyPanel");
                var row = Part<UIElement>(dialog, "FlyoutRow");
                Assert.Equal(Visibility.Visible, row.Visibility);
                Assert.Same(body, ((FrameworkElement)row).Parent);
                Assert.Equal(body.Children.IndexOf(Part<UIElement>(dialog, "ColorBlock")) - 1, body.Children.IndexOf(row));
                Assert.Equal(Visibility.Collapsed, Part<UIElement>(dialog, "ShiftMore").Visibility);

                combo.SelectedValue = LayerFlyout.Off;
                Assert.Equal(LayerFlyout.Off, dialog.FlyoutToStore());
                dialog.Close();

                var silent = new ShiftActivatorDialog("Base", "", "", LayerFlyout.Off, flyoutEnabled: true);
                Assert.Equal(LayerFlyout.Off, Part<ComboBox>(silent, "FlyoutCombo").SelectedValue);
                silent.Close();
            });
        }

        /// <summary>A window whose row is hidden never showed the choice, so
        /// a save hands back what the layer had, a value this build does not
        /// know included.</summary>
        [Fact]
        public void AHiddenRow_KeepsTheLayersStoredChoice()
        {
            DialogTestHost.Run(() =>
            {
                foreach (string stored in new[] { "", LayerFlyout.Brief, LayerFlyout.Off, "Later" })
                {
                    var layer = new ShiftActivatorDialog(NoInputs, Aim(stored), NoLayers);
                    Assert.Equal(stored, layer.FlyoutToStore());
                    layer.Close();

                    var baseLayer = new ShiftActivatorDialog("Base", "", "", stored);
                    Assert.Equal(stored, baseLayer.FlyoutToStore());
                    baseLayer.Close();
                }
            });
        }

        /// <summary>The row's reset goes to the layer's default: While the
        /// Layer Is On for a shift layer, For 2 Seconds for Base.</summary>
        [Fact]
        public void TheRowsReset_GoesToTheLayersDefault()
        {
            DialogTestHost.Run(() =>
            {
                var layer = new ShiftActivatorDialog(NoInputs, Aim(LayerFlyout.Off), NoLayers, flyoutEnabled: true);
                var reset = Part<Panel>(layer, "FlyoutRow").Children.OfType<SettingResetButton>().Single();
                Assert.Equal("Reset_?dialog_FlyoutCombo_581", AutomationProperties.GetAutomationId(reset));
                reset.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Assert.Equal(LayerFlyout.Stay, Part<ComboBox>(layer, "FlyoutCombo").SelectedValue);
                Assert.Equal("", layer.FlyoutToStore());
                layer.Close();

                var baseLayer = new ShiftActivatorDialog("Base", "", "", LayerFlyout.Off, flyoutEnabled: true);
                Part<Panel>(baseLayer, "FlyoutRow").Children.OfType<SettingResetButton>().Single()
                    .RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Assert.Equal(LayerFlyout.Brief, Part<ComboBox>(baseLayer, "FlyoutCombo").SelectedValue);
                Assert.Equal("", baseLayer.FlyoutToStore());
                baseLayer.Close();
            });
        }

        /// <summary>More opens by itself for a layer whose choice is not the
        /// default, while the row shows. With the flyout off the row is not
        /// there, so the choice opens nothing.</summary>
        [Fact]
        public void MoreOpensForALayerWhoseChoiceIsNotTheDefault()
        {
            Assert.False(ShiftActivatorDialog.UsesMore(Aim(), flyoutShown: true));
            Assert.False(ShiftActivatorDialog.UsesMore(Aim("Stay"), flyoutShown: true));
            Assert.True(ShiftActivatorDialog.UsesMore(Aim(LayerFlyout.Brief), flyoutShown: true));
            Assert.True(ShiftActivatorDialog.UsesMore(Aim(LayerFlyout.Off), flyoutShown: true));
            Assert.False(ShiftActivatorDialog.UsesMore(Aim(LayerFlyout.Off)));
            Assert.False(ShiftActivatorDialog.UsesMore(null, flyoutShown: true));

            DialogTestHost.Run(() =>
            {
                var silent = new ShiftActivatorDialog(NoInputs, Aim(LayerFlyout.Off), NoLayers, flyoutEnabled: true);
                Assert.True(Part<Expander>(silent, "ShiftMore").IsExpanded);
                silent.Close();

                var hidden = new ShiftActivatorDialog(NoInputs, Aim(LayerFlyout.Off), NoLayers);
                Assert.False(Part<Expander>(hidden, "ShiftMore").IsExpanded);
                hidden.Close();
            });
        }

        /// <summary>A save puts the choice in the result, for a shift layer
        /// and for Base, and the controller page hands every Shift Layer
        /// window whether the flyout is on, takes Base's choice back into the
        /// mapping set, and carries a layer's into the layer it edits. Read
        /// where they live, since a dialog's result can be set only on a
        /// shown dialog.</summary>
        [Fact]
        public void TheSaveAndTheControllerPage_CarryTheChoice()
        {
            string dialog = RepoText("PadForge.App", "Views", "ShiftActivatorDialog.xaml.cs");
            Assert.Equal(2, dialog.Split("Flyout = FlyoutToStore(),").Length - 1);

            string page = RepoText("PadForge.App", "Views", "PadPage.xaml.cs");
            Assert.Equal(3, page.Split("flyoutEnabled: LayerFlyoutIsOn())").Length - 1);
            Assert.Equal(3, page.Split("new ShiftActivatorDialog(").Length - 1);
            Assert.Contains("baseMs.BaseFlyout ?? \"\", flyoutEnabled: LayerFlyoutIsOn())", page);
            Assert.Contains("baseMs.BaseFlyout = bdlg.Result.Flyout ?? \"\";", page);
            Assert.Contains("existing.Flyout = replacement.Flyout;", page);
            Assert.Contains("mainVm.Dashboard.EnableShiftLayerFlyout;", page);
        }
    }
}
