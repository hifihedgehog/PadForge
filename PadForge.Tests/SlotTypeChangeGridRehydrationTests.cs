using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PadForge.Common.Input;
using PadForge.Engine.Data;
using PadForge.Services;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Discussion #396: mappings authored right after a slot type change
    /// never reached the virtual controller until forging was restarted.
    /// The type handlers merge, set the output type (whose rebuild
    /// re-hydrates the grid), then clear MappingsViewLoaded as a stale
    /// guard, and nothing on the sidebar path re-armed it. While the flag
    /// is clear the tier-1 push skips the pad, so every grid edit stays in
    /// the grid. The handlers now re-hydrate before they return.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class SlotTypeChangeGridRehydrationTests : IDisposable
    {
        private readonly SettingsCollection _savedSettings = SettingsManager.UserSettings;
        private readonly DeviceCollection _savedDevices = SettingsManager.UserDevices;
        private readonly MappingSet[] _savedMappingSets = SettingsManager.SlotMappingSets;
        private readonly bool[] _savedCreated = (bool[])SettingsManager.SlotCreated.Clone();

        public void Dispose()
        {
            SettingsManager.UserSettings = _savedSettings;
            SettingsManager.UserDevices = _savedDevices;
            SettingsManager.SlotMappingSets = _savedMappingSets;
            Array.Copy(_savedCreated, SettingsManager.SlotCreated, _savedCreated.Length);
        }

        private static (MainViewModel vm, SettingsService ss, MappingSet ms, MappingItem item) Arrange()
        {
            SettingsManager.UserDevices = new DeviceCollection();
            SettingsManager.UserSettings = new SettingsCollection();
            SettingsManager.SlotMappingSets = new MappingSet[InputManager.MaxPads];
            Array.Clear(SettingsManager.SlotCreated, 0, SettingsManager.SlotCreated.Length);
            SettingsManager.SlotCreated[0] = true;

            var ms = new MappingSet
            {
                Rows = new List<MappingRow>
                {
                    new MappingRow
                    {
                        Target = "ButtonA", LayerMask = "Base",
                        Sources = new List<MappingSource> { new MappingSource { Descriptor = "Button 3" } },
                    },
                },
            };
            SettingsManager.SlotMappingSets[0] = ms;

            var vm = new MainViewModel();
            var ss = new SettingsService(vm);
            var pad = vm.Pads[0];
            pad.Mappings.Clear();
            var item = new MappingItem("ButtonA", "ButtonA", MappingCategory.Buttons);
            pad.Mappings.Add(item);
            return (vm, ss, ms, item);
        }

        private static string RowDescriptor(MappingSet ms)
            => ms.Rows.Find(r => r.Target == "ButtonA")?.Sources.FirstOrDefault()?.Descriptor;

        /// <summary>The reporter's window: the flag is clear, the user edits,
        /// the push skips the pad, and the engine keeps the old source.</summary>
        [Fact]
        public void AnEditMadeWhileTheGridIsUnarmedNeverReachesTheEngine()
        {
            var (vm, ss, ms, item) = Arrange();
            vm.Pads[0].MappingsViewLoaded = false;

            item.SourceDescriptor = "Button 5";
            ss.PushUiExtraSourcesIntoSlotMappingSets();

            Assert.Equal("Button 3", RowDescriptor(ms));
        }

        /// <summary>The fix: re-hydrating the grid arms the flag, shows the
        /// engine's row, and the next edit reaches the engine on the next
        /// push, with no forging restart.</summary>
        [Fact]
        public void RehydratingTheGridArmsThePushAgain()
        {
            var (vm, ss, ms, item) = Arrange();
            var pad = vm.Pads[0];
            pad.MappingsViewLoaded = false;

            InputService.RefreshMappingsToViewModel(pad);

            Assert.True(pad.MappingsViewLoaded);
            Assert.Equal("Button 3", item.SourceDescriptor);

            item.SourceDescriptor = "Button 5";
            ss.PushUiExtraSourcesIntoSlotMappingSets();

            Assert.Equal("Button 5", RowDescriptor(ms));
        }

        /// <summary>Every handler that clears the flag must re-hydrate the
        /// pad before it returns. The sidebar tiles had no re-arm at all,
        /// and the dashboard track re-armed only when the device list
        /// refresh happened to change the selection. Since discussion #543
        /// one handler, ChangeSlotType, runs every type change from either
        /// card's type bar.</summary>
        [Fact]
        public void EveryTypeChangeHandlerRehydratesTheGridBeforeReturning()
        {
            string code = RepoText("PadForge.App", "MainWindow.xaml.cs").Replace("\r\n", "\n");
            const string clear = "MappingsViewLoaded = false;";
            int count = 0;
            int at = code.IndexOf(clear, StringComparison.Ordinal);
            Assert.True(at > 0, "no flag clear found in MainWindow");
            while (at >= 0)
            {
                count++;
                int handlerEnd = code.IndexOf("\n        }\n", at, StringComparison.Ordinal);
                int lambdaEnd = code.IndexOf("\n            };\n", at, StringComparison.Ordinal);
                int end = Math.Min(handlerEnd < 0 ? int.MaxValue : handlerEnd, lambdaEnd < 0 ? int.MaxValue : lambdaEnd);
                Assert.True(end != int.MaxValue, "handler end not found after a flag clear");
                string tail = code.Substring(at, end - at);
                Assert.Contains("RefreshMappingsToViewModel(", tail);
                at = code.IndexOf(clear, end, StringComparison.Ordinal);
            }
            Assert.Equal(1, count); // ChangeSlotType, the one type-change path
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
