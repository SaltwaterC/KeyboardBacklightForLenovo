using System;
using System.ServiceProcess;

namespace KeyboardBacklightForLenovo
{
    public sealed class ResetService : ServiceBase
    {
        public const string ServiceNameConst = "BacklightResetService";

        private ScreenOnWatcher? _screenWatcher;
        private SessionWatcher? _sessions;
        private ResetOrchestrator? _orchestrator;

        public ResetService()
        {
            ServiceName = ServiceNameConst;
            CanHandlePowerEvent = false;  // We rely on ScreenStateService events instead
            CanShutdown = true;
            CanStop = true;
        }

        protected override void OnStart(string[] args)
        {
            ServiceLogger.LogInfo("Service starting...");

            _sessions = new SessionWatcher();
            _orchestrator = new ResetOrchestrator(_sessions);

            // Boot-time reset if no interactive user
            _ = _orchestrator.TryResetIfNoUserAsync("Boot");

            _screenWatcher = new ScreenOnWatcher();
            _screenWatcher.OnScreenOn += OnScreenOnAsync;
            try
            {
                _screenWatcher.Start();
                ServiceLogger.LogInfo("Subscribed to Application log: Provider='ScreenStateService', EventID=1001.");
            }
            catch (Exception ex)
            {
                ServiceLogger.LogError("Failed to start ScreenOnWatcher: " + ex);
            }
        }

        private async System.Threading.Tasks.Task OnScreenOnAsync()
        {
            // Delegate to orchestrator; it already handles burst/throttle + user presence.
            if (_orchestrator is not null)
                await _orchestrator.TryResetIfNoUserAsync("ScreenOn(EventID=1001)");
        }

        protected override void OnStop()
        {
            _screenWatcher?.Dispose();
            _screenWatcher = null;
            ServiceLogger.LogInfo("Service stopped.");
        }

        protected override void OnShutdown()
        {
            OnStop();
            base.OnShutdown();
        }

        // Console helpers for dev runs (Program.cs uses these).
        internal void StartForConsole() => OnStart(Array.Empty<string>());
        internal void StopForConsole() => OnStop();
    }
}
