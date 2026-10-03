using System.Runtime.CompilerServices;
using CommunityToolkit.Mvvm.ComponentModel;

namespace PadForge.ViewModels
{
    /// <summary>
    /// An <see cref="ObservableObject"/> whose double and float SetProperty
    /// calls leave a property as it was when handed NaN, typed into a
    /// numeric box or read from a setting. Math.Clamp passes NaN through, so
    /// every clamped setter stored it. <see cref="ViewModelBase"/> derives
    /// from it, and so does every row and settings object with a persisted
    /// number. Those must not take ViewModelBase itself: its constructor
    /// subscribes each instance to the static culture event and never
    /// unsubscribes.
    /// </summary>
    public abstract class FiniteObservableObject : ObservableObject
    {
        /// <summary>A NaN leaves the property as it was. These overloads take
        /// every <c>SetProperty(ref double, ...)</c> and <c>SetProperty(ref
        /// float, ...)</c> call in a derived class. Infinity passes, so a
        /// clamped setter turns it into its bound.</summary>
        protected bool SetProperty(ref double field, double newValue, [CallerMemberName] string propertyName = null)
            => !double.IsNaN(newValue) && base.SetProperty(ref field, newValue, propertyName);

        /// <inheritdoc cref="SetProperty(ref double, double, string)"/>
        protected bool SetProperty(ref float field, float newValue, [CallerMemberName] string propertyName = null)
            => !float.IsNaN(newValue) && base.SetProperty(ref field, newValue, propertyName);
    }
}
