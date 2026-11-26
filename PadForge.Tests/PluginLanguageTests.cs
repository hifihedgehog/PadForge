using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Threading;
using PadForge.Resources.Strings;
using PadForge.Services.Automation;
using PadForge.Services.Plugins;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// A plugin's rows and its device's card in the language in use: a
    /// change of language reaches the words of PadForge's own they show
    /// ("Not picked", "Changed", the card's two lines) while they are shown.
    /// </summary>
    [Collection("CultureSwitching")]
    public sealed class PluginLanguageTests
    {
        [Fact]
        public void APluginsRows_AndItsDeviceCard_FollowALanguageChange()
        {
            var before = CultureInfo.CurrentUICulture;
            try
            {
                Strings.ChangeCulture(CultureInfo.GetCultureInfo("en"));
                var set = new PluginSettingSet();
                PluginLines.Answer(set, AutomationLine.Parse("setting key=pick kind=input name=Shift"));
                PluginLines.Answer(set, AutomationLine.Parse("setting key=dead kind=number name=Dead min=0 max=1 step=0.5"));
                var host = new PluginSettingsHost(set, null, "Test", Dispatcher.CurrentDispatcher);
                var pick = host.Item("pick");
                Assert.Equal(Strings.Instance.Plugins_Input_None, pick.InputText);
                var heard = new List<string>();
                pick.PropertyChanged += (_, e) => heard.Add(e.PropertyName);
                host.Item("dead").PropertyChanged += (_, e) => heard.Add("dead " + e.PropertyName);

                var card = new PluginDeviceCard("Pedal Box", null);
                var cardHeard = new List<string>();
                var notifying = (object)card as INotifyPropertyChanged;
                Assert.NotNull(notifying);
                notifying.PropertyChanged += (_, e) => cardHeard.Add(e.PropertyName);

                Strings.ChangeCulture(CultureInfo.GetCultureInfo("de"));
                Assert.Contains("InputText", heard);
                Assert.Contains("ChangedStatus", heard);
                Assert.Contains("dead NumberText", heard);
                Assert.Equal(Strings.Instance.Plugins_Input_None, pick.InputText);
                Assert.Equal(string.Format(CultureInfo.CurrentCulture, Strings.Instance.Plugins_Device_AddedBy_Format, "Pedal Box"), card.AddedBy);
                Assert.Equal(string.Format(CultureInfo.CurrentCulture, Strings.Instance.Plugins_Device_Settings_Format, "Pedal Box"), card.Title);
                Assert.Contains(nameof(PluginDeviceCard.AddedBy), cardHeard);
                Assert.Contains(nameof(PluginDeviceCard.Title), cardHeard);
                System.GC.KeepAlive(host);
                System.GC.KeepAlive(card);
            }
            finally { Strings.ChangeCulture(before); }
        }
    }
}
