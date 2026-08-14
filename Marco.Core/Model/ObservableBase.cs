using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Marco.Core.Model;

/// <summary>
/// Minimal INotifyPropertyChanged base. Lives in Core deliberately: it depends only on the
/// BCL (System.ComponentModel), so the domain model stays bindable by WPF without Core taking
/// any UI or MVVM-toolkit dependency. Scalar fields that change over a scan's lifecycle
/// (discovery fills identity, inventory fills the rest) raise change notifications so the grid
/// row updates in place instead of being rebuilt.
/// </summary>
public abstract class ObservableBase : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        Raise(name);
        return true;
    }

    protected void Raise([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
