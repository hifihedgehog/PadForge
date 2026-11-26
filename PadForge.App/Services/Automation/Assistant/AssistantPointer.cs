using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using PadForge.Search;
using PadForge.Views;

namespace PadForge.Services.Automation
{
    /// <summary>
    /// The guided pointer (#600): an assistant points at one place or walks
    /// the user through several, in its own words. Each step rings its place
    /// with Search's own ring, held while the step is up, and puts a callout
    /// beside it headed by the assistant's name, its words cleaned of format
    /// characters and cut at <see cref="LongestWords"/>, and the step's line.
    ///
    /// <para>One callout at a time: a new show replaces the last. A step goes
    /// on when the user clicks the ringed place, after its seconds, or at the
    /// program's next call, as the step says. A key typed in the ringed place
    /// of a step that stays until it is done counts as acting on it. The
    /// callout stays until the user acts (a click
    /// elsewhere in the window, a key elsewhere, Close), the next step comes,
    /// or <see cref="LongestUp"/> passes. Print Screen, the Windows keys and
    /// the modifiers leave it up, so a screenshot shows it.
    /// It is never placed over an approval: the Waiting for You card's
    /// buttons, which the user answers.</para>
    ///
    /// <para>Called on the interface's thread. It never raises or activates
    /// the window: the tool asks first when the window is not in
    /// front.</para>
    /// </summary>
    internal sealed class AssistantPointer
    {
        /// <summary>The most characters of a program's words a callout
        /// shows.</summary>
        internal const int LongestWords = 300;

        /// <summary>The most characters of a program's name.</summary>
        internal const int LongestName = 60;

        /// <summary>How long a callout stays at most.</summary>
        internal static readonly TimeSpan LongestUp = TimeSpan.FromMinutes(10);

        /// <summary>How long a step looks for its place once the page has
        /// gone to it.</summary>
        internal static readonly TimeSpan FindFor = TimeSpan.FromSeconds(2);

        /// <summary>One step: its words, how it goes on, how to bring its
        /// place up, how to find its element, and, for a click on the way,
        /// whether the user has made it already.</summary>
        internal sealed class Step
        {
            public string Words { get; init; }

            /// <summary>Words read as the step lands, in place of
            /// <see cref="Words"/>, for a step whose place is known only
            /// then.</summary>
            public Func<string> Say { get; init; }

            public AssistantAdvance Advance { get; init; }
            public int Seconds { get; init; }
            public Action Open { get; init; }
            public Func<FrameworkElement> Find { get; init; }
            public Func<bool> Done { get; init; }

            /// <summary>A click or a key on the way that leaves
            /// <see cref="Done"/> false keeps the step. A click rings its
            /// place again: a fold's switch was clicked, not its line, or
            /// another fold waits under the one just opened. A key typed in a
            /// search box that still hides the row leaves the ring
            /// up.</summary>
            public bool Stays { get; init; }
        }

        private readonly Func<Window> _window;
        private readonly Func<DateTime> _now;
        private string _program;
        private List<Step> _steps = new();
        private int _at = -1;
        private FrameworkElement _target;
        private SearchLandingAdorner _ring;
        private CalloutAdorner _callout;
        private DispatcherTimer _advanceTimer, _findTimer, _lifeTimer;
        private DateTime _findUntil;
        private Window _hooked;
        private int _generation;
        private int _owner;

        /// <param name="window">PadForge's main window, whose layer the
        /// callout is drawn on and whose input says the user acted.</param>
        public AssistantPointer(Func<Window> window, Func<DateTime> now = null)
        {
            _window = window;
            _now = now ?? (() => DateTime.UtcNow);
        }

        /// <summary>The callout shown now, for a test and for where.</summary>
        internal AssistantCallout Callout => _callout?.Callout;

        /// <summary>The element ringed now.</summary>
        internal FrameworkElement Target => _target;

        /// <summary>Raised when the callout goes, however it goes.</summary>
        internal event EventHandler Ended;

        /// <summary>Words a program gave, as a callout shows them: format and
        /// control characters out, runs of space as one, cut at
        /// <paramref name="most"/> with an ellipsis.</summary>
        internal static string Clean(string text, int most)
        {
            string clean = PadForge.Engine.Common.ShownText.Clean(text ?? string.Empty);
            var sb = new StringBuilder(clean.Length);
            bool space = false;
            foreach (char c in clean)
            {
                if (char.IsWhiteSpace(c) || char.IsControl(c)) { space = sb.Length > 0; continue; }
                if (space) { sb.Append(' '); space = false; }
                sb.Append(c);
            }
            string one = sb.ToString();
            if (one.Length <= most) return one;
            // An ellipsis in place of the rest, never half of a pair.
            int cut = most - 1;
            if (char.IsHighSurrogate(one[cut - 1])) cut--;
            return one.Substring(0, cut) + "…";
        }

        /// <summary>Shows a run of steps from the first, in place of any
        /// callout up now.</summary>
        public void Play(string program, IReadOnlyList<Step> steps, int owner = 0)
        {
            Clear(raise: false);
            if (steps == null || steps.Count == 0) return;
            _owner = owner;
            _program = Clean(program, LongestName);
            _steps = steps.ToList();
            _at = -1;
            _generation++;
            _lifeTimer = new DispatcherTimer { Interval = LongestUp };
            _lifeTimer.Tick += (_, _) => Clear();
            _lifeTimer.Start();
            Hook();
            NextStep();
        }

        /// <summary>The program called again: a step that waits for the next
        /// call goes on. True when one did.</summary>
        public bool NextCall(int owner)
        {
            if (owner != _owner || _at < 0 || _at >= _steps.Count || _steps[_at].Advance != AssistantAdvance.Next) return false;
            NextStep();
            return true;
        }

        /// <summary>Takes the callout away. True when one showed.</summary>
        public bool Clear() => Clear(raise: true);

        private bool Clear(bool raise)
        {
            bool had = _at >= 0;
            _generation++;
            Unring();
            _advanceTimer?.Stop();
            _findTimer?.Stop();
            _lifeTimer?.Stop();
            _advanceTimer = _findTimer = _lifeTimer = null;
            Unhook();
            _steps = new List<Step>();
            _at = -1;
            if (had && raise) Ended?.Invoke(this, EventArgs.Empty);
            return had;
        }

        /// <summary>What the callout says now, for where: its step and its
        /// words.</summary>
        public string Now()
        {
            if (_at < 0 || _callout == null) return null;
            var c = _callout.Callout;
            return (c.Step + " " + c.Words).Trim();
        }

        private void Unring()
        {
            if (_target != null) _target.RemoveHandler(UIElement.PreviewMouseLeftButtonUpEvent, (MouseButtonEventHandler)OnTargetClicked);
            _ring?.Release();
            _ring = null;
            _callout?.Remove();
            _callout = null;
            _target = null;
        }

        private void NextStep()
        {
            Unring();
            _advanceTimer?.Stop();
            // Work queued for the step that ends here (a check after the
            // user's key or click, a landing, a look for its place) belongs
            // to that step. A new generation drops it, so a second key's
            // check never reads the next step as done and ends the walk.
            _generation++;
            _at++;
            // A click on the way that the user has made already is passed.
            while (_at < _steps.Count && _steps[_at].Done?.Invoke() == true) _at++;
            if (_at >= _steps.Count)
            {
                Clear();
                return;
            }
            var step = _steps[_at];
            try { step.Open?.Invoke(); }
            catch (Exception) { /* the place could not be opened, and the step shows where it can */ }
            _findUntil = _now() + FindFor;
            int generation = _generation;
            Dispatcher().BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                if (generation == _generation) Land(step, generation);
            }));
        }

        private Dispatcher Dispatcher() => _window()?.Dispatcher ?? System.Windows.Threading.Dispatcher.CurrentDispatcher;

        /// <summary>The step's place is up, or is looked for again a moment
        /// later while it comes up.</summary>
        private void Land(Step step, int generation)
        {
            FrameworkElement element = null;
            try { element = step.Find?.Invoke(); }
            catch (Exception) { element = null; }
            if ((element == null || !element.IsVisible) && _now() < _findUntil)
            {
                _findTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
                _findTimer.Tick -= OnFindTick;
                _findTimer.Tick += OnFindTick;
                _findTimer.Tag = (step, generation);
                _findTimer.Start();
                return;
            }
            _findTimer?.Stop();
            if (element != null && element.IsVisible)
            {
                SearchNavigator.ScrollTo(element);
                _target = element;
                _ring = SearchLandingAdorner.Hold(element);
                if (step.Advance == AssistantAdvance.Click)
                    element.AddHandler(UIElement.PreviewMouseLeftButtonUpEvent, (MouseButtonEventHandler)OnTargetClicked, handledEventsToo: true);
            }
            ShowCallout(step);
            if (step.Advance == AssistantAdvance.Seconds)
            {
                _advanceTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(Math.Clamp(step.Seconds, 1, 60)) };
                _advanceTimer.Tick += (_, _) =>
                {
                    _advanceTimer?.Stop();
                    if (generation == _generation) NextStep();
                };
                _advanceTimer.Start();
            }
        }

        private void OnFindTick(object sender, EventArgs e)
        {
            var timer = (DispatcherTimer)sender;
            timer.Stop();
            if (timer.Tag is ValueTuple<Step, int> pending && pending.Item2 == _generation) Land(pending.Item1, pending.Item2);
        }

        private void ShowCallout(Step step)
        {
            var window = _window();
            if (window?.Content is not FrameworkElement root) return;
            var layer = AdornerLayer.GetAdornerLayer(root);
            if (layer == null) return;
            var s = PadForge.Resources.Strings.Strings.Instance;
            string line = _steps.Count > 1
                ? string.Format(CultureInfo.CurrentCulture, s.Automation_Pointer_Step_Format, _at + 1, _steps.Count)
                : string.Empty;
            if (step.Advance == AssistantAdvance.Click && _target != null)
                line = (line + " " + s.Automation_Pointer_ClickToGoOn).Trim();
            var callout = new AssistantCallout();
            string words;
            try { words = step.Say?.Invoke() ?? step.Words; }
            catch (Exception) { words = step.Words; }
            callout.Fill(_program, Clean(words, LongestWords), line);
            callout.Closed += (_, _) => Clear();
            _callout = new CalloutAdorner(root, callout, _target, () => Approvals(root));
            layer.Add(_callout);
        }

        /// <summary>The approvals on screen, which no callout covers: each
        /// question's Allow and Don't Allow on the Automation page.</summary>
        private static List<Rect> Approvals(FrameworkElement root)
        {
            var rects = new List<Rect>();
            foreach (var element in SearchLocator.VisualDescendants(root).OfType<FrameworkElement>())
            {
                string id = AutomationProperties.GetAutomationId(element);
                if (string.IsNullOrEmpty(id) || !element.IsVisible) continue;
                if (!id.StartsWith("Automation_Allow_", StringComparison.Ordinal) && !id.StartsWith("Automation_Deny_", StringComparison.Ordinal)) continue;
                try
                {
                    var r = element.TransformToAncestor(root).TransformBounds(new Rect(element.RenderSize));
                    r.Inflate(8, 8);
                    rects.Add(r);
                }
                catch (InvalidOperationException) { }
            }
            return rects;
        }

        // ── The user acts ──

        private void Hook()
        {
            var window = _window();
            if (window == null || ReferenceEquals(window, _hooked)) return;
            Unhook();
            _hooked = window;
            window.AddHandler(UIElement.PreviewMouseDownEvent, (MouseButtonEventHandler)OnWindowMouseDown, handledEventsToo: true);
            window.AddHandler(UIElement.PreviewKeyDownEvent, (KeyEventHandler)OnWindowKeyDown, handledEventsToo: true);
        }

        private void Unhook()
        {
            if (_hooked == null) return;
            _hooked.RemoveHandler(UIElement.PreviewMouseDownEvent, (MouseButtonEventHandler)OnWindowMouseDown);
            _hooked.RemoveHandler(UIElement.PreviewKeyDownEvent, (KeyEventHandler)OnWindowKeyDown);
            _hooked = null;
        }

        private static bool Inside(DependencyObject source, DependencyObject container)
        {
            for (var at = source; at != null; at = at is Visual || at is System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(at) ?? LogicalTreeHelper.GetParent(at) : LogicalTreeHelper.GetParent(at))
                if (ReferenceEquals(at, container)) return true;
            return false;
        }

        private void OnWindowMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (_at < 0) return;
            var source = e.OriginalSource as DependencyObject;
            if (_callout != null && Inside(source, _callout.Callout)) return;
            if (_target != null && Inside(source, _target)) return;
            Clear();
        }

        /// <summary>Keys that leave the callout up: the modifiers, the
        /// Windows keys and Print Screen, which a screenshot takes.</summary>
        internal static bool LeavesItUp(Key key) => key is Key.LeftShift or Key.RightShift or Key.LeftCtrl or Key.RightCtrl
            or Key.LeftAlt or Key.RightAlt or Key.System or Key.LWin or Key.RWin or Key.Snapshot or Key.PrintScreen;

        private void OnWindowKeyDown(object sender, KeyEventArgs e)
        {
            if (_at < 0) return;
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (LeavesItUp(key)) return;
            // A key typed in the ringed place of a step that stays until it
            // is done is the user doing the step, as a click on it is: a
            // search box a walk asks them to clear. A key anywhere else, or in
            // the place of any other step, is the user acting, as before.
            if (_target != null && _at < _steps.Count && _steps[_at].Stays && Inside(e.OriginalSource as DependencyObject, _target))
            {
                AfterTheUsersAct(again: false);
                return;
            }
            Clear();
        }

        private void OnTargetClicked(object sender, MouseButtonEventArgs e) => AfterTheUsersAct(again: true);

        /// <summary>The user clicked the ringed place or typed in it. The
        /// click or the key does its own work first, which can open the next
        /// step's page or empty the box. A step that stays goes on once it
        /// is done. Until then a click rings its place again, found anew. A
        /// key does that only when the step's place is now another control,
        /// so typing in a box leaves its ring be.</summary>
        private void AfterTheUsersAct(bool again)
        {
            int generation = _generation;
            Dispatcher().BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
            {
                if (generation != _generation) return;
                var step = _at >= 0 && _at < _steps.Count ? _steps[_at] : null;
                bool done;
                try { done = step?.Stays != true || step.Done?.Invoke() != false; }
                catch (Exception) { done = true; }
                if (done) NextStep();
                else if (again || !ReferenceEquals(FoundNow(step), _target)) Again(step);
            }));
        }

        /// <summary>A step's place as its finder gives it now, or null.</summary>
        private static FrameworkElement FoundNow(Step step)
        {
            try { return step?.Find?.Invoke(); }
            catch (Exception) { return null; }
        }

        /// <summary>Rings a step's place again, found anew: its click on the
        /// way is still to make.</summary>
        private void Again(Step step)
        {
            Unring();
            _advanceTimer?.Stop();
            _findUntil = _now() + FindFor;
            int generation = _generation;
            Dispatcher().BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                if (generation == _generation) Land(step, generation);
            }));
        }

        /// <summary>
        /// The callout on the window's own adorner layer, so no scrolled
        /// column clips it. It sits under its place, or beside or over it
        /// where there is no room, and never over an approval. It follows its
        /// place as the page lays out again.
        /// </summary>
        private sealed class CalloutAdorner : Adorner
        {
            private readonly AssistantCallout _callout;
            private readonly FrameworkElement _target;
            private readonly Func<List<Rect>> _approvals;
            private readonly VisualCollection _visuals;

            public CalloutAdorner(FrameworkElement root, AssistantCallout callout, FrameworkElement target, Func<List<Rect>> approvals) : base(root)
            {
                _callout = callout;
                _target = target;
                _approvals = approvals;
                _visuals = new VisualCollection(this) { callout };
                if (_target != null) _target.LayoutUpdated += OnTargetLayout;
            }

            public AssistantCallout Callout => _callout;

            private Rect _placedFor = Rect.Empty;

            /// <summary>The place moved: the callout follows it. Only a
            /// move asks for a new arrange, since every layout pass raises
            /// LayoutUpdated and an arrange asked each time would never
            /// end.</summary>
            private void OnTargetLayout(object sender, EventArgs e)
            {
                if (TargetRect() is Rect now && now != _placedFor) InvalidateArrange();
            }

            private Rect? TargetRect()
            {
                if (_target == null || !_target.IsVisible) return null;
                try { return _target.TransformToAncestor(AdornedElement).TransformBounds(new Rect(_target.RenderSize)); }
                catch (InvalidOperationException) { return null; }
            }

            public void Remove()
            {
                if (_target != null) _target.LayoutUpdated -= OnTargetLayout;
                AdornerLayer.GetAdornerLayer(AdornedElement)?.Remove(this);
            }

            protected override int VisualChildrenCount => _visuals.Count;

            protected override Visual GetVisualChild(int index) => _visuals[index];

            protected override Size MeasureOverride(Size constraint)
            {
                _callout.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                return AdornedElement.RenderSize;
            }

            protected override Size ArrangeOverride(Size finalSize)
            {
                var size = _callout.DesiredSize;
                var area = new Rect(AdornedElement.RenderSize);
                var at = Place(size, area);
                _callout.Arrange(new Rect(at, size));
                return finalSize;
            }

            /// <summary>The first of below the place, to its right, above it
            /// and to its left that fits the window and covers no approval.
            /// With no place drawn, at the top of the window.</summary>
            private Point Place(Size size, Rect area)
            {
                if (TargetRect() is not Rect target) return new Point(Math.Max(0, (area.Width - size.Width) / 2), 56);
                _placedFor = target;
                var approvals = _approvals();
                var tries = new[]
                {
                    new Point(Math.Min(target.Left, area.Right - size.Width), target.Bottom + 2),
                    new Point(target.Right + 6, target.Top - 6),
                    new Point(Math.Min(target.Left, area.Right - size.Width), target.Top - size.Height - 2),
                    new Point(target.Left - size.Width - 6, target.Top - 6),
                };
                foreach (var p in tries)
                {
                    var r = new Rect(p, size);
                    if (r.Left < 0 || r.Top < 0 || r.Right > area.Right || r.Bottom > area.Bottom) continue;
                    if (approvals.Any(a => a.IntersectsWith(r))) continue;
                    return p;
                }
                var fallback = new Point(Math.Clamp(target.Left, 0, Math.Max(0, area.Right - size.Width)), Math.Clamp(target.Bottom + 2, 0, Math.Max(0, area.Bottom - size.Height)));
                // Never over an approval: up from it, or down past it.
                foreach (var a in approvals)
                    if (a.IntersectsWith(new Rect(fallback, size))) fallback.Y = a.Top - size.Height - 2 >= 0 ? a.Top - size.Height - 2 : a.Bottom + 2;
                return fallback;
            }
        }
    }
}
