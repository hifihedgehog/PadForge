using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Common.Mapping;
using PadForge.Engine.Data;
using PadForge.SteamWorkshop.Translation;
using PadForge.Services;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>A Steam import's gyro activation at run time: the engaged
    /// layer's entry of WorkshopGyroActivation, else Base's, any or all of
    /// its buttons under Hold, Release or Toggle, ANDed into the slot's
    /// engage state, the re-center each time the gyro turns on, and the
    /// "Gyro Engaged" read the import gates its tilt rows on.</summary>
    [Collection("SettingsManagerStatics")]
    public sealed class GyroActivationLaneTests : IDisposable
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

        private readonly SettingsCollection _settings = SettingsManager.UserSettings;
        private readonly bool[] _created = SettingsManager.SlotCreated;
        private readonly MappingSet[] _sets = SettingsManager.SlotMappingSets;
        private readonly Func<string, string, int, bool> _held = SourceCoercion.ButtonHeldProvider;
        private readonly Func<int, bool> _engage = SourceCoercion.AimEngageStateProvider;
        private readonly Action<int> _recenterApply = InputManager.GyroRecenterApply;
        private readonly InputManager _im = new();
        private readonly HashSet<string> _down = new(StringComparer.Ordinal);
        private int _recenters;

        public GyroActivationLaneTests()
        {
            var device = Guid.NewGuid();
            SettingsManager.UserSettings = new SettingsCollection();
            SettingsManager.UserSettings.Items.Add(new UserSetting
            {
                InstanceGuid = device, ProductGuid = device, MapTo = 0, IsEnabled = true,
            });
            SettingsManager.SlotCreated = new bool[InputManager.MaxPads];
            SettingsManager.SlotCreated[0] = true;
            SettingsManager.SlotMappingSets = new MappingSet[InputManager.MaxPads];
            SourceCoercion.ButtonHeldProvider = (guid, descriptor, slot) => _down.Contains(descriptor);
            InputManager.GyroRecenterApply = slot => _recenters++;
            InputManager.ClearShiftRuntime(0);
        }

        public void Dispose()
        {
            SettingsManager.UserSettings = _settings;
            SettingsManager.SlotCreated = _created;
            SettingsManager.SlotMappingSets = _sets;
            SourceCoercion.ButtonHeldProvider = _held;
            SourceCoercion.AimEngageStateProvider = _engage;
            InputManager.GyroRecenterApply = _recenterApply;
            InputManager.ClearShiftRuntime(0);
        }

        /// <summary>Stamps slot 0 with <paramref name="activation"/> on a set
        /// that declares Layer_1 to Layer_3, and forces the engage config
        /// to re-read it on the next settle.</summary>
        private void Stamp(string activation, bool recenter = false)
        {
            var set = new MappingSet
            {
                Authoritative = true,
                WorkshopGyroActivation = activation,
                WorkshopGyroRecenterOnEngage = recenter,
            };
            foreach (var mask in new[] { "Layer_1", "Layer_2", "Layer_3" })
                set.ShiftActivators.Add(new ShiftActivator
                {
                    LayerMask = mask, LayerName = mask,
                    DeviceGuid = Guid.NewGuid().ToString(), Descriptor = "PadA",
                });
            SettingsManager.SlotMappingSets[0] = set;
            typeof(InputManager).GetField("_gyroEngageCfgRefreshTick", Private)!.SetValue(_im, 0L);
        }

        private bool Settle()
        {
            typeof(InputManager).GetMethod("UpdateGyroEngageStates", Private)!.Invoke(_im, null);
            return _im.GyroActivationOn[0];
        }

        [Fact]
        public void Hold_Any_IsOnWhileAnyButtonIsHeld()
        {
            Stamp("Base=Hold,Any,Gamepad ButtonA|Gamepad ButtonB");
            Assert.False(Settle());
            _down.Add("Gamepad ButtonB");
            Assert.True(Settle());
            _down.Clear();
            Assert.False(Settle());
        }

        [Fact]
        public void Hold_All_NeedsEveryButton()
        {
            Stamp("Base=Hold,All,Gamepad ButtonA|Gamepad ButtonB");
            _down.Add("Gamepad ButtonA");
            Assert.False(Settle());
            _down.Add("Gamepad ButtonB");
            Assert.True(Settle());
        }

        [Fact]
        public void Release_IsOnUnlessHeld()
        {
            Stamp("Base=Release,Any,Gamepad ButtonA");
            Assert.True(Settle());
            _down.Add("Gamepad ButtonA");
            Assert.False(Settle());
        }

        [Fact]
        public void Toggle_FlipsOnEachPress()
        {
            Stamp("Base=Toggle,Any,Gamepad ButtonA");
            Assert.False(Settle());
            _down.Add("Gamepad ButtonA");
            Assert.True(Settle());
            Assert.True(Settle());
            _down.Clear();
            Assert.True(Settle());
            _down.Add("Gamepad ButtonA");
            Assert.False(Settle());
        }

        [Fact]
        public void TheEngagedLayersEntryApplies_ElseBases()
        {
            Stamp("Base=Hold,Any,Gamepad ButtonA;Layer_1=Release,Any,Gamepad ButtonB;Layer_3=Hold,Any,");
            Assert.False(Settle());
            InputManager.ApplyMacroLayerSwitch(0, "Layer_1");
            Assert.True(Settle());
            _down.Add("Gamepad ButtonB");
            Assert.False(Settle());
            // A layer with no entry takes Base's, and one with no buttons is
            // always on.
            InputManager.ApplyMacroLayerSwitch(0, "Layer_2");
            Assert.False(Settle());
            _down.Add("Gamepad ButtonA");
            Assert.True(Settle());
            _down.Clear();
            InputManager.ApplyMacroLayerSwitch(0, "Layer_3");
            Assert.True(Settle());
        }

        [Fact]
        public void NoStamp_LeavesTheGyroOn()
        {
            Stamp("");
            Assert.True(Settle());
        }

        [Fact]
        public void AReplacedStamp_DropsTheOldTogglesStates()
        {
            Stamp("Base=Toggle,Any,Gamepad ButtonA");
            _down.Add("Gamepad ButtonA");
            Assert.True(Settle());
            _down.Clear();
            Stamp("Base=Toggle,Any,Gamepad ButtonA");
            Assert.False(Settle());
            _down.Add("Gamepad ButtonA");
            Assert.True(Settle());
            var toggles = (Array)typeof(InputManager).GetField("_activationToggled", Private)!.GetValue(_im)!;
            Assert.Single((IDictionary)toggles.GetValue(0)!);
        }

        [Fact]
        public void RecenterOnEngage_FiresEachTimeTheGyroTurnsOn()
        {
            Stamp("Base=Hold,Any,Gamepad ButtonA", recenter: true);
            Settle();
            Assert.Equal(0, _recenters);
            _down.Add("Gamepad ButtonA");
            Settle();
            Settle();
            Assert.Equal(1, _recenters);
            _down.Clear();
            Settle();
            _down.Add("Gamepad ButtonA");
            Settle();
            Assert.Equal(2, _recenters);
        }

        [Fact]
        public void WithoutTheStamp_NothingRecenters()
        {
            Stamp("Base=Hold,Any,Gamepad ButtonA");
            _down.Add("Gamepad ButtonA");
            Settle();
            Assert.Equal(0, _recenters);
        }

        [Fact]
        public void IsGyroEngaged_NeedsAnEngageSource_NoRatchet_AndTheLaneOn()
        {
            _im.GyroEngagedFromButton[0] = true;
            Assert.True(_im.IsGyroEngaged(0));
            _im.GyroActivationOn[0] = false;
            Assert.False(_im.IsGyroEngaged(0));
            _im.GyroActivationOn[0] = true;
            _im.GyroRatchetHeld[0] = true;
            Assert.False(_im.IsGyroEngaged(0));
            _im.GyroRatchetHeld[0] = false;
            _im.GyroEngagedFromButton[0] = false;
            Assert.False(_im.IsGyroEngaged(0));
            _im.GyroEngagedFromMacro[0] = true;
            Assert.True(_im.IsGyroEngaged(0));
            Assert.True(_im.IsGyroEngaged(-1));
            Assert.True(_im.IsGyroEngaged(InputManager.MaxPads));
        }

        /// <summary>The gyro evaluators ask the engage provider, and the
        /// provider answers with the same method the re-center reads.</summary>
        [Fact]
        public void TheEngageProvider_AnswersWithIsGyroEngaged()
        {
            var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "PadForge.sln")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            string svc = System.IO.File.ReadAllText(System.IO.Path.Combine(dir.FullName, "PadForge.App", "Services", "InputService.cs"));
            int at = svc.IndexOf("SourceCoercion.AimEngageStateProvider = slotIndex =>", StringComparison.Ordinal);
            Assert.True(at >= 0);
            int end = svc.IndexOf("};", at, StringComparison.Ordinal);
            Assert.Contains("return im == null || im.IsGyroEngaged(slotIndex);", svc.Substring(at, end - at));
            string im = System.IO.File.ReadAllText(System.IO.Path.Combine(dir.FullName, "PadForge.App", "Common", "Input", "InputManager.cs"));
            Assert.Contains("bool engaged = IsGyroEngaged(slot);", im);
        }

        [Fact]
        public void GyroEngaged_ReadsTheSlotsEngageState()
        {
            bool engaged = false;
            SourceCoercion.AimEngageStateProvider = slot => engaged;
            var state = new CustomInputState();
            var src = new MappingSource { Descriptor = SourceCoercion.GyroEngagedDescriptor };
            Assert.False(SourceCoercion.EvaluateForButtonTarget(state, src, 50, 0));
            engaged = true;
            Assert.True(SourceCoercion.EvaluateForButtonTarget(state, src, 50, 0));
            // Off a slot (a preview read) there is no engage state to gate on.
            engaged = false;
            Assert.True(SourceCoercion.EvaluateForButtonTarget(state, src, 50, -1));
        }

        [Fact]
        public void TheMaterializer_CarriesTheLaneAndTheRecenter_OntoEveryClaimedSlot()
        {
            var t = new TranslatedProfile { Name = "Lane", NeedsXboxSlot = true, NeedsKbmSlot = true };
            t.GyroActivationByLayer["Base"] = new TranslatedGyroActivation
            {
                Buttons = { "Gamepad ButtonA", "Gamepad ButtonB" }, Mode = "Hold",
            };
            t.GyroActivationByLayer["Layer_1"] = new TranslatedGyroActivation
            {
                Buttons = { "Gamepad ButtonX" }, Mode = "Release",
            };
            t.GyroRecenterOnEngage = true;
            var p = WorkshopProfileMaterializer.Materialize(t);
            int stamped = 0;
            foreach (var set in p.SlotMappingSets)
            {
                if (set == null || string.IsNullOrEmpty(set.WorkshopGyroActivation)) continue;
                stamped++;
                Assert.Equal("Base=Hold,Any,Gamepad ButtonA|Gamepad ButtonB;Layer_1=Release,Any,Gamepad ButtonX",
                    set.WorkshopGyroActivation);
                Assert.True(set.WorkshopGyroRecenterOnEngage);
                var parsed = set.WorkshopGyroActivationByLayer;
                Assert.Equal(new[] { "Gamepad ButtonA", "Gamepad ButtonB" }, parsed["Base"].Buttons);
                Assert.Equal("Release", parsed["Layer_1"].Mode);
            }
            Assert.Equal(2, stamped);
        }

        [Fact]
        public void CopyWorkshopStamps_CarriesTheLaneAndTheRecenter()
        {
            var src = new MappingSet
            {
                WorkshopGyroActivation = "Base=Toggle,All,Gamepad ButtonA|Gamepad ButtonB",
                WorkshopGyroRecenterOnEngage = true,
            };
            var dst = new MappingSet();
            src.CopyWorkshopStampsTo(dst);
            Assert.Equal(src.WorkshopGyroActivation, dst.WorkshopGyroActivation);
            Assert.True(dst.WorkshopGyroRecenterOnEngage);
            Assert.True(dst.WorkshopGyroActivationByLayer["Base"].All);
            Assert.Equal("Toggle", dst.WorkshopGyroActivationByLayer["Base"].Mode);
        }
    }
}
