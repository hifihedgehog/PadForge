using System;
using System.Globalization;
using System.IO;

namespace PadForge.Common.Themes
{
    /// <summary>
    /// Where the pictures of profiles' game looks are kept (#591): one PNG
    /// per Steam game, named by the game's app id, in a GameLooks folder
    /// beside PadForge, where it keeps everything a user asks it to make, so
    /// PadForge, its settings and its looks move together.
    ///
    /// The Steam cache is a cache: it lets art go when it is full, and
    /// Settings can clear it. A look's picture is kept here so neither takes
    /// it away and PadForge never has to ask Steam for it again on its own.
    /// A profile's look names its game, so two profiles of one game share
    /// one picture. No member throws for a file that cannot be read or
    /// written: the look then shows its colors alone.
    /// </summary>
    internal sealed class GameLookStore
    {
        internal const string FolderName = "GameLooks";

        internal string Folder { get; }

        /// <param name="folder">The folder to keep pictures in. Null is
        /// PadForge's own, beside PadForge.</param>
        internal GameLookStore(string folder = null)
        {
            Folder = folder ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, FolderName);
        }

        internal string PathOf(int appId) => Path.Combine(Folder, appId.ToString(CultureInfo.InvariantCulture) + ".png");

        /// <summary>When the game's picture was written, or null when there is none.</summary>
        internal DateTime? Stamp(int appId)
        {
            if (appId <= 0) return null;
            try
            {
                var info = new FileInfo(PathOf(appId));
                return info.Exists ? info.LastWriteTimeUtc : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or System.Security.SecurityException)
            {
                return null;
            }
        }

        /// <summary>The game's picture as its file holds it, or null.</summary>
        internal byte[] Read(int appId)
        {
            if (appId <= 0) return null;
            try
            {
                var info = new FileInfo(PathOf(appId));
                if (!info.Exists || info.Length == 0 || info.Length > ThemeFile.MaxPictureBytes) return null;
                return File.ReadAllBytes(info.FullName);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or System.Security.SecurityException)
            {
                return null;
            }
        }

        /// <summary>Keeps the game's picture, whole or not at all: the bytes
        /// go to a file beside it first, which then takes its place.</summary>
        internal bool Write(int appId, byte[] png)
        {
            if (appId <= 0 || png == null || png.Length == 0 || png.Length > ThemeFile.MaxPictureBytes) return false;
            string path = PathOf(appId);
            string temp = path + ".tmp-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            try
            {
                Directory.CreateDirectory(Folder);
                File.WriteAllBytes(temp, png);
                File.Move(temp, path, overwrite: true);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or System.Security.SecurityException)
            {
                return false;
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }

        /// <summary>Lets the game's picture go: no profile wears its look any more.</summary>
        internal void Remove(int appId)
        {
            if (appId <= 0) return;
            try
            {
                string path = PathOf(appId);
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or System.Security.SecurityException)
            {
                // A file another program holds stays. It is a picture no profile reads.
            }
        }
    }
}
