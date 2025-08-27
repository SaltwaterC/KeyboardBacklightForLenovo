using Microsoft.Extensions.Hosting;

namespace KeyboardBacklightForLenovo
{
    public sealed class ServiceHost : BackgroundService
    {
        private readonly SessionWatcher _sessions;
        private readonly ScreenOnWatcher _screenOn;
        private readonly ResetOrchestrator _reset;

        public ServiceHost(SessionWatcher sessions, ScreenOnWatcher screenOn, ResetOrchestrator reset)
        {
            _sessions = sessions;
            _screenOn = screenOn;
            _reset = reset;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            ServiceLogger.LogInfo("Service starting...");

            // Boot-time one-shot if no interactive user is present.
            await _reset.TryResetIfNoUserAsync("Boot");

            // Hook Application log signal from ScreenStateService (EventID=1001).
            _screenOn.OnScreenOn += async () => await _reset.TryResetIfNoUserAsync("ScreenOn(EventID=1001)");
            _screenOn.Start();

            try
            {
                await Task.Delay(Timeout.Infinite, stoppingToken);
            }
            catch (TaskCanceledException) { /* normal stop */ }

            _screenOn.Dispose();
            ServiceLogger.LogInfo("Service stopped.");
        }
    }
}
