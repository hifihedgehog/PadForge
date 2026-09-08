using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using System.Xml.Serialization;
using PadForge.Services;
using PadForge.ViewModels;

namespace PadForge.Tests;

public class WindowSettingsTests
{
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void CloseAndMinimizeOptionsPersistIndependently(bool close, bool minimize)
    {
        var serializer = new XmlSerializer(typeof(AppSettingsData));
        var settings = new AppSettingsData { CloseToTray = close, MinimizeToTray = minimize };
        using var writer = new StringWriter();
        serializer.Serialize(writer, settings);
        using var reader = new StringReader(writer.ToString());
        var restored = (AppSettingsData)serializer.Deserialize(reader);
        Assert.Equal(close, restored.CloseToTray);
        Assert.Equal(minimize, restored.MinimizeToTray);
    }

    [Fact]
    public void OlderSettingsKeepNormalCloseBehavior()
    {
        var serializer = new XmlSerializer(typeof(AppSettingsData));
        using var reader = new StringReader("<AppSettingsData><MinimizeToTray>true</MinimizeToTray></AppSettingsData>");
        var restored = (AppSettingsData)serializer.Deserialize(reader);
        Assert.False(restored.CloseToTray);
        Assert.True(restored.MinimizeToTray);
        Assert.False(new AppSettingsData().CloseToTray);
    }

    [Fact]
    public void WindowOptionsHaveSeparateNotifications()
    {
        var settings = new SettingsViewModel();
        var changes = new List<string>();
        settings.PropertyChanged += (_, e) => changes.Add(e.PropertyName);
        Assert.False(settings.CloseToTray);
        settings.CloseToTray = true;
        Assert.False(settings.MinimizeToTray);
        settings.CloseToTray = true;
        settings.CloseToTray = false;
        Assert.Equal(new[] { nameof(SettingsViewModel.CloseToTray), nameof(SettingsViewModel.CloseToTray) }, changes);
    }

    [Fact]
    public void WindowSettingReachesLoadSaveResetAndTheCheckbox()
    {
        string settings = Read("PadForge.App/Services/SettingsService.cs");
        string window = Read("PadForge.App/MainWindow.xaml.cs");
        string view = Read("PadForge.App/Views/SettingsPage.xaml");
        Assert.Contains("vm.CloseToTray = appSettings.CloseToTray;", Between(settings,
            "private void LoadAppSettings(", "private AppSettingsData BuildAppSettings("));
        Assert.Contains("CloseToTray = vm.CloseToTray,", settings[(settings.IndexOf("private AppSettingsData BuildAppSettings(", StringComparison.Ordinal))..]);
        Assert.Contains("LoadAppSettings(new AppSettingsData", Between(settings,
            "public void ResetToDefaults()", "public void Reload()"));
        Assert.Contains("nameof(SettingsViewModel.CloseToTray)", Between(window,
            "_viewModel.Settings.PropertyChanged +=", "_viewModel.Dashboard.PropertyChanged +="));
        Assert.Contains("IsChecked=\"{Binding CloseToTray}\"", view);
        Assert.Contains("Settings_CloseToTrayTip", view);
    }

    [Fact]
    public void BothWindowStringsExistInEveryLocale()
    {
        var files = Directory.GetFiles(Path.Combine(Root(), "PadForge.App/Resources/Strings"), "Strings*.resx");
        Assert.Equal(10, files.Length);
        foreach (var path in files)
        {
            var resources = XDocument.Load(path).Root.Elements("data");
            foreach (string key in new[] { "Settings_CloseToTray", "Settings_CloseToTrayTip" })
            {
                var resource = Assert.Single(resources, node => (string)node.Attribute("name") == key);
                Assert.False(string.IsNullOrWhiteSpace((string)resource.Element("value")), path + ": " + key);
            }
        }
    }

    [Fact]
    public void ExplicitAndSessionExitKeepTheShutdownPath()
    {
        string window = Read("PadForge.App/MainWindow.xaml.cs");
        string app = Read("PadForge.App/App.xaml.cs");
        string close = Between(window, "private async void OnClosing(", "private const string XboxSvgPath");
        int hide = close.IndexOf("Hide();", StringComparison.Ordinal);
        int shutdown = close.IndexOf("UnregisterDeviceNotification", StringComparison.Ordinal);
        Assert.True(hide >= 0 && shutdown > hide);
        Assert.Contains("_settingsService.Save();", close[..hide]);
        Assert.Contains("if (_shutdownStarted) return;", close[..hide]);
        Assert.Contains("!_exitRequested", close[..hide]);
        Assert.Contains("_notifyIcon?.Icon != null", close[..hide]);
        Assert.Contains("PrepareForExit();", Between(window, "exitItem.Click +=", "menu.Items.Add(exitItem)"));
        Assert.Contains("PrepareForExit();", Between(app, "private void ShutdownAfterStartupError()", "protected override void OnSessionEnding("));
        string session = Between(app, "protected override void OnSessionEnding(", "private static bool IsGpuLostException(");
        Assert.Contains("base.OnSessionEnding(e);", session);
        Assert.Contains("if (!e.Cancel)", session);
        Assert.Contains("PrepareForExit();", session);
        Assert.DoesNotContain("e.Cancel =", session);
    }

    private static string Root()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !Directory.Exists(Path.Combine(root.FullName, "PadForge.App"))) root = root.Parent;
        Assert.NotNull(root);
        return root.FullName;
    }

    private static string Read(string path) => File.ReadAllText(Path.Combine(Root(), path));

    private static string Between(string text, string begin, string end)
    {
        int first = text.IndexOf(begin, StringComparison.Ordinal);
        Assert.True(first >= 0, begin);
        int last = text.IndexOf(end, first + begin.Length, StringComparison.Ordinal);
        Assert.True(last > first, end);
        return text[first..last];
    }
}
