using System;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Win32;
using PadForge.Common.Input;
using PadForge.Engine.Common.BlissBox;
using PadForge.Engine.Data;
using PadForge.Resources.Strings;
using PadForge.Services;
using PadForge.ViewModels;
using PadForge.Views;

namespace PadForge
{
    public partial class MainWindow
    {
        /// <summary>
        /// A Bliss-Box port row's actions (issue #469): its player number, its
        /// Dreamcast screen, its Controller Pak and its native arrow poll. The
        /// transfers run on the port's worker as jobs, so the window stays
        /// live and the status line carries their progress.
        /// </summary>
        private async Task OnBlissBoxActionAsync(BlissBoxAction action)
        {
            var row = _viewModel.Devices.SelectedDevice;
            if (row == null) return;
            var port = BlissBoxRuntime.Find(FindUserDeviceForBlissBox(row.InstanceGuid));
            if (port == null || port.Replaced) return;
            var s = Strings.Instance;
            var culture = CultureInfo.CurrentCulture;

            switch (action)
            {
                case BlissBoxAction.Player:
                {
                    if (port.Session.Busy) return;
                    int player = BlissBoxPlayerDialog.Show(this, port.Session.Player, row.BlissBoxLine);
                    if (player == 0 || player == port.Session.Player) return;
                    // The port returns as a new device. A picture PadForge put
                    // on the VMU goes back first, and the old device's choices
                    // are dropped, since nothing would read them again.
                    var service = _inputService.DreamcastScreen;
                    // The dialog above runs a nested loop, in which another
                    // action may have queued a job or replaced the port.
                    if (port.Replaced || port.Session.Busy) return;
                    DreamcastScreenService.TryDecode(service.Get(port.InstanceGuid)?.AdapterPicture, out var original);
                    var job = new BlissBoxPlayerJob(player, original);
                    port.Session.Enqueue(job);
                    port.Wake();
                    var result = await job.Completion;
                    if (result.Ok)
                    {
                        port.Replaced = true;
                        service.Remove(port.InstanceGuid);
                        _viewModel.SetStatus(string.Format(culture, s.Status_BlissBoxPlayer_Format, player));
                    }
                    else _viewModel.SetStatus(JobErrorText(result), persist: true);
                    break;
                }

                case BlissBoxAction.DreamcastScreen:
                {
                    // A player change's end drops the old device's choices,
                    // which a dialog saved after it would bring back.
                    if (port.Session.Busy) return;
                    var service = _inputService.DreamcastScreen;
                    string profileId = SettingsManager.ActiveProfileId;
                    var profile = string.IsNullOrEmpty(profileId) ? null : SettingsManager.Profiles.Find(p => p.Id == profileId);
                    string profilePicture = profile != null ? profile.DreamcastPicture : _viewModel.Settings.DefaultProfileDreamcastPicture;
                    var choice = DreamcastScreenDialog.Show(this, service, port, service.Get(port.InstanceGuid),
                        DreamcastScreenService.ActiveProfileName(), profilePicture, row.BlissBoxLine);
                    if (choice == null) return;
                    if (profile != null) profile.DreamcastPicture = choice.ProfilePicture;
                    else _viewModel.Settings.DefaultProfileDreamcastPicture = choice.ProfilePicture;
                    service.Update(port.InstanceGuid, d =>
                    {
                        d.ScreenMode = choice.Mode;
                        d.Picture = choice.Picture;
                    });
                    _viewModel.SetStatus(string.Format(culture, s.Status_DreamcastScreen_Format,
                        DreamcastScreenDialog.ModeName(choice.Mode)));
                    break;
                }

                case BlissBoxAction.PakBackup:
                {
                    if (port.Session.Busy) return;
                    var dialog = new SaveFileDialog
                    {
                        Filter = s.BlissBoxPak_FileFilter,
                        DefaultExt = ".mpk",
                        FileName = string.Format(culture, s.BlissBoxPak_DefaultFileName_Format, port.Session.Player),
                    };
                    if (dialog.ShowDialog(this) != true) return;
                    // The dialog above runs a nested loop, in which another
                    // action may have queued a job or replaced the port.
                    if (port.Replaced || port.Session.Busy) return;
                    var job = new BlissBoxPakBackupJob(new Progress<double>(p =>
                        _viewModel.SetStatus(string.Format(culture, s.Status_PakReading_Format, p))));
                    port.Session.Enqueue(job);
                    port.Wake();
                    var result = await job.Completion;
                    if (!result.Ok)
                    {
                        _viewModel.SetStatus(JobErrorText(result), persist: true);
                        return;
                    }
                    try
                    {
                        await File.WriteAllBytesAsync(dialog.FileName, result.Data);
                        _viewModel.SetStatus(string.Format(culture, s.Status_PakSaved_Format, Path.GetFileName(dialog.FileName)));
                    }
                    catch (Exception ex)
                    {
                        _viewModel.SetStatus(string.Format(culture, s.Status_PakFileFailed_Format, ex.Message), persist: true);
                    }
                    break;
                }

                case BlissBoxAction.PakRestore:
                {
                    if (port.Session.Busy) return;
                    var dialog = new OpenFileDialog { Filter = s.BlissBoxPak_FileFilter };
                    if (dialog.ShowDialog(this) != true) return;
                    byte[] image;
                    try { image = await File.ReadAllBytesAsync(dialog.FileName); }
                    catch (Exception ex)
                    {
                        _viewModel.SetStatus(string.Format(culture, s.Status_PakFileFailed_Format, ex.Message), persist: true);
                        return;
                    }
                    if (image.Length != BlissBoxControllerPak.PakBytes)
                    {
                        _viewModel.SetStatus(string.Format(culture, s.Status_PakWrongSize_Format, image.Length), persist: true);
                        return;
                    }
                    if (!ConfirmDialog.Show(this, s.BlissBoxPak_RestoreTitle,
                            string.Format(culture, s.BlissBoxPak_RestoreMessage_Format, port.Session.Player),
                            s.BlissBoxPak_RestoreAction, Path.GetFileName(dialog.FileName)))
                        return;
                    // The file read above leaves the page live, so a player
                    // change or another restore may have started meanwhile.
                    if (port.Replaced || port.Session.Busy) return;
                    var job = new BlissBoxPakRestoreJob(image, new Progress<double>(p =>
                        _viewModel.SetStatus(string.Format(culture, s.Status_PakWriting_Format, p))));
                    port.Session.Enqueue(job);
                    port.Wake();
                    var result = await job.Completion;
                    if (result.Ok) _viewModel.SetStatus(string.Format(culture, s.Status_PakRestored_Format, Path.GetFileName(dialog.FileName)));
                    else _viewModel.SetStatus(JobErrorText(result), persist: true);
                    break;
                }

                case BlissBoxAction.NativeArrows:
                {
                    // The row already holds the value the checkbox or its
                    // reset set, so store that one rather than a toggle.
                    bool on = row.BlissBoxNativeArrows;
                    _inputService.DreamcastScreen.Update(port.InstanceGuid, d => d.NativeArrows = on);
                    break;
                }
            }
        }

        private static UserDevice FindUserDeviceForBlissBox(Guid instanceGuid)
        {
            var devices = SettingsManager.UserDevices;
            if (devices == null) return null;
            lock (devices.SyncRoot)
            {
                foreach (var ud in devices.Items)
                    if (ud != null && ud.InstanceGuid == instanceGuid) return ud;
            }
            return null;
        }

        /// <summary>Why a port job ended short, as the status line says it.</summary>
        internal static string JobErrorText(BlissBoxJobResult result)
        {
            var s = Strings.Instance;
            var culture = CultureInfo.CurrentCulture;
            return result.Error switch
            {
                BlissBoxJobError.Closed => s.BlissBoxJob_Closed,
                BlissBoxJobError.WrongController => s.BlissBoxJob_WrongController,
                BlissBoxJobError.NoPak => s.BlissBoxJob_NoPak,
                BlissBoxJobError.RumblePak => s.BlissBoxJob_RumblePak,
                BlissBoxJobError.BadBlock => string.Format(culture, s.BlissBoxJob_BadBlock_Format, result.Block),
                BlissBoxJobError.TooManyErrors => string.Format(culture, s.BlissBoxJob_TooManyErrors_Format, result.Block),
                BlissBoxJobError.OldFirmware => s.BlissBoxJob_OldFirmware,
                BlissBoxJobError.PlayerUnchanged => s.BlissBoxJob_PlayerUnchanged,
                _ => s.BlissBoxJob_NoReply,
            };
        }
    }
}
