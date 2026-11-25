using System.Linq;
using System.Threading.Tasks;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using PadForge.Services.Automation;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// A read's documentation page, on the web and in PadForge (#600 item 14,
    /// AS-59): the result links the page's address, which a client fetches
    /// itself, and the skill's copy, which PadForge serves over
    /// resources/read, so an assistant with no web access reads the page
    /// from PadForge.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class AssistantDocsCopyTests
    {
        [Fact]
        public async Task ARead_LinksThePageOnTheWebAndItsCopy_WhichPadForgeServes()
        {
            using var rig = new AssistantRig(AutomationLevel.Read);
            var client = await rig.ConnectAsync();
            var read = await client.CallToolAsync("read", AssistantRig.Args(("id", "settings/Settings_Switch")));
            var links = read.Content.OfType<ResourceLinkBlock>().ToList();
            Assert.Equal(new[] { AssistantDocs.Root + "features/settings/", AssistantSkill.Root + "references/docs/features/settings.md" }, links.Select(l => l.Uri));
            Assert.Equal(new[] { "text/html", "text/markdown" }, links.Select(l => l.MimeType));

            // The copy is served, and it is the page's text.
            var served = await client.ReadResourceAsync(links[1].Uri);
            string text = ((TextResourceContents)served.Contents.Single()).Text;
            Assert.StartsWith("<!-- " + AssistantDocs.Root + "features/settings/ ", text);
            Assert.Contains("\n# ", text);
            Assert.Equal(AssistantSkill.Files.Single(f => f.Uri == links[1].Uri).Text, text);

            // A read with no page links nothing.
            Assert.Empty((await client.CallToolAsync("read", AssistantRig.Args(("id", "pad/Pad_Deadzone@c1")))).Content.OfType<ResourceLinkBlock>());
        }

        /// <summary>Only an address of the table has a copy.</summary>
        [Fact]
        public void OnlyAPageOfTheTable_HasACopy()
        {
            Assert.Equal(AssistantSkill.Root + "references/docs/troubleshooting.md", AssistantDocs.CopyUri(AssistantDocs.Root + "troubleshooting/"));
            Assert.Null(AssistantDocs.CopyUri(AssistantDocs.Root + "features/wheel/"));
            Assert.Null(AssistantDocs.CopyUri("https://example.com/docs/features/settings/"));
            Assert.Null(AssistantDocs.CopyUri(null));
            Assert.Equal("references/docs/start/first-controller.md", AssistantDocs.CopyPathOf("start/first-controller/"));
        }
    }
}
