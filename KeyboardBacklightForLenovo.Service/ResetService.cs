using System;
using System.Diagnostics;
using System.ServiceProcess;

namespace KeyboardBacklightForLenovo
{
    public sealed class ResetService : ServiceBase
    {
        public const string ServiceNameConst = "BacklightResetService";

        private ScreenOnWatcher? _screenWatcher;

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

            TryReset("Boot");

            _screenWatcher = new ScreenOnWatcher();
            _screenWatcher.OnScreenOn += () =>
            {
                TryReset("ScreenOn(EventID=1001)");
                return System.Threading.Tasks.Task.CompletedTask;
            };
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

        private void TryReset(string reason)
        {
            if (IsTrayRunning())
            {
                ServiceLogger.LogInfo($"[{reason}] Tray running -> skip.");
                return;
            }

            try
            {
                int preferred = PreferredLevelStore.ReadPreferredLevel();
                ServiceLogger.LogInfo($"[{reason}] Tray not running. Preferred={preferred}. Applying reset...");
                using var ctrl = new KeyboardBacklightController();
                ctrl.ResetStatus(preferred);
                ServiceLogger.LogInfo($"[{reason}] Reset applied successfully.");
            }
            catch (Exception ex)
            {
                ServiceLogger.LogError($"[{reason}] Reset failed: {ex}");
            }
        }

        private static bool IsTrayRunning()
        {
            try
            {
                return Process.GetProcessesByName("BacklightTrayApp").Length > 0;
            }
            catch
            {
                return false;
            }
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
