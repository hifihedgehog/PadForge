using System;
using System.IO;
using System.Linq;
using System.Threading;

namespace PadForge.Services
{
    /// <summary>
    /// The modern recognition engine for voice macros (issue #317), behind
    /// the same session surface SAPI uses. Chosen after field measurement
    /// closed the case on the in-box engine: SAPI's 2006-era model scored a
    /// meow at 0.94 against "hello" and fired on the Windows device-connect
    /// chime, because a closed grammar there has nowhere else to put audio.
    /// Vosk's phrase-list mode carries an explicit [unk] bucket, so
    /// non-phrase audio decodes as UNKNOWN instead of force-matching the
    /// nearest phrase.
    ///
    /// The model (en-us small) ships INSIDE the executable and is unpacked
    /// to a cache directory on first use. It is never downloaded: a download
    /// makes voice macros unusable on a machine with no internet, which is
    /// not a supported outcome for a feature that advertises offline
    /// recognition. Until the unpack finishes, sessions fall back to SAPI so
    /// the feature keeps working through the one-time cost.
    /// </summary>
    internal static class VoskModelStore
    {
        private const string ModelName = "vosk-model-small-en-us-0.15";
        private const string ModelResource = "PadForge.VoiceModels.vosk-model-small-en-us-0.15.zip";

        /// <summary>Where the embedded model is unpacked.
        ///
        /// <para>Vosk loads a model from a DIRECTORY, so the bytes have to
        /// reach the disk somewhere. Not beside the exe, where only
        /// PadForge.xml, crash.log and the opt-in diagnostics log belong, and
        /// 68 MB of model is emphatically not one of those. A cache under
        /// TEMP is the same place the driver installers stage their payloads,
        /// and it is re-creatable: delete it and the next launch unpacks it
        /// again from the copy inside the exe.</para></summary>
        private static readonly string Root = Path.Combine(
            Path.GetTempPath(), "PadForge", "voice-models");

        private static Vosk.Model _model;
        private static int _state; // 0 absent, 1 downloading, 2 ready, 3 failed
        private static readonly object _lock = new();

        // One transient network blip at first launch must not pin the SAPI
        // fallback for the whole process: a failed download re-arms after
        // this delay and the next EnsureStarted retries.
        private static long _retryAtTicks;

        public static bool IsReady => Volatile.Read(ref _state) == 2;
        /// <summary>True while the embedded model is being unpacked. Kept
        /// under the old name so callers reading "the model is not ready
        /// yet, stay on SAPI" need no change; nothing is downloaded.</summary>
        public static bool IsUnpacking => Volatile.Read(ref _state) == 1;

        /// <summary>The loaded model, or null. Vosk models are shareable
        /// across recognizers; recognizer instances are not.</summary>
        public static Vosk.Model Model => IsReady ? _model : null;

        /// <summary>Loads the cached model if present, else starts the
        /// one-time background download. Safe to call every reconcile.</summary>
        public static void EnsureStarted()
        {
            int st = Volatile.Read(ref _state);
            if (st == 3 && Environment.TickCount64 >= Interlocked.Read(ref _retryAtTicks))
            {
                lock (_lock) if (_state == 3) _state = 0;
                st = Volatile.Read(ref _state);
            }
            if (st != 0) return;
            lock (_lock)
            {
                if (_state != 0) return;
                string dir = Path.Combine(Root, ModelName);
                if (File.Exists(Path.Combine(dir, "am", "final.mdl"))
                    || File.Exists(Path.Combine(dir, "final.mdl"))
                    || Directory.Exists(Path.Combine(dir, "graph")))
                {
                    try
                    {
                        Vosk.Vosk.SetLogLevel(-1);
                        _model = new Vosk.Model(dir);
                        Volatile.Write(ref _state, 2);
                        Engine.SdlDiagLog.WriteLine("VOICE vosk model loaded from cache");
                        return;
                    }
                    catch (Exception ex)
                    {
                        Engine.SdlDiagLog.WriteLine("VOICE vosk cached model failed to load: " + ex.Message);
                        try { Directory.Delete(dir, true); } catch { }
                    }
                }
                _state = 1;
                new Thread(Unpack) { IsBackground = true, Name = "VoskModelUnpack" }.Start();
            }
        }

        private static void Unpack()
        {
            try
            {
                Engine.SdlDiagLog.WriteLine("VOICE vosk model unpacking (one time) to " + Root);
                Directory.CreateDirectory(Root);

                var asm = System.Reflection.Assembly.GetExecutingAssembly();
                using (var src = asm.GetManifestResourceStream(ModelResource))
                {
                    if (src == null)
                        throw new FileNotFoundException("embedded model missing: " + ModelResource);

                    string extractTo = Path.Combine(Root, ModelName + ".extract");
                    try { Directory.Delete(extractTo, true); } catch { }
                    using (var zip = new System.IO.Compression.ZipArchive(
                        src, System.IO.Compression.ZipArchiveMode.Read))
                    {
                        System.IO.Compression.ZipFileExtensions.ExtractToDirectory(zip, extractTo);
                    }
                    // The archive carries a single top-level folder named like
                    // the model, the same shape upstream's download had.
                    string inner = Directory.GetDirectories(extractTo).FirstOrDefault() ?? extractTo;
                    string final = Path.Combine(Root, ModelName);
                    try { Directory.Delete(final, true); } catch { }
                    Directory.Move(inner, final);
                    try { Directory.Delete(extractTo, true); } catch { }

                    Vosk.Vosk.SetLogLevel(-1);
                    _model = new Vosk.Model(final);
                }
                Volatile.Write(ref _state, 2);
                Engine.SdlDiagLog.WriteLine("VOICE vosk model READY; sessions will rebuild onto it");
            }
            catch (Exception ex)
            {
                // A failed unpack is a disk problem (no space, a locked cache
                // from another instance), not a network one, so the same
                // re-arm applies: SAPI keeps the feature alive and the next
                // EnsureStarted past the delay tries again.
                Interlocked.Exchange(ref _retryAtTicks, Environment.TickCount64 + 5 * 60_000);
                Volatile.Write(ref _state, 3);
                Engine.SdlDiagLog.WriteLine("VOICE vosk model unpack FAILED: " + ex.Message
                    + " (SAPI fallback stays; retry in 5 min)");
            }
        }
    }

    /// <summary>Sink surface shared by both engines: producers push 16 kHz
    /// 16-bit mono PCM, the engine behind it does the rest.</summary>
    internal interface IVoicePcmSink
    {
        void Write(ReadOnlySpan<byte> pcm16k);
        void Dispose();
    }

    /// <summary>One Vosk recognizer per microphone. Feeds are synchronous
    /// (Vosk decodes faster than realtime on this class of hardware), and a
    /// final result fires the same dispatch SAPI sessions use. Grammar is
    /// the registered phrases plus "[unk]": anything that does not decode as
    /// a phrase decodes as unknown and is logged as garbage, which is the
    /// property the in-box engine could not provide.</summary>
    internal sealed class VoskSession : IVoicePcmSink
    {
        private readonly object _lock = new();
        private Vosk.VoskRecognizer _rec;
        private readonly Action<string, float> _onFinal;   // (text, confidence)
        private readonly Action<string> _onGarbage;

        public VoskSession(Vosk.Model model, string[] phrases,
            Action<string, float> onFinal, Action<string> onGarbage)
        {
            _onFinal = onFinal;
            _onGarbage = onGarbage;
            string grammar = BuildGrammarJson(phrases);
            _rec = new Vosk.VoskRecognizer(model, 16000.0f, grammar);
            _rec.SetWords(true);
        }

        public void Write(ReadOnlySpan<byte> pcm16k)
        {
            byte[] buf = pcm16k.ToArray();
            string final = null;
            lock (_lock)
            {
                if (_rec == null) return;
                if (_rec.AcceptWaveform(buf, buf.Length))
                    final = _rec.Result();
            }
            if (final != null) HandleFinal(final);
        }

        private void HandleFinal(string json)
        {
            try
            {
                // {"result":[{"conf":0.98,...,"word":"hello"}],"text":"hello"}
                string text = ExtractJsonString(json, "text");
                if (string.IsNullOrWhiteSpace(text)) return;
                if (text.Contains("[unk]", StringComparison.Ordinal))
                {
                    _onGarbage(text);
                    return;
                }
                float conf = MinWordConf(json);
                _onFinal(text, conf);
            }
            catch (Exception ex)
            {
                // The dispatch chain (pulse stamp, UI event) must not die
                // silently: a throwing subscriber would otherwise eat
                // recognitions with no trace.
                Engine.SdlDiagLog.WriteLine("VOICE vosk dispatch error: " + ex.Message);
            }
        }

        /// <summary>The phrase-list grammar as Vosk's JSON array, phrases
        /// JSON-escaped (a raw backslash or quote would corrupt the array
        /// and kill every session build) plus the [unk] bucket. Internal
        /// and pure so the escaping is testable without the native lib.</summary>
        internal static string BuildGrammarJson(string[] phrases)
            => "[" + string.Join(",",
                phrases.Select(p => "\"" + p.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"").Append("\"[unk]\"")) + "]";

        internal static string ExtractJsonString(string json, string key)
        {
            // The KEY is a quoted name followed by a colon. Taking the last
            // raw occurrence alone mis-hits when the recognized VALUE is the
            // key's own spelling (the phrase "text" in {"text" : "text"}).
            string needle = "\"" + key + "\"";
            int at = -1;
            for (int i = json.IndexOf(needle, StringComparison.Ordinal); i >= 0;
                 i = json.IndexOf(needle, i + 1, StringComparison.Ordinal))
            {
                int k = i + needle.Length;
                while (k < json.Length && char.IsWhiteSpace(json[k])) k++;
                if (k < json.Length && json[k] == ':') at = i;
            }
            if (at < 0) return null;
            int c = json.IndexOf(':', at + needle.Length);
            int q = json.IndexOf('"', c + 1);
            if (q < 0) return null;
            int j = json.IndexOf('"', q + 1);
            return j < 0 ? null : json.Substring(q + 1, j - q - 1);
        }

        private static float MinWordConf(string json)
        {
            float min = 1f;
            int idx = 0;
            bool any = false;
            while ((idx = json.IndexOf("\"conf\"", idx, StringComparison.Ordinal)) >= 0)
            {
                int c = json.IndexOf(':', idx);
                int e = c;
                while (++e < json.Length && (char.IsDigit(json[e]) || json[e] == '.' || json[e] == ' ')) { }
                if (float.TryParse(json.AsSpan(c + 1, e - c - 1).Trim(),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out float v))
                { any = true; if (v < min) min = v; }
                idx = e;
            }
            return any ? min : 1f;
        }

        public void Dispose()
        {
            lock (_lock)
            {
                try { _rec?.Dispose(); } catch { }
                _rec = null;
            }
        }
    }
}
