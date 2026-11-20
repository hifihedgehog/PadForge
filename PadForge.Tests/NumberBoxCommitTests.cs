using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using Xunit;
using NumberBox = Wpf.Ui.Controls.NumberBox;

namespace PadForge.Tests
{
    /// <summary>
    /// A number box saves what its arrows set (#511).
    ///
    /// <para>WPF UI's NumberBox writes its Value back when the box loses
    /// focus, unless the binding says otherwise: ValueProperty registers
    /// UpdateSourceTrigger.LostFocus as its default (NumberBox.cs, since
    /// 3.0.0). Its arrow buttons, and the Up, Down, Page Up and Page Down
    /// keys, step the value through StepValue without giving the box focus.
    /// A click on an arrow changed the number on screen and saved nothing
    /// until the box was clicked into and out of again. So every box binds
    /// with UpdateSourceTrigger=PropertyChanged. Typing still saves on Enter
    /// or on leaving the box, because the control sets Value only then.</para>
    ///
    /// <para>A port box also waits one second after the last change
    /// (Delay=1000). A port change restarts its server, and the web
    /// controller's restart runs netsh up to three times. A held arrow
    /// repeats every 33 to 400 ms after a first wait of 250 to 1000 ms
    /// (RepeatButton.GetKeyboardSpeed and GetKeyboardDelay), so the server
    /// restarts once after the arrow is let go, not on every step.</para>
    /// </summary>
    public class NumberBoxCommitTests
    {
        /// <summary>Every XAML file of the app, its build output left out.</summary>
        private static List<string> AppXaml()
        {
            var d = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (d != null && !File.Exists(Path.Combine(d.FullName, "SharedVersion.cs")))
                d = d.Parent;
            Assert.True(d != null, "repository root not found");
            string app = Path.Combine(d.FullName, "PadForge.App");
            var files = new List<string>(Directory.GetFiles(app, "*.xaml"));
            foreach (string dir in Directory.GetDirectories(app))
            {
                string name = Path.GetFileName(dir);
                if (name.Equals("bin", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("obj", StringComparison.OrdinalIgnoreCase)) continue;
                files.AddRange(Directory.GetFiles(dir, "*.xaml", SearchOption.AllDirectories));
            }
            return files;
        }

        /// <summary>Every number box in the app saves each change, and a box
        /// bound to a port waits a second, the only boxes that do.</summary>
        [Fact]
        public void EveryNumberBoxSavesEachChange_AndAPortBoxWaitsASecond()
        {
            var offenders = new List<string>();
            int boxes = 0;
            foreach (string file in AppXaml())
            {
                string text = File.ReadAllText(file);
                // A property element (<ui:NumberBox.ToolTip>) is part of a
                // box, not a box of its own.
                foreach (Match m in Regex.Matches(text, @"<ui:NumberBox(?=[\s/>])[^>]*>"))
                {
                    boxes++;
                    int line = text.Take(m.Index).Count(c => c == '\n') + 1;
                    string where = $"{Path.GetFileName(file)}:{line}";
                    var b = Regex.Match(m.Value, @"Value=""\{Binding ([A-Za-z0-9_]+)([^""]*)\}""");
                    if (!b.Success)
                    {
                        offenders.Add($"{where} binds no Value");
                        continue;
                    }
                    string name = b.Groups[1].Value, args = b.Groups[2].Value;
                    if (!args.Contains("UpdateSourceTrigger=PropertyChanged", StringComparison.Ordinal))
                        offenders.Add($"{where} {name} saves only when the box loses focus");
                    bool port = name.EndsWith("Port", StringComparison.Ordinal);
                    if (port && !args.Contains("Delay=1000", StringComparison.Ordinal))
                        offenders.Add($"{where} {name} restarts its server on every step of a held arrow");
                    else if (!port && args.Contains("Delay=", StringComparison.Ordinal))
                        offenders.Add($"{where} {name} waits, and no server restarts");
                }
            }

            // Positive control: the pattern still finds the boxes, 23 of
            // them on 2026-10-15.
            Assert.True(boxes >= 23, $"found only {boxes} number boxes");
            Assert.True(offenders.Count == 0,
                "number boxes that do not save each change:\n  " + string.Join("\n  ", offenders));
        }

        /// <summary>
        /// The report and the fix on the control itself. The box has no
        /// focus, as it has none when an arrow is clicked, and the step runs
        /// through the Up key, whose handler calls the StepValue the arrows
        /// call. With the control's own default the box shows 16 and the
        /// setting keeps 15, which is what #511 saw. With PropertyChanged
        /// the setting has 16 at once, in one write.
        /// </summary>
        [Fact]
        public void AStepReachesTheSettingOnlyWhenTheBindingSaysPropertyChanged() => RunSta(() => WithKeySource((source, keyboard) =>
        {
            var (before, unsaved) = Bound(UpdateSourceTrigger.Default);
            StepUp(before, source, keyboard);
            Assert.Equal(16d, before.Value);
            Assert.Equal(15, unsaved.Value);
            Assert.Equal(0, unsaved.Writes);

            var (after, saved) = Bound(UpdateSourceTrigger.PropertyChanged);
            StepUp(after, source, keyboard);
            Assert.Equal(16d, after.Value);
            Assert.Equal(16, saved.Value);
            Assert.Equal(1, saved.Writes);
        }));

        /// <summary>
        /// A port box's wait. Five steps with no pause, as from a held
        /// arrow, leave the setting alone until a second has passed, then
        /// reach it in one write. BindingExpressionBase.Dirty restarts the
        /// delay timer on every change, and OnTimerTick writes the last
        /// value. The dispatcher does not run between the steps and the
        /// first check, so the timer cannot fire early.
        /// </summary>
        [Fact]
        public void APortBoxSavesAHeldArrowOnce() => RunSta(() => WithKeySource((source, keyboard) =>
        {
            var (box, setting) = Bound(UpdateSourceTrigger.PropertyChanged, delay: 1000);
            for (int i = 0; i < 5; i++)
                StepUp(box, source, keyboard);
            Assert.Equal(20d, box.Value);
            Assert.Equal(0, setting.Writes);

            PumpFor(TimeSpan.FromMilliseconds(1500));
            Assert.Equal(20, setting.Value);
            Assert.Equal(1, setting.Writes);
        }));

        /// <summary>The setting a box is bound to, counting the writes it
        /// takes.</summary>
        private sealed class Setting : INotifyPropertyChanged
        {
            private int _value = 15;

            public int Writes { get; private set; }

            public int Value
            {
                get => _value;
                set
                {
                    _value = value;
                    Writes++;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value)));
                }
            }

            public event PropertyChangedEventHandler PropertyChanged;
        }

        private sealed class NoKeysHeld : KeyboardDevice
        {
            public NoKeysHeld() : base(System.Windows.Input.InputManager.Current) { }

            protected override KeyStates GetKeyStatesFromSystem(Key key) => KeyStates.None;
        }

        /// <summary>A box bound to a new setting of 15, as the views bind
        /// theirs: two-way, with the given trigger and wait.</summary>
        private static (NumberBox Box, Setting Setting) Bound(UpdateSourceTrigger trigger, int delay = 0)
        {
            var setting = new Setting();
            var box = new NumberBox { Minimum = 5, Maximum = 50, SmallChange = 1 };
            box.SetBinding(NumberBox.ValueProperty, new Binding(nameof(Setting.Value))
            {
                Source = setting,
                Mode = BindingMode.TwoWay,
                UpdateSourceTrigger = trigger,
                Delay = delay,
            });
            // Positive control: the binding put the setting's number in the box.
            Assert.Equal(15d, box.Value);
            return (box, setting);
        }

        /// <summary>Up, as a key the box handles itself: OnPreviewKeyDown
        /// calls StepValue(SmallChange).</summary>
        private static void StepUp(NumberBox box, PresentationSource source, KeyboardDevice keyboard)
            => box.RaiseEvent(new KeyEventArgs(keyboard, source, 0, Key.Up) { RoutedEvent = Keyboard.PreviewKeyDownEvent });

        /// <summary>A key event needs a presentation source. An off-screen
        /// window that never takes focus gives one.</summary>
        private static void WithKeySource(Action<PresentationSource, KeyboardDevice> body)
        {
            var window = new Window
            {
                Width = 100, Height = 100, ShowActivated = false, ShowInTaskbar = false,
                Left = -10000, Top = -10000,
            };
            try
            {
                window.Show();
                var source = PresentationSource.FromVisual(window);
                Assert.NotNull(source);
                body(source, new NoKeysHeld());
            }
            finally
            {
                window.Close();
            }
        }

        /// <summary>Runs the dispatcher, timers included, for the given
        /// time.</summary>
        private static void PumpFor(TimeSpan span)
        {
            var frame = new DispatcherFrame();
            var stop = new DispatcherTimer(DispatcherPriority.Background) { Interval = span };
            stop.Tick += (_, _) =>
            {
                stop.Stop();
                frame.Continue = false;
            };
            stop.Start();
            Dispatcher.PushFrame(frame);
        }

        /// <summary>WPF elements need an STA thread.</summary>
        private static void RunSta(Action body)
        {
            Exception failure = null;
            var thread = new Thread(() =>
            {
                try { body(); }
                catch (Exception ex) { failure = ex; }
                finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();
            Assert.True(thread.Join(30000), "the number box test timed out");
            if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
