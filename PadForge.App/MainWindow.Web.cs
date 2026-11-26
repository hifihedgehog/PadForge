using System;
using PadForge.Resources.Strings;
using PadForge.Services.Automation.Web;
using PadForge.ViewModels;

namespace PadForge
{
    public partial class MainWindow
    {
        private WebDoor _webDoor;

        /// <summary>
        /// Reach PadForge From the Web (#600 item 11): the web door, the
        /// page's switch and card, and the title bar's globe. With the switch
        /// on in PadForge.xml, the helper starts with PadForge, and it ends
        /// with PadForge through the launcher's job.
        /// </summary>
        private void StartWebDoor()
        {
            _webDoor = new WebDoor(_automation, WebAccessStore.Shared, new PadForge.Services.Plugins.PluginLauncher());
            // A change the user or a sign-in made is kept. A file read in
            // starts the helper through the door when its switch is on.
            WebAccessStore.Shared.Changed += (s, e) => Dispatcher.BeginInvoke(new Action(() => _settingsService?.MarkDirty()));
            // Reset to Defaults turns both doors off as their switches do,
            // before it empties the store.
            if (_settingsService != null)
                _settingsService.TurnWebDoorsOff = () =>
                {
                    if (WebAccessStore.Shared.NetworkOn) _webDoor?.TurnNetworkOff();
                    if (WebAccessStore.Shared.On) _webDoor?.TurnOff();
                };
            var view = new WebAccessViewModel(_webDoor, WebAccessStore.Shared)
            {
                Post = action => Dispatcher.BeginInvoke(action),
                // Turning it on asks first: an extra that listens on the network.
                AskToTurnOn = () => Views.ConfirmDialog.Show(this, Strings.Instance.Web_Reach_Ask_Title, Strings.Instance.Web_Reach_Ask_Body,
                    Strings.Instance.Web_Reach_Ask_Confirm, kind: Views.ConfirmKind.Primary),
                // Reach PadForge From This Network (#600 item 53): it asks
                // first too, and its key and port windows are the user's.
                AskToTurnNetworkOn = () => Views.ConfirmDialog.Show(this, Strings.Instance.Net_Reach_Ask_Title, Strings.Instance.Net_Reach_Ask_Body,
                    Strings.Instance.Web_Reach_Ask_Confirm, kind: Views.ConfirmKind.Primary),
                AskNewKey = cap => Views.NetworkAssistantDialog.Ask(this, cap, WebAccessStore.Shared.Keys.Count >= NetworkDoor.MostKeys),
                ShowKey = (name, key, address) => Views.NetworkAssistantDialog.ShowKey(this, name, key, address),
                AskPort = port => Views.NetworkPortDialog.Ask(this, port),
                UserLevel = () => _automation?.Level ?? PadForge.Services.Automation.AutomationLevel.Commands,
                // A key's reach (#600 DESIGN-2 N3), as an Allowed row's.
                PickReachDevices = (key, name, reach) => Views.PickDevicesDialog.Pick(this, _automation?.Sessions.Devices, key, name, reach),
            };
            _viewModel.Dashboard.PickReachDevices = (key, name, reach) => Views.PickDevicesDialog.Pick(this, _automation?.Sessions.Devices, key, name, reach);
            _viewModel.Dashboard.WebAccess = view;
            // The switch, the card's controls and each allowed web assistant
            // join the catalog, each the user's alone (#600 items 1 and 2).
            PadForge.Services.Catalog.LiveCatalog.AddProvider(view.CatalogItems);
            if (WebAccessStore.Shared.On || WebAccessStore.Shared.NetworkOn) _webDoor.Start();
        }

        private void StopWebDoor()
        {
            _webDoor?.Dispose();
            _webDoor = null;
        }
    }
}
