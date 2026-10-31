using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows.Markup;

namespace PadForge.Tests
{
    /// <summary>
    /// Builds WPF's table of known XAML properties before any test runs.
    /// </summary>
    /// <remarks>
    /// WPF builds an entry of that table the first time XAML names the property, inside the lock of
    /// its shared schema context, and building it runs the static constructor of the type that owns
    /// the property. Some of those constructors take the same lock: ItemsControl's builds its default
    /// items panel, which runs ContentPresenter's, which looks up a type in the schema context. When
    /// one test parsed XAML naming ItemsControl.ItemsSource while another touched ItemsControl for the
    /// first time (a new ComboBox, or a view model's collection view), each thread held what the
    /// other needed, and the WPF tests behind them timed out until the host was killed (hang dumps
    /// of 2026-10-27 and 2026-10-30). Built here, on one thread, the entries' static constructors
    /// have all finished before any test starts, and none runs inside that lock again. The members
    /// read below are internal to WPF, so a WPF that renames them fails WpfKnownPropertyTableTests
    /// with the reason. This must not start a thread and wait for it: no code of this assembly runs
    /// on another thread until the initializer returns.
    /// </remarks>
    internal static class WpfKnownPropertyTable
    {
        /// <summary>Why the table was not built, or null once it was.</summary>
        internal static string Failure { get; private set; } = "not built";

        /// <summary>How many entries were built.</summary>
        internal static int Built { get; private set; }

#pragma warning disable CA2255 // Only the test host loads this assembly.
        [ModuleInitializer]
        internal static void Build()
#pragma warning restore CA2255
        {
            try
            {
                object context = typeof(XamlReader)
                    .GetProperty("BamlSharedSchemaContext", BindingFlags.NonPublic | BindingFlags.Static)
                    .GetValue(null);
                MethodInfo entry = context.GetType().GetMethod("GetKnownBamlMember", BindingFlags.NonPublic | BindingFlags.Instance);
                int count = (int)context.GetType().GetField("KnownPropertyCount", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
                string failure = null;
                for (int id = 1; id <= count; id++)
                {
                    try
                    {
                        entry.Invoke(context, new object[] { (short)-id });
                        Built++;
                    }
                    // The table skips a number (137 in .NET 10's WPF).
                    catch (TargetInvocationException e) when (e.InnerException is InvalidOperationException
                        && e.InnerException.TargetSite?.Name == "CreateKnownMember") { }
                    catch (Exception e) { failure ??= "entry " + id + ": " + (e.InnerException ?? e); }
                }
                Failure = failure;
            }
            catch (Exception e) { Failure = e.ToString(); }
        }
    }
}
