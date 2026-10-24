using System.ComponentModel;
using System.Windows;

namespace ContextMenuMgr.Frontend.Services;

/// <summary>Tracks real list queries independently of item mutation busy state.</summary>
public sealed class MenuListActivityService : INotifyPropertyChanged
{
    private int _count;
    public bool IsScanning => Volatile.Read(ref _count) > 0;
    public event PropertyChangedEventHandler? PropertyChanged;
    public IDisposable Begin()
    {
        Interlocked.Increment(ref _count);
        Changed();
        return new Scan(this);
    }
    private void Changed()
    {
        void Raise() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsScanning)));
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess()) _ = dispatcher.BeginInvoke((Action)Raise);
        else Raise();
    }
    private sealed class Scan(MenuListActivityService owner) : IDisposable
    {
        private MenuListActivityService? _owner = owner;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _owner, null) is { } service)
            { Interlocked.Decrement(ref service._count); service.Changed(); }
        }
    }
}
