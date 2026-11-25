using System;
using System.Collections.Generic;
using PadForge.Engine;
using PadForge.Services.Automation;

namespace PadForge.Services.Plugins
{
    /// <summary>
    /// The two plugins PadForge carries (#597), each a switch on the
    /// Plugins page: the Test Controller and Replay. Their devices are
    /// program devices that belong to no session
    /// (<see cref="ProgramDevices.DeclareBuiltIn"/>), on the Devices page
    /// while their switch and Plugins are on and the engine runs, and
    /// offline when either switch goes off.
    ///
    /// <para>PadForge words their names, so they follow the interface
    /// language: each is kept in <see cref="BuiltInDeviceNames"/> for the
    /// rows that show it, and <see cref="RefreshNames"/> renames the live
    /// devices when the language changes.</para>
    ///
    /// <para>Called on the interface's thread.</para>
    /// </summary>
    internal sealed class BuiltInPlugins : IDisposable
    {
        internal const string TestControllerProgram = "Test Controller";
        internal const string TestControllerKey = "test-controller";
        internal const string ReplayProgram = "Replay";

        /// <summary>The word a Replay device's path carries, and the start
        /// of that path: <c>program://replay/id</c>.</summary>
        internal const string ReplayPathKind = "replay";
        internal const string ReplayPathPrefix = "program://" + ReplayPathKind + "/";

        /// <summary>A replayed device's name in the language in use, made
        /// from the name it was recorded with.</summary>
        internal static string ReplayShownName(string recorded)
            => string.Format(System.Globalization.CultureInfo.CurrentCulture,
                PadForge.Resources.Strings.Strings.Instance.Plugins_Replay_DeviceName_Format, recorded);

        private readonly ProgramDevices _devices;
        private readonly Func<string> _testControllerName;
        private readonly Func<string, string> _replayName;
        private bool _pluginsOn, _testOn, _replayOn, _repeat;
        private ReplayFile _file;
        private readonly List<ProgramDevices.Entry> _replayEntries = new();
        // The name each device is given, read in the language in use, by its
        // identity. Kept for the rows of devices gone offline too.
        private readonly Dictionary<Guid, Func<string>> _names = new();
        private ReplayPlayer _player;

        /// <param name="devices">The registry the Automation pipe's devices
        /// are in.</param>
        /// <param name="testControllerName">The Test Controller's name in the
        /// language in use.</param>
        /// <param name="replayName">A replayed device's name made from its
        /// recorded name.</param>
        public BuiltInPlugins(ProgramDevices devices, Func<string> testControllerName, Func<string, string> replayName)
        {
            _devices = devices;
            _testControllerName = testControllerName ?? (() => TestControllerProgram);
            _replayName = replayName ?? (name => name);
            // The Test Controller's identity is fixed, so its row is named in
            // the language in use before it is first switched on in a run.
            Name(TestControllerGuid, _testControllerName);
            TestControllerPattern.Declare(TestSettings);
            _testConfig = TestControllerPattern.Read(TestSettings);
            TestSettings.ValueChanged += OnTestSettings;
        }

        /// <summary>Takes the Test Controller's values PadForge.xml
        /// holds.</summary>
        public void LoadTestValues(IEnumerable<PluginValueData> values)
        {
            TestSettings.Load(values);
            _testConfig = TestControllerPattern.Read(TestSettings);
            ApplyTestController();
        }

        /// <summary>The identity of the first Test Controller's device.</summary>
        internal static Guid TestControllerGuid => TestControllerGuidOf(0);

        private void Name(Guid instanceGuid, Func<string> name)
        {
            _names[instanceGuid] = name;
            BuiltInDeviceNames.Set(instanceGuid, name);
        }

        /// <summary>Gives each live device its name in the language in use,
        /// keeping its shape and its state. The interface's language
        /// changed.</summary>
        public void RefreshNames()
        {
            // The settings' words are PadForge's own, in the language in use.
            TestControllerPattern.Declare(TestSettings);
            for (int i = 0; i < _tests.Count; i++) _tests[i].Entry.Device.Rename(TestControllerNameOf(i));
            foreach (var entry in _replayEntries)
                if (_names.TryGetValue(entry.Guid, out var name)) entry.Device.Rename(name());
        }

        /// <summary>Raised when Replay stops at its file's end, on the
        /// player's thread.</summary>
        public event Action ReplayEnded;

        public void SetPluginsOn(bool on)
        {
            if (_pluginsOn == on) return;
            _pluginsOn = on;
            ApplyTestController();
            ApplyReplay();
        }

        // ── Test Controller ──

        /// <summary>The Test Controller's declared settings (#600 item 19),
        /// kept as a plugin's are.</summary>
        public PluginSettingSet TestSettings { get; } = new();

        private volatile TestControllerConfig _testConfig = new();
        private readonly List<(ProgramDevices.Entry Entry, TestControllerMotion Motion)> _tests = new();

        public bool TestControllerOn
        {
            get => _testOn;
            set
            {
                if (_testOn == value) return;
                _testOn = value;
                ApplyTestController();
            }
        }

        /// <summary>Whether a Test Controller's device is on the list
        /// now.</summary>
        public bool TestControllerLive => _tests.Count > 0;

        /// <summary>How many Test Controllers are on the list now.</summary>
        public int TestControllerCount => _tests.Count;

        /// <summary>The first Test Controller's device while it is on.</summary>
        internal ProgramDevice TestControllerDevice => _tests.Count > 0 ? _tests[0].Entry.Device : null;

        /// <summary>The Test Controllers' devices while they are on.</summary>
        internal IReadOnlyList<ProgramDevice> TestControllerDevices
        {
            get
            {
                var list = new List<ProgramDevice>(_tests.Count);
                foreach (var (entry, _) in _tests) list.Add(entry.Device);
                return list;
            }
        }

        /// <summary>The settings in force, as the poll thread reads
        /// them.</summary>
        internal TestControllerConfig TestConfig => _testConfig;

        /// <summary>The key of Test Controller <paramref name="index"/>,
        /// from 0. The first keeps the key it always had, so its mappings
        /// hold.</summary>
        internal static string TestControllerKeyOf(int index)
            => index == 0 ? TestControllerKey : TestControllerKey + "-" + (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>The identity of Test Controller <paramref name="index"/>'s
        /// device.</summary>
        internal static Guid TestControllerGuidOf(int index)
            => ProgramDeviceDeclaration.InstanceGuid(ProgramDevices.BuiltInKind, TestControllerProgram, TestControllerKeyOf(index));

        private string TestControllerNameOf(int index)
            => index == 0 ? _testControllerName()
                : _testControllerName() + " " + (index + 1).ToString(System.Globalization.CultureInfo.CurrentCulture);

        /// <summary>A setting changed: the poll thread reads the new ones,
        /// and the count of controllers follows.</summary>
        private void OnTestSettings(string device, string key)
        {
            _testConfig = TestControllerPattern.Read(TestSettings);
            ApplyTestController();
        }

        /// <summary>Reads per Second: the first controller's reads in the
        /// last second, as its status line. Called once a second.</summary>
        public void CountReads()
        {
            if (_tests.Count == 0) return;
            int reads = _tests[0].Motion.TakeReads();
            for (int i = 1; i < _tests.Count; i++) _tests[i].Motion.TakeReads();
            TestSettings.PluginSet(null, TestControllerPattern.KeyRate, reads.ToString("N0", System.Globalization.CultureInfo.CurrentCulture));
        }

        private void ApplyTestController()
        {
            int want = _pluginsOn && _testOn ? _testConfig.Controllers : 0;
            while (_tests.Count < want)
            {
                int index = _tests.Count;
                var entry = _devices.DeclareBuiltIn(TestControllerProgram, TestControllerKeyOf(index), TestControllerNameOf(index),
                    TestControllerPattern.Shape());
                var motion = new TestControllerMotion(entry.Device, () => _testConfig);
                entry.Device.Live = motion.Read;
                int at = index;
                Name(entry.Guid, () => TestControllerNameOf(at));
                _tests.Add((entry, motion));
            }
            while (_tests.Count > want)
            {
                var (entry, _) = _tests[_tests.Count - 1];
                _tests.RemoveAt(_tests.Count - 1);
                entry.Device.Live = null;
                entry.Device.Rest();
                _devices.RemoveBuiltIn(entry);
            }
            if (want == 0) TestSettings.PluginSet(null, TestControllerPattern.KeyRate, string.Empty);
        }

        // ── Replay ──

        public bool ReplayOn
        {
            get => _replayOn;
            set
            {
                if (_replayOn == value) return;
                _replayOn = value;
                ApplyReplay();
            }
        }

        public bool Repeat
        {
            get => _repeat;
            set
            {
                _repeat = value;
                if (_player != null) _player.Repeat = value;
            }
        }

        /// <summary>The recording open now, or null.</summary>
        public ReplayFile File => _file;

        public bool Playing => _player?.Playing == true;

        public long PositionMicros => _player?.PositionMicros ?? 0;

        /// <summary>How many devices Replay plays now.</summary>
        public int ReplayDeviceCount => _player != null ? _replayEntries.Count : 0;

        /// <summary>Opens a recording, in place of the one open. Null when it
        /// opened, or the reason in a word.</summary>
        public string Open(string path)
        {
            var file = ReplayFile.Read(path, out string error);
            if (file == null) return error;
            CloseReplay();
            _file = file;
            ApplyReplay();
            return null;
        }

        public void Play()
        {
            if (_player == null) return;
            if (_player.Playing) return;
            // A pass that ended leaves its thread done, and Play starts a
            // new one.
            _player.Stop();
            _player.Play();
        }

        public void Stop() => _player?.Stop();

        private void ApplyReplay()
        {
            bool want = _pluginsOn && _replayOn && _file != null;
            if (want && _player == null)
            {
                var devices = new Dictionary<int, ProgramDevice>();
                foreach (var device in _file.Devices)
                {
                    string recorded = device.Name;
                    Func<string> name = () => _replayName(recorded);
                    // The row keeps the recorded name, in no language, so a
                    // row left offline by an earlier run is named in the
                    // language in use too (InputService.LocalizedDeviceName).
                    var entry = _devices.DeclareBuiltIn(ReplayProgram, device.Key, name(), device.Shape,
                        productName: recorded, pathKind: ReplayPathKind);
                    Name(entry.Guid, name);
                    _replayEntries.Add(entry);
                    devices[device.Number] = entry.Device;
                }
                _player = new ReplayPlayer(_file, devices) { Repeat = _repeat };
                _player.Ended += () => ReplayEnded?.Invoke();
            }
            else if (!want && _player != null) CloseReplay(keepFile: true);
        }

        private void CloseReplay(bool keepFile = false)
        {
            if (_player != null)
            {
                _player.Dispose();
                _player = null;
            }
            foreach (var entry in _replayEntries) _devices.RemoveBuiltIn(entry);
            _replayEntries.Clear();
            if (!keepFile) _file = null;
        }

        public void Dispose()
        {
            _pluginsOn = false;
            ApplyTestController();
            CloseReplay();
            foreach (var (guid, name) in _names) BuiltInDeviceNames.Forget(guid, name);
            _names.Clear();
        }
    }
}
