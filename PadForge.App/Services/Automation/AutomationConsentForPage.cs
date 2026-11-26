using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using PadForge.ViewModels;

namespace PadForge.Services.Automation
{
    /// <summary>
    /// The consent store as the Automation page sees it (#600 items 5 and 9):
    /// each allowed program in the page's terms, Remove, which also takes the
    /// level from its open sessions, and the Add and Remove on an assistant's
    /// row, keyed by the program that starts PadForge.exe --mcp, or by the
    /// script an interpreter runs for it (AssistantIdentity).
    /// <see cref="Changed"/> is raised when the list changes and when a
    /// program connects or leaves, since each row says whether its program
    /// is connected.
    /// </summary>
    internal sealed class AutomationConsentForPage : IAutomationConsent
    {
        private readonly AutomationConsent _store;
        private readonly AutomationSessions _sessions;
        private readonly AutomationClients _clients;

        public AutomationConsentForPage(AutomationConsent store, AutomationSessions sessions, AutomationClients clients)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _sessions = sessions;
            _clients = clients;
            _store.Changed += (s, e) => Changed?.Invoke(this, EventArgs.Empty);
            if (_clients != null) _clients.Changed += (s, e) => Changed?.Invoke(this, EventArgs.Empty);
        }

        public event EventHandler Changed;

        public IReadOnlyList<AllowedProgramView> Allowed
        {
            get
            {
                // By the store's key: scripts share their interpreter's
                // path, and each is its own program.
                var connected = _sessions?.ConnectedKeys() ?? new HashSet<string>();
                var asking = _sessions?.HeldByItsAskKeys() ?? new HashSet<string>();
                return _store.Programs.Select(p => new AllowedProgramView(p.Key, p.Name ?? ShortName(p.Path), WhoOf(p), p.Level,
                    p.LastUsed, connected.Contains(p.Key), p.Reach, asking.Contains(p.Key))).ToList();
            }
        }

        public void Remove(string key)
        {
            if (string.IsNullOrEmpty(key)) return;
            _store.Remove(key);
            _sessions?.Revoke(key);
        }

        public void SetReach(string key, ProgramReach reach)
        {
            if (string.IsNullOrEmpty(key)) return;
            // The sessions first: the store's change redraws the row, which
            // reads whether a session's own ask now holds it.
            _sessions?.SetReach(key, reach);
            _store.SetReach(key, reach);
        }

        public void AllowAssistant(string hostKey, string hostName, string program)
        {
            if (string.IsNullOrWhiteSpace(program)) return;
            _store.AllowAssistant(program, hostName);
        }

        public void ForgetAssistant(string hostKey, string program)
        {
            if (string.IsNullOrWhiteSpace(program)) return;
            Remove(AutomationConsent.AssistantKeyOf(program));
        }

        /// <summary>A program's path and signer, or a script's interpreter
        /// and file with the interpreter's signer, as one line.</summary>
        internal static string WhoOf(AllowedProgram program)
        {
            var s = PadForge.Resources.Strings.Strings.Instance;
            var culture = CultureInfo.CurrentCulture;
            string what = program.Script != null
                ? string.Format(culture, s.Automation_Consent_Running_Format, program.Path ?? "?", program.Script)
                : program.Path ?? "?";
            string signed = string.IsNullOrEmpty(program.Signer)
                ? s.Automation_Consent_NotSigned
                : string.Format(culture, s.Automation_Consent_Signed_Format, program.Signer);
            return what + " · " + signed;
        }

        private static string ShortName(string path)
            => string.IsNullOrEmpty(path) ? null : System.IO.Path.GetFileNameWithoutExtension(path);
    }
}
