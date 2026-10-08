using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using PadForge.Engine;
using PadForge.Services;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// How VoskModelStore reads the outcome of loading a model.
    ///
    /// <para>libvosk says "I could not read this model" by returning null,
    /// and the managed binding wraps that null in a Model without throwing.
    /// The store used to publish that Model as ready, and the first
    /// recognizer built on it dereferenced the null in native code, which
    /// ends the process. A temp cleaner that removes a cached model's files
    /// and leaves its folders is all it takes.</para>
    ///
    /// <para>The other outcome it misread is a libvosk that will not load.
    /// That says nothing about the model, and the store answered it by
    /// deleting the cache and unpacking 35 MB again, every five minutes. The
    /// ARM64 build carries a libvosk that has not yet run on hardware, so
    /// that is the build on which it would matter first.</para>
    /// </summary>
    public class VoskModelStoreTests : IDisposable
    {
        private readonly string _root;

        public VoskModelStoreTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "PadForge-vosk-store-test-" + Path.GetRandomFileName());
            VoskModelStore.ResetForTests(_root);
        }

        public void Dispose()
        {
            VoskModelStore.ResetForTests(null);
            try { Directory.Delete(_root, true); } catch { }
        }

        /// <summary>The diagnostics ring's latest voice lines, for a failure
        /// message.</summary>
        private static string VoiceLines()
        {
            var lines = SdlDiagLog.Snapshot().Split('\n');
            var voice = new System.Collections.Generic.List<string>();
            foreach (var line in lines)
                if (line.Contains("VOICE vosk")) voice.Add(line.TrimEnd('\r'));
            int skip = Math.Max(0, voice.Count - 6);
            return voice.Count == 0 ? "The ring has no voice lines." : string.Join(" | ", voice.GetRange(skip, voice.Count - skip));
        }

        /// <summary>A Model over a null native pointer, made the way the
        /// binding makes one, with no call into libvosk. Disposing it is
        /// safe: the binding skips the native free when the handle is zero.</summary>
        private static Vosk.Model NullModel()
        {
            var ctor = typeof(Vosk.Model).GetConstructor(
                BindingFlags.Instance | BindingFlags.NonPublic, null, new[] { typeof(IntPtr) }, null);
            Assert.True(ctor != null, "Vosk.Model no longer has its (IntPtr) constructor");
            return (Vosk.Model)ctor.Invoke(new object[] { IntPtr.Zero });
        }

        /// <summary>A model that records its own disposal. The binding's one
        /// public constructor calls libvosk, which returns null for a folder
        /// that is not there, so this is a null model too and safe to
        /// dispose. Null when libvosk does not load in this test host, where
        /// the tests that need it have nothing to say.</summary>
        private sealed class RecordingModel : Vosk.Model
        {
            public bool Disposed;
            public RecordingModel(string nowhere) : base(nowhere) { }
            protected override void Dispose(bool disposing)
            {
                Disposed = true;
                base.Dispose(disposing);
            }
        }

        private RecordingModel TryMakeRecordingModel()
        {
            try
            {
                Vosk.Vosk.SetLogLevel(-1);
                return new RecordingModel(Path.Combine(_root, "nowhere"));
            }
            catch (Exception ex) when (ex is DllNotFoundException || ex is BadImageFormatException
                                    || ex is EntryPointNotFoundException)
            {
                return null;
            }
        }

        private string CachedModelFolder()
        {
            string dir = Path.Combine(_root, VoskModelStore.ModelName);
            Directory.CreateDirectory(Path.Combine(dir, "graph"));
            return dir;
        }

        // ── The check the store depends on ──

        /// <summary>The binding has no accessor for its native pointer, so
        /// the store reads the private field. A Vosk package that renames or
        /// retypes it would switch Vosk off in the field, so it goes red
        /// here first.</summary>
        [Fact]
        public void TheBindingKeepsItsNativePointerWhereTheStoreLooks()
        {
            var field = typeof(Vosk.Model).GetField("handle", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.True(field != null, "Vosk.Model has no private field named handle");
            Assert.Equal(typeof(HandleRef), field.FieldType);

            using var model = NullModel();
            Assert.Equal(IntPtr.Zero, VoskModelStore.NativeHandleOf(model));
        }

        /// <summary>A reader that answered zero for everything would pass the
        /// check above and turn every good model away. So it is shown a
        /// pointer that is not zero. The pointer is made up, so it is taken
        /// back out before the model can be disposed, which would hand it to
        /// libvosk to free.</summary>
        [Fact]
        public void TheStoreReadsTheNativePointerThatIsThere()
        {
            var field = typeof(Vosk.Model).GetField("handle", BindingFlags.Instance | BindingFlags.NonPublic);
            var ctor = typeof(Vosk.Model).GetConstructor(
                BindingFlags.Instance | BindingFlags.NonPublic, null, new[] { typeof(IntPtr) }, null);
            var model = (Vosk.Model)ctor.Invoke(new object[] { new IntPtr(0x1234) });
            // Before anything that can throw: the finalizer frees a pointer
            // that is not zero, and a package that moved the field, which is
            // the very change this class exists to notice, would make the
            // cleanup below throw before it got that far.
            GC.SuppressFinalize(model);
            try
            {
                Assert.Equal(new IntPtr(0x1234), VoskModelStore.NativeHandleOf(model));
            }
            finally
            {
                field?.SetValue(model, new HandleRef(null, IntPtr.Zero));
            }
        }

        // ── One load, and how it is read ──

        [Fact]
        public void AModelLibvoskRefused_IsABadModel_NotAReadyOne()
        {
            VoskModelStore.CreateModel = _ => NullModel();

            var outcome = VoskModelStore.TryLoad("anywhere", out var model, out string why);

            Assert.Equal(VoskModelStore.ModelLoad.BadModel, outcome);
            Assert.Null(model);
            Assert.False(string.IsNullOrEmpty(why));
        }

        /// <summary>The three ways a P/Invoke reports a library it cannot
        /// use. The binding's P/Invoke class has an empty static constructor,
        /// so they arrive bare.</summary>
        [Theory]
        [InlineData("missing")]
        [InlineData("wrong-architecture")]
        [InlineData("missing-export")]
        public void ALibraryThatWillNotLoad_MakesVoskUnusable_AndSaysNothingAboutTheModel(string how)
        {
            VoskModelStore.CreateModel = _ => throw (how == "missing" ? new DllNotFoundException("libvosk")
                : how == "wrong-architecture" ? new BadImageFormatException("libvosk")
                : (Exception)new EntryPointNotFoundException("vosk_model_new"));

            var outcome = VoskModelStore.TryLoad("anywhere", out var model, out _);

            Assert.Equal(VoskModelStore.ModelLoad.Unusable, outcome);
            Assert.Null(model);
        }

        [Fact]
        public void AnyOtherFailure_IsABadModel()
        {
            VoskModelStore.CreateModel = _ => throw new IOException("disk");
            Assert.Equal(VoskModelStore.ModelLoad.BadModel, VoskModelStore.TryLoad("anywhere", out _, out _));
        }

        /// <summary>A model that cannot be checked is never published. If the
        /// binding stops keeping its pointer where the store looks, the store
        /// cannot tell a good model from a null one, and a null one ends the
        /// process, so Vosk is switched off and SAPI carries on.</summary>
        [Fact]
        public void AModelThatCannotBeChecked_IsNeverPublished()
        {
            VoskModelStore.CreateModel = _ => NullModel();
            VoskModelStore.ReadNativeHandle = _ => null;

            Assert.Equal(VoskModelStore.ModelLoad.Unusable, VoskModelStore.TryLoad("anywhere", out var model, out _));
            Assert.Null(model);
        }

        [Fact]
        public void AModelWithANativePointer_IsLoaded_AndIsTheOneHandedBack()
        {
            var made = NullModel();
            VoskModelStore.CreateModel = _ => made;
            VoskModelStore.ReadNativeHandle = _ => new IntPtr(1);

            Assert.Equal(VoskModelStore.ModelLoad.Loaded, VoskModelStore.TryLoad("anywhere", out var model, out _));
            Assert.Same(made, model);
        }

        /// <summary>A model that is turned away is disposed, and one that is
        /// handed over is not: the store owns it from then on, and disposing
        /// it would free the native model under every recognizer.</summary>
        [Fact]
        public void AModelIsDisposed_OnlyWhenItIsTurnedAway()
        {
            var refused = TryMakeRecordingModel();
            if (refused == null) return;
            VoskModelStore.CreateModel = _ => refused;
            Assert.Equal(VoskModelStore.ModelLoad.BadModel, VoskModelStore.TryLoad("anywhere", out _, out _));
            Assert.True(refused.Disposed, "a model libvosk refused was left undisposed");

            var unverifiable = TryMakeRecordingModel();
            VoskModelStore.CreateModel = _ => unverifiable;
            VoskModelStore.ReadNativeHandle = _ => null;
            Assert.Equal(VoskModelStore.ModelLoad.Unusable, VoskModelStore.TryLoad("anywhere", out _, out _));
            Assert.True(unverifiable.Disposed, "a model that could not be checked was left undisposed");

            var kept = TryMakeRecordingModel();
            VoskModelStore.CreateModel = _ => kept;
            VoskModelStore.ReadNativeHandle = _ => new IntPtr(1);
            Assert.Equal(VoskModelStore.ModelLoad.Loaded, VoskModelStore.TryLoad("anywhere", out var model, out _));
            Assert.Same(kept, model);
            Assert.False(kept.Disposed, "the model the store was handed had already been disposed");
        }

        /// <summary>The real binding and the real check, on a folder that
        /// holds no model. Where libvosk loads, it returns null, the binding
        /// wraps the null without a word, and only the handle check calls it
        /// a bad model: that is the crash, met for real. Where libvosk does
        /// not load, Vosk is unusable. Nowhere is it a model that loaded.
        /// Whether it loads is read from the libvosk.dll beside this
        /// assembly: it loads when that file is built for this process.</summary>
        [Fact]
        public void AFolderWithNoModelInIt_NeverLoads_ThroughTheRealBinding()
        {
            string empty = Path.Combine(_root, "no-model-here");
            Directory.CreateDirectory(empty);
            string library = Path.Combine(AppContext.BaseDirectory, "libvosk.dll");
            ushort wanted = RuntimeInformation.ProcessArchitecture == Architecture.X64 ? (ushort)0x8664
                          : RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? (ushort)0xAA64
                          : (ushort)0;
            bool libraryIsHere = File.Exists(library) && new PeImage(File.ReadAllBytes(library)).Machine == wanted;

            var outcome = VoskModelStore.TryLoad(empty, out var model, out _);

            Assert.Equal(libraryIsHere ? VoskModelStore.ModelLoad.BadModel : VoskModelStore.ModelLoad.Unusable,
                         outcome);
            Assert.Null(model);
        }

        // ── What the store does with each outcome ──

        /// <summary>The loop this closes: a cached model, a library that will
        /// not load, the cache deleted as though it were corrupt, 35 MB
        /// unpacked again, the same failure, every five minutes.</summary>
        [Fact]
        public void WithACachedModelAndNoLibrary_TheCacheStays_NothingUnpacks_AndNothingRetries()
        {
            if (!PlatformSupport.VoskAvailable) return;
            string cache = CachedModelFolder();
            int loads = 0;
            bool unpackStarted = false;
            VoskModelStore.CreateModel = _ => { loads++; throw new DllNotFoundException("libvosk"); };
            VoskModelStore.StartUnpack = () => unpackStarted = true;

            VoskModelStore.EnsureStarted();
            VoskModelStore.EnsureStarted();

            Assert.True(VoskModelStore.IsUnusable);
            Assert.False(VoskModelStore.IsReady);
            Assert.Null(VoskModelStore.Model);
            Assert.True(Directory.Exists(cache), "the cache was deleted for a fault that is not the cache's");
            Assert.False(unpackStarted);
            Assert.Equal(1, loads);
        }

        /// <summary>The crash this closes: a cache whose folders outlived its
        /// files loads as a null model. It is deleted and unpacked again,
        /// and it is never ready.</summary>
        [Fact]
        public void WithACachedModelLibvoskRefuses_TheCacheGoes_AndTheUnpackStarts()
        {
            if (!PlatformSupport.VoskAvailable) return;
            string cache = CachedModelFolder();
            bool unpackStarted = false;
            VoskModelStore.CreateModel = _ => NullModel();
            VoskModelStore.StartUnpack = () => unpackStarted = true;

            VoskModelStore.EnsureStarted();

            Assert.False(VoskModelStore.IsReady);
            Assert.Null(VoskModelStore.Model);
            Assert.False(Directory.Exists(cache));
            Assert.True(unpackStarted);
            Assert.True(VoskModelStore.IsUnpacking);
        }

        [Fact]
        public void WithACachedModelThatLoads_TheStoreIsReady_WithThatModel()
        {
            if (!PlatformSupport.VoskAvailable) return;
            string cache = CachedModelFolder();
            var made = NullModel();
            bool unpackStarted = false;
            VoskModelStore.CreateModel = _ => made;
            VoskModelStore.ReadNativeHandle = _ => new IntPtr(1);
            VoskModelStore.StartUnpack = () => unpackStarted = true;

            VoskModelStore.EnsureStarted();

            Assert.True(VoskModelStore.IsReady);
            Assert.Same(made, VoskModelStore.Model);
            Assert.True(Directory.Exists(cache));
            Assert.False(unpackStarted);
        }

        /// <summary>The unpack itself, end to end: the embedded model comes
        /// out of the app onto the disk, and the load at the end of it goes
        /// through the same judgment. Only the model's construction is
        /// replaced, so an unpack that went back to building the model
        /// directly would publish some other object than this one. The
        /// unpack runs on this thread, so it is over before the test is and
        /// nothing is left writing into a folder the test has given up.</summary>
        [Fact]
        public void WithNoCache_TheEmbeddedModelUnpacks_AndItsLoadIsJudgedTheSameWay()
        {
            if (!PlatformSupport.VoskAvailable) return;
            var made = NullModel();
            string asked = null;
            bool unpackAskedFor = false;
            VoskModelStore.CreateModel = dir => { asked = dir; return made; };
            VoskModelStore.ReadNativeHandle = _ => new IntPtr(1);
            VoskModelStore.StartUnpack = () => unpackAskedFor = true;

            VoskModelStore.EnsureStarted();
            Assert.True(unpackAskedFor, "a store with no cache did not ask for the unpack");
            VoskModelStore.UnpackNow();

            // The unpack reports why it failed only to the diagnostics ring,
            // so a failure carries the ring's voice lines with it.
            Assert.True(VoskModelStore.IsReady, "the unpack did not end with a ready model. " + VoiceLines());
            Assert.Same(made, VoskModelStore.Model);
            string final = Path.Combine(_root, VoskModelStore.ModelName);
            Assert.Equal(final, asked);
            Assert.True(File.Exists(Path.Combine(final, "am", "final.mdl")), "the acoustic model did not reach the cache");
        }

        /// <summary>The same three outcomes at the end of a fresh unpack. A
        /// library that will not load switches the store off and keeps what
        /// was unpacked. A model that will not load is thrown to the unpack's
        /// handler, which is the retry a failed unpack already gets.</summary>
        [Fact]
        public void AfterAnUnpack_TheSameThreeOutcomes()
        {
            var made = NullModel();
            VoskModelStore.CreateModel = _ => made;
            VoskModelStore.ReadNativeHandle = _ => new IntPtr(1);
            VoskModelStore.CompleteUnpack("anywhere");
            Assert.True(VoskModelStore.IsReady);
            Assert.Same(made, VoskModelStore.Model);

            VoskModelStore.ResetForTests(_root);
            VoskModelStore.CreateModel = _ => throw new BadImageFormatException("libvosk");
            VoskModelStore.CompleteUnpack("anywhere");
            Assert.True(VoskModelStore.IsUnusable);
            Assert.False(VoskModelStore.IsReady);

            VoskModelStore.ResetForTests(_root);
            VoskModelStore.CreateModel = _ => NullModel();
            Assert.Throws<InvalidDataException>(() => VoskModelStore.CompleteUnpack("anywhere"));
            Assert.False(VoskModelStore.IsReady);
            Assert.False(VoskModelStore.IsUnusable);
        }
    }
}
