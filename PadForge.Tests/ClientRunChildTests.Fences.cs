using System;
using System.Collections.Generic;
using PadForge.Services;
using PadForge.Services.Automation.Web;
using Xunit;

namespace PadForge.Tests
{
    public partial class ClientRunChildTests
    {
        // ─────────────────────────────────────────────
        //  The fences a test's child carries
        // ─────────────────────────────────────────────

        /// <summary>The variables of a test's child: the test's own pipe
        /// under the name PadForge's web helper reads it by
        /// (WebHelper.PipeVariable), which makes the child's run a test's
        /// (ClientRun.Seams.ForThisRun).</summary>
        internal static Func<string, string> ChildVariables(string pipe)
            => name => name == WebHelper.PipeVariable ? pipe : null;

    }
}
