using System;
using System.Windows;
using System.Windows.Media;

namespace PadForge.Common
{
    /// <summary>
    /// Holds a view's <see cref="CompositionTarget.Rendering"/> handler only
    /// while the view can be seen. Any handler on that static event makes WPF
    /// render a frame every vsync, and with the window hidden in the tray WPF
    /// times those frames itself, so one handler that returns at once still
    /// woke the UI thread hundreds of times a second while nothing showed.
    /// The handler is attached while the view wants frames, is visible, and
    /// the main window can render (<see cref="AmbientMotionProbe.IsWindowMinimized"/>),
    /// and is removed the moment any of the three goes false.
    /// </summary>
    internal sealed class RenderingSubscription
    {
        private readonly FrameworkElement _view;
        private readonly EventHandler _handler;
        private readonly AmbientMotionProbe _probe;
        private readonly Action<EventHandler> _attach;
        private readonly Action<EventHandler> _detach;
        private bool _wanted;
        private bool _attached;

        public RenderingSubscription(FrameworkElement view, EventHandler handler)
            : this(view, handler, AmbientMotionProbe.Instance,
                   h => CompositionTarget.Rendering += h,
                   h => CompositionTarget.Rendering -= h) { }

        /// <summary>Test seam: the probe and the attach and detach calls.</summary>
        internal RenderingSubscription(FrameworkElement view, EventHandler handler,
            AmbientMotionProbe probe, Action<EventHandler> attach, Action<EventHandler> detach)
        {
            _view = view;
            _handler = handler;
            _probe = probe;
            _attach = attach;
            _detach = detach;
            view.IsVisibleChanged += (s, e) => Sync();
        }

        /// <summary>Whether the handler is attached right now.</summary>
        public bool IsAttached => _attached;

        /// <summary>Whether the view wants frames: set on Loaded and on a
        /// bind, cleared on Unloaded and on an unbind. The probe's change
        /// event is held only while this is set, so an unloaded view is not
        /// rooted by the probe.</summary>
        public bool Wanted
        {
            get => _wanted;
            set
            {
                if (_wanted == value) { Sync(); return; }
                _wanted = value;
                if (value) _probe.RenderabilityChanged += OnRenderabilityChanged;
                else _probe.RenderabilityChanged -= OnRenderabilityChanged;
                Sync();
            }
        }

        /// <summary>The rule, kept pure for tests.</summary>
        internal static bool ShouldAttach(bool wanted, bool visible, bool windowCannotRender)
            => wanted && visible && !windowCannotRender;

        private void OnRenderabilityChanged(object sender, EventArgs e) => Sync();

        private void Sync()
        {
            bool attach = ShouldAttach(_wanted, _view.IsVisible, _probe.IsWindowMinimized);
            if (attach == _attached) return;
            _attached = attach;
            if (attach) _attach(_handler);
            else _detach(_handler);
        }
    }
}
