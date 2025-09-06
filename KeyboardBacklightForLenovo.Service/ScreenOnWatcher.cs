using System.Diagnostics.Eventing.Reader;

namespace KeyboardBacklightForLenovo
{
  /// <summary>
  /// Subscribes to Application log events: Provider="ScreenStateService", EventID=1001 (Screen On).
  /// You can override via env vars:
  ///   KBL_ScreenProviderName (default: "ScreenStateService")
  ///   KBL_ScreenEventId     (default: 1001)
  /// </summary>
  public sealed class ScreenOnWatcher : IDisposable
  {
    private EventLogWatcher? _watcher;
    public event Func<Task>? OnScreenOn;

    private readonly string _provider;
    private readonly int _eventId;

    public ScreenOnWatcher()
    {
      _provider = "ScreenStateService";
      _eventId = 1001;
    }

    public void Start()
    {
      var query = new EventLogQuery("Application", PathType.LogName,
          $@"*[System[Provider[@Name='{_provider}'] and (EventID={_eventId})]]");

      _watcher = new EventLogWatcher(query);
      _watcher.EventRecordWritten += async (_, args) =>
      {
        if (args.EventException != null)
        {
          ServiceLogger.LogError($"Event watcher error: {args.EventException}");
          return;
        }
        try
        {
          var handler = OnScreenOn;
          if (handler is not null) await handler();
        }
        catch (Exception ex)
        {
          ServiceLogger.LogError($"OnScreenOn handler failed: {ex}");
        }
      };

      _watcher.Enabled = true;
      ServiceLogger.LogInfo($"ScreenOnWatcher started. Provider='{_provider}', EventID={_eventId}.");
    }

    public void Dispose()
    {
      if (_watcher is not null)
      {
        _watcher.Enabled = false;
        _watcher.Dispose();
        _watcher = null;
      }
    }
  }
}
