using System;

namespace PadForge.Common.Input
{
    /// <summary>
    /// Where Update Now downloads the controller database from (#600 item
    /// 49, Download From): the community's own file on GitHub unless the
    /// user sets another address under the Controller Database row's More.
    /// Only the user sets it: the line and its dialog are theirs alone
    /// (Reach.Class UsersOwn), so no program, assistant or script chooses
    /// the source.
    ///
    /// <para>An address is taken only over HTTPS on the default port, with
    /// a host and no user name, password or fragment, and is kept in the one
    /// form <see cref="TryRead"/> gives, so whether it is the community's is
    /// one comparison of text. From any other address a download keeps new
    /// rows only (<see cref="ControllerDatabaseSection.FromDownload"/>): no
    /// date in a file from another host tells a newer row from an older
    /// one, so a changed row from there could roll back a row this release
    /// carries.</para>
    /// </summary>
    internal static class ControllerDatabaseAddress
    {
        /// <summary>The community's own file, at its master branch.</summary>
        internal const string Community =
            "https://raw.githubusercontent.com/mdqinc/SDL_GameControllerDB/master/gamecontrollerdb.txt";

        /// <summary>The longest address taken.</summary>
        internal const int MaxLength = 2048;

        /// <summary>Reads an address as the user typed it or the settings
        /// file holds it. False for anything but an absolute HTTPS address on
        /// the default port that names a host, with no user name, password or
        /// fragment, of at most <see cref="MaxLength"/> characters.
        /// <paramref name="address"/> is then its one form: the host in
        /// lower case, the default port left out.</summary>
        internal static bool TryRead(string text, out string address)
        {
            address = null;
            string trimmed = text?.Trim();
            if (string.IsNullOrEmpty(trimmed) || trimmed.Length > MaxLength) return false;
            if (!Uri.TryCreate(trimmed, UriKind.Absolute, out Uri uri)) return false;
            if (uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort || string.IsNullOrEmpty(uri.IdnHost)
                || uri.UserInfo.Length > 0 || uri.Fragment.Length > 0)
                return false;
            string form = uri.AbsoluteUri;
            if (form.Length > MaxLength) return false;
            address = form;
            return true;
        }

        /// <summary>Whether an address is the community's own file.</summary>
        internal static bool IsCommunity(string address) => string.Equals(address, Community, StringComparison.Ordinal);

        /// <summary>The host an address names, as the status line says it.</summary>
        internal static string HostOf(string address) =>
            Uri.TryCreate(address, UriKind.Absolute, out Uri uri) ? uri.Host : address ?? string.Empty;
    }
}
