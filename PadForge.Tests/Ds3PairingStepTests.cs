using System;
using System.Collections.Generic;
using System.Linq;
using PadForge.Resources.Strings;
using PadForge.Services;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The pairing dialog shows each step of a PlayStation 3, Move or
    /// Navigation pairing in the UI's language: every line the service sends
    /// to its step callback is a value of the string table, and its English
    /// narration, with addresses, paths and error codes, goes to the
    /// diagnostics log alone. The test host installs no driver, so each run
    /// stops at the driver step, as a run on a PC whose install failed.
    /// </summary>
    public class Ds3PairingStepTests
    {
        private static HashSet<string> TableValues()
        {
            var s = Strings.Instance;
            return new HashSet<string>(typeof(Strings).GetProperties()
                .Where(p => p.PropertyType == typeof(string) && p.GetIndexParameters().Length == 0)
                .Select(p => p.GetValue(s) as string)
                .Where(v => v != null), StringComparer.Ordinal);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void EveryStepTheDialogShows_IsALineOfTheStringTable(bool moveFamily)
        {
            var shown = new List<string>();
            var svc = new Ds3PairingService(step: shown.Add);
            var result = moveFamily ? svc.RunMovePairing() : svc.RunPairing();

            Assert.Equal("install-failed", result.Error);
            Assert.NotEmpty(shown);
            var table = TableValues();
            Assert.All(shown, line => Assert.Contains(line, table));
        }

        [Fact]
        public void AFault_IsShownByItsKind_FromTheStringTable()
        {
            var s = Strings.Instance;
            Assert.Equal(s.Ds3Pair_FaultAccess, Ds3PairingService.FaultLine(new UnauthorizedAccessException("Access to HKLM denied")));
            Assert.Equal(s.Ds3Pair_FaultDevice, Ds3PairingService.FaultLine(new System.IO.IOException("The device is not ready")));
            Assert.Equal(s.Ds3Pair_FaultOther, Ds3PairingService.FaultLine(new InvalidOperationException("x")));
        }
    }
}
