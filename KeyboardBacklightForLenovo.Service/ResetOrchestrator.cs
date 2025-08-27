namespace KeyboardBacklightForLenovo
{
    public sealed class ResetOrchestrator
    {
        private readonly SessionWatcher _sessions;
        private DateTime _lastReset = DateTime.MinValue;

        public ResetOrchestrator(SessionWatcher sessions) => _sessions = sessions;

        public async Task TryResetIfNoUserAsync(string reason)
        {
            try
            {
                if (_sessions.IsAnyInteractiveUserActive())
                {
                    ServiceLogger.LogInfo($"[{reason}] Interactive user present -> skip.");
                    return;
                }
                else
                {
                    ServiceLogger.LogInfo($"[{reason}] No unlocked user session detected.");
                }

                // Avoid duplicate application within bursts of 1001
                if ((DateTime.UtcNow - _lastReset) < TimeSpan.FromSeconds(1))
                    return;

                var preferred = PreferredLevelStore.ReadPreferredLevel();
                ServiceLogger.LogInfo($"[{reason}] No user. Preferred={preferred}. Applying reset...");

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
