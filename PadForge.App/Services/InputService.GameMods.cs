using System;
using System.Collections.Generic;
using System.Globalization;
using PadForge.Common;
using PadForge.Common.Input;
using PadForge.Engine.Data;
using PadForge.Resources.Strings;
using PadForge.ViewModels;

namespace PadForge.Services
{
    public partial class InputService
    {
        // ─────────────────────────────────────────────
        //  Game mods (#529)
        // ─────────────────────────────────────────────

        private GameModServer _gameModServer;
        private long _gameModLinesVersion = -1;
        private long _gameModLinesMs;

        /// <summary>How often a server kept from its port by another
        /// program tries the bind again: the game mod server, the motion
        /// server and the web controller, on the dashboard tick.</summary>
        internal const long PortInUseRetryMs = 3000;

        /// <summary>Why the last start failed, with its port and when, or
        /// Stopped when the server runs or was stopped. A language change
        /// rebuilds the card's line from it (<see cref="GameModStoppedStatus"/>).
        /// Written where the server starts and stops, on the threads those
        /// run on.</summary>
        private GameModServer.ServerState _gameModFailure = GameModServer.ServerState.Stopped;
        private int _gameModFailurePort;
        private long _gameModFailedMs;

        /// <summary>The card's line while no server runs: why the last start
        /// failed, or Stopped.</summary>
        private string GameModStoppedStatus() => _gameModFailure == GameModServer.ServerState.Stopped
            ? Strings.Instance.Common_Stopped
            : GameModServer.FormatStatus(_gameModFailure, _gameModFailurePort, null, 0);

        /// <summary>Starts the server for DSX game mods when its switch is
        /// on and the engine runs, the motion server's rule.</summary>
        private void StartGameModServerIfEnabled()
        {
            if (!_mainVm.Dashboard.EnableGameModServer || _inputManager == null) return;
            if (_gameModServer != null) return;

            var server = new GameModServer();
            server.StatusChanged += (_, _) => _dispatcher.BeginInvoke(RefreshGameModStatus);
            _gameModServer = server;

            int port = _mainVm.Dashboard.GameModServerPort;
            if (port < 1024 || port > 65535) port = GameModServer.DefaultPort;
            if (!server.Start(port, _mainVm.Dashboard.GameModDsxStandIn))
            {
                // The status the failed start raised stays on the card: the
                // port in use or the bind that failed.
                _gameModServer = null;
                _gameModFailure = server.State;
                _gameModFailurePort = port;
                _gameModFailedMs = Environment.TickCount64;
                string failed = server.FormatStatus();
                server.Dispose();
                _dispatcher.BeginInvoke(() =>
                {
                    _mainVm.Dashboard.GameModServerStatus = failed;
                    _mainVm.Dashboard.IsGameModServerRunning = false;
                });
                return;
            }
            _gameModFailure = GameModServer.ServerState.Stopped;
        }

        private void StopGameModServer()
        {
            _gameModFailure = GameModServer.ServerState.Stopped;
            var server = _gameModServer;
            if (server == null)
            {
                // A start that failed left its reason on the card, and no
                // server runs to report the stop. Queued like that reason,
                // so a start that follows at once (a port change) still
                // shows its own line.
                _dispatcher.BeginInvoke(() =>
                {
                    if (_gameModServer != null) return;
                    _mainVm.Dashboard.GameModServerStatus = Strings.Instance.Common_Stopped;
                    _mainVm.Dashboard.IsGameModServerRunning = false;
                });
                return;
            }
            _gameModServer = null;
            server.Dispose();
            _dispatcher.BeginInvoke(() =>
            {
                _mainVm.Dashboard.GameModServerStatus = Strings.Instance.Common_Stopped;
                _mainVm.Dashboard.IsGameModServerRunning = false;
                _gameModLinesVersion = -1;
            });
        }

        /// <summary>The card's line and flame from the server's state.</summary>
        private void RefreshGameModStatus()
        {
            var server = _gameModServer;
            var dash = _mainVm.Dashboard;
            if (server == null)
            {
                dash.IsGameModServerRunning = false;
                return;
            }
            dash.GameModServerStatus = server.FormatStatus();
            dash.IsGameModServerRunning = server.State == GameModServer.ServerState.Listening;
        }

        /// <summary>The Dashboard line and the pad page lines, on the
        /// dashboard cadence. Rebuilt when what the mods hold changed, and
        /// once a second while they hold anything, since the virtual
        /// controller a mod's index names moves with the pads. That
        /// once-a-second order read also tells the virtual controllers when
        /// the order moved (<see cref="GameModEffects.RefreshOrder"/>). One
        /// read when nothing moved.</summary>
        private void UpdateGameModLines()
        {
            long version = GameModEffects.Version;
            long now = Environment.TickCount64;
            // A port another program held, such as DSX: the bind is tried
            // again every few seconds while the switch is on and the engine
            // runs, so closing that program brings the server up. A start
            // that fails again records the new time.
            if (_gameModFailure == GameModServer.ServerState.PortInUse && _gameModServer == null
                && now - _gameModFailedMs >= PortInUseRetryMs)
                StartGameModServerIfEnabled();
            bool held = GameModEffects.AnyHeld;
            if (version == _gameModLinesVersion && !(held && now - _gameModLinesMs >= 1000)) return;
            _gameModLinesVersion = version;
            _gameModLinesMs = now;

            if (_gameModServer != null) RefreshGameModStatus();
            int[] order = held ? GameModEffects.RefreshOrder() : Array.Empty<int>();
            foreach (var pad in _mainVm.Pads)
            {
                var hold = held ? GameModEffects.ForController(Array.IndexOf(order, pad.PadIndex)) : null;
                SetGameModPadLines(pad, hold);
            }
        }

        /// <summary>A slot's three pad page lines from what a mod holds on
        /// it: the Adaptive Triggers and Impulse Triggers tabs while it holds
        /// a trigger program, the Lighting tab while it holds a light.</summary>
        internal static void SetGameModPadLines(PadViewModel pad, GameModEffects.Hold hold)
        {
            var s = Strings.Instance;
            var culture = CultureInfo.CurrentCulture;
            string name = hold?.SenderName ?? s.GameMods_UnknownSender;
            bool triggers = hold != null && hold.HasTriggers;
            bool lights = hold != null && hold.HasLights;
            pad.GameModTriggersLine = triggers ? string.Format(culture, s.Pad_GameModTriggers_Format, name) : string.Empty;
            pad.GameModImpulseLine = triggers ? string.Format(culture, s.Pad_GameModImpulse_Format, name) : string.Empty;
            pad.GameModLightsLine = lights ? string.Format(culture, s.Pad_GameModLights_Format, name) : string.Empty;
        }

        /// <summary>The selected row's game mod line (#529), on the Devices
        /// page's tick, shown with Clear Mod Effects. A row no mod reaches
        /// shows neither.</summary>
        private void UpdateGameModDeviceRow(DeviceRowViewModel row, UserDevice ud)
        {
            row.GameModLine = GameModDeviceLine(ud);
        }

        /// <summary>What the mods hold on the virtual controllers this device
        /// feeds, as this device carries it: a DualSense's triggers and
        /// lights, a Flydigi Apex pad's (#586), a DualShock 4's lightbar,
        /// the lightbar color on a mouse,
        /// keyboard or lighting row the slot lights, or the vibrations an
        /// Xbox pad's impulse triggers play with Translate Adaptive Trigger
        /// Vibrations on. Empty when none of that reaches it.</summary>
        internal static string GameModDeviceLine(UserDevice ud)
        {
            if (ud == null || !GameModEffects.AnyHeld) return string.Empty;
            var settings = SettingsManager.UserSettings;
            if (settings == null) return string.Empty;

            var fed = new List<(int Slot, bool Impulse)>(1);
            lock (settings.SyncRoot)
            {
                foreach (var us in settings.Items)
                {
                    if (us == null || us.InstanceGuid != ud.InstanceGuid || us.MapTo < 0) continue;
                    var ps = us.GetPadSetting();
                    fed.Add((us.MapTo, ps != null && ps.AtVibrationToImpulseEnabled == "1"));
                }
            }
            if (fed.Count == 0) return string.Empty;

            var order = GameModEffects.OrderedSlots();
            bool sony = GameModEffects.IsSonyPad(ud.VendorId, ud.ProdId);
            bool dualSense = GameModEffects.IsDualSense(ud.VendorId, ud.ProdId);
            bool impulse = !sony && ud.Device != null && ud.Device.HasRumbleTriggers;
            // A Flydigi Apex pad with a session plays a mod's trigger effects
            // themselves, and its lights are the controller's own (#586).
            bool flydigi = !sony && PadForge.Common.Input.Flydigi.FlydigiApexService.SessionOf(ud.InstanceGuid) != null;
            bool triggers = false, lights = false, peripheralLights = false, impulseVibrations = false;
            string name = null;
            foreach (var (slot, translate) in fed)
            {
                var hold = GameModEffects.ForController(Array.IndexOf(order, slot));
                if (hold == null) continue;
                bool reached = false;
                if (dualSense)
                {
                    if (hold.HasTriggers) { triggers = true; reached = true; }
                    if (hold.HasLights) { lights = true; reached = true; }
                }
                else if (sony && hold.Lightbar != null)
                {
                    lights = true;
                    reached = true;
                }
                else
                {
                    // A mouse, keyboard or lighting row the slot lights
                    // takes the mod's color where no game color is live
                    // (UserEffectsDispatcher.LightPeripheral). The slot's
                    // claim on the row is that dispatcher's own answer.
                    if (hold.Lightbar != null && PadForge.Common.Input.Peripherals.PeripheralOutputs.IsLitBy(ud.InstanceGuid, slot))
                    {
                        if (flydigi) lights = true;
                        else peripheralLights = true;
                        reached = true;
                    }
                    if (flydigi && hold.HasTriggers)
                    {
                        triggers = true;
                        reached = true;
                    }
                    else if (impulse && translate && hold.HasTriggers)
                    {
                        impulseVibrations = true;
                        reached = true;
                    }
                }
                if (reached) name ??= hold.SenderName;
            }

            var s = Strings.Instance;
            var culture = CultureInfo.CurrentCulture;
            name ??= s.GameMods_UnknownSender;
            if (triggers && lights) return string.Format(culture, s.Devices_GameModTriggersAndLights_Format, name);
            if (triggers) return string.Format(culture, s.Devices_GameModTriggers_Format, name);
            if (lights) return string.Format(culture, s.Devices_GameModLights_Format, name);
            // A mouse, keyboard or lighting row is no controller.
            if (peripheralLights) return string.Format(culture, s.Devices_GameModPeripheralLights_Format, name);
            if (impulseVibrations) return string.Format(culture, s.Devices_GameModImpulse_Format, name);
            return string.Empty;
        }
    }
}
