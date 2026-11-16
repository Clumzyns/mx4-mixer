using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Mixer.ViewModels;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }

    protected void Raise([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class RelayCommand : System.Windows.Input.ICommand
{
    private readonly Action _run;
    public RelayCommand(Action run) => _run = run;
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => _run();
}

public sealed class DeviceOption
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public override string ToString() => Name;
}

public sealed class ChannelChoice
{
    public required Audio.ChannelId Id { get; init; }
    public required string Name { get; init; }
    public override string ToString() => Name;
}
