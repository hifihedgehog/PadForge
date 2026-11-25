namespace PadForge.ViewModels
{
    public partial class DashboardViewModel
    {
        private WebAccessViewModel _webAccess;

        /// <summary>Reach PadForge From the Web and the Assistants on the Web
        /// card (#600 item 11), set by the window once the web door is
        /// made.</summary>
        public WebAccessViewModel WebAccess
        {
            get => _webAccess;
            internal set => SetProperty(ref _webAccess, value);
        }
    }
}
