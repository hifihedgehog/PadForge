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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AlwaysShowTrayIconPersistsAndDefaultsOff(bool on)
    {
        var serializer = new XmlSerializer(typeof(AppSettingsData));
        using var writer = new StringWriter();
        serializer.Serialize(writer, new AppSettingsData { AlwaysShowTrayIcon = on, CloseToTray = !on });
        using var reader = new StringReader(writer.ToString());
        var restored = (AppSettingsData)serializer.Deserialize(reader);
        Assert.Equal(on, restored.AlwaysShowTrayIcon);
        Assert.Equal(!on, restored.CloseToTray);

        using var older = new StringReader("<AppSettingsData><CloseToTray>true</CloseToTray></AppSettingsData>");
        Assert.False(((AppSettingsData)serializer.Deserialize(older)).AlwaysShowTrayIcon);
        Assert.False(new AppSettingsData().AlwaysShowTrayIcon);

        var settings = new SettingsViewModel();
        var changes = new List<string>();
        settings.PropertyChanged += (_, e) => changes.Add(e.PropertyName);
        Assert.False(settings.AlwaysShowTrayIcon);
        settings.AlwaysShowTrayIcon = true;
        Assert.Equal(new[] { nameof(SettingsViewModel.AlwaysShowTrayIcon) }, changes);
        Assert.False(settings.CloseToTray);
        // The generic reset re-raises the property after setting it (see
        // SettingResetTests), so only the name is pinned here, not a count.
        settings.ResetSettingCommand.Execute(nameof(SettingsViewModel.AlwaysShowTrayIcon));
        Assert.False(settings.AlwaysShowTrayIcon);
        Assert.All(changes, name => Assert.Equal(nameof(SettingsViewModel.AlwaysShowTrayIcon), name));
    }

    [Fact]
    public void WindowSettingReachesLoadSaveResetAndTheCheckbox()
    {
        string settings = Read("PadForge.App/Services/SettingsService.cs");
        string window = Read("PadForge.App/MainWindow.xaml.cs");
        string view = Read("PadForge.App/Views/SettingsPage.xaml");
        // Both are internal: the assistants' undo takes and puts back the app
        // settings through them (#597).
        Assert.Contains("vm.CloseToTray = appSettings.CloseToTray;", Between(settings,
            "internal void LoadAppSettings(", "internal AppSettingsData BuildAppSettings("));
        Assert.Contains("CloseToTray = vm.CloseToTray,", settings[(settings.IndexOf("internal AppSettingsData BuildAppSettings(", StringComparison.Ordinal))..]);
        Assert.Contains("LoadAppSettings(new AppSettingsData", Between(settings,
            "public void ResetToDefaults()", "public void Reload()"));
        Assert.Contains("nameof(SettingsViewModel.CloseToTray)", Between(window,
            "_viewModel.Settings.PropertyChanged +=", "_viewModel.Dashboard.PropertyChanged +="));
        Assert.Contains("IsChecked=\"{Binding CloseToTray}\"", view);
        Assert.Contains("Settings_CloseToTrayTip", view);
        Assert.Contains("vm.AlwaysShowTrayIcon = appSettings.AlwaysShowTrayIcon;", Between(settings,
            "internal void LoadAppSettings(", "internal AppSettingsData BuildAppSettings("));
        Assert.Contains("AlwaysShowTrayIcon = vm.AlwaysShowTrayIcon,", settings[(settings.IndexOf("internal AppSettingsData BuildAppSettings(", StringComparison.Ordinal))..]);
        Assert.Contains("nameof(SettingsViewModel.AlwaysShowTrayIcon)", Between(window,
            "_viewModel.Settings.PropertyChanged +=", "_viewModel.Dashboard.PropertyChanged +="));
        Assert.Contains("IsChecked=\"{Binding AlwaysShowTrayIcon}\"", view);
        Assert.Contains("Settings_AlwaysShowTrayIconTip", view);
        Assert.True(SettingsViewModel.CanResetSetting(nameof(SettingsViewModel.AlwaysShowTrayIcon)));
    }

    [Fact]
    public void BothWindowStringsExistInEveryLocale()
    {
        // The files that hold every string. A partial translation and British
        // English take what they leave out from Strings.resx.
        var files = LocaleFiles.Complete.Select(f => Path.Combine(Root(), "PadForge.App/Resources/Strings", f)).ToArray();
        foreach (var path in files)
        {
            var resources = XDocument.Load(path).Root.Elements("data");
            foreach (string key in new[] { "Settings_CloseToTray", "Settings_CloseToTrayTip",
                                           "Settings_AlwaysShowTrayIcon", "Settings_AlwaysShowTrayIconTip" })
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
        // The tray's two ways out are named methods, which the window's close,
        // the tray menu and the automation command set all call (#597).
        string automation = Read("PadForge.App/MainWindow.Automation.cs");
        string toTray = Between(automation, "private void HideToTray()", "private void ExitFromTray()");
        string exitFromTray = Between(automation, "private void ExitFromTray()", "private void ToggleEngine()");
        string close = Between(window, "private async void OnClosing(", "private const string XboxSvgPath");
        int hide = close.IndexOf("HideToTray();", StringComparison.Ordinal);
        int shutdown = close.IndexOf("UnregisterDeviceNotification", StringComparison.Ordinal);
        Assert.True(hide >= 0 && shutdown > hide);
        Assert.Contains("_settingsService.Save();", toTray);
        Assert.Contains("_notifyIcon.Visible = true;", toTray);
        Assert.Contains("Hide();", toTray);
        Assert.Contains("if (_shutdownStarted) return;", close[..hide]);
        Assert.Contains("!_exitRequested", close[..hide]);
        Assert.Contains("_notifyIcon?.Icon != null", close[..hide]);
        // The tray menu is one list (#600). It holds Exit, the menu built from
        // it sends Exit to ExitFromTray and adds each item it shows, and a program
        // pressing Exit takes the same path.
        Assert.Contains(PadForge.Services.Catalog.TrayMenu.Items, item => item.Key == "Exit");
        string trayMenu = Between(window, "private void ShowTrayContextMenu()", "private void OnStateChanged(");
        string eachItem = Between(trayMenu, "foreach (var item in PadForge.Services.Catalog.TrayMenu.Items)", "menu.IsOpen = true;");
        Assert.Contains("\"Exit\" => (s, e) => ExitFromTray(),", eachItem);
        Assert.Contains("menu.Items.Add(menuItem);", eachItem);
        string assistant = Read("PadForge.App/MainWindow.Assistant.cs");
        Assert.Contains("if (key == \"Exit\") ExitFromTray();", Between(assistant, "PressTray = key =>", "MainWindow = () => this,"));
        Assert.Contains("PrepareForExit();", exitFromTray);
        Assert.Contains("PrepareForExit();", Between(app, "private void ShutdownAfterStartupError()", "protected override void OnSessionEnding("));
        string session = Between(app, "protected override void OnSessionEnding(", "private static bool IsGpuLostException(");
        Assert.Contains("base.OnSessionEnding(e);", session);
        Assert.Contains("if (!e.Cancel)", session);
        Assert.Contains("PrepareForExit();", session);
        Assert.DoesNotContain("e.Cancel =", session);
    }

    /// <summary>A window in its shutdown is never brought back, and bringing
    /// one back reads a tray icon only while there is one. On 2026-11-26 a
    /// second start's show reached a copy in its shutdown and threw on the
    /// tray icon the shutdown had let go of.</summary>
    [Fact]
    public void AWindowInItsShutdown_IsNeverBroughtBack_AndItsRestoreNeedsNoTrayIcon()
    {
        string window = Read("PadForge.App/MainWindow.xaml.cs");
        string restore = Between(window, "private void RestoreFromTray(", "private void OnCopySettings(");
        int guard = restore.IndexOf("if (_shutdownStarted) return;", StringComparison.Ordinal);
        int restoring = restore.IndexOf("_isRestoring = true;", StringComparison.Ordinal);
        Assert.True(guard >= 0 && restoring > guard, "RestoreFromTray brings back a window in its shutdown");
        Assert.Contains("if (_notifyIcon != null) _notifyIcon.Visible = _viewModel.Settings.AlwaysShowTrayIcon;", restore);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(restore, @"_notifyIcon\.Visible\b"));
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
