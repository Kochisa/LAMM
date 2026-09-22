using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace LocalAIModelManager.App.Infrastructure;

/// <summary>Minimal INotifyPropertyChanged base (no third-party MVVM package is available offline).</summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

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

    /// <summary>Raised when a property changes, for cross-property dependencies.</summary>
    protected void RaiseAllPropertiesChanged() => OnPropertyChanged(string.Empty);

    /// <summary>Marshals to the UI thread and raises the change notification.</summary>
    protected void OnPropertyChangedFromAnyThread(string? propertyName = null) =>
        UiDispatcher.Invoke(() => OnPropertyChanged(propertyName));
}
