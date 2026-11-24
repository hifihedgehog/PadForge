using PadForge.Services.Plugins;

namespace PadForge
{
    /// <summary>
    /// The main window's part in Plugins (#597): it makes the plugins' part
    /// once the Automation host is made, since plugins connect over the
    /// host's pipe, and ends it before the host, so every plugin is stopped
    /// while the pipe it speaks on still stands.
    /// </summary>
    public partial class MainWindow
    {
        private PluginsController _plugins;

        /// <summary>The plugins' part. Null until the host is made.</summary>
        internal PluginsController Plugins => _plugins;

        private void StartPlugins()
        {
            if (_automation == null || _plugins != null) return;
            _plugins = new PluginsController(_automation, _viewModel.Dashboard, Dispatcher);
        }

        private void StopPlugins()
        {
            _plugins?.Dispose();
            _plugins = null;
        }
    }
}
