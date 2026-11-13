using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using PadForge.Engine;
using PadForge.ViewModels;
using PadForge.Views;
using Xunit;
using Strings = PadForge.Resources.Strings.Strings;

namespace PadForge.Tests
{
    /// <summary>
    /// The dialogs of the Links board (#574): the pairing dialog, Record a
    /// Gesture, the two Remote Link dialogs and Controller Presets. Devices
    /// Shared with a paired PC has its own tests
    /// (RemoteAssignmentsDialogChromeTests). Each dialog is made and read,
    /// and never shown.
    /// </summary>
    [Collection("WpfApplicationSingleton")]
    public class LinksDialogTests
    {
        private const BindingFlags Own = BindingFlags.NonPublic | BindingFlags.Instance;

        private static T Part<T>(Window dialog, string name) where T : class
        {
            var part = dialog.FindName(name) as T;
            Assert.True(part != null, name + " is not a " + typeof(T).Name + " of " + dialog.GetType().Name);
            return part;
        }

        // ── Pairing ──

        /// <summary>The dialog is titled for the family it pairs. The Wii
        /// shows its two numbered steps, every other family its paragraph.
        /// The live line shows while it has something to say. While a
        /// search runs the foot shows Cancel alone, and Pair is back when
        /// it ends with nothing paired.</summary>
        [Fact]
        public void ThePairingDialogIsTitledForItsFamily()
        {
            DialogTestHost.Run(() =>
            {
                var dialog = new PairDeviceDialog(new SettingsViewModel());
                try
                {
                    var s = Strings.Instance;
                    var family = Part<ComboBox>(dialog, "FamilyCombo");
                    var steps = Part<UIElement>(dialog, "WiiSteps");
                    var paragraph = Part<TextBlock>(dialog, "InstructionsText");
                    var live = Part<UIElement>(dialog, "StatusPanel");
                    var pair = Part<Button>(dialog, "PairButton");

                    Assert.Equal(s.WiiPair_Title, dialog.Title);
                    Assert.Null(dialog.Subtitle);
                    Assert.Equal(Visibility.Visible, steps.Visibility);
                    Assert.Equal(Visibility.Collapsed, paragraph.Visibility);
                    Assert.Equal(Visibility.Collapsed, live.Visibility);

                    var titles = new[] { s.WiiPair_Title, s.Ds3Pair_Title, s.MovePair_Title, s.SerialPair_Title, s.DjiPair_Title };
                    var paragraphs = new[] { null, s.Ds3Pair_Instructions, s.MovePair_Instructions, s.SerialPair_Instructions, s.DjiPair_Instructions };
                    for (int i = 1; i < titles.Length; i++)
                    {
                        family.SelectedIndex = i;
                        Assert.Equal(titles[i], dialog.Title);
                        Assert.Equal(Visibility.Collapsed, steps.Visibility);
                        Assert.Equal(Visibility.Visible, paragraph.Visibility);
                        Assert.Equal(paragraphs[i], paragraph.Text);
                    }
                    family.SelectedIndex = 0;
                    Assert.Equal(s.WiiPair_Title, dialog.Title);
                    Assert.Equal(Visibility.Visible, steps.Visibility);
                    Assert.Equal(Visibility.Collapsed, paragraph.Visibility);

                    // The live line follows the status, and keeps the step
                    // before a verdict over it.
                    var say = typeof(PairDeviceDialog).GetMethod("SetStatus", Own);
                    say.Invoke(dialog, new object[] { s.WiiPair_Searching, true, false, false });
                    Assert.Equal(Visibility.Visible, live.Visibility);
                    Assert.Equal(s.WiiPair_Searching, Part<TextBlock>(dialog, "StatusText").Text);
                    say.Invoke(dialog, new object[] { s.WiiPair_Failed, false, false, true });
                    Assert.Equal(s.WiiPair_Searching, Part<TextBlock>(dialog, "LastStepText").Text);
                    Assert.Equal(Visibility.Visible, Part<UIElement>(dialog, "LastStepText").Visibility);
                    say.Invoke(dialog, new object[] { string.Empty, false, false, false });
                    Assert.Equal(Visibility.Collapsed, live.Visibility);

                    var working = typeof(PairDeviceDialog).GetMethod("ShowWorking", Own);
                    working.Invoke(dialog, new object[] { true });
                    Assert.Equal(Visibility.Collapsed, pair.Visibility);
                    Assert.False(pair.IsEnabled);
                    working.Invoke(dialog, new object[] { false });
                    Assert.Equal(Visibility.Visible, pair.Visibility);
                    Assert.True(pair.IsEnabled);
                }
                finally { dialog.Close(); }
            });
        }

        // ── Remote Link ──

        /// <summary>Reject holds the default and Cancel, so Enter and Esc
        /// both decline, and Pair takes a click. The code reads in two
        /// groups of three and the identity in groups of four.</summary>
        [Fact]
        public void TheRemotePairDialogDeclinesByDefault()
        {
            DialogTestHost.Run(() =>
            {
                var dialog = new RemoteLinkPairDialog("482915", "A1B2C3D4E5F6071829304A5B6C7D8E9F");
                try
                {
                    var reject = Part<Button>(dialog, "RejectButton");
                    var pair = Part<Button>(dialog, "PairButton");
                    Assert.True(reject.IsDefault);
                    Assert.True(reject.IsCancel);
                    Assert.False(pair.IsDefault);
                    Assert.False(pair.IsCancel);
                    Assert.Equal("482 915", Part<TextBlock>(dialog, "SasText").Text);
                    Assert.Equal("A1B2 C3D4 E5F6 0718", Part<TextBlock>(dialog, "IdentityText").Text);
                    Assert.Equal(Strings.Instance.RemoteLink_PairTitle, dialog.Title);
                    Assert.Equal(Strings.Instance.RemoteLink_PairPrompt, dialog.Subtitle);
                    Assert.False(dialog.GamepadOnly);
                    Assert.False(dialog.AllowRemoteAssignments);
                    Part<CheckBox>(dialog, "GamepadOnlyCheck").IsChecked = true;
                    Assert.True(dialog.GamepadOnly);
                }
                finally { dialog.Close(); }
            });
        }

        /// <summary>The password dialog is titled for its mode, puts the
        /// caller's prompt under the title, asks twice only to set a
        /// password, and says on its own line why a password was not
        /// taken.</summary>
        [Fact]
        public void ThePasswordDialogSaysWhyItDidNotTakeThePassword()
        {
            DialogTestHost.Run(() =>
            {
                var s = Strings.Instance;
                var set = new RemoteLinkPasswordDialog(true, s.RemoteLink_PasswordSetPrompt);
                try
                {
                    Assert.Equal(s.RemoteLink_PasswordSetTitle, set.Title);
                    Assert.Equal(s.RemoteLink_PasswordSetPrompt, set.Subtitle);
                    Assert.Equal(Visibility.Visible, Part<UIElement>(set, "ConfirmRow").Visibility);
                    var error = Part<TextBlock>(set, "ErrorText");
                    var ok = Part<Button>(set, "OkButton");
                    Assert.True(ok.IsDefault);
                    Assert.Equal(Visibility.Collapsed, error.Visibility);

                    ok.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    Assert.Equal(Visibility.Visible, error.Visibility);
                    Assert.Equal(s.RemoteLink_PasswordEmpty, error.Text);

                    Part<PasswordBox>(set, "PwBox").Password = "one";
                    Part<PasswordBox>(set, "ConfirmBox").Password = "two";
                    ok.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    Assert.Equal(s.RemoteLink_PasswordMismatch, error.Text);
                    Assert.Equal(string.Empty, set.Password);
                }
                finally { set.Close(); }

                var unlock = new RemoteLinkPasswordDialog(false, s.RemoteLink_PasswordUnlockPrompt);
                try
                {
                    Assert.Equal(s.RemoteLink_PasswordUnlockTitle, unlock.Title);
                    Assert.Equal(s.RemoteLink_PasswordUnlockPrompt, unlock.Subtitle);
                    Assert.Equal(Visibility.Collapsed, Part<UIElement>(unlock, "ConfirmRow").Visibility);
                }
                finally { unlock.Close(); }
            });
        }

        // ── Record a Gesture ──

        private static TouchpadInputState Finger(float x, float y, bool down = true)
        {
            var pad = new TouchpadInputState(2);
            pad.FingerDown[0] = down;
            pad.FingerContactId[0] = 7;
            pad.FingerX[0] = x;
            pad.FingerY[0] = y;
            return pad;
        }

        private static List<List<List<Vector2>>> Samples(TouchpadGestureRecorderDialog dialog)
            => (List<List<List<Vector2>>>)typeof(TouchpadGestureRecorderDialog).GetField("_capturedSamples", Own).GetValue(dialog);

        private static Canvas LaidOutSurface(TouchpadGestureRecorderDialog dialog)
        {
            new WindowInteropHelper(dialog).EnsureHandle();
            dialog.ApplyTemplate();
            var root = (FrameworkElement)VisualTreeHelper.GetChild(dialog, 0);
            root.Measure(new Size(560, 800));
            root.Arrange(new Rect(0, 0, 560, 800));
            root.UpdateLayout();
            var surface = Part<Canvas>(dialog, "DrawingCanvas");
            Assert.True(surface.ActualWidth > 100 && surface.ActualHeight > 100, "the surface has no size");
            return surface;
        }

        /// <summary>A gesture drawn on a touchpad is kept in the touchpad's
        /// own coordinates, which are what the recognizer is handed when the
        /// gesture is drawn again, so the shape kept does not take the
        /// proportions of the surface that mirrors it. The surface draws
        /// the stroke where the finger is, with a mark at its end while the
        /// finger is down.</summary>
        [Fact]
        public void AGestureFromATouchpadKeepsThePadsOwnCoordinates()
        {
            DialogTestHost.Run(() =>
            {
                var dialog = new TouchpadGestureRecorderDialog();
                try
                {
                    var surface = LaidOutSurface(dialog);
                    // The surface is not as the pad is: wider than tall.
                    Assert.True(surface.ActualWidth > surface.ActualHeight * 1.5);
                    var tick = typeof(TouchpadGestureRecorderDialog).GetMethod("ApplyEngineTick", Own);
                    void Tick(TouchpadInputState pad) => tick.Invoke(dialog, new object[] { pad });

                    Tick(Finger(0.2f, 0.5f));
                    var line = Assert.Single(surface.Children.OfType<Polyline>());
                    var dot = Assert.Single(surface.Children.OfType<Ellipse>());
                    Assert.Equal(5, line.StrokeThickness);
                    Assert.Same(line.Stroke, dot.Fill);
                    Tick(Finger(0.4f, 0.9f));
                    Tick(Finger(0.9f, 0.1f));
                    // The stroke is where the finger is on the surface.
                    Assert.Equal(new Point(0.9f * surface.ActualWidth, 0.1f * surface.ActualHeight), line.Points[^1]);
                    Assert.Equal(0.9f * surface.ActualWidth - 8, Canvas.GetLeft(dot), 3);
                    Assert.Equal(0.1f * surface.ActualHeight - 8, Canvas.GetTop(dot), 3);
                    Assert.Empty(Samples(dialog));

                    // The finger lifts: the sample is the pad's own points.
                    Tick(Finger(0.9f, 0.1f, down: false));
                    var path = Assert.Single(Assert.Single(Samples(dialog)));
                    Assert.Equal(new[] { new Vector2(0.2f, 0.5f), new Vector2(0.4f, 0.9f), new Vector2(0.9f, 0.1f) }, path);
                    Assert.Empty(surface.Children);
                }
                finally { dialog.Close(); }
            });
        }

        /// <summary>A gesture drawn on the surface itself, with no touchpad
        /// to record from, is kept in the surface's points. A stroke of one
        /// point is no sample, and Clear starts the samples over.</summary>
        [Fact]
        public void AGestureDrawnOnTheSurfaceKeepsTheSurfacesPoints()
        {
            DialogTestHost.Run(() =>
            {
                var dialog = new TouchpadGestureRecorderDialog();
                try
                {
                    var surface = LaidOutSurface(dialog);
                    var begin = typeof(TouchpadGestureRecorderDialog).GetMethod("BeginFinger", Own, null, new[] { typeof(int), typeof(Point) }, null);
                    var go = typeof(TouchpadGestureRecorderDialog).GetMethod("ContinueFinger", Own, null, new[] { typeof(int), typeof(Point) }, null);
                    var end = typeof(TouchpadGestureRecorderDialog).GetMethod("EndFinger", Own);

                    begin.Invoke(dialog, new object[] { -1, new Point(120, 110) });
                    Assert.Single(surface.Children.OfType<Ellipse>());
                    go.Invoke(dialog, new object[] { -1, new Point(190, 170) });
                    // A move of less than half a point draws and keeps nothing.
                    go.Invoke(dialog, new object[] { -1, new Point(190.2, 170.1) });
                    go.Invoke(dialog, new object[] { -1, new Point(380, 40) });
                    end.Invoke(dialog, new object[] { -1 });
                    var path = Assert.Single(Assert.Single(Samples(dialog)));
                    Assert.Equal(new[] { new Vector2(120, 110), new Vector2(190, 170), new Vector2(380, 40) }, path);

                    // A tap is no stroke.
                    begin.Invoke(dialog, new object[] { -1, new Point(50, 50) });
                    end.Invoke(dialog, new object[] { -1 });
                    Assert.Single(Samples(dialog));

                    // The name is asked for once there is a sample, on the
                    // line under the name, and Save waits for it.
                    var problem = Part<TextBlock>(dialog, "ValidationText");
                    Assert.Equal(Visibility.Visible, problem.Visibility);
                    Assert.Equal(Strings.Instance.Recorder_Error_NameEmpty, problem.Text);
                    Assert.False(Part<Button>(dialog, "SaveBtn").IsEnabled);
                    Part<TextBox>(dialog, "NameBox").Text = "Check";
                    Assert.Equal(Visibility.Collapsed, problem.Visibility);
                    Assert.True(Part<Button>(dialog, "SaveBtn").IsEnabled);

                    // With no engine to save to, Save says so on that line
                    // and the dialog stays open with its sample.
                    if (PadPage.InputService == null)
                    {
                        Part<Button>(dialog, "SaveBtn").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                        Assert.Equal(Strings.Instance.Recorder_Error_NoEngine, problem.Text);
                        Assert.Equal(Visibility.Visible, problem.Visibility);
                        Assert.Single(Samples(dialog));
                    }

                    Part<Button>(dialog, "TryAgainBtn").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    Assert.Empty(Samples(dialog));
                    Assert.Empty(surface.Children);
                    Assert.False(Part<Button>(dialog, "SaveBtn").IsEnabled);
                }
                finally { dialog.Close(); }
            });
            // No message box is left in the dialog.
            Assert.DoesNotContain("MessageBox", ExtrasPageTests.RepoText("PadForge.App", "Views", "TouchpadGestureRecorderDialog.xaml.cs"));
        }

        // ── Controller Presets ──

        /// <summary>The device picker lists the devices connected as it
        /// opens. The pick stays on the device it was on when that device is
        /// still listed, and otherwise goes to the first joystick, gamepad
        /// or multi-axis device, else the first device.</summary>
        [Fact]
        public void ThePresetPickerKeepsItsPick()
        {
            var mouse = ((ushort)0x046D, (ushort)0xC08B, (ushort)0x01, (ushort)0x02);
            var keyboard = ((ushort)0x046D, (ushort)0xC33F, (ushort)0x01, (ushort)0x06);
            var pad = ((ushort)0x054C, (ushort)0x0CE6, (ushort)0x01, (ushort)0x05);
            var stick = ((ushort)0x068E, (ushort)0x00F2, (ushort)0x01, (ushort)0x04);
            var vendor = ((ushort)0x054C, (ushort)0x0CE6, (ushort)0xFF00, (ushort)0x01);
            var all = new[] { mouse, keyboard, pad, stick, vendor };

            // No pick before: the first game controller.
            Assert.Equal(2, ManageProfilesDialog.PickAfterRefill(all, null));
            // The pick stays where it was, whatever kind of device it is.
            Assert.Equal(1, ManageProfilesDialog.PickAfterRefill(all, keyboard));
            Assert.Equal(3, ManageProfilesDialog.PickAfterRefill(all, stick));
            // The same controller's other collection is another device.
            Assert.Equal(4, ManageProfilesDialog.PickAfterRefill(all, vendor));
            // The picked device is gone: back to the first game controller.
            Assert.Equal(1, ManageProfilesDialog.PickAfterRefill(new[] { mouse, stick }, pad));
            // No game controller at all: the first device, and none of none.
            Assert.Equal(0, ManageProfilesDialog.PickAfterRefill(new[] { mouse, keyboard }, pad));
            Assert.Equal(-1, ManageProfilesDialog.PickAfterRefill(Array.Empty<(ushort, ushort, ushort, ushort)>(), pad));

            // Each row has its own Export and Delete, and the picker fills
            // as it opens.
            string page = ExtrasPageTests.RepoText("PadForge.App", "Views", "ManageProfilesDialog.xaml");
            Assert.Contains("Click=\"ExportRow_Click\" Tag=\"{Binding}\"", page);
            Assert.Contains("Click=\"DeleteRow_Click\" Tag=\"{Binding}\"", page);
            Assert.Contains("DropDownOpened=\"DeviceList_DropDownOpened\"", page);
            string code = ExtrasPageTests.RepoText("PadForge.App", "Views", "ManageProfilesDialog.xaml.cs");
            Assert.Contains("private void DeviceList_DropDownOpened(object sender, EventArgs e) => PopulateDevices(sayCount: false);", code);
            Assert.DoesNotContain("RefreshButton", code);
        }
    }
}
