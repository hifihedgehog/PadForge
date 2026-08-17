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
    /// The model (~40 MB, en-us small) lives under LocalAppData, never
    /// beside the exe, downloaded on first use and cached across sessions.
    /// Until it is ready, sessions fall back to SAPI so the feature keeps
    /// working during the one-time download.
    /// </summary>
    internal static class VoskModelStore
    {
        private const string ModelName = "vosk-model-small-en-us-0.15";
        private const string ModelUrl = "https://alphacephei.com/vosk/models/vosk-model-small-en-us-0.15.zip";

        private static readonly string Root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PadForge", "voice-models");

        private static Vosk.Model _model;
        private static int _state; // 0 absent, 1 downloading, 2 ready, 3 failed
        private static readonly object _lock = new();

        public static bool IsReady => Volatile.Read(ref _state) == 2;
        public static bool IsDownloading => Volatile.Read(ref _state) == 1;

        /// <summary>The loaded model, or null. Vosk models are shareable
        /// across recognizers; recognizer instances are not.</summary>
        public static Vosk.Model Model => IsReady ? _model : null;

        /// <summary>Loads the cached model if present, else starts the
        /// one-time background download. Safe to call every reconcile.</summary>
        public static void EnsureStarted()
        {
            if (Volatile.Read(ref _state) != 0) return;
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
                        _state = 2;
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
                new Thread(Download) { IsBackground = true, Name = "VoskModelDownload" }.Start();
            }
        }

        private static void Download()
        {
            try
            {
                Engine.SdlDiagLog.WriteLine("VOICE vosk model downloading (~40 MB, one time) to " + Root);
                Directory.CreateDirectory(Root);
                string zip = Path.Combine(Root, ModelName + ".zip.partial");
                using (var http = new System.Net.Http.HttpClient())
                {
                    http.Timeout = TimeSpan.FromMinutes(10);
                    using var resp = http.GetAsync(ModelUrl,
                        System.Net.Http.HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
                    resp.EnsureSuccessStatusCode();
                    long total = resp.Content.Headers.ContentLength ?? -1;
                    using var src = resp.Content.ReadAsStream();
                    using var dst = File.Create(zip);
                    var buf = new byte[81920];
                    long done = 0, nextLog = 0;
                    int n;
                    while ((n = src.Read(buf, 0, buf.Length)) > 0)
                    {
                        dst.Write(buf, 0, n);
                        done += n;
                        if (total > 0 && done >= nextLog)
                        {
                            Engine.SdlDiagLog.WriteLine($"VOICE vosk model download {done * 100 / total}%");
                            nextLog = done + total / 5;
                        }
                    }
                }
                string extractTo = Path.Combine(Root, ModelName + ".extract");
                try { Directory.Delete(extractTo, true); } catch { }
                System.IO.Compression.ZipFile.ExtractToDirectory(zip, extractTo);
                // The zip carries a single top-level folder named like the model.
                string inner = Directory.GetDirectories(extractTo).FirstOrDefault() ?? extractTo;
                string final = Path.Combine(Root, ModelName);
                try { Directory.Delete(final, true); } catch { }
                Directory.Move(inner, final);
                try { Directory.Delete(extractTo, true); } catch { }
                try { File.Delete(zip); } catch { }
                Vosk.Vosk.SetLogLevel(-1);
                _model = new Vosk.Model(final);
                Volatile.Write(ref _state, 2);
                Engine.SdlDiagLog.WriteLine("VOICE vosk model READY; sessions will rebuild onto it");
            }
            catch (Exception ex)
            {
                Volatile.Write(ref _state, 3);
                Engine.SdlDiagLog.WriteLine("VOICE vosk model download FAILED: " + ex.Message + " (SAPI fallback stays)");
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
            string grammar = "[" + string.Join(",",
                phrases.Select(p => "\"" + p.Replace("\"", "") + "\"").Append("\"[unk]\"")) + "]";
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
            catch { }
        }

        private static string ExtractJsonString(string json, string key)
        {
            int i = json.LastIndexOf("\"" + key + "\"", StringComparison.Ordinal);
            if (i < 0) return null;
            i = json.IndexOf(':', i);
            if (i < 0) return null;
            i = json.IndexOf('"', i);
            if (i < 0) return null;
            int j = json.IndexOf('"', i + 1);
            return j < 0 ? null : json.Substring(i + 1, j - i - 1);
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
