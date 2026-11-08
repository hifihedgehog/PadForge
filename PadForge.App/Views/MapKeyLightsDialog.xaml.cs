using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using PadForge.Common.Input;
using PadForge.Common.Input.Peripherals;
using PadForge.Resources.Strings;
using PadForge.ViewModels;

namespace PadForge.Views
{
    /// <summary>
    /// Map Key Lights (#557): one key light lit at a time, named by the key
    /// pressed next (<see cref="MapKeyLightsViewModel"/>). The key is found
    /// by the scan code its window message carries, as the Key Colors
    /// card's picker finds it, so the physical key is the one named on any
    /// input layout. The footer exports the model's names as a file or
    /// imports one, the Learn Handheld Button dialog's footer.
    /// </summary>
    public partial class MapKeyLightsDialog : Wpf.Ui.Controls.FluentWindow
    {
        private sealed class ExportFile
        {
            [JsonPropertyName("padforgeKeyLights")] public int Version { get; set; } = 1;
            [JsonPropertyName("product")] public string Product { get; set; }
            [JsonPropertyName("device")] public string Device { get; set; }
            [JsonPropertyName("names")] public string Names { get; set; }
        }

        private const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105;
        private const int VK_TAB = 0x09, VK_SNAPSHOT = 0x2C;

        private readonly MapKeyLightsViewModel _vm;

        internal MapKeyLightsDialog(OutputPath path, string deviceName)
        {
            InitializeComponent();
            _vm = new MapKeyLightsViewModel(path, deviceName);
            DataContext = _vm;
            MouseLeftButtonDown += (_, __) => { try { DragMove(); } catch { } };
            PreviewKeyDown += OnPreviewKeyDown;
            PreviewKeyUp += OnPreviewKeyUp;
            Closed += (_, _) => _vm.Dispose();
        }

        private void StartButton_Click(object sender, RoutedEventArgs e)
        {
            _vm.Start();
            // The keys go to the window while it listens, wherever the focus
            // was: Start itself keeps it, and Space no longer presses it.
            Keyboard.Focus(this);
        }

        private void BackButton_Click(object sender, RoutedEventArgs e) => _vm.Back();
        private void SkipButton_Click(object sender, RoutedEventArgs e) => _vm.Skip();
        private void ForgetButton_Click(object sender, RoutedEventArgs e) => _vm.Forget();
        private void StopButton_Click(object sender, RoutedEventArgs e) => _vm.Stop();
        private void ForgetAllButton_Click(object sender, RoutedEventArgs e) => _vm.ForgetAll();

        private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

        // ── Listening ──

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (!_vm.IsListening) return;
            e.Handled = true;
            var msg = ComponentDispatcher.CurrentKeyboardMessage;
            if (msg.message is not (WM_KEYDOWN or WM_SYSKEYDOWN) || e.IsRepeat) return;
            int vk = (int)(long)msg.wParam & 0xFF;
            if (vk == VK_TAB && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
            {
                _vm.Stop();
                return;
            }
            // AltGr's Left Ctrl is no key the user pressed. Its Right Alt
            // names the light.
            if (KeyLightsView.IsAltGrLeftControl(msg)) return;
            NameFrom(KeyLightsView.ScanKey(msg), vk);
        }

        private void OnPreviewKeyUp(object sender, KeyEventArgs e)
        {
            if (!_vm.IsListening) return;
            e.Handled = true;
            var msg = ComponentDispatcher.CurrentKeyboardMessage;
            if (msg.message is not (WM_KEYUP or WM_SYSKEYUP)) return;
            // Windows sends Print Screen's release alone.
            int vk = (int)(long)msg.wParam & 0xFF;
            if (vk == VK_SNAPSHOT) NameFrom(KeyLightsView.ScanKey(msg), vk);
        }

        private void NameFrom(int scanKey, int vk)
        {
            ushort usage = scanKey != 0
                ? _vm.Choose(KbmHidKeyMap.UsagesOfScanKey(scanKey))
                : KbmHidKeyMap.Resolve(vk, KbmHidKeyMap.UiLayout()) is { IsKeyboard: true } key ? key.Usage : (ushort)0;
            if (usage != 0) _vm.Name(usage);
        }

        // ── Export / import ──

        private void ExportButton_Click(object sender, RoutedEventArgs e)
        {
            string product = _vm.Product;
            if (string.IsNullOrEmpty(product)) return;
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = Strings.Instance.MapKeyLights_Export,
                Filter = Strings.Instance.FileDialog_KeyLightsFilter,
                FileName = SafeFileName(_vm.DeviceName) + "-key-lights.json",
            };
            if (dlg.ShowDialog(this) != true) return;
            try
            {
                var names = LearnedKeyMaps.For(product);
                var file = new ExportFile { Product = product, Device = _vm.DeviceName, Names = LearnedKeyMaps.Format(names) };
                File.WriteAllText(dlg.FileName, JsonSerializer.Serialize(file, new JsonSerializerOptions { WriteIndented = true }));
                ShowNote(string.Format(Strings.Instance.MapKeyLights_ExportedFormat, names.Count));
            }
            catch (Exception ex)
            {
                ShowNote(ex.Message);
            }
        }

        private void ImportButton_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = Strings.Instance.MapKeyLights_Import,
                Filter = Strings.Instance.FileDialog_KeyLightsFilter,
            };
            if (dlg.ShowDialog(this) != true) return;
            try
            {
                var file = JsonSerializer.Deserialize<ExportFile>(File.ReadAllText(dlg.FileName));
                if (string.IsNullOrWhiteSpace(file?.Product))
                {
                    ShowNote(Strings.Instance.MapKeyLights_ImportNotAFile);
                    return;
                }
                var names = LearnedKeyMaps.Parse(file.Names);
                LearnedKeyMaps.Replace(file.Product.Trim(), names);
                ShowNote(string.Equals(file.Product.Trim(), _vm.Product, StringComparison.OrdinalIgnoreCase)
                    ? string.Format(Strings.Instance.MapKeyLights_ImportedFormat, names.Count)
                    : string.Format(Strings.Instance.MapKeyLights_ImportedOtherFormat, names.Count, file.Device ?? file.Product));
            }
            catch (Exception ex)
            {
                ShowNote(ex.Message);
            }
        }

        private void ShowNote(string text)
        {
            NoteText.Text = text ?? string.Empty;
            NoteText.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        }

        private static string SafeFileName(string s)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var chars = (s ?? "keyboard").Select(c => invalid.Contains(c) || c == ' ' ? '-' : c).ToArray();
            string r = new string(chars).Trim('-');
            return r.Length == 0 ? "keyboard" : r;
        }
    }
}
