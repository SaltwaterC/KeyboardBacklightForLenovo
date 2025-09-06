using System;
using System.Diagnostics; // EventLog, EntryWrittenEventArgs
using Microsoft.Win32;     // SystemEvents, Power/Session events
using System.Runtime.Versioning;

namespace KeyboardBacklightForLenovo
{
  public enum PowerEventType
  {
    Boot,
    PowerResume,
    SessionLogon,
    SessionUnlock,
    ConsoleConnect,
    ScreenOn
  }

  /// <summary>
  /// Fires a callback with the event type for:
  ///  - Boot (fires once on Start)
  ///  - Wake from sleep/hibernate (PowerModes.Resume)
  ///  - User logon/unlock/console connect
  ///  - Screen ON (from ScreenStateService in the Windows Event Log)
  /// </summary>
  [SupportedOSPlatform("windows")]
  public sealed class PowerEventsWatcher : IDisposable
  {
    private readonly Action<PowerEventType> _onTrigger;
    private readonly string _screenServiceLogName;
    private readonly string _screenServiceSource;
    private readonly int _screenOnEventId;

    private EventLog? _screenLog;

    public PowerEventsWatcher(
        Action<PowerEventType> onTrigger,
        string screenServiceLogName = "Application",
        string screenServiceSource = "ScreenStateService",
        int screenOnEventId = 1001)
    {
      _onTrigger = onTrigger ?? throw new ArgumentNullException(nameof(onTrigger));
      _screenServiceLogName = screenServiceLogName;
      _screenServiceSource = screenServiceSource;
      _screenOnEventId = screenOnEventId;
    }

    public void Start()
    {
      // Fire once to cover "boot" (i.e., app start after boot).
      SafeInvoke(PowerEventType.Boot);

      SystemEvents.PowerModeChanged += OnPowerModeChanged;
      SystemEvents.SessionSwitch += OnSessionSwitch;

      // Screen ON from Event Log
      try
      {
        _screenLog = new EventLog(_screenServiceLogName);
        _screenLog.EnableRaisingEvents = true;
        _screenLog.EntryWritten += OnEntryWritten;
      }
      catch
      {
        if (_screenLog is not null)
        {
          try { _screenLog.Dispose(); } catch { /* ignore */ }
          _screenLog = null;
        }
      }
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
      if (e.Mode == PowerModes.Resume)
        SafeInvoke(PowerEventType.PowerResume);
    }

    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
      switch (e.Reason)
      {
        case SessionSwitchReason.SessionLogon:
          SafeInvoke(PowerEventType.SessionLogon);
          break;
        case SessionSwitchReason.SessionUnlock:
          SafeInvoke(PowerEventType.SessionUnlock);
          break;
        case SessionSwitchReason.ConsoleConnect:
          SafeInvoke(PowerEventType.ConsoleConnect);
          break;
      }
    }

    private void OnEntryWritten(object? sender, EntryWrittenEventArgs e)
    {
      try
      {
        var entry = e.Entry;
        if (entry is null) return;

        bool sourceMatch = string.Equals(entry.Source, _screenServiceSource, StringComparison.OrdinalIgnoreCase);
        bool idMatch = unchecked((int)entry.InstanceId) == _screenOnEventId;
        if (sourceMatch && idMatch)
          SafeInvoke(PowerEventType.ScreenOn);
      }
      catch { /* ignore */ }
    }

    private void SafeInvoke(PowerEventType evt)
    {
      try { _onTrigger(evt); } catch { /* ignore */ }
    }

    public void Dispose()
    {
      SystemEvents.PowerModeChanged -= OnPowerModeChanged;
      SystemEvents.SessionSwitch -= OnSessionSwitch;

      if (_screenLog is not null)
      {
        try { _screenLog.EntryWritten -= OnEntryWritten; } catch { }
        try { _screenLog.EnableRaisingEvents = false; } catch { }
        try { _screenLog.Dispose(); } catch { }
        _screenLog = null;
      }
    }
  }
}
