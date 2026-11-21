using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace PadForge.Common
{
    /// <summary>
    /// The machine's Uninstall registrations, both registry views, read once
    /// and read again only after Windows says something under the keys
    /// changed: a program installed, removed or updated.
    ///
    /// <para>The driver rows found HidHide, Windows MIDI Services, vJoy and
    /// ViGEmBus by walking every subkey of both Uninstall keys and opening
    /// each one, and the status timer ran two such walks every five seconds
    /// on the interface thread: thousands of registry handles a minute, the
    /// largest share of what the program threw away while it sat still
    /// (measured 2026-11-16). The keys are watched with
    /// RegNotifyChangeKeyValue, so a change shows at the next read. Where
    /// the watch cannot be set up, every read tries again to open and watch
    /// the keys it lacks, and walks the keys again, as before.</para>
    /// </summary>
    internal sealed class UninstallRegistry
    {
        /// <summary>One registration: its subkey's name and the values the
        /// driver rows read.</summary>
        internal readonly record struct Entry(string SubKeyName, string DisplayName,
            string DisplayVersion, string UninstallString, object WindowsInstaller);

        internal const string UninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

        /// <summary>HKLM's Uninstall keys, 64-bit view first, then 32-bit,
        /// the order the walks read them in.</summary>
        internal static UninstallRegistry Machine { get; } = new(RegistryHive.LocalMachine, UninstallPath);

        private const int REG_NOTIFY_CHANGE_NAME = 0x1;
        private const int REG_NOTIFY_CHANGE_LAST_SET = 0x4;
        private const int REG_NOTIFY_THREAD_AGNOSTIC = 0x10000000;

        [DllImport("advapi32.dll")]
        private static extern int RegNotifyChangeKeyValue(SafeRegistryHandle key, bool watchSubtree,
            int notifyFilter, SafeWaitHandle changed, bool asynchronous);

        private static readonly RegistryView[] s_views = { RegistryView.Registry64, RegistryView.Registry32 };

        private readonly object _gate = new();
        private readonly RegistryHive _hive;
        private readonly string _path;
        private RegistryKey[] _keys;
        private AutoResetEvent[] _changed;
        /// <summary>Each view's watch is set and has not fired.</summary>
        private bool[] _armed;
        private bool _watching;
        private IReadOnlyList<Entry> _entries;

        internal UninstallRegistry(RegistryHive hive, string path)
        {
            _hive = hive;
            _path = path;
        }

        /// <summary>How many times the keys were walked, for the tests.</summary>
        internal int Walks { get; private set; }

        /// <summary>Every registration, in the order a walk of both views
        /// meets them.</summary>
        internal IReadOnlyList<Entry> Entries
        {
            get
            {
                lock (_gate)
                {
                    // A read with a key or a watch missing walks, after it
                    // tries again to open and watch what is missing.
                    bool walk = _entries == null || !_watching;
                    if (!_watching) Watch();
                    // A change seen re-arms its watch before the walk, so a
                    // change during the walk shows at the next read.
                    for (int i = 0; i < _changed.Length; i++)
                    {
                        if (_changed[i] != null && _changed[i].WaitOne(0))
                        {
                            walk = true;
                            _armed[i] = Arm(i);
                            if (!_armed[i]) _watching = false;
                        }
                    }
                    if (walk)
                        _entries = Walk();
                    return _entries;
                }
            }
        }

        /// <summary>Opens each view's key that is not open and sets each
        /// watch that is not set. The keys are watched when every view's
        /// is.</summary>
        private void Watch()
        {
            if (_keys == null)
            {
                _keys = new RegistryKey[s_views.Length];
                _changed = new AutoResetEvent[s_views.Length];
                _armed = new bool[s_views.Length];
            }
            _watching = true;
            for (int i = 0; i < s_views.Length; i++)
            {
                if (_keys[i] == null)
                {
                    try
                    {
                        using var root = RegistryKey.OpenBaseKey(_hive, s_views[i]);
                        _keys[i] = root.OpenSubKey(_path, false);
                    }
                    catch { }
                }
                if (_keys[i] == null)
                {
                    // A view with no key cannot say when one appears.
                    _watching = false;
                    continue;
                }
                _changed[i] ??= new AutoResetEvent(false);
                if (!_armed[i]) _armed[i] = Arm(i);
                if (!_armed[i]) _watching = false;
            }
        }

        private bool Arm(int i)
        {
            try
            {
                return RegNotifyChangeKeyValue(_keys[i].Handle, true,
                    REG_NOTIFY_CHANGE_NAME | REG_NOTIFY_CHANGE_LAST_SET | REG_NOTIFY_THREAD_AGNOSTIC,
                    _changed[i].SafeWaitHandle, true) == 0;
            }
            catch
            {
                return false;
            }
        }

        private IReadOnlyList<Entry> Walk()
        {
            Walks++;
            var entries = new List<Entry>();
            for (int i = 0; i < s_views.Length; i++)
            {
                var key = _keys[i];
                if (key == null) continue;
                // A view that fails partway keeps what it read, as the walks
                // that stopped at their first match did.
                try
                {
                    foreach (var subName in key.GetSubKeyNames())
                    {
                        using var sub = key.OpenSubKey(subName, false);
                        if (sub == null) continue;
                        entries.Add(new Entry(subName,
                            sub.GetValue("DisplayName") as string,
                            sub.GetValue("DisplayVersion") as string,
                            sub.GetValue("UninstallString") as string,
                            sub.GetValue("WindowsInstaller")));
                    }
                }
                catch { }
            }
            return entries;
        }
    }
}
