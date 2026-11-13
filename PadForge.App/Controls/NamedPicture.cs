using System.Collections.Generic;
using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace PadForge.Controls
{
    /// <summary>
    /// A drawing that tells a screen reader what it shows (#574). Shapes on
    /// a panel have no part in UI Automation, so a picture drawn with them
    /// said nothing: the two stick rings of the Devices page's test showed
    /// where each stick stood and read as nothing at all. This holds such a
    /// drawing and reads as an image, with the name set on it through
    /// AutomationProperties.Name and what it shows now through
    /// AutomationProperties.HelpText. It draws nothing of its own and takes
    /// no focus.
    /// </summary>
    public sealed class NamedPicture : Decorator
    {
        protected override AutomationPeer OnCreateAutomationPeer() => new PicturePeer(this);

        private sealed class PicturePeer : FrameworkElementAutomationPeer
        {
            public PicturePeer(NamedPicture owner) : base(owner) { }

            protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Image;

            protected override string GetClassNameCore() => nameof(NamedPicture);

            // The shapes inside are the picture, and the picture is one thing.
            protected override List<AutomationPeer> GetChildrenCore() => null;
        }
    }
}
