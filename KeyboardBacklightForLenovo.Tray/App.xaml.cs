using System;
using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using WinForms = System.Windows.Forms;

namespace KeyboardBacklightForLenovo
{
    public partial class App : System.Windows.Application
    {
        private WinForms.NotifyIcon? _notifyIcon;
        private Icon? _iconOff, _iconLow, _iconHigh;

        // Menu items for checkmarks
        private WinForms.ToolStripMenuItem _offItem = null!;
        private WinForms.ToolStripMenuItem _lowItem = null!;
        private WinForms.ToolStripMenuItem _highItem = null!;

        // Core pieces
        private KeyboardBacklightController _controller = new();

        // Power watcher (callback-based)
        private PowerEventsWatcher? _powerWatcher;

        // Track last seen hardware state to avoid redundant UI work
        private int _lastLevel = -1;

        // Persistence guard:
        // - Set true by Sleep Predictor when TTS <= 10s
        // - Cleared by user input OR any power event
        private volatile bool _suppressPersistence;

        // Avoid persisting on very first hardware sync
        private bool _initialSyncDone;

        // Low-level keyboard hook
        private static IntPtr _hookId = IntPtr.Zero;
        private LowLevelKeyboardProc? _hookProc; // keep delegate alive
        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int WM_LBUTTONDOWN = 0x0201;
        private const int WM_RBUTTONDOWN = 0x0204;
        private const int WM_MBUTTONDOWN = 0x0207;

        // Slow refresh timer (every 5s)
        private DispatcherTimer? _slowTimer;

        // Sleep predictor timer (every 1s)
        private DispatcherTimer? _sleepPredictorTimer;

        // Burst polling control after keypress
        private int _burstInProgress; // 0/1
        private readonly TimeSpan _burstDuration = TimeSpan.FromMilliseconds(900);
        private readonly TimeSpan _burstInterval = TimeSpan.FromMilliseconds(120);

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // Load icons once
            _iconOff = LoadEmbeddedIcon("KeyboardBacklightForLenovo.IconOff.ico");
            _iconLow = LoadEmbeddedIcon("KeyboardBacklightForLenovo.IconLow.ico");
            _iconHigh = LoadEmbeddedIcon("KeyboardBacklightForLenovo.IconHigh.ico");

            _notifyIcon = new WinForms.NotifyIcon
            {
                Icon = _iconLow ?? SystemIcons.Application,   // temporary until first sync
                Visible = true,
                Text = "Keyboard Backlight Controller for Lenovo"
            };

            var menu = new WinForms.ContextMenuStrip();

            _offItem = new WinForms.ToolStripMenuItem("Keyboard Backlight Off", null, (_, __) => SetLevelUserIntent(0));
            _lowItem = new WinForms.ToolStripMenuItem("Keyboard Backlight Low", null, (_, __) => SetLevelUserIntent(1));
            _highItem = new WinForms.ToolStripMenuItem("Keyboard Backlight High", null, (_, __) => SetLevelUserIntent(2));

            menu.Items.AddRange(new WinForms.ToolStripItem[]
            {
                _offItem, _lowItem, _highItem,
                new WinForms.ToolStripSeparator(),
                new WinForms.ToolStripMenuItem("Exit", null, (_, __) => ExitApp())
            });

            _notifyIcon.ContextMenuStrip = menu;

            // Apply PreferredLevel at startup (no persist here)
            int preferredLevel = PreferredLevelStore.ReadPreferredLevel();
            _controller.ResetStatus(preferredLevel);

            // PowerEventsWatcher — callback style
            try
            {
                // If your ctor requires params: new PowerEventsWatcher(OnPowerEvent, "Display", "System", 1001)
                _powerWatcher = new PowerEventsWatcher(OnPowerEvent);
                _powerWatcher.Start();
                Debug.WriteLine("[Tray] PowerEventsWatcher started.");
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[Tray] Failed to start PowerEventsWatcher: " + ex);
            }

            // Initial sync from hardware (do NOT persist)
            UpdateCheckedItemSafe(immediate: true);
            _initialSyncDone = true;

            // Install low-level hook (keyboard + a few mouse buttons) to both burst-poll and clear suppression
            _hookProc = HookCallback;
            using var proc = Process.GetCurrentProcess();
            using var mod = proc.MainModule!;
            _hookId = SetWindowsHookEx(WH_KEYBOARD_LL, _hookProc, GetModuleHandle(mod.ModuleName), 0);

            // Slow refresh every 5 seconds
            _slowTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _slowTimer.Tick += (_, __) => UpdateCheckedItemSafe(immediate: false);
            _slowTimer.Start();

            // Sleep Predictor — computes TTS (time-to-sleep) every second
            _sleepPredictorTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _sleepPredictorTimer.Tick += (_, __) => EvaluateSleepPrediction();
            _sleepPredictorTimer.Start();

            // Hide template window if present
            Current.MainWindow?.Hide();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            try
            {
                _powerWatcher?.Dispose();
                _powerWatcher = null;
            }
            catch { /* ignore */ }

            if (_hookId != IntPtr.Zero) UnhookWindowsHookEx(_hookId);

            if (_slowTimer is not null)
            {
                _slowTimer.Stop();
                _slowTimer = null;
            }

            if (_sleepPredictorTimer is not null)
            {
                _sleepPredictorTimer.Stop();
                _sleepPredictorTimer = null;
            }

            _iconOff?.Dispose();
            _iconLow?.Dispose();
            _iconHigh?.Dispose();

            if (_notifyIcon is not null)
            {
                _notifyIcon.Visible = false;
                _notifyIcon.Dispose();
            }

            base.OnExit(e);
        }

        // ===== Power event callback =====
        private void OnPowerEvent(PowerEventType evt)
        {
            try
            {
                // Any power event means “plans changed” → clear suppression
                if (_suppressPersistence)
                {
                    _suppressPersistence = false;
                    Debug.WriteLine($"[Tray] PowerEvent {evt} → clear suppression");
                }

                switch (evt)
                {
                    case PowerEventType.Boot:
                    case PowerEventType.PowerResume:
                    case PowerEventType.SessionLogon:
                    case PowerEventType.SessionUnlock:
                    case PowerEventType.ConsoleConnect:
                    case PowerEventType.ScreenOn:
                        Debug.WriteLine($"[Tray] {evt} → restore preferred");
                        RestorePreferred(); // UI+HW only; no extra persist
                        break;

                    default:
                        Debug.WriteLine("[Tray] Unhandled PowerEventType: " + evt);
                        break;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[Tray] OnPowerEvent failed: " + ex);
            }
        }

        private void RestorePreferred()
        {
            try
            {
                int pref = PreferredLevelStore.ReadPreferredLevel();
                _controller.ResetStatus(pref);
                _lastLevel = pref;
                Dispatcher.Invoke(() => UpdateCheckedItem(pref)); // UI only; no persist
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[Tray] RestorePreferred failed: " + ex.Message);
            }
        }

        // ===== Explicit user intent =====
        // Only this path writes PreferredLevelStore unconditionally.
        private void SetLevelUserIntent(int level)
        {
            PreferredLevelStore.SavePreferredLevel(level); // persist NOW (user intent)
            _controller.ResetStatus(level);
            _lastLevel = level;

            UpdateCheckedItem(level); // UI only
            UpdateTrayIcon(level);
        }

        // Reads hardware on a background task, then updates UI if changed.
        // If we detect a change and it's *not* suppressed, we treat it as
        // user-initiated (e.g., Fn key) and persist it.
        private void UpdateCheckedItemSafe(bool immediate)
        {
            Task.Run(() =>
            {
                int level = ReadSafe();
                if (level is 0 or 1 or 2)
                {
                    bool changed = (level != _lastLevel) || immediate;
                    if (changed)
                    {
                        _lastLevel = level;
                        Dispatcher.Invoke(() =>
                        {
                            UpdateCheckedItem(level); // UI only

                            // Persist only when:
                            //  - past the first sync,
                            //  - NOT suppressed by the 10s pre-sleep window
                            if (_initialSyncDone && !_suppressPersistence)
                            {
                                try
                                {
                                    PreferredLevelStore.SavePreferredLevel(level);
                                    Debug.WriteLine($"[Tray] Learned preferred={level}");
                                }
                                catch (Exception ex)
                                {
                                    Debug.WriteLine("[Tray] Persist failed: " + ex.Message);
                                }
                            }
                            else
                            {
                                if (!_initialSyncDone)
                                    Debug.WriteLine("[Tray] Suppress persist: initial sync");
                                else if (_suppressPersistence)
                                    Debug.WriteLine("[Tray] Suppress persist: pre-sleep window");
                            }
                        });
                    }
                }
            });
        }

        private int ReadSafe()
        {
            try { return _controller.GetStatus(); }
            catch { return -1; }
        }

        private void UpdateCheckedItem(int level)
        {
            _offItem.Checked = (level == 0);
            _lowItem.Checked = (level == 1);
            _highItem.Checked = (level == 2);
            UpdateTrayIcon(level);
        }

        private void UpdateTrayIcon(int level)
        {
            if (_notifyIcon is null) return;

            switch (level)
            {
                case 0: _notifyIcon.Icon = _iconOff ?? _notifyIcon.Icon; break;
                case 1: _notifyIcon.Icon = _iconLow ?? _notifyIcon.Icon; break;
                case 2: _notifyIcon.Icon = _iconHigh ?? _notifyIcon.Icon; break;
            }
        }

        // ===== Sleep Predictor =====
        private void EvaluateSleepPrediction()
        {
            try
            {
                // Read effective Sleep-After timeout (seconds) for current power source
                int sleepSeconds = GetEffectiveSleepTimeoutSeconds();
                if (sleepSeconds <= 0) { _suppressPersistence = false; return; }

                // Compute idle seconds since last user input (tick64-safe)
                ulong idle = GetIdleSeconds();

                // Time-to-sleep
                long tts = (long)sleepSeconds - (long)idle;

                // Enter suppression when we are within <= 10s of sleep
                bool wantSuppress = tts <= 10 && tts >= 0;
                if (wantSuppress && !_suppressPersistence)
                {
                    _suppressPersistence = true;
                    Debug.WriteLine($"[Tray] Pre-sleep window entered (TTS={tts}s) → suppress persistence");
                }
                else if (!wantSuppress && _suppressPersistence)
                {
                    // If we moved away from the window (e.g., due to background timers or policy), keep suppression
                    // until definitive user input or power event clears it. Do nothing here.
                }
            }
            catch (Exception ex)
            {
                // On any error, fail-open (no suppression) to avoid stale flags.
                _suppressPersistence = false;
                Debug.WriteLine("[Tray] EvaluateSleepPrediction failed: " + ex.Message);
            }
        }

        private static ulong GetIdleSeconds()
        {
            LASTINPUTINFO lii = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
            if (!GetLastInputInfo(ref lii)) return 0;

            ulong now = GetTickCount64();
            uint last = lii.dwTime;
            // GetTickCount64 is ms; LASTINPUTINFO.dwTime is in ms (32-bit). Cast carefully.
            ulong deltaMs = now - (ulong)last;
            return deltaMs / 1000UL;
        }

        private static int GetEffectiveSleepTimeoutSeconds()
        {
            // Active scheme
            IntPtr pScheme = IntPtr.Zero;
            int status = PowerGetActiveScheme(IntPtr.Zero, out pScheme);
            if (status != 0 || pScheme == IntPtr.Zero) throw new InvalidOperationException("PowerGetActiveScheme failed.");

            try
            {
                // AC/DC detection
                bool onAC = WinForms.SystemInformation.PowerStatus.PowerLineStatus == WinForms.PowerLineStatus.Online;

                // Read STANDBYIDLE from SUB_SLEEP
                uint value;
                status = onAC
                    ? PowerReadACValueIndex(IntPtr.Zero, pScheme, ref GUID_SLEEP_SUBGROUP, ref GUID_STANDBYIDLE, out value)
                    : PowerReadDCValueIndex(IntPtr.Zero, pScheme, ref GUID_SLEEP_SUBGROUP, ref GUID_STANDBYIDLE, out value);

                if (status != 0) throw new InvalidOperationException("PowerRead*ValueIndex failed: " + status);
                return unchecked((int)value); // seconds
            }
            finally
            {
                try { LocalFree(pScheme); } catch { }
            }
        }

        // ===== Hooks & burst polling =====
        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                int msg = wParam.ToInt32();
                if (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN || msg == WM_LBUTTONDOWN || msg == WM_RBUTTONDOWN || msg == WM_MBUTTONDOWN)
                {
                    // Any user input → definitely not going to sleep now
                    if (_suppressPersistence)
                    {
                        _suppressPersistence = false;
                        Debug.WriteLine("[Tray] User input → clear suppression");
                    }

                    // Kick off a burst if not already running
                    if (Interlocked.Exchange(ref _burstInProgress, 1) == 0)
                    {
                        _ = BurstPollAsync();
                    }
                }
            }
            return CallNextHookEx(_hookId, nCode, wParam, lParam);
        }

        // Poll every 120ms for ~900ms, stop early if we detect a change
        private async Task BurstPollAsync()
        {
            try
            {
                var deadline = DateTime.UtcNow + _burstDuration;

                while (DateTime.UtcNow < deadline)
                {
                    await Task.Delay(_burstInterval);

                    int current = ReadSafe();
                    if (current is 0 or 1 or 2)
                    {
                        if (current != _lastLevel)
                        {
                            _lastLevel = current;
                            Dispatcher.Invoke(() => {
                                UpdateCheckedItem(current); // UI only

                                // Persist learned value unless in pre-sleep suppression
                                if (_initialSyncDone && !_suppressPersistence)
                                {
                                    PreferredLevelStore.SavePreferredLevel(current);
                                    Debug.WriteLine($"[Tray] Learned preferred={current} (burst)");
                                }
                                else
                                {
                                    if (!_initialSyncDone)
                                        Debug.WriteLine("[Tray] Suppress persist: initial sync (burst)");
                                    else if (_suppressPersistence)
                                        Debug.WriteLine("[Tray] Suppress persist: pre-sleep window (burst)");
                                }
                            });
                            break; // early exit on first observed change
                        }
                    }
                }
            }
            catch { /* ignore */ }
            finally
            {
                Interlocked.Exchange(ref _burstInProgress, 0);
            }
        }

        // --- Icon loading (EmbeddedResource or WPF Resource) ---
        private static Icon? LoadEmbeddedIcon(string resourceName)
        {
            using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
            return s is null ? null : new Icon(s);
        }

        private void ExitApp()
        {
            if (_notifyIcon is not null)
            {
                _notifyIcon.Visible = false;
                _notifyIcon.Dispose();
            }
            Shutdown();
        }

        // ===== P/Invokes =====

        // Input/Idle
        [StructLayout(LayoutKind.Sequential)]
        private struct LASTINPUTINFO
        {
            public uint cbSize;
            public uint dwTime;
        }

        [DllImport("user32.dll")]
        private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

        [DllImport("kernel32.dll")]
        private static extern ulong GetTickCount64();

        // Hooks
        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        // PowrProf (sleep timeout)
        [DllImport("powrprof.dll", SetLastError = true)]
        private static extern int PowerGetActiveScheme(IntPtr UserRootPowerKey, out IntPtr ActivePolicyGuid);

        [DllImport("powrprof.dll", SetLastError = true)]
        private static extern int PowerReadACValueIndex(IntPtr RootPowerKey, IntPtr SchemeGuid, ref Guid SubGroupOfPowerSettingsGuid, ref Guid PowerSettingGuid, out uint AcValueIndex);

        [DllImport("powrprof.dll", SetLastError = true)]
        private static extern int PowerReadDCValueIndex(IntPtr RootPowerKey, IntPtr SchemeGuid, ref Guid SubGroupOfPowerSettingsGuid, ref Guid PowerSettingGuid, out uint DcValueIndex);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr hMem);

        // GUIDs
        private static Guid GUID_SLEEP_SUBGROUP = new Guid("238c9fa8-0aad-41ed-83f4-97be242c8f20"); // SUB_SLEEP
        private static Guid GUID_STANDBYIDLE = new Guid("29f6c1db-86da-48c5-9fdb-f2b67b1f44da"); // Sleep after (seconds)
    }
}
