using System;
using System.Security.Cryptography;
using System.Text;

namespace PadForge.Common.Input.KeyboardScreens
{
    /// <summary>
    /// One present keyboard screen (issue #519). Its row on the Devices page,
    /// its settings, its keys and the picture the host writes to it all go by
    /// <see cref="Id"/>: a monochrome screen by its USB IDs and Windows'
    /// container ID, the analog keyboards' grouping, a G19 by the instance ID
    /// of its screen interface, and Arx Control and the S.T.R.I.K.E. 7 by a
    /// fixed name, since each vendor library draws one applet on every screen
    /// it reaches.
    /// </summary>
    internal sealed record KeyboardScreenInfo(string Id, KeyboardScreenKind Kind, string Name, string Model)
    {
        public const string ArxId = "arx";
        public const string Strike7Id = "strike7";
        /// <summary>The most Screen Overlays a PC runs at once (#560).</summary>
        public const int MaxOverlays = 8;

        private const string OverlayIdPrefix = "overlay";

        /// <summary>The key a Screen Overlay's choices are kept under (#560), by its
        /// number, beside the keyboard screens'. An overlay is a window and no device,
        /// so nothing makes a row of it on the Devices page and it feeds no slot.</summary>
        public static Guid OverlayRow(int number) => RowGuidOf(OverlayIdPrefix + number.ToString(System.Globalization.CultureInfo.InvariantCulture));

        /// <summary>The overlay's number, or 0 for any other screen.</summary>
        public int OverlayNumber => Kind == KeyboardScreenKind.Overlay
            && int.TryParse(Id.AsSpan(OverlayIdPrefix.Length), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int number)
                ? number : 0;

        /// <summary>The row's identity, the same for the screen every time
        /// it is found.</summary>
        public Guid RowGuid => RowGuidOf(Id);

        public static Guid RowGuidOf(string id) => Md5Guid("pfkeyboardscreen|" + id);

        public static KeyboardScreenInfo Arx() => new(ArxId, KeyboardScreenKind.Arx, "Logitech Arx Control", "Arx Control");

        public static KeyboardScreenInfo Overlay(int number)
        {
            string text = number.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return new(OverlayIdPrefix + text, KeyboardScreenKind.Overlay, "Screen Overlay " + text, "Screen Overlay");
        }

        public static KeyboardScreenInfo Strike7() => new(Strike7Id, KeyboardScreenKind.Strike7, "Mad Catz S.T.R.I.K.E. 7 Screen", "S.T.R.I.K.E. 7");

        /// <summary>A monochrome screen's identity: its USB IDs and the
        /// container its collections share, or the collection's path when
        /// Windows names no container.</summary>
        public static KeyboardScreenInfo Monochrome(ushort pid, string container, string path)
            => new(string.IsNullOrEmpty(container)
                    ? "hid:" + (path ?? string.Empty).ToLowerInvariant()
                    : string.Format(System.Globalization.CultureInfo.InvariantCulture, "hid:{0:x4}:{1:x4}:{2}",
                        KeyboardScreenDevices.LogitechVid, pid, container.ToLowerInvariant()),
                KeyboardScreenKind.Monochrome, "Logitech " + KeyboardScreenDevices.NameOf(pid) + " Screen", KeyboardScreenDevices.NameOf(pid));

        public static KeyboardScreenInfo G19(string instanceId)
            => new("usb:" + (instanceId ?? string.Empty).ToLowerInvariant(), KeyboardScreenKind.Color, "Logitech G19 Screen", "G19");

        private static Guid Md5Guid(string identifier)
        {
            using var md5 = MD5.Create();
            return new Guid(md5.ComputeHash(Encoding.UTF8.GetBytes(identifier)));
        }
    }
}
