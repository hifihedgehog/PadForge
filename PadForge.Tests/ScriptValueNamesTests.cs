using System;
using PadForge.Common.Input;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The Script Value picker's names (#597): a name a session sets while
    /// the list is first filled is offered, whichever step it lands beside.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public sealed class ScriptValueNamesTests
    {
        [Fact]
        public void ANameSetWhileTheListIsFilled_IsOffered()
        {
            ScriptValues.ResetForTest();
            Action unhook = null;
            try
            {
                Assert.True(ScriptValues.Set("early", 1, 7));
                var items = ScriptValueNames.Build(() => Assert.True(ScriptValues.Set("late", 2, 7)), out unhook);
                Assert.Contains("early", items);
                Assert.Contains("late", items);
                Assert.Equal(2, items.Count);
            }
            finally
            {
                unhook?.Invoke();
                ScriptValues.ResetForTest();
            }
        }
    }

    /// <summary>
    /// The picker's names on a real interface thread: each change is applied
    /// there in the order ScriptValues tells it.
    /// </summary>
    [Collection("WpfApplicationSingleton")]
    public sealed class ScriptValueNamesOrderTests
    {
        /// <summary>A name a session thread adds, and the interface's thread
        /// then removes before the add has run there: the picker applies the
        /// two in that order and ends without the name, as ScriptValues
        /// holds it.</summary>
        [Fact]
        public void ARemoveToldOnTheInterfacesThread_AfterAnAddQueuedFromAnother_LeavesTheNameOut()
        {
            // A name and a session number no other test uses, so tests that
            // run beside this one leave them alone.
            string name = "pickerorder" + Guid.NewGuid().ToString("N").Substring(0, 8);
            const int owner = 917_331;
            PageRenderDumpPages.WithApp((_, dispatcher) =>
            {
                Action unhook = null;
                try
                {
                    var items = ScriptValueNames.Build(null, out unhook);
                    var adding = new System.Threading.Thread(() => ScriptValues.Set(name, 1, owner)) { IsBackground = true };
                    adding.Start();
                    Assert.True(adding.Join(5000));
                    Assert.DoesNotContain(name, items);                   // queued, not yet run

                    ScriptValues.ReleaseOwner(owner);                     // told on this thread
                    PageRenderDump.Settle(dispatcher);
                    Assert.DoesNotContain(name, ScriptValues.Names());
                    Assert.DoesNotContain(name, items);
                }
                finally
                {
                    unhook?.Invoke();
                    ScriptValues.ReleaseOwner(owner);
                }
            }, TimeSpan.FromMinutes(2), "the picker's names test timed out");
        }
    }
}
