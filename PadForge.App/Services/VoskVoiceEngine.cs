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
        internal const string ModelName = "vosk-model-small-en-us-0.15";
        private const string ModelResource = "PadForge.VoiceModels.vosk-model-small-en-us-0.15.zipbr";

        /// <summary>Where the embedded model is unpacked.
        ///
        /// <para>Vosk loads a model from a directory, so the bytes have to
        /// reach the disk somewhere, and not beside the exe, where only
        /// PadForge.xml and crash.log belong. A cache under TEMP is the same
        /// place the driver installers stage their payloads, and it is
        /// re-creatable: delete it and the next use unpacks it again from the
        /// copy inside the exe.</para></summary>
        private static string DefaultRoot => Path.Combine(Path.GetTempPath(), "PadForge", "voice-models");
        private static string _root;
        private static string Root
        {
            get => _root ??= DefaultRoot;
            set => _root = value;
        }

        private static Vosk.Model _model;
        // 0 absent, 1 unpacking, 2 ready, 3 failed and retried after a delay,
        // 4 unusable in this process and never retried
        private static int _state;
        private static readonly object _lock = new();

        // One failed unpack at first launch (a full disk, a temp folder the
        // process cannot write) must not pin the SAPI fallback for the whole
        // process: a failure re-arms after this delay and the next
        // EnsureStarted retries.
        private static long _retryAtTicks;

        public static bool IsReady => Volatile.Read(ref _state) == 2;
        /// <summary>True while the embedded model is being unpacked. Kept
        /// under the old name so callers reading "the model is not ready
        /// yet, stay on SAPI" need no change; nothing is downloaded.</summary>
        public static bool IsUnpacking => Volatile.Read(ref _state) == 1;

        /// <summary>The loaded model, or null. Vosk models are shareable
        /// across recognizers; recognizer instances are not.</summary>
        public static Vosk.Model Model => IsReady ? _model : null;

        /// <summary>True once a load showed that Vosk cannot be used in this
        /// process at all: libvosk would not load, or the binding no longer
        /// has the field the null-model check reads. Nothing retries after
        /// that, because nothing about it changes while the process lives,
        /// and the cache stays, because nothing is wrong with the model.</summary>
        public static bool IsUnusable => Volatile.Read(ref _state) == 4;

        /// <summary>How one attempt to load a model ended.</summary>
        internal enum ModelLoad
        {
            /// <summary>A model libvosk accepted.</summary>
            Loaded,
            /// <summary>libvosk ran and refused the model. The cache is the
            /// suspect, so it is deleted and unpacked again.</summary>
            BadModel,
            /// <summary>Vosk cannot be used in this process, whatever the
            /// model is.</summary>
            Unusable,
        }

        // Seams, so every outcome of a load can be reached in a test with no
        // model on disk and no library to call.
        internal static Func<string, Vosk.Model> CreateModel = DefaultCreateModel;
        internal static Func<Vosk.Model, IntPtr?> ReadNativeHandle = NativeHandleOf;
        internal static Action StartUnpack = DefaultStartUnpack;

        private static Vosk.Model DefaultCreateModel(string dir)
        {
            // The first call into libvosk, so it sits inside the load that is
            // being judged and a library that will not load is caught there.
            Vosk.Vosk.SetLogLevel(-1);
            return new Vosk.Model(dir);
        }

        private static void DefaultStartUnpack()
            => new Thread(Unpack) { IsBackground = true, Name = "VoskModelUnpack" }.Start();

        /// <summary>
        /// Loads a model and says how that went.
        ///
        /// <para>Two outcomes used to be missed. libvosk reports a model it
        /// cannot read by returning null (vosk_api.cc: vosk_model_new catches
        /// everything and returns nullptr), and the managed binding wraps
        /// that null in a Model without a word. The store marked it ready,
        /// and the first recognizer built on it dereferenced the null in
        /// native code, which ends the process. A temp cleaner that removes
        /// the model's files and leaves its folders produces exactly that
        /// cache. So the native handle is read, and a null one is a bad
        /// model.</para>
        ///
        /// <para>The other is a library that will not load. That says nothing
        /// about the model, so deleting the cache and unpacking 35 MB again
        /// cannot help, and it did that every five minutes. The binding's
        /// P/Invoke class has an empty static constructor, so these three
        /// exceptions arrive bare, with nothing wrapped around them.</para>
        /// </summary>
        internal static ModelLoad TryLoad(string dir, out Vosk.Model model, out string why)
        {
            model = null;
            why = null;
            Vosk.Model made;
            try
            {
                made = CreateModel(dir);
            }
            catch (Exception ex) when (ex is DllNotFoundException
                                    || ex is BadImageFormatException
                                    || ex is EntryPointNotFoundException)
            {
                why = ex.GetType().Name + ": " + ex.Message;
                return ModelLoad.Unusable;
            }
            catch (Exception ex)
            {
                why = ex.Message;
                return ModelLoad.BadModel;
            }

            IntPtr? native = ReadNativeHandle(made);
            if (native == null)
            {
                // The check cannot be made, so the model cannot be trusted,
                // and a model that might be null is never published.
                try { made?.Dispose(); } catch { }
                why = "this Vosk binding has no handle field to check the model by";
                return ModelLoad.Unusable;
            }
            if (native.Value == IntPtr.Zero)
            {
                // Safe on a null handle: the binding skips the native free
                // when the handle is zero.
                try { made.Dispose(); } catch { }
                why = "libvosk could not read the model";
                return ModelLoad.BadModel;
            }
            model = made;
            return ModelLoad.Loaded;
        }

        /// <summary>The native pointer inside a Vosk.Model, or null when the
        /// binding no longer keeps it where this looks. The binding has no
        /// accessor for it, so the private field is read. VoskModelStoreTests
        /// pins that field's name and type against the referenced package, so
        /// a package that moves it turns a test red before it ships.</summary>
        internal static IntPtr? NativeHandleOf(Vosk.Model model)
        {
            try
            {
                if (model == null) return null;
                var field = typeof(Vosk.Model).GetField("handle",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                if (field == null || field.FieldType != typeof(System.Runtime.InteropServices.HandleRef))
                    return null;
                return ((System.Runtime.InteropServices.HandleRef)field.GetValue(model)).Handle;
            }
            catch { return null; }
        }

        private static void LatchUnusable(string why)
        {
            Volatile.Write(ref _state, 4);
            Engine.SdlDiagLog.WriteLine("VOICE vosk cannot be used in this process: " + why
                + ". Voice macros stay on SAPI and the model cache is left alone");
        }

        /// <summary>For tests: a cache root of their own, a clean state, and
        /// the real seams back. Null restores the real root.</summary>
        internal static void ResetForTests(string root)
        {
            lock (_lock)
            {
                Root = root ?? DefaultRoot;
                _model = null;
                Volatile.Write(ref _state, 0);
                Interlocked.Exchange(ref _retryAtTicks, 0);
                CreateModel = DefaultCreateModel;
                ReadNativeHandle = NativeHandleOf;
                StartUnpack = DefaultStartUnpack;
            }
        }

        /// <summary>Loads the unpacked model if present, else starts the
        /// one-time background unpack of the embedded one. Safe to call
        /// every reconcile.</summary>
        public static void EnsureStarted()
        {
            // libvosk exists for x64 and ARM64 processes. In any other, the
            // store never starts, IsReady stays false, and VoiceMacroService
            // keeps every session on its SAPI fallback.
            //
            // This return once did a second job. Left to run in a process
            // with no libvosk, the cached-model branch below caught the
            // DllNotFoundException from the first Vosk call, DELETED the
            // cached model as though it were corrupt, unpacked all 35 MB
            // again, failed the same way, and repeated on every retry for as
            // long as the app was open. TryLoad closes that loop itself now,
            // for every architecture: a library that will not load is told
            // apart from a model that will not, and it switches the store
            // off for the process with the cache left alone.
            if (!Engine.PlatformSupport.VoskAvailable) return;

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
                // No protected folder to unpack into: voice macros stay on
                // SAPI, as when the model cannot be used (#600).
                if (Root == null)
                {
                    LatchUnusable("the protected folder for the voice model could not be made");
                    return;
                }
                string dir = Path.Combine(Root, ModelName);
                if (File.Exists(Path.Combine(dir, "am", "final.mdl"))
                    || File.Exists(Path.Combine(dir, "final.mdl"))
                    || Directory.Exists(Path.Combine(dir, "graph")))
                {
                    switch (TryLoad(dir, out Vosk.Model cached, out string why))
                    {
                        case ModelLoad.Loaded:
                            _model = cached;
                            Volatile.Write(ref _state, 2);
                            Engine.SdlDiagLog.WriteLine("VOICE vosk model loaded from cache");
                            return;
                        case ModelLoad.Unusable:
                            LatchUnusable(why);
                            return;
                        default:
                            Engine.SdlDiagLog.WriteLine("VOICE vosk cached model failed to load: " + why);
                            try { Directory.Delete(dir, true); } catch { }
                            break;
                    }
                }
                _state = 1;
                StartUnpack();
            }
        }

        /// <summary>Loads the model that was just unpacked and publishes the
        /// outcome. A model that will not load is thrown to Unpack's handler,
        /// which gives it the five-minute retry a failed unpack already gets.</summary>
        internal static void CompleteUnpack(string final)
        {
            switch (TryLoad(final, out Vosk.Model unpacked, out string why))
            {
                case ModelLoad.Loaded:
                    _model = unpacked;
                    Volatile.Write(ref _state, 2);
                    Engine.SdlDiagLog.WriteLine("VOICE vosk model READY, sessions will rebuild onto it");
                    break;
                case ModelLoad.Unusable:
                    LatchUnusable(why);
                    break;
                default:
                    throw new InvalidDataException("the unpacked model did not load: " + why);
            }
        }

        /// <summary>For tests: the unpack on the caller's thread, so a test
        /// that runs the real thing owns its end and no worker outlives it.</summary>
        internal static void UnpackNow() => Unpack();

        /// <summary>Moves the unpacked model folder to where the model is
        /// loaded from, and tries again while Windows refuses.
        ///
        /// <para>Windows will not rename a folder while any file inside it
        /// is open, however that file was opened, and a file the unpack has
        /// just written is often open for a moment in another program: a
        /// virus scanner, the search indexer, a sync client. The refusal
        /// arrives as an IOException carrying 0x80070005 (dotnet/runtime,
        /// FileSystem.Windows.cs, MoveDirectory). Taken as final, it failed
        /// the whole unpack and left voice macros on SAPI until the
        /// five-minute retry. The tries and waits are the ones the .NET SDK
        /// gives the same move of a staged folder
        /// (FileAccessRetrier.RetryOnMoveAccessFailure): ten tries, waits from
        /// 10 ms doubling, about five seconds before the refusal stands and
        /// goes to Unpack's handler.</para></summary>
        internal static void MoveIntoPlace(string from, string to, Action<int> sleep)
        {
            const int AccessDenied = unchecked((int)0x80070005);
            int wait = 10;
            for (int tries = 1; ; tries++)
            {
                try
                {
                    Directory.Move(from, to);
                    return;
                }
                catch (IOException ex) when (ex.HResult == AccessDenied && tries < 10)
                {
                    sleep(wait);
                    wait *= 2;
                }
            }
        }

        private static void Unpack()
        {
            // Read once. Every path below hangs off this one value, so an
            // unpack that is under way cannot be pointed at another folder.
            string root = Root;
            try
            {
                Engine.SdlDiagLog.WriteLine("VOICE vosk model unpacking (one time) to " + root);
                Directory.CreateDirectory(root);

                var asm = System.Reflection.Assembly.GetExecutingAssembly();
                using (var src = asm.GetManifestResourceStream(ModelResource))
                {
                    if (src == null)
                        throw new FileNotFoundException("embedded model missing: " + ModelResource);

                    string extractTo = Path.Combine(root, ModelName + ".extract");
                    try { Directory.Delete(extractTo, true); } catch { }

                    // The model is packed: an archive whose members are stored
                    // rather than deflated, compressed as a whole, which is
                    // 4 MB smaller than the archive compressing its own
                    // members. Unpacking gives the archive back. It goes to a
                    // file rather than memory because it expands to 68 MB and
                    // ZipArchive has to seek around it.
                    string staged = Path.Combine(root, ModelName + ".zip");
                    try
                    {
                        using (var packed = new System.IO.Compression.BrotliStream(
                                   src, System.IO.Compression.CompressionMode.Decompress))
                        using (var file = File.Create(staged))
                            packed.CopyTo(file);

                        using (var zip = System.IO.Compression.ZipFile.OpenRead(staged))
                            System.IO.Compression.ZipFileExtensions.ExtractToDirectory(zip, extractTo);
                    }
                    finally
                    {
                        try { File.Delete(staged); } catch { }
                    }
                    // The archive carries a single top-level folder named like
                    // the model, the same shape upstream's download had.
                    string inner = Directory.GetDirectories(extractTo).FirstOrDefault() ?? extractTo;
                    string final = Path.Combine(root, ModelName);
                    try { Directory.Delete(final, true); } catch { }
                    MoveIntoPlace(inner, final, Thread.Sleep);
                    try { Directory.Delete(extractTo, true); } catch { }

                    CompleteUnpack(final);
                }
            }
            catch (Exception ex)
            {
                // A failed unpack is a disk problem (no space, a locked cache
                // from another instance), not a network one, and so is a model
                // that unpacked and would not load. The same re-arm applies:
                // SAPI keeps the feature alive and the next EnsureStarted past
                // the delay tries again.
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
