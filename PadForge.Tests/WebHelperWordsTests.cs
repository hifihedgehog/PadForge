using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using PadForge.Services.Automation;
using PadForge.Services.Automation.Web;
using Xunit;
using Strings = PadForge.Resources.Strings.Strings;

namespace PadForge.Tests
{
    /// <summary>
    /// The sign-in pages' words come from PadForge (#600 item 52): the door
    /// sends them in the interface language with its state, and the web
    /// helper's pages read them from its link. The helper never reads
    /// PadForge's strings itself, since their resource manager reads every
    /// attribute of PadForge's assembly, WPF's among them, and so loaded WPF
    /// into a run that loads none (ClientRunChildTests).
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class WebHelperWordsTests
    {
        [Fact]
        public async Task TheDoorsState_CarriesThePagesWords_AndTheLinkGivesThemToThePages()
        {
            using var rig = new AssistantRig(AutomationLevel.ChangeSettings);
            using var door = new WebDoor(rig.Host, new WebAccessStore(), new WebTestLauncher(), () => @"C:\PadForge\PadForge.exe", rig.PipeName);
            var words = door.State()["words"]?.AsObject();
            Assert.NotNull(words);
            Assert.Equal(WebPages.WordKeys.OrderBy(k => k), words.Select(w => w.Key).OrderBy(k => k));
            foreach (string key in WebPages.WordKeys) Assert.Equal(Strings.Get(key), words[key].GetValue<string>());

            // The helper's link takes them with the state, and its pages say
            // them. A word the state does not carry reads as its key, never
            // through PadForge's strings.
            var state = door.State();
            state["words"]["Web_SignIn_NotAccepting"] = "Words from PadForge.";
            state["words"].AsObject().Remove("Web_SignIn_Expired");
            state["words"]["Web_SignIn_Code"] = 5;
            await using var link = new WebLink(new MemoryStream());
            link.Take(state);
            Assert.Equal("Words from PadForge.", link.Word("Web_SignIn_NotAccepting"));
            Assert.Equal("Web_SignIn_Expired", link.Word("Web_SignIn_Expired"));
            Assert.Equal("Web_SignIn_Code", link.Word("Web_SignIn_Code"));
            var page = WebPages.Refused(link, WebPages.NotAccepting(link));
            Assert.Contains("Words from PadForge.", page.Body);
            Assert.Contains(System.Net.WebUtility.HtmlEncode(Strings.Get("Web_SignIn_Title")), page.Body.Replace("&#39;", "'"));
        }

        [Fact]
        public void TheHelpersCode_ReadsNoneOfPadForgesStrings()
        {
            foreach (string file in new[] { "WebHelper.cs", "WebPages.cs", "WebAuth.cs", "WebFront.cs", "WebMcp.cs", "WebReply.cs", "WebGrant.cs" })
            {
                string source = OffLimitsSource.Read("PadForge.App/Services/Automation/Web/" + file);
                // The app side's default, outside the helper, is the one
                // reference: WebLink gives the words it was sent.
                int reads = System.Text.RegularExpressions.Regex.Matches(source, @"\bStrings\.(Instance|Get|ChangeCulture)\b").Count;
                Assert.True(reads == (file == "WebAuth.cs" ? 1 : 0), file + " reads PadForge's strings " + reads + " times");
            }
        }
    }
}
