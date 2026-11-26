namespace PadForge.ViewModels
{
    public partial class DashboardViewModel
    {
        private AnswerAccessViewModel _answerAccess;

        /// <summary>Answer From Another Device (#600), set by the window once
        /// Automation's host is made.</summary>
        public AnswerAccessViewModel AnswerAccess
        {
            get => _answerAccess;
            internal set => SetProperty(ref _answerAccess, value);
        }
    }
}
