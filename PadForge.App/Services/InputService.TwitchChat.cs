using System;
using System.Globalization;
using PadForge.Common.Input;
using PadForge.Common.Input.Twitch;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Resources.Strings;
using PadForge.ViewModels;

namespace PadForge.Services
{
    public partial class InputService
    {
        // ─────────────────────────────────────────────
        //  Twitch chat (#555)
        // ─────────────────────────────────────────────

        // Cache key for the Dashboard line: the row INSTANCE plus its
        // StatusVersion and the pause, the Game Telemetry card's key, since a
        // reopen restarts the version at 0.
        private object _twitchChatStatusDevice;
        private int _twitchChatStatusVersion = int.MinValue;
        private bool _twitchChatStatusPaused;
        private const int TwitchChatStatusStopped = -1;

        /// <summary>The Dashboard card's line and flame (issue #555): the same
        /// text as the Devices row, rebuilt on the dashboard tick only when
        /// the row's StatusVersion or the pause moves. "Stopped" while the
        /// switch is off, the engine is down, or the row has not opened
        /// yet.</summary>
        private void UpdateTwitchChatStatus()
        {
            var dash = _mainVm.Dashboard;
            var im = _inputManager;
            var row = im != null && im.IsRunning && dash.EnableTwitchChat ? im.TwitchChat : null;
            dash.IsTwitchChatOpen = row != null;
            if (row == null)
            {
                dash.IsTwitchChatRunning = false;
                if (_twitchChatStatusVersion == TwitchChatStatusStopped)
                    return;
                _twitchChatStatusDevice = null;
                _twitchChatStatusVersion = TwitchChatStatusStopped;
                dash.TwitchChatStatus = Strings.Instance.Common_Stopped;
                return;
            }
            int version = row.StatusVersion;
            bool paused = TwitchChatRuntime.Paused;
            if (ReferenceEquals(_twitchChatStatusDevice, row) && _twitchChatStatusVersion == version
                && _twitchChatStatusPaused == paused)
                return;
            _twitchChatStatusDevice = row;
            _twitchChatStatusVersion = version;
            _twitchChatStatusPaused = paused;
            var state = row.State;
            dash.TwitchChatStatus = BuildTwitchChatStatus(row.Channel, state, paused);
            dash.IsTwitchChatRunning = state == TwitchChatState.Reading;
        }

        /// <summary>Drops the cache, for a language change.</summary>
        private void ResetTwitchChatStatusCache()
        {
            _twitchChatStatusDevice = null;
            _twitchChatStatusVersion = int.MinValue;
        }

        /// <summary>The line the Dashboard card and the Devices row share:
        /// the channel the row reads, or why it reads nothing, and the
        /// pause.</summary>
        internal static string BuildTwitchChatStatus(string channel, TwitchChatState? state, bool paused)
        {
            var s = Strings.Instance;
            var culture = CultureInfo.CurrentCulture;
            string line = state switch
            {
                null => s.TwitchChat_Status_NoChannel,
                TwitchChatState.Reading => string.Format(culture, s.TwitchChat_Status_Reading_Format, channel),
                TwitchChatState.Retrying => string.Format(culture, s.TwitchChat_Status_Retrying_Format, channel),
                TwitchChatState.NoChat => string.Format(culture, s.TwitchChat_Status_NoChat_Format, channel),
                _ => string.Format(culture, s.TwitchChat_Status_Connecting_Format, channel),
            };
            return paused ? line + " " + s.TwitchChat_Status_Paused : line;
        }

        /// <summary>The selected row's line on the Devices page, rebuilt only
        /// when the row says its status moved, the Game Telemetry row's
        /// cache.</summary>
        private static void UpdateTwitchChatDevicePanel(DevicesViewModel devVm, UserDevice ud)
        {
            if (!devVm.IsTwitchChatDevice) return;
            if (ud.Device is TwitchChatDevice row)
            {
                int version = row.StatusVersion + (TwitchChatRuntime.Paused ? 1 << 30 : 0);
                if (!ReferenceEquals(devVm.TwitchChatStatusDevice, row)
                    || devVm.TwitchChatStatusVersion != version
                    || string.IsNullOrEmpty(devVm.TwitchChatStatus))
                {
                    devVm.TwitchChatStatusDevice = row;
                    devVm.TwitchChatStatusVersion = version;
                    devVm.TwitchChatStatus = BuildTwitchChatStatus(row.Channel, row.State, TwitchChatRuntime.Paused);
                }
            }
            else
            {
                devVm.TwitchChatStatus = string.Empty;
                devVm.TwitchChatStatusDevice = null;
            }
        }

        /// <summary>The web controller's layout key for a 2D asset folder:
        /// the folder lower-cased, which is the web controller's own key for
        /// each drawing it has, and the nearest drawing for the one it
        /// lacks.</summary>
        internal static string WebLayoutFor(string folder2D) => folder2D switch
        {
            null or "" => "xbox360",
            "DS3" => "ds4",
            _ => folder2D.ToLowerInvariant(),
        };

        /// <summary>The controller picture page's view of a slot (#555): its
        /// combined output, the drawing its virtual controller wears and the
        /// finish the pad page shows. Null while the slot is off. Called on
        /// the page's socket task, so it reads only fields written whole (a
        /// string, an enum, a struct copy), and the server catches a throw
        /// from a collection the UI changes underneath.</summary>
        internal WebControllerServer.DisplayFrame? DisplayFrameFor(int slot)
        {
            var im = _inputManager;
            if (im == null || slot < 0 || slot >= InputManager.MaxPads) return null;
            var created = SettingsManager.SlotCreated;
            if (slot >= created.Length || !created[slot]) return null;
            var pads = _mainVm.Pads;
            var pad = slot < pads.Count ? pads[slot] : null;
            if (pad == null) return null;
            string profileId = pad.ProfileId;
            var type = pad.OutputType;
            var (name2D, _) = HMaestroProfileCatalog.ResolveAssetFolders(profileId, type);
            var (family, set) = PadForge.Models2D.Controller2DColorways.For(name2D);
            string finish = family != null && set != null ? pad.GetModelAppearance(family) ?? string.Empty : string.Empty;
            // The pad page's rule (PadViewModel.PreviewRidesRawSurface): a
            // Nintendo slot and a Valve lettered one show their raw surface.
            bool raw = type == VirtualControllerType.Nintendo
                || (type == VirtualControllerType.Extended && PadForge.Models2D.NintendoPreviewMap.IsValve(profileId));
            var pose = raw
                ? DisplayPose.FromRaw(im.CombinedRawHidStates[slot], profileId)
                : DisplayPose.FromGamepad(im.CombinedOutputStates[slot]);
            return new WebControllerServer.DisplayFrame(pose, WebLayoutFor(name2D), finish);
        }

        /// <summary>The first slot (0-based) the Twitch Chat row is assigned
        /// to, or -1, for the picture page's default.</summary>
        internal static int FirstTwitchChatSlot()
        {
            var id = TwitchChatDevice.Identity;
            int first = -1;
            lock (SettingsManager.UserSettings.SyncRoot)
            {
                foreach (var us in SettingsManager.UserSettings.Items)
                    if (us != null && us.InstanceGuid == id && us.MapTo >= 0 && (first < 0 || us.MapTo < first))
                        first = us.MapTo;
            }
            return first;
        }

        /// <summary>The 1-based number PadForge shows for a slot, its index +
        /// 1 for a slot in no group's order list (the fallback
        /// GetGlobalSlotNumber names).</summary>
        internal static int ShownSlotNumber(int slot)
        {
            int number = SettingsManager.SlotOrders.GetGlobalSlotNumber(slot);
            return number > 0 ? number : slot + 1;
        }

        /// <summary>Re-derives the Twitch Chat row's words, the voice row's
        /// refresh: its objects and its button span follow the list, or a
        /// word added after the row opened could not be mapped until the row
        /// reopened.</summary>
        private void RefreshChatObjects()
        {
            try
            {
                lock (SettingsManager.UserDevices.SyncRoot)
                {
                    foreach (var ud in SettingsManager.UserDevices.Items)
                    {
                        if (ud?.Device == null || ud.CapType != PadForge.Engine.InputDeviceType.TwitchChat) continue;
                        ud.DeviceObjects = ud.Device.GetDeviceObjects();
                        ud.RawButtonCount = ud.Device.RawButtonCount;
                    }
                }
            }
            catch { /* refresh is best-effort */ }
        }

        private void OnChatWordFreed(int button)
            => _dispatcher.BeginInvoke(new Action(() => FreedButtonCleanup.ForChatWord(button, _mainVm.Pads)));

        private void OnVoicePhraseFreed(int button)
            => _dispatcher.BeginInvoke(new Action(() => FreedButtonCleanup.ForVoicePhrase(button, _mainVm.Pads)));

        private void OnNfcTagFreed(int button)
            => _dispatcher.BeginInvoke(new Action(() => FreedButtonCleanup.ForNfcTag(button, _mainVm.Pads)));

        private void OnHandheldButtonFreed(int button)
            => _dispatcher.BeginInvoke(new Action(() => FreedButtonCleanup.ForHandheldButton(button, _mainVm.Pads)));

        /// <summary>The chat word list changed (#555): the voice phrase
        /// handler's rules. Re-derive the row's objects under the UserDevices
        /// lock, refresh the pickers and the Devices preview outside it, and
        /// persist.</summary>
        private void OnChatCommandRegistryChanged(object sender, EventArgs e)
        {
            _dispatcher.BeginInvoke(new Action(() =>
            {
                RefreshChatObjects();
                try
                {
                    var devVm = _mainVm.Devices;
                    var selected = devVm?.SelectedDevice;
                    if (devVm != null && devVm.IsTwitchChatDevice && selected != null
                        && FindUserDevice(selected.InstanceGuid) is { CapType: PadForge.Engine.InputDeviceType.TwitchChat } ud)
                        devVm.RebuildNamedButtons(ud.DeviceObjects);
                }
                catch { /* preview refresh is cosmetic */ }
                try
                {
                    foreach (var padVm in _mainVm.Pads)
                        if (padVm != null) RefreshAvailableInputsForSlot(padVm);
                }
                catch { /* picker refresh is cosmetic */ }
                try { _settingsService?.Save(); } catch { /* persisted on next save regardless */ }
            }));
        }
    }
}
