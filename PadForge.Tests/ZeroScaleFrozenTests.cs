using System;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>A hidden 3D element draws on any test thread, since WPF's
    /// zero-scale transform is frozen before the tests start
    /// (<see cref="ZeroScaleFrozen"/>).</summary>
    public class ZeroScaleFrozenTests
    {
        [Fact]
        public void TheTransformIsFrozen()
        {
            var field = typeof(Visual3D).GetField(ZeroScaleFrozen.Field, BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(field);
            var zero = Assert.IsAssignableFrom<Freezable>(field.GetValue(null));
            Assert.True(zero.IsFrozen);
        }

        /// <summary>Two new threads in turn, each drawing a hidden element:
        /// neither made the transform, as the HelixViewport3D of a page test
        /// does on its own thread.</summary>
        [Fact]
        public void AHiddenElement3D_DrawsOnTwoNewThreadsInTurn()
        {
            for (int i = 0; i < 2; i++)
                RunSta(DrawAHiddenElement3D);
        }

        private static void DrawAHiddenElement3D()
        {
            var mesh = new MeshGeometry3D
            {
                Positions = new Point3DCollection { new Point3D(0, 0, 0), new Point3D(1, 0, 0), new Point3D(0, 1, 0) },
                TriangleIndices = new Int32Collection { 0, 1, 2 },
            };
            var hidden = new ModelUIElement3D
            {
                Model = new GeometryModel3D(mesh, new DiffuseMaterial(Brushes.Gray)),
                Visibility = Visibility.Hidden,
            };
            var viewport = new Viewport3D
            {
                Camera = new PerspectiveCamera(new Point3D(0, 0, 5), new Vector3D(0, 0, -1), new Vector3D(0, 1, 0), 45),
                Width = 64,
                Height = 64,
            };
            viewport.Children.Add(new ModelVisual3D { Content = new AmbientLight(Colors.White) });
            viewport.Children.Add(hidden);
            viewport.Measure(new Size(64, 64));
            viewport.Arrange(new Rect(0, 0, 64, 64));
            new RenderTargetBitmap(64, 64, 96, 96, PixelFormats.Pbgra32).Render(viewport);
        }

        private static void RunSta(Action body)
        {
            Exception error = null;
            var thread = new Thread(() =>
            {
                try { body(); }
                catch (Exception e) { error = e; }
                finally { Dispatcher.FromThread(Thread.CurrentThread)?.InvokeShutdown(); }
            })
            { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "the drawing thread did not finish");
            Assert.True(error == null, "drawing a hidden 3D element threw: " + error);
        }
    }
}
