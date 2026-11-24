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

        private readonly ProgramDevices _devices;
        private readonly Func<string> _testControllerName;
        private readonly Func<string, string> _replayName;
        private bool _pluginsOn, _testOn, _replayOn, _repeat;
        private int _speed = TestControllerPattern.NormalSpeed;
        private ProgramDevices.Entry _testEntry;
        private TestControllerFeeder _feeder;
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
        }

        /// <summary>The identity of the Test Controller's device.</summary>
        internal static Guid TestControllerGuid
            => ProgramDeviceDeclaration.InstanceGuid(ProgramDevices.BuiltInKind, TestControllerProgram, TestControllerKey);

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
            if (_feeder != null) _testEntry?.Device.Rename(_testControllerName());
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

        /// <summary>The speed's place in the list: Slow, Normal, Fast,
        /// Fastest.</summary>
        public int TestControllerSpeed
        {
            get => _speed;
            set
            {
                _speed = Math.Clamp(value, 0, TestControllerPattern.Speeds.Length - 1);
                if (_feeder != null) _feeder.Speed = _speed;
            }
        }

        /// <summary>Whether the Test Controller's device is on the list
        /// now.</summary>
        public bool TestControllerLive => _feeder != null;

        /// <summary>The Test Controller's device while it is on.</summary>
        internal ProgramDevice TestControllerDevice => _feeder != null ? _testEntry?.Device : null;

        private void ApplyTestController()
        {
            bool want = _pluginsOn && _testOn;
            if (want && _feeder == null)
            {
                _testEntry = _devices.DeclareBuiltIn(TestControllerProgram, TestControllerKey, _testControllerName(), TestControllerPattern.Shape());
                _feeder = new TestControllerFeeder(_testEntry.Device) { Speed = _speed };
                _feeder.Start();
            }
            else if (!want && _feeder != null)
            {
                _feeder.Dispose();
                _feeder = null;
                _devices.RemoveBuiltIn(_testEntry);
            }
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
                    var entry = _devices.DeclareBuiltIn(ReplayProgram, device.Key, name(), device.Shape);
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
