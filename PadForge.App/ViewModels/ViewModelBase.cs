using System.Runtime.CompilerServices;
using CommunityToolkit.Mvvm.ComponentModel;
using PadForge.Resources.Strings;

namespace PadForge.ViewModels
{
    /// <summary>
    /// Base class for all PadForge view models.
    /// Inherits <see cref="ObservableObject"/> from CommunityToolkit.Mvvm,
    /// which provides INotifyPropertyChanged and SetProperty helpers.
    /// Subscribes to <see cref="Strings.CultureChanged"/> so derived VMs
    /// can override <see cref="OnCultureChanged"/> to refresh their own
    /// culture-dependent properties when the UI language changes at runtime.
    /// </summary>
    public abstract class ViewModelBase : ObservableObject
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

        /// <summary>A NaN, typed into a numeric box or read from a setting,
        /// leaves the property as it was. Math.Clamp passes NaN through, so
        /// every clamped setter stored it. These overloads take every
        /// <c>SetProperty(ref double, ...)</c> and <c>SetProperty(ref float,
        /// ...)</c> call in a derived view model.</summary>
        protected bool SetProperty(ref double field, double newValue, [CallerMemberName] string propertyName = null)
            => !double.IsNaN(newValue) && base.SetProperty(ref field, newValue, propertyName);

        /// <inheritdoc cref="SetProperty(ref double, double, string)"/>
        protected bool SetProperty(ref float field, float newValue, [CallerMemberName] string propertyName = null)
            => !float.IsNaN(newValue) && base.SetProperty(ref field, newValue, propertyName);

        /// <summary>
        /// Called when the UI culture changes at runtime. Override in derived
        /// ViewModels to refresh culture-dependent computed properties.
        /// </summary>
        protected virtual void OnCultureChanged()
        {
        }
    }
}
