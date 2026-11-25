using System.Globalization;
using System.Net;
using System.Text;

namespace PadForge.Services.Automation.Web
{
    /// <summary>
    /// The sign-in pages the web helper shows in the user's browser (#600
    /// item 11). Plain HTML with no script, no outside file and no form,
    /// under a policy that loads nothing and lets no other site frame it.
    /// Every word a client supplies is escaped. The waiting page shows the
    /// sign-in's six digits and looks again every two seconds, and once the
    /// user answers on the card it sends the browser back to the client.
    ///
    /// <para>Its words come from PadForge in the interface language, with
    /// the door's state (<see cref="IWebAppSide.Word"/>): the helper reads
    /// none of PadForge's strings itself, since their resource manager reads
    /// every attribute of PadForge's assembly, WPF's among them, and the
    /// helper loads no WPF (#600 item 52).</para>
    /// </summary>
    internal static class WebPages
    {
        /// <summary>The string keys of every word the pages show, which the
        /// door sends.</summary>
        internal static readonly string[] WordKeys =
        {
            "Web_SignIn_Title", "Web_SignIn_Asks_Format", "Web_SignIn_CallsItself_Format", "Web_SignIn_LocalWarning",
            "Web_SignIn_Code", "Web_SignIn_Moves", "Web_SignIn_NotAccepting", "Web_SignIn_CannotGoOn", "Web_SignIn_Expired",
        };

        /// <summary>The waiting page of a sign-in.</summary>
        internal static WebReply Waiting(IWebAppSide app, WebSignIn signIn, string baseUrl)
        {
            var culture = CultureInfo.CurrentCulture;
            var body = new StringBuilder();
            body.Append("<p class=\"lead\">").Append(E(string.Format(culture, app.Word("Web_SignIn_Asks_Format"), signIn.Origin))).Append("</p>");
            if (!string.IsNullOrEmpty(signIn.Name))
                body.Append("<p class=\"muted\">").Append(E(string.Format(culture, app.Word("Web_SignIn_CallsItself_Format"), signIn.Name))).Append("</p>");
            if (signIn.Loopback) body.Append("<p class=\"warn\">").Append(E(app.Word("Web_SignIn_LocalWarning"))).Append("</p>");
            body.Append("<p>").Append(E(app.Word("Web_SignIn_Code"))).Append("</p>");
            body.Append("<p class=\"code\">").Append(E(WebSecrets.Spaced(signIn.Code))).Append("</p>");
            body.Append("<p class=\"muted\">").Append(E(app.Word("Web_SignIn_Moves"))).Append("</p>");
            string next = baseUrl + "/authorize/next?sid=" + System.Uri.EscapeDataString(signIn.Sid);
            return Page(app, 200, body.ToString(), "<meta http-equiv=\"refresh\" content=\"2;url=" + E(next) + "\">");
        }

        /// <summary>A page that ends a sign-in without sending the browser
        /// anywhere.</summary>
        internal static WebReply Refused(IWebAppSide app, string words) => Page(app, 403, "<p class=\"lead\">" + E(words) + "</p>", null);

        internal static string NotAccepting(IWebAppSide app) => app.Word("Web_SignIn_NotAccepting");

        /// <summary>A sign-in that cannot go on. The reason is for a test and
        /// a developer reading the page's source, in a comment the page does
        /// not show.</summary>
        internal static string CannotGoOn(IWebAppSide app, string reason) => app.Word("Web_SignIn_CannotGoOn") + "\u0000" + reason;

        internal static string Expired(IWebAppSide app) => app.Word("Web_SignIn_Expired");

        private static WebReply Page(IWebAppSide app, int status, string body, string head)
        {
            string reason = null;
            int cut = body.IndexOf("\u0000", System.StringComparison.Ordinal);
            if (cut >= 0)
            {
                int end = body.IndexOf("</p>", cut, System.StringComparison.Ordinal);
                reason = body.Substring(cut + 1, end - cut - 1);
                body = body.Substring(0, cut) + body.Substring(end);
            }
            var html = new StringBuilder();
            html.Append("<!doctype html><html lang=\"").Append(E(CultureInfo.CurrentUICulture.TwoLetterISOLanguageName)).Append("\"><head><meta charset=\"utf-8\">")
                .Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">")
                .Append("<meta name=\"referrer\" content=\"no-referrer\">")
                .Append(head ?? string.Empty)
                .Append("<title>").Append(E(app.Word("Web_SignIn_Title"))).Append("</title><style>")
                .Append(":root{color-scheme:light dark;--bg:#f4f6f9;--card:#fff;--text:#141a24;--muted:#5b6678;--ember:#e0561c;--code:#c2410c}")
                .Append("@media (prefers-color-scheme:dark){:root{--bg:#0a0e15;--card:#151c29;--text:#eef2f7;--muted:#8693a6;--ember:#ff8a57;--code:#ffb08a}}")
                .Append("body{margin:0;background:var(--bg);color:var(--text);font-family:\"Segoe UI Variable Text\",\"Segoe UI\",system-ui,sans-serif}")
                .Append("main{box-sizing:border-box;max-width:440px;margin:12vh auto;padding:22px 24px;background:var(--card);border-radius:10px}")
                .Append("h1{margin:0 0 12px;font-size:13px;font-weight:600;color:var(--ember)}p{margin:0 0 10px;font-size:15px;line-height:1.45}")
                .Append(".lead{font-size:18px;font-weight:600}.muted{color:var(--muted);font-size:13px}.warn{color:var(--ember);font-size:13px}")
                .Append(".code{font-size:34px;font-weight:600;letter-spacing:6px;color:var(--code);margin:6px 0 14px}")
                .Append("</style></head><body><main><h1>PadForge</h1>").Append(body).Append("</main>");
            if (reason != null) html.Append("<!-- ").Append(E(reason).Replace("--", "- -")).Append(" -->");
            html.Append("</body></html>");
            return new WebReply(status, WebReply.Html, html.ToString())
                .With("Content-Security-Policy", "default-src 'none'; style-src 'unsafe-inline'; frame-ancestors 'none'; form-action 'none'; base-uri 'none'")
                .With("X-Frame-Options", "DENY")
                .With("X-Content-Type-Options", "nosniff")
                .With("Referrer-Policy", "no-referrer")
                .With("Cache-Control", "no-store");
        }

        private static string E(string text) => WebUtility.HtmlEncode(text ?? string.Empty);
    }
}
