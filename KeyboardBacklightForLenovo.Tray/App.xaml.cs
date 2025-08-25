using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
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

        // Your core controller
        private KeyboardBacklightController _controller = new();

        // Track last seen hardware state to avoid redundant UI work
        private int _lastLevel = -1;

        // Low-level keyboard hook
        private static IntPtr _hookId = IntPtr.Zero;
        private LowLevelKeyboardProc? _hookProc; // keep delegate alive
        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;

        // Slow refresh timer (every 5s)
        private DispatcherTimer? _slowTimer;

        // Burst polling control
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
                Text = "ThinkPad Keyboard Backlight Controller"
            };

            var menu = new WinForms.ContextMenuStrip();

            _offItem = new WinForms.ToolStripMenuItem("Keyboard Backlight Off", null, (_, __) => SetLevel(0));
            _lowItem = new WinForms.ToolStripMenuItem("Keyboard Backlight Low", null, (_, __) => SetLevel(1));
            _highItem = new WinForms.ToolStripMenuItem("Keyboard Backlight High", null, (_, __) => SetLevel(2));

            menu.Items.AddRange(new WinForms.ToolStripItem[]
            {
                _offItem, _lowItem, _highItem,
                new WinForms.ToolStripSeparator(),
                new WinForms.ToolStripMenuItem("Exit", null, (_, __) => ExitApp())
            });

            _notifyIcon.ContextMenuStrip = menu;

            // Sync preferred level from registry
            int preferredLevel = ReadPreferredLevel();
            _controller.ResetStatus(preferredLevel);

            // Initial sync from hardware
            UpdateCheckedItemSafe(immediate: true);

            // Install low-level keyboard hook (for ANY key press → burst poll)
            _hookProc = HookCallback;
            using var proc = System.Diagnostics.Process.GetCurrentProcess();
            using var mod = proc.MainModule!;
            _hookId = SetWindowsHookEx(WH_KEYBOARD_LL, _hookProc, GetModuleHandle(mod.ModuleName), 0);

            // Slow refresh every 5 seconds
            _slowTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _slowTimer.Tick += (_, __) => UpdateCheckedItemSafe(immediate: false);
            _slowTimer.Start();

            // Hide template window if present
            Current.MainWindow?.Hide();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            if (_hookId != IntPtr.Zero) UnhookWindowsHookEx(_hookId);

            if (_slowTimer is not null)
            {
                _slowTimer.Stop();
                _slowTimer = null;
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

        // Menu click → set + persist + update
        private void SetLevel(int level)
        {
            SavePreferredLevel(level);

            // We set hardware directly, so reflect immediately in UI
            _controller.ResetStatus(level);
            _lastLevel = level;

            UpdateCheckedItem(level);
            UpdateTrayIcon(level);
        }

        private static int ReadPreferredLevel()
        {
            // Default to 2 (High) if not set
            return (int)(Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\KeyboardBacklightForLenovo",
                "PreferredLevel",
                2) ?? 2);
        }

        private static void SavePreferredLevel(int level)
        {
            Registry.SetValue(
                @"HKEY_CURRENT_USER\Software\KeyboardBacklightForLenovo",
                "PreferredLevel",
                level,
                RegistryValueKind.DWord
            );
        }

        // Reads hardware on a background task, then updates UI if changed
        private void UpdateCheckedItemSafe(bool immediate)
        {
            Task.Run(() =>
            {
                int level = ReadSafe();
                if (level is 0 or 1 or 2)
                {
                    if (level != _lastLevel || immediate)
                    {
                        _lastLevel = level;
                        Dispatcher.Invoke(() => UpdateCheckedItem(level));
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
            SavePreferredLevel(level);
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
                default: /* leave current icon */ break;
            }
        }

        // Keyboard hook: we can’t see Fn, but we DO see other keys.
        // On ANY keydown, run a short "burst" polling window to catch
        // the Fn+Space change quickly without waiting for the 5s timer.
        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && wParam == (IntPtr)WM_KEYDOWN)
            {
                // Kick off a burst if not already running
                if (Interlocked.Exchange(ref _burstInProgress, 1) == 0)
                {
                    _ = BurstPollAsync();
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
                int last = _lastLevel;

                while (DateTime.UtcNow < deadline)
                {
                    await Task.Delay(_burstInterval);

                    int current = ReadSafe();
                    if (current is 0 or 1 or 2)
                    {
                        if (current != _lastLevel)
                        {
                            _lastLevel = current;
                            Dispatcher.Invoke(() => UpdateCheckedItem(current));
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

        // P/Invoke
        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct KBDLLHOOKSTRUCT
        {
            public int vkCode;
            public int scanCode;
            public int flags;
            public int time;
            public IntPtr dwExtraInfo;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);
    }
}
