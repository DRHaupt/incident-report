using Shared.Core.Interop;

namespace FieldMap.Services;

/// <summary>Watches this app's exchange inbox and raises <see cref="Received"/> (on the UI thread) for each point sent by Field Toolkit.</summary>
public sealed class ExchangeWatcher : IDisposable
{
    private readonly string _inbox;
    private readonly FileSystemWatcher? _watcher;
    private readonly System.Windows.Forms.Timer _poll = new() { Interval = 5000 };
    private readonly SynchronizationContext _ui;

    public ExchangeWatcher(string inbox)
    {
        _inbox = inbox;
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        Directory.CreateDirectory(inbox);
        try
        {
            _watcher = new FileSystemWatcher(inbox, "*.json") { EnableRaisingEvents = true };
            _watcher.Created += (_, _) => _ui.Post(_ => Check(), null);
            _watcher.Renamed += (_, _) => _ui.Post(_ => Check(), null);
        }
        catch (Exception)
        {
            // Polling still works (e.g. on a network profile without change notifications).
        }
        _poll.Tick += (_, _) => Check();
        _poll.Start();
    }

    public event EventHandler<ExchangeMessage>? Received;

    public void Check()
    {
        foreach (var m in Exchange.Receive(_inbox))
            Received?.Invoke(this, m);
    }

    public void Dispose()
    {
        _poll.Dispose();
        _watcher?.Dispose();
    }
}
