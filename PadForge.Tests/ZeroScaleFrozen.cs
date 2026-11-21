using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Windows;
using System.Windows.Media.Media3D;
using System.Windows.Threading;

namespace PadForge.Tests
{
    /// <summary>
    /// Freezes the transform WPF draws every hidden 3D element with, before any test runs.
    /// </summary>
    /// <remarks>
    /// WPF draws a UIElement3D whose Visibility is not Visible under one static
    /// ScaleTransform3D, Visual3D._zeroScale (dotnet/wpf, Media3D/Visual3D.cs), which it makes
    /// unfrozen on the first thread that touches Visual3D. A collapsed Viewport3D still draws, at
    /// opacity 0 (UIElement.ensureInvisible), and HelixToolkit's view cube hides its edges and
    /// corners (ViewCubeVisual3D.EnableDisableEdgeClicks), so every HelixViewport3D draws that
    /// transform. PadForge builds its 3D views on its one interface thread, with the main window,
    /// before its stills worker starts. The test host builds a window on a thread of each test,
    /// and a test that drew a 3D view on any other thread than the first threw "a different
    /// thread owns it" inside WPF's render, which ended the test host (2026-11-21: 21,781 of the
    /// suite's tests ran). Frozen, the transform belongs to no thread. It is made and frozen on a
    /// thread of its own, since Freeze asks for the thread that made it.
    /// </remarks>
    internal static class ZeroScaleFrozen
    {
        internal const string Field = "_zeroScale";

#pragma warning disable CA2255 // Only the test host loads this assembly.
        [ModuleInitializer]
        internal static void Run()
        {
            var thread = new Thread(() =>
            {
                try
                {
                    if (typeof(Visual3D).GetField(Field, BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null)
                            is Freezable zero && zero.CanFreeze)
                        zero.Freeze();
                }
                finally
                {
                    Dispatcher.FromThread(Thread.CurrentThread)?.InvokeShutdown();
                }
            })
            { IsBackground = true, Name = "Zero scale" };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
        }
#pragma warning restore CA2255
    }
}
