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
    /// next create after a short hold runs the setup again, a successful retry
    /// clears the error it left in the status line, and an engine stop and
    /// start no longer turns off the exit sweep for the rest of the session.
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
        /// stamps the hold. A success clears it, reports the recovery, and
        /// puts the exit sweep back on for the new context. An engine stop
        /// clears the hold too.</summary>
        [Fact]
        public void TheSetupRetriesAndReportsRecovery()
        {
            string step5 = RepoFile("PadForge.App", "Common", "Input", "InputManager.Step5.VirtualDevices.cs");
            Assert.DoesNotContain("_hmaestroContextFailed", step5);

            string ensure = Body(step5, "private void EnsureHMaestroContext()", 5000);
            Assert.Contains("if (_hmaestroContext != null || HmSetupHeld())", ensure);
            Assert.Contains("bool recovered = _hmaestroSetupFailedTick != 0;", ensure);
            Assert.Contains("Volatile.Write(ref _hmaestroSetupFailedTick, 0);", ensure);
            Assert.Contains("_cleanShutdownPerformed = false;", ensure);
            Assert.Contains("if (recovered) RaiseErrorResolved(HmSetupFailedMessage);", ensure);
            Assert.Contains("try { ctx?.Dispose(); } catch { }", ensure);
            Assert.Contains("Volatile.Write(ref _hmaestroSetupFailedTick, Math.Max(1, Environment.TickCount64));", ensure);
            Assert.Contains("RaiseError(HmSetupFailedMessage, ex);", ensure);

            string shutdown = Body(step5, "private void DisposeHMaestroContextOnShutdown()", 900);
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
                vm.SetStatus(string.Format(Strings.Instance.Status_Error_Format, failure), persist: true);
                svc.ClearResolvedError(failure);
                Assert.Equal(string.Empty, vm.StatusText);

                vm.SetStatus("Settings saved.", persist: true);
                svc.ClearResolvedError(failure);
                Assert.Equal("Settings saved.", vm.StatusText);
            }
            finally
            {
                svc.Dispose();
                SettingsManager.UserDevices = saved;
            }
        }
    }
}
