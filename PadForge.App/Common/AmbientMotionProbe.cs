using System.ComponentModel;

namespace PadForge.Common
{
    /// <summary>
    /// Bindable singleton carrying whether ambient (always-running) motion
    /// should animate right now. MainWindow drives <see cref="IsAppActive"/>
    /// from window activation and minimize state.
    ///
    /// <para>Why this exists: an isolated, profiled measurement (2026-07-16)
    /// showed that ONE permanently breathing glow costs ~18% of a CPU core at
    /// 60fps on the reference machine, independent of caching, window size,
    /// or which property animates. That is the fixed per-frame cost of WPF's
    /// animation/render pipeline, and it is paid even when the app is behind
    /// a game and nobody can see the glow. Gating ambient motion on
    /// foreground state keeps every effect exactly as designed whenever the
    /// user can see the app, and returns the whole animation budget whenever
    /// they cannot. Transitional animations (hover, ignite, flash) are not
    /// gated; they are finite and event-driven.</para>
    ///
    /// <para>Same pattern as <see cref="EmberThemeProbe"/>: XAML binds via
    /// <c>{Binding IsAppActive, Source={x:Static common:AmbientMotionProbe.Instance}}</c>
    /// inside MultiDataTrigger conditions, so EnterActions start the breathe
    /// when the app comes to the foreground and ExitActions ease it back to
    /// its static glow when it leaves.</para>
    /// </summary>
    public sealed class AmbientMotionProbe : INotifyPropertyChanged
    {
        public static AmbientMotionProbe Instance { get; } = new AmbientMotionProbe();

        private bool _isAppActive = true;

        public bool IsAppActive
        {
            get => _isAppActive;
            set
            {
                if (_isAppActive == value) return;
                _isAppActive = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsAppActive)));
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
    }
}
