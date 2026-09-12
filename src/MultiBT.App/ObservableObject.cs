using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace MultiBT.App;

/// <summary>
/// Minimal <see cref="INotifyPropertyChanged"/> base for the view models.
/// </summary>
/// <remarks>
/// Hand-rolled rather than pulling in an MVVM toolkit: at this size a package dependency is
/// more risk (another version to keep in step) than it saves. Swap it out when the view-model
/// count justifies source-generated properties.
/// </remarks>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    /// <summary>Assigns a field and raises change notification only when the value actually changed.</summary>
    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}
