using System;
using PadForge.Services;
using PadForge.Services.Automation.Web;
using PadForge.ViewModels;

namespace PadForge
{
    public partial class MainWindow
    {
        private Views.AnswerPairDialog _answerPairing;

        /// <summary>
        /// Answer From Another Device (#600): the page at /answer that the
        /// web controller's listener serves with PadForge's rights, its row
        /// under the Assistants card's More, and the pairing window. The
        /// listener runs for the page alone while it is wanted and the web
        /// controller is off (InputService.ApplyAnswerRoute).
        /// </summary>
        private void StartAnswerPage()
        {
            var answers = WebAnswer.Shared;
            answers.Attach(_automation.Approvals);
            // A paired or removed device is kept in PadForge.xml.
            answers.Changed += (s, e) => Dispatcher.BeginInvoke(new Action(() => _settingsService?.MarkDirty()));
            var view = new AnswerAccessViewModel(answers)
            {
                Post = action => Dispatcher.BeginInvoke(action),
                OpenPairing = OpenAnswerPairing,
            };
            _viewModel.Dashboard.AnswerAccess = view;
            PadForge.Services.Catalog.LiveCatalog.AddProvider(view.CatalogItems);
            // The row shows while a web assistant is allowed, and the
            // listener follows the page's state and the row.
            void Follow() => Dispatcher.BeginInvoke(new Action(() =>
            {
                view.Refresh();
                _inputService?.ApplyAnswerRoute();
            }));
            answers.StateChanged += (s, e) => Follow();
            WebAccessStore.Shared.Changed += (s, e) => Follow();
            WebAccessStore.Shared.Loaded += (s, e) => Follow();
            Follow();
        }

        /// <summary>Pair a Device…: a window of its own, hidden from screen
        /// capture, with a new pairing. One shows at a time.</summary>
        private void OpenAnswerPairing()
        {
            if (_answerPairing != null)
            {
                _answerPairing.Activate();
                return;
            }
            var dialog = new Views.AnswerPairDialog(WebAnswer.Shared) { Owner = this };
            dialog.Closed += (s, e) => _answerPairing = null;
            _answerPairing = dialog;
            dialog.Show();
        }
    }
}
