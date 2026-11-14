using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace PadForge.Controls
{
    /// <summary>The label that leads a list's entries as one of the list's
    /// own items, so it wraps with them on their lines (#574): Used By and
    /// its chips on the Devices page. It draws exactly as a TextBlock does.
    ///
    /// <para>The list reads the label's words as its name. Left a plain
    /// TextBlock, the label is also an entry of the list to a screen
    /// reader: an item that says the words again and holds nothing. An
    /// element in a plain list is its own container, and the item's peer
    /// answers what that element's peer answers (ItemAutomationPeer's
    /// GetWrapper, IsControlElementCore and IsContentElementCore). This
    /// peer is neither a control nor a content element, so the item leaves
    /// both views.</para></summary>
    public sealed class ListLabel : TextBlock
    {
        protected override AutomationPeer OnCreateAutomationPeer() => new LabelPeer(this);

        private sealed class LabelPeer : TextBlockAutomationPeer
        {
            public LabelPeer(ListLabel owner) : base(owner) { }

            protected override bool IsControlElementCore() => false;

            protected override bool IsContentElementCore() => false;
        }
    }
}
