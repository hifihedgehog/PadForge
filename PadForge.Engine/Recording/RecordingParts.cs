using System;
using System.Collections.Generic;

namespace PadForge.Engine.Recording
{
    /// <summary>
    /// The parts of a problem recording (#600): what What to Record lets the
    /// user leave out, and what the review shows one by one. Bits are
    /// append-only, since PadForge.xml keeps the parts left out as this
    /// number.
    /// </summary>
    [Flags]
    public enum RecordingParts
    {
        None = 0,
        /// <summary>The system, the devices' facts, the virtual controllers,
        /// the settings, and every setting changed while recording.</summary>
        SettingsAndDevices = 1 << 0,
        /// <summary>What each device reports, and what PadForge's readers say
        /// of it.</summary>
        Input = 1 << 1,
        /// <summary>What each virtual controller is handed, whether it went
        /// out, and what a game reads back.</summary>
        Output = 1 << 2,
        /// <summary>Rumble, lights and force effects, both ways.</summary>
        Feedback = 1 << 3,
        /// <summary>The engine's passes, stalls and late starts.</summary>
        Timing = 1 << 4,
        /// <summary>The window, the program in front, health lines and
        /// errors.</summary>
        WindowAndErrors = 1 << 5,
        /// <summary>What programs asked of PadForge (Automation's
        /// Activity).</summary>
        Activity = 1 << 6,
        /// <summary>PadForge's own diagnostics lines.</summary>
        DiagnosticsLines = 1 << 7,

        /// <summary>Every part a recording can hold.</summary>
        All = SettingsAndDevices | Input | Output | Feedback | Timing | WindowAndErrors | Activity | DiagnosticsLines,
    }

    /// <summary>What a part is called in a file, and which layers of the
    /// events it holds.</summary>
    public static class RecordingPartWords
    {
        /// <summary>The parts in the order the page and the review list
        /// them.</summary>
        public static readonly RecordingParts[] Order =
        {
            RecordingParts.SettingsAndDevices, RecordingParts.Input, RecordingParts.Output, RecordingParts.Feedback,
            RecordingParts.Timing, RecordingParts.WindowAndErrors, RecordingParts.Activity, RecordingParts.DiagnosticsLines,
        };

        /// <summary>A part's words in the file's Holds line. Fixed English,
        /// whatever the interface language.</summary>
        public static string Word(RecordingParts part) => part switch
        {
            RecordingParts.SettingsAndDevices => "settings and devices",
            RecordingParts.Input => "input",
            RecordingParts.Output => "output",
            RecordingParts.Feedback => "feedback",
            RecordingParts.Timing => "timing",
            RecordingParts.WindowAndErrors => "window and errors",
            RecordingParts.Activity => "activity",
            RecordingParts.DiagnosticsLines => "diagnostics lines",
            _ => string.Empty,
        };

        /// <summary>The Holds line's text after its prefix: the parts held,
        /// in order, or "only this header".</summary>
        public static string Holds(RecordingParts parts)
        {
            var words = new List<string>();
            foreach (var part in Order)
                if ((parts & part) != 0) words.Add(Word(part));
            return words.Count == 0 ? "only this header" : string.Join(", ", words);
        }

        /// <summary>Reads what <see cref="Holds"/> wrote. A file with no
        /// Holds line, which format 1 wrote, holds every part.</summary>
        public static RecordingParts ParseHolds(string text)
        {
            if (text == null) return RecordingParts.All;
            var parts = RecordingParts.None;
            foreach (string word in text.Split(", ", StringSplitOptions.RemoveEmptyEntries))
                foreach (var part in Order)
                    if (string.Equals(Word(part), word.Trim().TrimEnd('.'), StringComparison.Ordinal)) parts |= part;
            return parts;
        }

        /// <summary>The part an event line of <paramref name="layer"/>
        /// belongs to, or None for a marker, which every file keeps.</summary>
        public static RecordingParts PartOf(char layer) => layer switch
        {
            RecordingCodec.LayerInput or RecordingCodec.LayerReader => RecordingParts.Input,
            RecordingCodec.LayerOutput or RecordingCodec.LayerSent or RecordingCodec.LayerReadBack => RecordingParts.Output,
            RecordingCodec.LayerFeedback => RecordingParts.Feedback,
            RecordingCodec.LayerTiming => RecordingParts.Timing,
            RecordingCodec.LayerWindow or RecordingCodec.LayerError => RecordingParts.WindowAndErrors,
            RecordingCodec.LayerActivity => RecordingParts.Activity,
            RecordingCodec.LayerLine => RecordingParts.DiagnosticsLines,
            RecordingCodec.LayerSettingChange => RecordingParts.SettingsAndDevices,
            _ => RecordingParts.None,
        };
    }
}
