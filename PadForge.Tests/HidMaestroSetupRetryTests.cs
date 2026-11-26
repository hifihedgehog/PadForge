using System;
using System.IO;
using System.Linq;
using PadForge.Common;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Resources.Strings;
using PadForge.Services;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// A failed HIDMaestro setup no longer waits for an engine restart. The
    /// next create after a short hold runs the setup again, and a successful
    /// retry clears the error it left in the status line.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class HidMaestroSetupRetryTests
    {
        private static string RepoFile(params string[] parts)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PadForge.sln")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return File.ReadAllText(Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray()));
        }

        private static string Body(string source, string signature, int length)
        {
            int at = source.IndexOf(signature, StringComparison.Ordinal);
            Assert.True(at > 0, signature);
            return source.Substring(at, Math.Min(length, source.Length - at));
        }

        /// <summary>No failure holds nothing. A failure holds the setup for
        /// five seconds, which covers the other slots of the same burst, and
        /// lets the next create after that run it again.</summary>
        [Fact]
        public void AFailedSetupIsHeldBrieflyThenRetried()
        {
            Assert.False(InputManager.HmSetupHeldAt(0, 1_000_000));
            Assert.True(InputManager.HmSetupHeldAt(1_000_000, 1_000_000));
            Assert.True(InputManager.HmSetupHeldAt(1_000_000, 1_004_999));
            Assert.False(InputManager.HmSetupHeldAt(1_000_000, 1_005_000));
            Assert.False(InputManager.HmSetupHeldAt(1_000_000, 1_600_000));
        }

        /// <summary>The setup reads the hold, never a latch that only an
        /// engine stop clears. A failure disposes the half-built context and
        /// stamps the hold. A success clears it and reports the recovery. An
        /// engine stop clears the hold too.</summary>
        [Fact]
        public void TheSetupRetriesAndReportsRecovery()
        {
            string step5 = RepoFile("PadForge.App", "Common", "Input", "InputManager.Step5.VirtualDevices.cs");
            Assert.DoesNotContain("_hmaestroContextFailed", step5);

            string ensure = Body(step5, "private void EnsureHMaestroContext()", 5000);
            Assert.Contains("if (_hmaestroContext != null || HmSetupHeld())", ensure);
            Assert.Contains("bool recovered = _hmaestroSetupFailedTick != 0;", ensure);
            Assert.Contains("Volatile.Write(ref _hmaestroSetupFailedTick, 0);", ensure);
            Assert.Contains("if (recovered) RaiseErrorResolved(_hmSetupFailedStatus ?? Strings.Instance.Status_HidMaestroSetupFailed, HmSetupFailedLog);", ensure);
            Assert.Contains("try { ctx?.Dispose(); } catch { }", ensure);
            Assert.Contains("Volatile.Write(ref _hmaestroSetupFailedTick, Math.Max(1, Environment.TickCount64));", ensure);
            Assert.Contains("RaiseError(_hmSetupFailedStatus, HmSetupFailedLog, ex);", ensure);

            string shutdown = Body(step5, "DisposeHMaestroContextOnShutdown(IReadOnlyList", 2500);
            Assert.Contains("Volatile.Write(ref _hmaestroSetupFailedTick, 0);", shutdown);

            string svc = RepoFile("PadForge.App", "Services", "InputService.cs");
            Assert.Contains("_inputManager.ErrorResolved += OnErrorResolved;", svc);
            Assert.Contains("_inputManager.ErrorResolved -= OnErrorResolved;", svc);
        }

        /// <summary>A recovery clears the status line only while it still
        /// shows that error, and leaves a later message alone.</summary>
        [Fact]
        public void ARecoveryClearsOnlyItsOwnError()
        {
            var saved = SettingsManager.UserDevices;
            SettingsManager.UserDevices = new DeviceCollection();
            var vm = new MainViewModel();
            var svc = new InputService(vm);
            try
            {
                const string failure = "Failed to initialize HIDMaestro.";
                svc.ShowError(failure);
                Assert.Equal(string.Format(Strings.Instance.Status_Error_Format, failure), vm.StatusText);
                svc.ClearResolvedError(failure);
                Assert.Equal(string.Empty, vm.StatusText);

                svc.ShowError(failure);
                vm.SetStatus("Settings saved.", persist: true);
                svc.ClearResolvedError(failure);
                Assert.Equal("Settings saved.", vm.StatusText);

                // Another error's recovery leaves this one's line up.
                svc.ShowError(failure);
                svc.ClearResolvedError("Failed to create the virtual controller.");
                Assert.Equal(string.Format(Strings.Instance.Status_Error_Format, failure), vm.StatusText);
            }
            finally
            {
                svc.Dispose();
                SettingsManager.UserDevices = saved;
            }
        }

        /// <summary>The recovery takes back the line as it was written. After
        /// a language change the current format reads differently, and a
        /// compare with it left the error up.</summary>
        [Fact]
        public void ARecoveryAfterALanguageChangeStillClearsItsError()
        {
            var saved = SettingsManager.UserDevices;
            SettingsManager.UserDevices = new DeviceCollection();
            var vm = new MainViewModel();
            var svc = new InputService(vm);
            var before = System.Threading.Thread.CurrentThread.CurrentUICulture;
            try
            {
                const string failure = "Failed to initialize HIDMaestro.";
                System.Threading.Thread.CurrentThread.CurrentUICulture = System.Globalization.CultureInfo.GetCultureInfo("en");
                svc.ShowError(failure);
                string written = vm.StatusText;

                System.Threading.Thread.CurrentThread.CurrentUICulture = System.Globalization.CultureInfo.GetCultureInfo("de");
                Assert.NotEqual(written, string.Format(Strings.Instance.Status_Error_Format, failure));
                svc.ClearResolvedError(failure);

                Assert.Equal(string.Empty, vm.StatusText);
            }
            finally
            {
                System.Threading.Thread.CurrentThread.CurrentUICulture = before;
                svc.Dispose();
                SettingsManager.UserDevices = saved;
            }
        }

        /// <summary>The context is published only once nothing after it in
        /// the setup can throw. The catch disposes the context, so one
        /// published before a throw stayed in the field disposed, and every
        /// later setup returned early on it.</summary>
        [Fact]
        public void TheContextIsPublishedLast()
        {
            string step5 = RepoFile("PadForge.App", "Common", "Input", "InputManager.Step5.VirtualDevices.cs");
            string ensure = Body(step5, "private void EnsureHMaestroContext()", 7000);
            int end = ensure.IndexOf("catch (Exception ex)", StringComparison.Ordinal);
            Assert.True(end > 0);
            string attempt = ensure.Substring(0, end);

            int publish = attempt.IndexOf("_hmaestroContext = ctx;", StringComparison.Ordinal);
            Assert.True(publish > 0, "the context is never published");
            Assert.Equal(publish, attempt.LastIndexOf("_hmaestroContext = ctx;", StringComparison.Ordinal));
            Assert.True(publish > attempt.IndexOf("ctx.InstallDriver();", StringComparison.Ordinal));
            Assert.True(publish > attempt.IndexOf("if (recovered) RaiseErrorResolved(_hmSetupFailedStatus ?? Strings.Instance.Status_HidMaestroSetupFailed, HmSetupFailedLog);", StringComparison.Ordinal));
            Assert.True(publish > attempt.IndexOf("AppDomain.CurrentDomain.ProcessExit +=", StringComparison.Ordinal));
        }
    }
}
