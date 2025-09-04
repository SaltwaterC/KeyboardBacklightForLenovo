namespace KeyboardBacklightForLenovo
{
  public sealed class ResetOrchestrator
  {
    private readonly SessionWatcher _sessions;
    private DateTime _lastReset = DateTime.MinValue;

    public ResetOrchestrator(SessionWatcher sessions) => _sessions = sessions;

    public async Task TryResetIfNoUserAsync(string reason, TimeSpan waitForUserSession = default)
    {
      try
      {
        // Optional: wait briefly for a session to appear if requested
        if (waitForUserSession > TimeSpan.Zero)
        {
          var deadline = DateTime.UtcNow + waitForUserSession;
          var delay = TimeSpan.FromMilliseconds(500);
          while (DateTime.UtcNow < deadline)
          {
            if (_sessions.IsAnyUserLoggedOn(reason))
            {
              ServiceLogger.LogInfo($"[{reason}] User logged on (after wait) -> skip.");
              return;
            }
            await Task.Delay(delay);
            // Exponential backoff up to 5s between checks
            if (delay < TimeSpan.FromSeconds(5)) delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 2, 5000));
          }
        }

        if (_sessions.IsAnyUserLoggedOn(reason))
        {
          ServiceLogger.LogInfo($"[{reason}] User logged on -> skip.");
          return;
        }

        ServiceLogger.LogInfo($"[{reason}] No logged-on user detected.");

        // Avoid duplicate application within bursts of 1001
        if ((DateTime.UtcNow - _lastReset) < TimeSpan.FromSeconds(1))
          return;

        var preferred = PreferredLevelStore.ReadPreferredLevel();
        ServiceLogger.LogInfo($"[{reason}] No user session. Preferred={preferred}. Applying reset...");

        using var ctrl = new KeyboardBacklightController();
        int status = PreferredLevelStore.ReadPreferredLevel();
        ctrl.ResetStatus(status);

        _lastReset = DateTime.UtcNow;
        ServiceLogger.LogInfo($"[{reason}] Reset applied successfully.");
      }
      catch (Exception ex)
      {
        ServiceLogger.LogError($"[{reason}] Reset failed: {ex}");
      }

      await Task.CompletedTask;
    }
  }
}
