using CommunityToolkit.Mvvm.ComponentModel;
using PadForge.Resources.Strings;

namespace PadForge.ViewModels
{
    /// <summary>
    /// Base class for all PadForge view models.
    /// Inherits <see cref="FiniteObservableObject"/>, CommunityToolkit.Mvvm's
    /// ObservableObject with SetProperty overloads that refuse NaN.
    /// Subscribes to <see cref="Strings.CultureChanged"/> so derived VMs
    /// can override <see cref="OnCultureChanged"/> to refresh their own
    /// culture-dependent properties when the UI language changes at runtime.
    /// </summary>
    public abstract class ViewModelBase : FiniteObservableObject
    {
        protected ViewModelBase()
        {
            Strings.CultureChanged += OnCultureChanged;
        }

        private string _title = string.Empty;

        /// <summary>
        /// Display title for the view. Used by navigation and page headers.
        /// </summary>
        public string Title
        {
            get => _title;
            set => SetProperty(ref _title, value);
        }

        /// <summary>
        /// Called when the UI culture changes at runtime. Override in derived
        /// ViewModels to refresh culture-dependent computed properties.
        /// </summary>
        protected virtual void OnCultureChanged()
        {
        }
    }
}
