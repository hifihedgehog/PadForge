using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Models2D;
using PadForge.Models3D;

namespace PadForge.Common
{
    /// <summary>The picture of a controller on its Home card's stage (#574,
    /// the world-class pass): what the controller's own page shows for its
    /// preset, in the finish chosen on that page, with a gray copy for a
    /// controller that is not forging. While the page shows the 3D view the
    /// picture is a still of the 3D model, and while it shows the 2D view
    /// it is the 2D art.
    ///
    /// <para>The rule for which controllers have a picture is the page's:
    /// an Xbox and a PlayStation controller always have a body, and where
    /// their preset is unknown they have their family's oldest one, as the
    /// page draws it. A Nintendo and an Extended controller have one only
    /// when PadForge ships that controller's own art, since another
    /// family's body under their name would be wrong. A Keyboard + Mouse, a
    /// MIDI and a VR controller have none, and the card shows the type's
    /// symbol.</para></summary>
    internal static class StagePictures
    {
        /// <summary>The art's path for a preset of a type, or empty when the
        /// type has no picture. <paramref name="finishOf"/> answers the
        /// finish chosen for a family of art, or null for none.</summary>
        internal static string PathFor(string profileId, VirtualControllerType type, Func<string, string> finishOf)
        {
            if (!HasBody(profileId, type)) return string.Empty;
            var (folder, _) = HMaestroProfileCatalog.ResolveAssetFolders(profileId, type);

            var (family, set) = Controller2DColorways.For(folder);
            if (set == null || set.Length == 0) return BasePath(folder);
            var chosen = set[0];
            string wanted = family != null ? finishOf?.Invoke(family) : null;
            if (wanted != null)
                foreach (var finish in set)
                    if (finish.Id == wanted) { chosen = finish; break; }
            return s_paths.GetOrAdd((folder, chosen.BaseFile), static key => "2DModels/" + key.Item1 + "/" + key.Item2);
        }

        private static readonly ConcurrentDictionary<(string, string), string> s_paths = new();

        /// <summary>The picture of a device on the Devices page: the art
        /// folder and the 3D model of the first preset in the catalog that
        /// has the device's own vendor and product ids and art of its own.
        /// Nothing for a device no such preset names, since another
        /// controller's body under its name would be wrong. A preset whose
        /// ids cannot be read names no device.</summary>
        internal static (string Folder, string Family) ForDevice(ushort vendorId, ushort productId)
        {
            if (vendorId == 0 && productId == 0) return (null, null);
            foreach (var preset in HMaestroProfileCatalog.AllProfiles)
            {
                try
                {
                    if (preset.VendorId != vendorId || preset.ProductId != productId) continue;
                }
                catch (Exception error) when (error is FormatException or OverflowException or ArgumentException)
                {
                    continue;
                }
                var (folder, family) = HMaestroProfileCatalog.ResolveAssetFolders(preset.Id, VirtualControllerType.Xbox, out bool own);
                if (own) return (folder, family);
            }
            return (null, null);
        }

        /// <summary>The 2D art of a folder in its first finish.</summary>
        internal static string FirstPath(string folder)
        {
            var (_, set) = Controller2DColorways.For(folder);
            return set == null || set.Length == 0
                ? BasePath(folder)
                : s_paths.GetOrAdd((folder, set[0].BaseFile), static key => "2DModels/" + key.Item1 + "/" + key.Item2);
        }

        /// <summary>The first finish of a family's 3D model, or null for a
        /// family of one finish.</summary>
        internal static string FirstFinish(string family) => ControllerStill.FinishFor(family, null);

        /// <summary>Whether a controller of this preset and type has a body
        /// to show: the rule in this class's summary.</summary>
        internal static bool HasBody(string profileId, VirtualControllerType type)
        {
            if (type is VirtualControllerType.Xbox or VirtualControllerType.PlayStation) return true;
            if (type is not (VirtualControllerType.Nintendo or VirtualControllerType.Extended)) return false;
            return HMaestroProfileCatalog.HasDedicatedArt(profileId, type);
        }

        /// <summary>The 3D model a preset of a type is drawn with, and the
        /// finish it takes: the family's own name for the choice
        /// <paramref name="finishOf"/> answers, or its first. A null family
        /// when the type has no picture.</summary>
        internal static (string Family, string Finish) StillFor(string profileId, VirtualControllerType type, Func<string, string> finishOf)
        {
            if (!HasBody(profileId, type)) return (null, null);
            var (_, family) = HMaestroProfileCatalog.ResolveAssetFolders(profileId, type);
            return (family, ControllerStill.FinishFor(family, finishOf?.Invoke(family)));
        }

        private static readonly ConcurrentDictionary<(string, string), (ImageSource Color, ImageSource Gray)> s_stills = new();

        /// <summary>What draws a model's still. A test puts its own here.</summary>
        internal static Func<string, string, BitmapSource> DrawStill = ControllerStill.Render;

        /// <summary>The still of a family's model in a finish, as the stage
        /// shows it, when it has been made. A model that could not be drawn
        /// has been made too, as nothing, and the stage keeps its 2D art.</summary>
        internal static bool TryGetStill(string family, string finish, out ImageSource color, out ImageSource gray)
        {
            bool made = s_stills.TryGetValue((family, finish ?? string.Empty), out var still);
            color = still.Color;
            gray = still.Gray;
            return made;
        }

        /// <summary>Makes a model's still: draws the model, cuts the
        /// picture to the controller, fits it to the stage and makes its
        /// gray copy, once for each model and finish. A model takes up to
        /// a second and a half to draw, so the interface asks through
        /// <see cref="MakeStillOnAWorker"/>.</summary>
        internal static void MakeStill(string family, string finish)
        {
            var key = (family, finish ?? string.Empty);
            if (s_stills.ContainsKey(key)) return;
            BitmapSource fit = null;
            try
            {
                fit = Fit(DrawStill(family, finish));
            }
            catch (Exception error)
            {
                System.Diagnostics.Debug.WriteLine("[StagePictures] no still of " + family + ": " + error.Message);
            }
            s_stills[key] = (fit, MakeGray(fit));
        }

        /// <summary>Makes a model's still on a thread of its own and then
        /// tells <paramref name="tell"/>'s thread, so the interface never
        /// waits for the drawing. Everything the still is made of belongs
        /// to that thread or is frozen: a model reads its meshes and its
        /// textures from the program itself, and what it reads of the
        /// theme is a color and a frozen brush. The still is frozen, so any
        /// thread may show it.</summary>
        internal static void MakeStillOnAWorker(string family, string finish, Dispatcher tell, Action made)
        {
            var worker = new Thread(() =>
            {
                try
                {
                    MakeStill(family, finish);
                }
                finally
                {
                    // The drawing gave this thread a dispatcher, which
                    // ends with it.
                    Dispatcher.CurrentDispatcher.InvokeShutdown();
                    tell.BeginInvoke(DispatcherPriority.Background, made);
                }
            })
            { IsBackground = true, Name = "PadForge stills", Priority = ThreadPriority.BelowNormal };
            worker.SetApartmentState(ApartmentState.STA);
            worker.Start();
        }

        /// <summary>Forgets every still, for a test.</summary>
        internal static void ForgetStills() => s_stills.Clear();

        /// <summary>The base art of a folder with one finish, as the 2D
        /// view's own table has it (ControllerModel2DView.BuildCanvas).</summary>
        internal static string BasePath(string folder) => folder switch
        {
            "DS3" => DualShock3Layout.BasePath,
            "DS4" => DS4Layout.BasePath,
            "DualSense" => DualSenseLayout.BasePath,
            "DUALSENSEEDGE" => DualSenseEdgeLayout.BasePath,
            "XBOXONE" => XboxOneSLayout.BasePath,
            "XBOXSERIES" => XboxSeriesXLayout.BasePath,
            "SWITCHPRO" => SwitchProLayout.BasePath,
            "SWITCH2PRO" => Switch2ProLayout.BasePath,
            "STEAMDECK" => SteamDeckLayout.BasePath,
            "STEAMCONTROLLER" => SteamControllerLayout.BasePath,
            "STEAMCONTROLLER2" => SteamController2Layout.BasePath,
            _ => Xbox360Layout.BasePath,
        };

        /// <summary>The most room a stage gives a picture, at twice its
        /// size on screen, so the copy stays sharp on a dense display.</summary>
        internal const int BoxWidth = 352, BoxHeight = 232;

        /// <summary>The art at a path as the stage shows it, made once: cut
        /// to the controller and no larger than the stage needs. Null when
        /// there is none.</summary>
        internal static ImageSource Color(string path)
            => string.IsNullOrEmpty(path) ? null : s_color.GetOrAdd(path, static p => Fit(Views.EmbeddedBitmaps.Load(p)));

        /// <summary>The gray copy of that picture, made once.</summary>
        internal static ImageSource Gray(string path)
            => string.IsNullOrEmpty(path) ? null : s_gray.GetOrAdd(path, static p => MakeGray(Color(p) as BitmapSource));

        private static readonly ConcurrentDictionary<string, ImageSource> s_color = new(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, ImageSource> s_gray = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>A picture cut to what is drawn in it and made to fit
        /// the stage's box. The art is some 1500 pixels wide with clear
        /// room around the controller, and a stage is 204: a copy of its
        /// own keeps the card from holding and rescaling the large one.</summary>
        internal static BitmapSource Fit(BitmapSource source)
        {
            if (source == null) return null;
            BitmapSource pixels = source.Format == PixelFormats.Pbgra32
                ? source
                : new FormatConvertedBitmap(source, PixelFormats.Pbgra32, null, 0);
            int width = pixels.PixelWidth, height = pixels.PixelHeight, stride = width * 4;
            var data = new byte[stride * height];
            pixels.CopyPixels(data, stride, 0);
            var drawn = Drawn(data, width, height);
            if (drawn.IsEmpty) return null;

            BitmapSource cut = new CroppedBitmap(pixels, drawn);
            double scale = Math.Min(1.0, Math.Min(BoxWidth / (double)drawn.Width, BoxHeight / (double)drawn.Height));
            if (scale < 1.0) cut = new TransformedBitmap(cut, new ScaleTransform(scale, scale));
            // A copy of its own pixels, so the large picture is not kept
            // alive behind it.
            int w = cut.PixelWidth, h = cut.PixelHeight;
            var own = new byte[w * 4 * h];
            cut.CopyPixels(own, w * 4, 0);
            var made = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, own, w * 4);
            made.Freeze();
            return made;
        }

        /// <summary>The box around every pixel that is not clear, or empty
        /// when all of them are.</summary>
        internal static System.Windows.Int32Rect Drawn(byte[] data, int width, int height)
        {
            int left = width, top = height, right = -1, bottom = -1;
            for (int y = 0; y < height; y++)
            {
                int row = y * width * 4;
                for (int x = 0; x < width; x++)
                {
                    if (data[row + x * 4 + 3] <= 8) continue;
                    if (x < left) left = x;
                    if (x > right) right = x;
                    if (y < top) top = y;
                    bottom = y;
                }
            }
            return right < 0 ? System.Windows.Int32Rect.Empty : new System.Windows.Int32Rect(left, top, right - left + 1, bottom - top + 1);
        }

        /// <summary>A picture with its colors taken out and its shape kept:
        /// each pixel's three colors become its brightness, and how clear
        /// the pixel is stays as it was.</summary>
        internal static BitmapSource MakeGray(BitmapSource source)
        {
            if (source == null) return null;
            BitmapSource pixels = source.Format == PixelFormats.Pbgra32
                ? source
                : new FormatConvertedBitmap(source, PixelFormats.Pbgra32, null, 0);
            int width = pixels.PixelWidth, height = pixels.PixelHeight, stride = width * 4;
            var data = new byte[stride * height];
            pixels.CopyPixels(data, stride, 0);
            for (int i = 0; i < data.Length; i += 4)
            {
                // Blue, green, red, each already scaled by the pixel's
                // opacity, so their weighted sum is the gray scaled the
                // same way. The weights are the television ones, in 256ths.
                byte gray = (byte)((data[i] * 29 + data[i + 1] * 150 + data[i + 2] * 77 + 128) >> 8);
                data[i] = data[i + 1] = data[i + 2] = gray;
            }
            var made = BitmapSource.Create(width, height, source.DpiX, source.DpiY, PixelFormats.Pbgra32, null, data, stride);
            made.Freeze();
            return made;
        }
    }
}
