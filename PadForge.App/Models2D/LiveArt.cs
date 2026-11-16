using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PadForge.Common;
using PadForge.Views;

namespace PadForge.Models2D
{
    /// <summary>A controller's 2D art as a Home card shows it moving: the
    /// layers the controller page's 2D view draws, in its order
    /// (ControllerModel2DView.BuildCanvas), each made the size it has on the
    /// card's picture and set where it stands on it. At rest the layers make
    /// that picture (<see cref="Still"/>): the triggers behind the body, the
    /// body, the sticks and the printed marks. A card draws them as the page
    /// moves them (Controls.LiveArtStage): a stick's art shifts with the
    /// stick, a trigger's art fills with the pull, and a held button shows
    /// its pressed art.</summary>
    public sealed class LiveArt
    {
        /// <summary>How a layer takes what is held, as the page's view
        /// drives it: never, by a stick's tilt, by a trigger's pull, shown
        /// while its button is held, or a stick's click art, shown whole
        /// while clicked and faintly while the stick's top is touched.</summary>
        internal enum Kind : byte { Still, Stick, Trigger, Button, StickClick }

        /// <summary>One layer: what drives it, its button's bit
        /// (ControllerModelView.ButtonRoles), whether its control is on the
        /// left, its picture, and its place on the card's picture in the
        /// units of <see cref="Width"/>.</summary>
        internal readonly record struct Layer(Kind Kind, int Bit, bool Left, BitmapSource Picture, Rect Place);

        private LiveArt(BitmapSource still, Int32Rect cut, double width, double height, double stickTravel,
            Layer[] layers, ulong buttons)
        {
            Still = still;
            Cut = cut;
            Width = width;
            Height = height;
            StickTravel = stickTravel;
            Layers = layers;
            Buttons = buttons;
        }

        /// <summary>The card's picture: the layers at rest, cut to the
        /// controller, at the scale the stage's pictures are made at
        /// (StagePictures.Fit). Frozen.</summary>
        internal BitmapSource Still { get; }

        /// <summary>The piece of the art at rest that was kept, in the art's
        /// pixels.</summary>
        internal Int32Rect Cut { get; }

        /// <summary>The size of the card's picture in the units the layers'
        /// places are given in.</summary>
        internal double Width { get; }
        internal double Height { get; }

        /// <summary>How far a stick's art shifts at full tilt, in the same
        /// units: the art's own StickMaxTravel.</summary>
        internal double StickTravel { get; }

        /// <summary>The layers, in the order they are drawn.</summary>
        internal IReadOnlyList<Layer> Layers { get; }

        /// <summary>The buttons the art shows held, one bit for each role of
        /// ControllerModelView.ButtonRoles.</summary>
        internal ulong Buttons { get; }

        /// <summary>The art at a path made into the layers, or null when the
        /// path names no art PadForge ships or nothing is drawn in it. Call
        /// it on a thread with a dispatcher: the picture at rest is drawn
        /// with WPF. Everything made is frozen.</summary>
        internal static LiveArt From(string path)
        {
            string folder = StagePictures.FolderOf(path);
            if (folder.Length == 0) return null;
            var (_, width, height, overlays, travel) = StagePictures.LayoutOf(folder);
            BitmapSource body = EmbeddedBitmaps.Load(path);
            if (body == null || width <= 0 || height <= 0) return null;

            // The finish the path is the body of, for the art it swaps
            // (ControllerModel2DView.BuildCanvas, Resolve).
            Colorway2D finish = null;
            var (_, set) = Controller2DColorways.For(folder);
            if (set != null)
                foreach (var each in set)
                    if (string.Equals("2DModels/" + folder + "/" + each.BaseFile, path, StringComparison.OrdinalIgnoreCase))
                    {
                        finish = each;
                        break;
                    }
            string Resolve(string file) => finish != null && finish.Overrides.TryGetValue(file, out var swapped) ? swapped : file;

            // The page keeps one picture for each name, the last of the
            // name, and it is that one the page shows and hides.
            var last = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < overlays.Length; i++) last[overlays[i].TargetName] = i;
            var roles = ControllerModelView.ButtonRoles;
            int BitOf(string role)
            {
                for (int i = 0; i < roles.Count; i++)
                    if (roles[i] == role) return i;
                return -1;
            }

            // The page's order: the triggers' rest art behind the body, then
            // the body, then the rest in the table's order. A layer the page
            // never shows is left out.
            var drawn = new List<(Rect Place, Kind Kind, int Bit, bool Left, BitmapSource Art)>();
            foreach (var part in overlays)
                if (part.ElementType == OverlayElementType.TriggerBase)
                    Add(part, Kind.Still, -1, false);
            drawn.Add((new Rect(0, 0, width, height), Kind.Still, -1, false, body));
            for (int i = 0; i < overlays.Length; i++)
            {
                var part = overlays[i];
                if (part.ElementType == OverlayElementType.TriggerBase) continue;
                string name = part.TargetName;
                bool driven = last[name] == i;
                int bit = BitOf(name);
                if (driven && part.ElementType == OverlayElementType.StickRing && name is "LeftThumbRing" or "RightThumbRing")
                    Add(part, Kind.Stick, -1, name == "LeftThumbRing");
                else if (driven && part.ElementType == OverlayElementType.Trigger && name is "LeftTrigger" or "RightTrigger")
                    Add(part, Kind.Trigger, -1, name == "LeftTrigger");
                else if (driven && name is "LeftThumbButton" or "RightThumbButton" && bit >= 0)
                    Add(part, Kind.StickClick, bit, name == "LeftThumbButton");
                else if (driven && bit >= 0)
                    Add(part, Kind.Button, bit, false);
                else if (part.ElementType is OverlayElementType.StickRing or OverlayElementType.Decal)
                    Add(part, Kind.Still, -1, false);
            }
            void Add(OverlayElement part, Kind kind, int bit, bool left)
            {
                var art = EmbeddedBitmaps.Load("2DModels/" + folder + "/" + Resolve(part.ImageFile));
                if (art != null) drawn.Add((new Rect(part.X, part.Y, part.Width, part.Height), kind, bit, left, art));
            }

            // The piece of the art the layers at rest cover: each layer's
            // drawn pixels, where the layer stands. Drawing the art at its
            // own size to find it took the interface's thread 35 to 55 ms
            // an art, against 3 to 11 for the bare body.
            double left0 = double.MaxValue, top0 = double.MaxValue, right0 = double.MinValue, bottom0 = double.MinValue;
            foreach (var (at, kind, _, _, art) in drawn)
            {
                if (kind is not (Kind.Still or Kind.Stick)) continue;
                var (pixels, w, h) = PixelsOf(art);
                var box = StagePictures.Drawn(pixels, w, h);
                if (box.IsEmpty) continue;
                double sx = at.Width / w, sy = at.Height / h;
                left0 = Math.Min(left0, at.X + box.X * sx);
                top0 = Math.Min(top0, at.Y + box.Y * sy);
                right0 = Math.Max(right0, at.X + (box.X + box.Width) * sx);
                bottom0 = Math.Max(bottom0, at.Y + (box.Y + box.Height) * sy);
            }
            if (right0 <= left0 || bottom0 <= top0) return null;
            int cutX = (int)Math.Floor(left0), cutY = (int)Math.Floor(top0);
            var cut = new Int32Rect(cutX, cutY, (int)Math.Ceiling(right0) - cutX, (int)Math.Ceiling(bottom0) - cutY);

            // Each layer made the size it is on the picture, at the scale the
            // stage's pictures are made at (StagePictures.Fit), and set on
            // whole pixels, so it is copied onto the picture as it is.
            double scale = Math.Min(1.0, Math.Min(StagePictures.BoxWidth / (double)cut.Width, StagePictures.BoxHeight / (double)cut.Height));
            var layers = new Layer[drawn.Count];
            ulong buttons = 0;
            for (int i = 0; i < layers.Length; i++)
            {
                var (at, kind, bit, left, art) = drawn[i];
                var picture = Sized(art, at.Width * scale, at.Height * scale);
                var place = new Rect(Math.Round((at.X - cut.X) * scale), Math.Round((at.Y - cut.Y) * scale), picture.PixelWidth, picture.PixelHeight);
                layers[i] = new Layer(kind, bit, left, picture, place);
                if (bit >= 0) buttons |= 1UL << bit;
            }

            // The picture: the layers at rest.
            int stillWidth = Math.Max(1, (int)Math.Round(cut.Width * scale)), stillHeight = Math.Max(1, (int)Math.Round(cut.Height * scale));
            var rest = new DrawingVisual();
            using (var dc = rest.RenderOpen())
                foreach (var layer in layers)
                    if (layer.Kind is Kind.Still or Kind.Stick)
                        dc.DrawImage(layer.Picture, layer.Place);
            var still = new RenderTargetBitmap(stillWidth, stillHeight, 96, 96, PixelFormats.Pbgra32);
            still.Render(rest);
            still.Freeze();
            return new LiveArt(still, cut, stillWidth, stillHeight, travel * scale, layers, buttons);
        }

        /// <summary>A picture's pixels, premultiplied, with its size.</summary>
        private static (byte[] Pixels, int Width, int Height) PixelsOf(BitmapSource art)
        {
            BitmapSource pixels = art.Format == PixelFormats.Pbgra32 ? art : new FormatConvertedBitmap(art, PixelFormats.Pbgra32, null, 0);
            int w = pixels.PixelWidth, h = pixels.PixelHeight;
            var data = new byte[w * 4 * h];
            pixels.CopyPixels(data, w * 4, 0);
            return (data, w, h);
        }

        /// <summary>A picture made a size, in its own frozen pixels, so a card
        /// holds no larger copy than it shows.</summary>
        private static BitmapSource Sized(BitmapSource art, double width, double height)
        {
            int w = Math.Max(1, (int)Math.Round(width)), h = Math.Max(1, (int)Math.Round(height));
            BitmapSource pixels = art.Format == PixelFormats.Pbgra32 ? art : new FormatConvertedBitmap(art, PixelFormats.Pbgra32, null, 0);
            if (w != pixels.PixelWidth || h != pixels.PixelHeight)
                pixels = new TransformedBitmap(pixels, new ScaleTransform(w / (double)pixels.PixelWidth, h / (double)pixels.PixelHeight));
            var own = new byte[pixels.PixelWidth * 4 * pixels.PixelHeight];
            pixels.CopyPixels(own, pixels.PixelWidth * 4, 0);
            var made = BitmapSource.Create(pixels.PixelWidth, pixels.PixelHeight, 96, 96, PixelFormats.Pbgra32, null, own, pixels.PixelWidth * 4);
            made.Freeze();
            return made;
        }
    }
}
