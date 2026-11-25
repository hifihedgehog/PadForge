using System;

namespace PadForge.ViewModels
{
    /// <summary>
    /// A Paste of text read before it ran (#600): a program's press of a
    /// Paste carries the text its question card read, and the paste lands
    /// that text, never a second read of the clipboard. A person's Paste
    /// carries none and reads the clipboard when it runs.
    /// </summary>
    public sealed class PasteTextEventArgs : EventArgs
    {
        public PasteTextEventArgs(string text) => Text = text;

        /// <summary>The text to paste, null when there was none.</summary>
        public string Text { get; }
    }
}
