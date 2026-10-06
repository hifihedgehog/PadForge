using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using PadForge.Common.Input;
using PadForge.Engine.Common.OpenXr;
using PadForge.Resources.Strings;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The OpenXR runtime picker (issue #403).
    ///
    /// <para>The first version of this shipped to the bench and killed the
    /// app on launch. The collection's getter raised a change notification
    /// for the selected item, whose getter read the collection, which raised
    /// again. That recursion ends as a stack overflow, and a stack overflow
    /// takes the process down with no managed exception, no crash log and
    /// nothing in the event log worth reading. It was found by launching the
    /// deployed build, which is the only thing that would have found
    /// it.</para>
    ///
    /// <para>So the test is the cheap one: read both, in both orders, and
    /// require it to come back.</para>
    ///
    /// <para>Turning the input on writes the static the engine sweep reads,
    /// so the class runs with the other head tracking tests and puts that
    /// static and the saved runtime back.</para>
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class OpenXrRuntimePickerTests : IDisposable
    {
        private const string SteamVr = @"C:\SteamVR\steamxr_win64.json";
        private const string Meta = @"C:\Program Files\Oculus\Support\oculus-runtime\oculus_openxr_64.json";

        private readonly bool _savedOpenXr = HeadTrackingRuntime.OpenXrEnabled;
        private readonly string _savedRuntime = HeadTrackingRuntime.OpenXrRuntimeManifest;

        public void Dispose()
        {
            HeadTrackingRuntime.OpenXrEnabled = _savedOpenXr;
            HeadTrackingRuntime.OpenXrRuntimeManifest = _savedRuntime;
        }

        /// <summary>
        /// Reading these properties the way a BINDING reads them terminates.
        ///
        /// <para>Reading them plainly is not the test. With nothing
        /// subscribed, a change notification goes nowhere and the recursion
        /// that killed the app cannot happen, so a plain read passes against
        /// the broken code and proves nothing. A binding re-reads the
        /// property it was told changed, and that is what closes the
        /// loop.</para>
        ///
        /// <para>The depth is bounded here rather than left to overflow,
        /// because a real stack overflow takes the test host down with it and
        /// reports nothing useful.</para>
        /// </summary>
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void ReadingThemLikeABindingDoesTerminates(bool listFirst)
        {
            var vm = new DashboardViewModel();
            int depth = 0, deepest = 0;

            vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName != nameof(DashboardViewModel.SelectedOpenXrRuntime)) return;
                depth++;
                deepest = Math.Max(deepest, depth);
                // A binding re-reads what it was told about. Bail out well
                // before the stack does, so the failure is a message rather
                // than a dead process.
                if (depth < 32) _ = vm.SelectedOpenXrRuntime;
                depth--;
            };

            if (listFirst)
            {
                Assert.NotEmpty(vm.OpenXrRuntimes);
                Assert.NotNull(vm.SelectedOpenXrRuntime);
            }
            else
            {
                Assert.NotNull(vm.SelectedOpenXrRuntime);
                Assert.NotEmpty(vm.OpenXrRuntimes);
            }

            Assert.True(deepest < 4,
                $"reading the picker re-entered {deepest} deep, which is the recursion that "
                + "takes the process down with no crash log");
        }

        /// <summary>Refreshing runs when the input is turned on, and it
        /// notifies. It must still terminate.</summary>
        [Fact]
        public void RefreshingTerminatesAndKeepsTheDefaultFirst()
        {
            var vm = new DashboardViewModel();
            vm.RefreshOpenXrRuntimes();
            vm.RefreshOpenXrRuntimes();
            Assert.NotEmpty(vm.OpenXrRuntimes);
            Assert.Equal(string.Empty, vm.OpenXrRuntimes[0].ManifestPath);
        }

        /// <summary>The machine's default is always offered, even where no
        /// runtime is installed, so the box is never empty.</summary>
        [Fact]
        public void TheSystemDefaultIsAlwaysAnOption()
        {
            var vm = new DashboardViewModel();
            Assert.Contains(vm.OpenXrRuntimes, r => r.ManifestPath == string.Empty);
            Assert.Equal(string.Empty, vm.SelectedOpenXrRuntime.ManifestPath);
        }

        /// <summary>Choosing one records it, and choosing the default clears
        /// it. Empty means the machine's default, which is what the source
        /// reads as "ask the registry".</summary>
        [Fact]
        public void ChoosingARuntimeRecordsItAndClearsBack()
        {
            string before = PadForge.Common.Input.HeadTrackingRuntime.OpenXrRuntimeManifest;
            try
            {
                var vm = new DashboardViewModel();
                var pick = new DashboardViewModel.OpenXrRuntimeChoice
                {
                    ManifestPath = @"C:\nowhere\runtime.json",
                    Display = "runtime",
                };
                vm.SelectedOpenXrRuntime = pick;
                Assert.Equal(@"C:\nowhere\runtime.json",
                             PadForge.Common.Input.HeadTrackingRuntime.OpenXrRuntimeManifest);

                vm.SelectedOpenXrRuntime = vm.OpenXrRuntimes.First(r => r.ManifestPath == string.Empty);
                Assert.Equal(string.Empty,
                             PadForge.Common.Input.HeadTrackingRuntime.OpenXrRuntimeManifest);
            }
            finally
            {
                PadForge.Common.Input.HeadTrackingRuntime.OpenXrRuntimeManifest = before;
            }
        }

        /// <summary>A runtime the user picked and then uninstalled still
        /// shows, or the box would read as the default while the saved
        /// setting still names the missing one.</summary>
        [Fact]
        public void AChosenRuntimeThatIsGoneStillAppears()
        {
            string before = PadForge.Common.Input.HeadTrackingRuntime.OpenXrRuntimeManifest;
            try
            {
                PadForge.Common.Input.HeadTrackingRuntime.OpenXrRuntimeManifest =
                    @"C:\uninstalled\runtime.json";
                var vm = new DashboardViewModel();
                Assert.Contains(vm.OpenXrRuntimes,
                                r => r.ManifestPath == @"C:\uninstalled\runtime.json");
                Assert.Equal(@"C:\uninstalled\runtime.json", vm.SelectedOpenXrRuntime.ManifestPath);
            }
            finally
            {
                PadForge.Common.Input.HeadTrackingRuntime.OpenXrRuntimeManifest = before;
            }
        }

        /// <summary>
        /// The user page's step for SteamVR: make it the system runtime in
        /// SteamVR's settings, then turn the input off and back on. The list
        /// was built before SteamVR registered, and turning the input on is
        /// what reads the registry again.
        /// </summary>
        [Fact]
        public void ARuntimeRegisteredWhilePadForgeRunsIsListedOnceTheInputIsTurnedOn()
        {
            HeadTrackingRuntime.OpenXrRuntimeManifest = string.Empty;
            var registry = new List<OpenXrRuntimeEntry> { Runtime(Meta, "Oculus") };
            var vm = new DashboardViewModel { DiscoverOpenXrRuntimes = () => registry };
            Assert.Equal(new[] { "", Meta }, vm.OpenXrRuntimes.Select(r => r.ManifestPath));

            registry.Insert(0, Runtime(SteamVr, "SteamVR"));
            Assert.Equal(new[] { "", Meta }, vm.OpenXrRuntimes.Select(r => r.ManifestPath));

            vm.HeadTrackingOpenXr = true;

            Assert.Equal(new[] { "", SteamVr, Meta }, vm.OpenXrRuntimes.Select(r => r.ManifestPath));
            Assert.Equal("SteamVR", vm.OpenXrRuntimes[1].Display);
            Assert.Equal("", vm.SelectedOpenXrRuntime.ManifestPath);

            // And the pick the status line then asks for still lands.
            vm.SelectedOpenXrRuntime = vm.OpenXrRuntimes[1];
            Assert.Equal(SteamVr, HeadTrackingRuntime.OpenXrRuntimeManifest);
        }

        /// <summary>
        /// The picker matches manifest paths without regard to case, as its
        /// selection does, so a path the registry spells differently from the
        /// saved one is the same entry and keeps its instance.
        /// </summary>
        [Fact]
        public void APathThatDiffersOnlyInCaseIsTheSameEntry()
        {
            string saved = SteamVr.ToUpperInvariant();
            HeadTrackingRuntime.OpenXrRuntimeManifest = saved;
            var registry = new List<OpenXrRuntimeEntry>();
            var vm = new DashboardViewModel { DiscoverOpenXrRuntimes = () => registry };
            var kept = vm.SelectedOpenXrRuntime;
            Assert.Equal(saved, kept.ManifestPath);

            registry.Add(Runtime(SteamVr, "SteamVR"));
            vm.HeadTrackingOpenXr = true;

            Assert.Equal(2, vm.OpenXrRuntimes.Count);
            Assert.Same(kept, vm.OpenXrRuntimes[1]);
            Assert.Equal("SteamVR", kept.Display);
            Assert.Same(kept, vm.SelectedOpenXrRuntime);
        }

        /// <summary>
        /// Turning the input off and on rebuilds the list under a bound box.
        /// A rebuild that cleared the list removed the selected entry, WPF
        /// wrote the empty selection back, and the setter took it as the
        /// system default, so the step would have dropped the runtime the
        /// user picked. The entry keeps its instance instead.
        /// </summary>
        [Fact]
        public void TurningTheInputOffAndOnKeepsTheRuntimeTheBoxShows()
        {
            RunSta(() =>
            {
                // Picked while SteamVR was the default, and Meta has taken the
                // default back, so the registry no longer names SteamVR and
                // the box lists it under its manifest's file name.
                HeadTrackingRuntime.OpenXrRuntimeManifest = SteamVr;
                var registry = new List<OpenXrRuntimeEntry> { Runtime(Meta, "Oculus") };
                var vm = new DashboardViewModel { DiscoverOpenXrRuntimes = () => registry };
                var box = Bind(vm);
                var shown = Assert.IsType<DashboardViewModel.OpenXrRuntimeChoice>(box.SelectedItem);
                Assert.Equal(SteamVr, shown.ManifestPath);
                Assert.Equal("steamxr_win64", shown.Display);

                vm.HeadTrackingOpenXr = true;
                vm.HeadTrackingOpenXr = false;
                vm.HeadTrackingOpenXr = true;

                Assert.Equal(SteamVr, HeadTrackingRuntime.OpenXrRuntimeManifest);
                Assert.Same(shown, box.SelectedItem);
            });
        }

        /// <summary>
        /// When SteamVR is the default again, its entry moves to where the
        /// registry lists it and takes the manifest's name, and the box
        /// keeps showing it.
        /// </summary>
        [Fact]
        public void AKeptRuntimeTheRegistryNamesAgainTakesItsPlaceAndName()
        {
            RunSta(() =>
            {
                HeadTrackingRuntime.OpenXrRuntimeManifest = SteamVr;
                var registry = new List<OpenXrRuntimeEntry> { Runtime(Meta, "Oculus") };
                var vm = new DashboardViewModel { DiscoverOpenXrRuntimes = () => registry };
                var box = Bind(vm);
                var shown = Assert.IsType<DashboardViewModel.OpenXrRuntimeChoice>(box.SelectedItem);
                Assert.Equal(new[] { "", Meta, SteamVr }, vm.OpenXrRuntimes.Select(r => r.ManifestPath));

                registry.Insert(0, Runtime(SteamVr, "SteamVR"));
                vm.HeadTrackingOpenXr = true;

                Assert.Equal(new[] { "", SteamVr, Meta }, vm.OpenXrRuntimes.Select(r => r.ManifestPath));
                Assert.Same(shown, box.SelectedItem);
                Assert.Equal("SteamVR", shown.Display);
                Assert.Equal(SteamVr, HeadTrackingRuntime.OpenXrRuntimeManifest);
            });
        }

        /// <summary>
        /// A load that saves another runtime the registry does not name
        /// removes the entry the box shows, and the box writes its emptied
        /// selection back while the list is rebuilt. The loaded runtime must
        /// survive that and be the one shown.
        /// </summary>
        [Fact]
        public void ALoadThatSavesAnotherUnlistedRuntimeShowsIt()
        {
            RunSta(() =>
            {
                const string first = @"C:\old\first.json", second = @"C:\old\second.json";
                HeadTrackingRuntime.OpenXrRuntimeManifest = first;
                var vm = new DashboardViewModel { DiscoverOpenXrRuntimes = () => Array.Empty<OpenXrRuntimeEntry>() };
                var box = Bind(vm);
                Assert.Equal(first, Assert.IsType<DashboardViewModel.OpenXrRuntimeChoice>(box.SelectedItem).ManifestPath);

                // LoadAppSettings writes the static, then tells the view model.
                HeadTrackingRuntime.OpenXrRuntimeManifest = second;
                vm.NotifyHeadTrackingRangesChanged();

                Assert.Equal(second, HeadTrackingRuntime.OpenXrRuntimeManifest);
                Assert.Equal(second, Assert.IsType<DashboardViewModel.OpenXrRuntimeChoice>(box.SelectedItem).ManifestPath);
                Assert.DoesNotContain(vm.OpenXrRuntimes, r => r.ManifestPath == first);
            });
        }

        /// <summary>
        /// Nothing written to the selection while the list is rebuilt counts
        /// as a pick, the audio mirror picker's rule. A box synchronized with
        /// the list's current item writes the null and then a neighbor when
        /// its entry leaves, and ignoring a null alone lets the neighbor
        /// through. The handler here writes that neighbor.
        /// </summary>
        [Fact]
        public void APickWrittenWhileTheListIsRebuiltIsIgnored()
        {
            const string first = @"C:\old\first.json", second = @"C:\old\second.json";
            HeadTrackingRuntime.OpenXrRuntimeManifest = first;
            var vm = new DashboardViewModel { DiscoverOpenXrRuntimes = () => Array.Empty<OpenXrRuntimeEntry>() };
            Assert.Equal(first, vm.SelectedOpenXrRuntime.ManifestPath);
            vm.OpenXrRuntimes.CollectionChanged += (_, e) =>
            {
                if (e.Action == NotifyCollectionChangedAction.Remove)
                    vm.SelectedOpenXrRuntime = vm.OpenXrRuntimes[0];
            };

            HeadTrackingRuntime.OpenXrRuntimeManifest = second;
            vm.NotifyHeadTrackingRangesChanged();

            Assert.Equal(second, HeadTrackingRuntime.OpenXrRuntimeManifest);
            Assert.Equal(second, vm.SelectedOpenXrRuntime.ManifestPath);
        }

        /// <summary>
        /// A box that loses its list writes a null back through the two-way
        /// binding. That is never a pick, and the box reads the saved
        /// runtime back.
        /// </summary>
        [Fact]
        public void TheBoxEmptyingItsSelectionIsNotAPick()
        {
            RunSta(() =>
            {
                HeadTrackingRuntime.OpenXrRuntimeManifest = SteamVr;
                var vm = new DashboardViewModel { DiscoverOpenXrRuntimes = () => new[] { Runtime(SteamVr, "SteamVR") } };
                var box = Bind(vm);
                Assert.NotNull(box.SelectedItem);

                BindingOperations.ClearBinding(box, ItemsControl.ItemsSourceProperty);

                Assert.Equal(SteamVr, HeadTrackingRuntime.OpenXrRuntimeManifest);
                Assert.Equal(SteamVr, Assert.IsType<DashboardViewModel.OpenXrRuntimeChoice>(box.SelectedItem).ManifestPath);
            });
        }

        private static OpenXrRuntimeEntry Runtime(string manifest, string name) => new()
        {
            ManifestPath = manifest,
            Name = name,
            // Any file on disk, since the picker lists only runtimes whose
            // library is there.
            LibraryPath = typeof(object).Assembly.Location,
        };

        /// <summary>The Dashboard's picker: the list, the two-way selection
        /// and the caption, bound as DashboardPage.xaml binds them.</summary>
        private static ComboBox Bind(DashboardViewModel vm)
        {
            var box = new ComboBox
            {
                DataContext = vm,
                DisplayMemberPath = nameof(DashboardViewModel.OpenXrRuntimeChoice.Display),
            };
            box.SetBinding(ItemsControl.ItemsSourceProperty,
                           new Binding(nameof(DashboardViewModel.OpenXrRuntimes)));
            box.SetBinding(Selector.SelectedItemProperty,
                           new Binding(nameof(DashboardViewModel.SelectedOpenXrRuntime)) { Mode = BindingMode.TwoWay });
            return box;
        }

        private static void RunSta(Action body)
        {
            Exception failure = null;
            var thread = new Thread(() =>
            {
                try { body(); }
                catch (Exception ex) { failure = ex; }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();
            Assert.True(thread.Join(15000), "the picker binding timed out");
            if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    /// <summary>
    /// The picker's System Default row follows a language switch. It kept
    /// the previous language's word, as the audio mirror picker's row did
    /// before its rows were re-captioned in place.
    /// </summary>
    [Collection("CultureSwitching")]
    public class OpenXrRuntimePickerCultureTests
    {
        [Fact]
        public void TheSystemDefaultRowFollowsALanguageSwitch()
        {
            var before = CultureInfo.CurrentUICulture;
            string savedRuntime = HeadTrackingRuntime.OpenXrRuntimeManifest;
            try
            {
                Strings.ChangeCulture(CultureInfo.GetCultureInfo("en"));
                HeadTrackingRuntime.OpenXrRuntimeManifest = string.Empty;
                var vm = new DashboardViewModel { DiscoverOpenXrRuntimes = () => Array.Empty<OpenXrRuntimeEntry>() };
                var row = vm.OpenXrRuntimes[0];
                string english = row.Display;

                Strings.ChangeCulture(CultureInfo.GetCultureInfo("de"));

                Assert.Same(row, vm.OpenXrRuntimes[0]);
                Assert.Equal(Strings.Instance.Dashboard_HeadTrackingOpenXrSystemDefault, row.Display);
                Assert.NotEqual(english, row.Display);
            }
            finally
            {
                Strings.ChangeCulture(before);
                HeadTrackingRuntime.OpenXrRuntimeManifest = savedRuntime;
            }
        }
    }
}
