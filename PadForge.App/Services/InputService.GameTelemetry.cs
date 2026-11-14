using System;
using System.Globalization;
using PadForge.Common.Input.Telemetry;
using PadForge.Engine.Data;
using PadForge.Resources.Strings;
using PadForge.ViewModels;

namespace PadForge.Services
{
    public partial class InputService
    {
        // ─────────────────────────────────────────────
        //  Game telemetry (#531)
        // ─────────────────────────────────────────────

        // Cache key for the Dashboard line: the row INSTANCE plus its
        // StatusVersion, the head tracking card's key, since a reopen
        // restarts the version at 0.
        private object _gameTelemetryStatusDevice;
        private int _gameTelemetryStatusVersion = int.MinValue;
        private int _gameTelemetryStatusForward = -1;
        private const int GameTelemetryStatusStopped = -1;

        /// <summary>The Dashboard card's line and flame (issue #531): the
        /// same text as the Devices row, rebuilt on the dashboard tick only
        /// when the row's StatusVersion or the forward port moves. "Stopped"
        /// while the switch is off, the engine is down, or the row has not
        /// opened yet.</summary>
        private void UpdateGameTelemetryStatus()
        {
            var dash = _mainVm.Dashboard;
            var im = _inputManager;
            var row = im != null && im.IsRunning && dash.EnableGameTelemetry ? im.GameTelemetry : null;
            if (row == null)
            {
                dash.IsGameTelemetryReceiving = false;
                if (_gameTelemetryStatusVersion == GameTelemetryStatusStopped)
                    return;
                _gameTelemetryStatusDevice = null;
                _gameTelemetryStatusVersion = GameTelemetryStatusStopped;
                dash.GameTelemetryStatus = Strings.Instance.Common_Stopped;
                return;
            }
            int version = row.StatusVersion;
            int forward = GameTelemetryRuntime.ForwardPort;
            if (ReferenceEquals(_gameTelemetryStatusDevice, row) && _gameTelemetryStatusVersion == version
                && _gameTelemetryStatusForward == forward)
                return;
            _gameTelemetryStatusDevice = row;
            _gameTelemetryStatusVersion = version;
            _gameTelemetryStatusForward = forward;
            var status = row.Status;
            dash.GameTelemetryStatus = BuildGameTelemetryStatus(status, row.Format);
            // The Extras page's flame (#574): ember while a game's data
            // arrives, gold while the row waits for one.
            dash.IsGameTelemetryReceiving = status.Source != TelemetrySource.None;
        }

        /// <summary>Drops the cache, for a language change.</summary>
        private void ResetGameTelemetryStatusCache()
        {
            _gameTelemetryStatusDevice = null;
            _gameTelemetryStatusVersion = int.MinValue;
            _gameTelemetryStatusForward = -1;
        }

        /// <summary>
        /// The line the Dashboard card and the Devices row share: why nothing
        /// arrives, or which game sends and from where. The forward port is
        /// named whenever packets go on to one, so a user who set it can see
        /// it is in use.
        /// </summary>
        internal static string BuildGameTelemetryStatus(TelemetryStatus status, GameTelemetryFormat format)
        {
            var s = Strings.Instance;
            var culture = CultureInfo.CurrentCulture;
            string line;
            string game = status.Source == TelemetrySource.None ? null : SourceName(status);
            switch (status.State)
            {
                case TelemetryListenState.PortInUse:
                    line = string.Format(culture, s.Telemetry_Status_PortInUse_Format, status.Port);
                    break;
                case TelemetryListenState.SharedMemoryOnly:
                    line = game != null
                        ? string.Format(culture, s.Telemetry_Status_SharedMemory_Format, game)
                        : s.Telemetry_Status_WaitingSharedMemory;
                    break;
                default:
                    if (game == null)
                        line = string.Format(culture, s.Telemetry_Status_Waiting_Format, status.Port);
                    else if (status.Source is TelemetrySource.AssettoCorsa or TelemetrySource.AssettoCorsaCompetizione)
                        line = string.Format(culture, s.Telemetry_Status_SharedMemory_Format, game);
                    else
                        line = string.Format(culture, s.Telemetry_Status_Receiving_Format, status.Port, game,
                            string.IsNullOrEmpty(status.Peer) ? "?" : status.Peer);
                    break;
            }
            if (status.State == TelemetryListenState.Listening && status.ForwardPort > 0 && status.ForwardPort != status.Port)
                line += " " + string.Format(culture, s.Telemetry_Status_Forwarding_Format, status.ForwardPort);
            return line;
        }

        /// <summary>The sender's name for the status line. A game's own name
        /// is never translated. A format two games send with nothing in it
        /// that tells them apart (Project
        /// CARS 2's, which Automobilista 2 also sends, and the ETS2 plugin's,
        /// which American Truck Simulator also loads) takes the picker's
        /// entry, which names both.</summary>
        internal static string SourceName(TelemetryStatus status) => status.Source switch
        {
            TelemetrySource.ForzaSled => "Forza",
            TelemetrySource.ForzaMotorsport7 => "Forza Motorsport 7",
            TelemetrySource.ForzaMotorsport => "Forza Motorsport",
            TelemetrySource.ForzaHorizon => "Forza Horizon",
            TelemetrySource.F1 => "F1 " + (status.F1Year % 100).ToString("00", CultureInfo.InvariantCulture),
            TelemetrySource.Codemasters => "Codemasters",
            TelemetrySource.EaSportsWrc => "EA SPORTS WRC",
            TelemetrySource.BeamNg => "BeamNG.drive",
            TelemetrySource.LiveForSpeed => "Live for Speed",
            TelemetrySource.ProjectCars2 => Strings.Instance.Telemetry_Game_ProjectCars2,
            TelemetrySource.AssettoCorsa => "Assetto Corsa",
            TelemetrySource.AssettoCorsaCompetizione => "Assetto Corsa Competizione",
            TelemetrySource.TruckSimulator => Strings.Instance.Telemetry_Game_TruckSimulator,
            TelemetrySource.Custom => status.CustomName,
            _ => string.Empty,
        };

        /// <summary>The selected row's line on the Devices page, rebuilt only
        /// when the row says its status moved, the head tracker's cache.</summary>
        private static void UpdateGameTelemetryDevicePanel(DevicesViewModel devVm, UserDevice ud)
        {
            if (!devVm.IsGameTelemetryDevice) return;
            if (ud.Device is GameTelemetryDevice row)
            {
                int version = row.StatusVersion;
                if (!ReferenceEquals(devVm.GameTelemetryStatusDevice, row)
                    || devVm.GameTelemetryStatusVersion != version
                    || string.IsNullOrEmpty(devVm.GameTelemetryStatus))
                {
                    devVm.GameTelemetryStatusDevice = row;
                    devVm.GameTelemetryStatusVersion = version;
                    devVm.GameTelemetryStatus = BuildGameTelemetryStatus(row.Status, row.Format);
                }
            }
            else
            {
                devVm.GameTelemetryStatus = string.Empty;
                devVm.GameTelemetryStatusDevice = null;
            }
        }
    }
}
