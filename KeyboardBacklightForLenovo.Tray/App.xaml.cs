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
        private readonly KeyboardBacklightController _controller = new();
        private PowerEventsWatcher? _powerWatcher;

        // Track last seen hardware state
        private int _lastLevel = -1;

        // Preference caching & learning control
        private int _preferredCached;              // always restore from this (not from registry mid-wake)
        private bool _initialSyncDone;             // avoid learning on first hardware read
        private volatile bool _postResumeGuard;    // block learning until real user input after resume-ish events

        // Wake race guards
        private DateTime _quiesceUntilUtc = DateTime.MinValue;           // during this time, never learn/persist
        private DateTime _lastUserInputUtc = DateTime.MinValue;          // for OFF learning gate
        private DateTime _lastResumeSignalUtc = DateTime.MinValue;       // diagnostics / future tuning
        private static readonly TimeSpan WakeQuiesce = TimeSpan.FromMilliseconds(800);
        private static readonly TimeSpan LearnOffRequiresInputWithin = TimeSpan.FromSeconds(3);

        // Coalesced delayed restores after resume-ish events
        private CancellationTokenSource? _restoreCts;
        private static readonly int[] RestoreChainDelaysMs = new[] { 140, 520 }; // two passes to outlast firmware wobble

        // Low-level hooks (keyboard + mouse)
        private static IntPtr _kbHookId = IntPtr.Zero;
        private static IntPtr _mouseHookId = IntPtr.Zero;
        private LowLevelKeyboardProc? _kbHookProc;
        private LowLevelMouseProc? _mouseHookProc;

        private const int WH_KEYBOARD_LL = 13;
        private const int WH_MOUSE_LL = 14;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_SYSKEYDOWN = 0x0104;

        // Slow refresh timer (every 5s)
        private DispatcherTimer? _slowTimer;

        // Burst polling control after keypress
        private int _burstInProgress; // 0/1
        private readonly TimeSpan _burstDuration = TimeSpan.FromMilliseconds(900);
        private readonly TimeSpan _burstInterval = TimeSpan.FromMilliseconds(120);

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // Icons
            _iconOff = LoadEmbeddedIcon("KeyboardBacklightForLenovo.IconOff.ico");
            _iconLow = LoadEmbeddedIcon("KeyboardBacklightForLenovo.IconLow.ico");
            _iconHigh = LoadEmbeddedIcon("KeyboardBacklightForLenovo.IconHigh.ico");

            _notifyIcon = new WinForms.NotifyIcon
            {
                Icon = _iconLow ?? SystemIcons.Application,
                Visible = true,
                Text = "Keyboard Backlight Controller for Lenovo"
            };

            // --- Build tray menu ---
            var menu = new WinForms.ContextMenuStrip();

            // Level items (selectable)
            _offItem = new WinForms.ToolStripMenuItem("Keyboard Backlight Off", null, (_, __) => SetLevelUserIntent(0));
            _lowItem = new WinForms.ToolStripMenuItem("Keyboard Backlight Low", null, (_, __) => SetLevelUserIntent(1));
            _highItem = new WinForms.ToolStripMenuItem("Keyboard Backlight High", null, (_, __) => SetLevelUserIntent(2));

            var settingsItem = new WinForms.ToolStripMenuItem("Settings", null, (_, __) => OpenSettingsWindow());
            
            // Info items (non-selectable)
            var acpiPrincipal = _controller.Principal.StartsWith(@"\\.\")
                ? _controller.Principal.Substring(4)
                : _controller.Principal;
            var description = _controller.Description;

            var infoDriverItem = new WinForms.ToolStripMenuItem($"Driver: {acpiPrincipal}") { Enabled = false };
            var infoPlatformItem = new WinForms.ToolStripMenuItem($"Supports: {description}") { Enabled = false };

            menu.Items.AddRange(new WinForms.ToolStripItem[]
            {
                _offItem, _lowItem, _highItem,
                new WinForms.ToolStripSeparator(),
                settingsItem,
                new WinForms.ToolStripSeparator(),
                infoDriverItem,
                infoPlatformItem,
                new WinForms.ToolStripSeparator(),
                new WinForms.ToolStripMenuItem("Exit", null, (_, __) => ExitApp())
            });

            _notifyIcon.ContextMenuStrip = menu;

            // Load preferred once; keep cached in memory
            _preferredCached = PreferredLevelStore.ReadPreferredLevel();
            _controller.ResetStatus(_preferredCached);

            // Initial sync from hardware (DO NOT persist)
            UpdateCheckedItemSafe(immediate: true);
            _initialSyncDone = true;

            // Power events (callback-based watcher)
            try
            {
                _powerWatcher = new PowerEventsWatcher(OnPowerEvent);
                _powerWatcher.Start();
                Debug.WriteLine("[Tray] PowerEventsWatcher started.");
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[Tray] Failed to start PowerEventsWatcher: " + ex);
            }

            // Hooks
            _kbHookProc = KeyboardHookCallback;
            _mouseHookProc = MouseHookCallback;
            using var proc = Process.GetCurrentProcess();
            using var mod = proc.MainModule!;
            IntPtr hMod = GetModuleHandle(mod.ModuleName);

            _kbHookId = SetWindowsHookEx(WH_KEYBOARD_LL, _kbHookProc, hMod, 0);
            _mouseHookId = SetWindowsHookExMouse(WH_MOUSE_LL, _mouseHookProc, hMod, 0);

            // Slow refresh every 5 seconds
            _slowTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _slowTimer.Tick += (_, __) => UpdateCheckedItemSafe(immediate: false);
            _slowTimer.Start();

            // Hide template window if present
            Current.MainWindow?.Hide();
        }

        private void OpenSettingsWindow()
        {
            var wnd = new SettingsWindow(nightLightAvailable: false); // TODO: wire up night light detection
            wnd.ShowDialog();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            try { _powerWatcher?.Dispose(); } catch { /* ignore */ }
            _powerWatcher = null;

            if (_kbHookId != IntPtr.Zero) UnhookWindowsHookEx(_kbHookId);
            if (_mouseHookId != IntPtr.Zero) UnhookWindowsHookEx(_mouseHookId);

            if (_slowTimer is not null) { _slowTimer.Stop(); _slowTimer = null; }

            _restoreCts?.Cancel(); _restoreCts?.Dispose(); _restoreCts = null;

            _iconOff?.Dispose(); _iconLow?.Dispose(); _iconHigh?.Dispose();

            if (_notifyIcon is not null) { _notifyIcon.Visible = false; _notifyIcon.Dispose(); }

            base.OnExit(e);
        }

        // ===== Power event callback =====
        private void OnPowerEvent(PowerEventType evt)
        {
            try
            {
                switch (evt)
                {
                    case PowerEventType.Boot:
                        // Already applied preferred at startup
                        break;

                    case PowerEventType.PowerResume:
                    case PowerEventType.SessionLogon:
                    case PowerEventType.SessionUnlock:
                    case PowerEventType.ConsoleConnect:
                    case PowerEventType.ScreenOn:
                        // Enter guarded wake window
                        _postResumeGuard = true;
                        _lastResumeSignalUtc = DateTime.UtcNow;
                        _quiesceUntilUtc = _lastResumeSignalUtc + WakeQuiesce;

                        // Coalesce multiple resume-ish events to one sequence of restores
                        ScheduleRestorePreferredChain(RestoreChainDelaysMs);
                        break;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[Tray] OnPowerEvent failed: " + ex);
            }
        }

        private void ScheduleRestorePreferredChain(int[] delaysMs)
        {
            var ctsOld = Interlocked.Exchange(ref _restoreCts, new CancellationTokenSource());
            try { ctsOld?.Cancel(); } catch { }
            ctsOld?.Dispose();

            var cts = _restoreCts!;
            _ = Task.Run(async () =>
            {
                try
                {
                    foreach (var d in delaysMs)
                    {
                        await Task.Delay(d, cts.Token);
                        TryRestorePreferredNoPersist();
                    }
                }
                catch (TaskCanceledException) { /* ok */ }
                catch (Exception ex)
                {
                    Debug.WriteLine("[Tray] Restore chain failed: " + ex.Message);
                }
                finally
                {
                    cts.Dispose();
                    Interlocked.CompareExchange(ref _restoreCts, null, cts);
                }
            });
        }

        private void TryRestorePreferredNoPersist()
        {
            try
            {
                int current = ReadSafe();
                if (current != _preferredCached)
                {
                    _controller.ResetStatus(_preferredCached);
                    _lastLevel = _preferredCached;
                    Dispatcher.Invoke(() => UpdateCheckedItemUIOnly(_preferredCached));
                    Debug.WriteLine($"[Tray] Restored preferred {_preferredCached} (current was {current})");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[Tray] TryRestorePreferred failed: " + ex.Message);
            }
        }

        // ===== Explicit user intent (tray menu) =====
        private void SetLevelUserIntent(int level)
        {
            PreferredLevelStore.SavePreferredLevel(level); // persist now
            _preferredCached = level;

            _controller.ResetStatus(level);
            _lastLevel = level;

            UpdateCheckedItemUIOnly(level);
            UpdateTrayIcon(level);
        }

        // ===== Periodic hardware sync (and on-demand) =====
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
                            UpdateCheckedItemUIOnly(level);

                            if (CanPersistLearned(level))
                            {
                                PreferredLevelStore.SavePreferredLevel(level);
                                _preferredCached = level;
                                Debug.WriteLine($"[Tray] Learned preferred={level}");
                            }
                            else
                            {
                                Debug.WriteLine($"[Tray] Suppressed learning (lvl={level}, guard={_postResumeGuard}, quiesce={(DateTime.UtcNow < _quiesceUntilUtc)})");
                            }
                        });
                    }
                }
            });
        }

        private bool CanPersistLearned(int level)
        {
            if (!_initialSyncDone) return false;
            // Never persist during the quiesce window after a wake signal
            if (DateTime.UtcNow < _quiesceUntilUtc) return false;
            // Block learning entirely until real user input after resume-ish events
            if (_postResumeGuard) return false;

            // Special rule: OFF (0) is only learned if there's recent user input.
            if (level == 0)
            {
                var sinceInput = DateTime.UtcNow - _lastUserInputUtc;
                if (sinceInput > LearnOffRequiresInputWithin) return false;
            }

            return true;
        }

        private int ReadSafe()
        {
            try { return _controller.GetStatus(); }
            catch { return -1; }
        }

        private void UpdateCheckedItemUIOnly(int level)
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

        // ===== Hooks =====
        private IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                int msg = wParam.ToInt32();
                if (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN)
                {
                    OnUserInput();

                    // Kick off a burst if not already running
                    if (Interlocked.Exchange(ref _burstInProgress, 1) == 0)
                        _ = BurstPollAsync();
                }
            }
            return CallNextHookEx(_kbHookId, nCode, wParam, lParam);
        }

        private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                OnUserInput();
            }
            return CallNextHookEx(_mouseHookId, nCode, wParam, lParam);
        }

        private void OnUserInput()
        {
            _lastUserInputUtc = DateTime.UtcNow;

            // First real input after resume clears the guard
            if (_postResumeGuard)
            {
                _postResumeGuard = false;
                Debug.WriteLine("[Tray] User input -> post-resume guard cleared");
            }
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
                            Dispatcher.Invoke(() =>
                            {
                                UpdateCheckedItemUIOnly(current);

                                if (CanPersistLearned(current))
                                {
                                    PreferredLevelStore.SavePreferredLevel(current);
                                    _preferredCached = current;
                                    Debug.WriteLine($"[Tray] Learned preferred={current} (burst)");
                                }
                                else
                                {
                                    Debug.WriteLine($"[Tray] Suppressed learning (burst, lvl={current})");
                                }
                            });
                            break; // early exit
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

        // --- Icon loading ---
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

        // ===== P/Invoke =====
        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);
        private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

        // Separate overload for mouse to avoid delegate type ambiguity
        [DllImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)]
        private static extern IntPtr SetWindowsHookExMouse(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);
    }
}
