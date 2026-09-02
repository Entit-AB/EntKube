using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace EntKube.Installer.Gui.ViewModels;

/// <summary>
/// The minimum needed for bindings to update.
///
/// No MVVM framework: the repository has no third-party UI dependencies anywhere else, and this app
/// needs change notification and nothing more. A framework would be a large dependency bought for
/// twenty lines.
/// </summary>
public abstract class ViewModelBase : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Raise([CallerMemberName] string? property = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));

    /// <summary>
    /// Assigns and notifies, returning whether anything changed.
    ///
    /// <paramref name="also"/> names properties computed from this one — an "is the form valid" flag,
    /// a summary line — which have no setter of their own to raise from.
    /// </summary>
    protected bool Set<T>(
        ref T field,
        T value,
        string[]? also = null,
        [CallerMemberName] string? property = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        Raise(property);

        foreach (string dependent in also ?? [])
        {
            Raise(dependent);
        }

        return true;
    }
}
