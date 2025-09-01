using System;
using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using System.Windows.Interop;
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
    private WinForms.ToolStripMenuItem _autoItem = null!;

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

    // Night light + settings
    private readonly NightLight _nightLight = new NightLight();
    private TraySettings _settings = TraySettingsStore.LoadOrDefaults();

    // Auto engine
    private DispatcherTimer? _autoTimer;        // periodic evaluator
    private bool AutoEnabled => _settings.AutoEnabled; // convenience
    private static string ModeLabel(OperatingMode mode)
        => mode == OperatingMode.TimeBased ? "Time based" : "Night light";
    // Coalesced auto re-evals (startup/resume) for Night light
    private CancellationTokenSource? _autoKickCts;

    private static Mutex? _singleInstanceMutex;
    private const string SingleInstanceName = "BacklightTrayApp";
    private Window? _restartManagerWindow; // hidden sink for Restart Manager messages (WPF, legacy)
    private HwndSource? _rmSource;         // native popup sink (eligible as main window without taskbar)
    private IntPtr _rmHwnd;                // handle of native sink
    private bool _rmExitInitiated; // ensure shutdown path runs once

    protected override void OnStartup(StartupEventArgs e)
    {
      // ---- single instance guard ----
      bool createdNew;
      _singleInstanceMutex = new Mutex(initiallyOwned: true, name: SingleInstanceName, createdNew: out createdNew);
      if (!createdNew)
      {
        // Another instance is running, exit this one
        Shutdown();
        return;
      }

      base.OnStartup(e);

      // Create a hidden window to receive Restart Manager shutdown messages (WM_QUERYENDSESSION/WM_ENDSESSION)
      InitializeRestartManagerSink();

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

      // Auto toggle item (does not change which of Off/Low/High is selected)
      // Replace your current _autoItem creation with:
      _autoItem = new WinForms.ToolStripMenuItem($"Auto ({ModeLabel(_settings.Mode)})", null, (_, __) => ToggleAuto());
      _autoItem.CheckOnClick = false;
      _autoItem.Checked = _settings.AutoEnabled;

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
                _autoItem,
                new WinForms.ToolStripSeparator(),
                settingsItem,
                new WinForms.ToolStripSeparator(),
                infoDriverItem, infoPlatformItem,
                new WinForms.ToolStripSeparator(),
                new WinForms.ToolStripMenuItem("Exit", null, (_, __) => ExitApp())
      });

      _notifyIcon.ContextMenuStrip = menu;

      // Night light registry watcher
      _nightLight.Changed += OnNightLightChanged;
      _nightLight.StartWatching();

      // Auto-at-start behavior:
      if (AutoEnabled)
      {
        if (_settings.Mode == OperatingMode.NightLight)
        {
          // Resolve NL immediately; default to OFF if unknown
          bool nlActive = _nightLight.Enabled;
          int desired = nlActive ? _settings.NightLevel : _settings.DayLevel;
          if (desired != PreferredLevelStore.ReadPreferredLevel())
            PreferredLevelStore.SavePreferredLevel(desired);
          _preferredCached = desired;

          // Coalesced re-checks to capture any lagging registry state.
          KickAutoSoon("startup-nightlight", 300, 1500, 6000, 15000);
        }
        else
        {
          // Time-based: safe to seed immediately.
          int desired = ComputeDesiredLevelByDayNight();
          if (desired != PreferredLevelStore.ReadPreferredLevel())
            PreferredLevelStore.SavePreferredLevel(desired);
          _preferredCached = desired;
        }
      }
      else
      {
        _preferredCached = PreferredLevelStore.ReadPreferredLevel();
      }


      // Apply preferred to hardware (ResetStatus avoids unnecessary sets)
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

      // Auto evaluation timer. Keep this light; day/night flips are infrequent.
      _autoTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
      _autoTimer.Tick += (_, __) => EvaluateAuto(applyHardware: true, reason: "timer");
      _autoTimer.Start();

      // Run an immediate evaluation at startup.
      EvaluateAuto(applyHardware: true, reason: "startup");

      // Hide template window if present
      Current.MainWindow?.Hide();
    }

    private void UpdateAutoMenuText()
    {
      if (_autoItem == null) return;
      _autoItem.Text = $"Auto ({ModeLabel(_settings.Mode)})";
    }

    private void OpenSettingsWindow()
    {
      var wnd = new SettingsWindow(nightLightAvailable: _nightLight.Supported);
      wnd.ShowDialog();

      _settings = TraySettingsStore.LoadOrDefaults();

      _autoItem.Checked = _settings.AutoEnabled;
      UpdateAutoMenuText();             // <— ensure the “(Time based|Night light)” label updates

      EvaluateAuto(applyHardware: true, reason: "settings-changed");
    }


    protected override void OnExit(ExitEventArgs e)
    {
      try { _restartManagerWindow?.Close(); } catch { /* ignore */ }
      _restartManagerWindow = null;
      try { _rmSource?.Dispose(); } catch { /* ignore */ }
      _rmSource = null;
      _rmHwnd = IntPtr.Zero;
      // Night light polling no longer used (replaced by registry watcher)
      try { _powerWatcher?.Dispose(); } catch { /* ignore */ }
      _powerWatcher = null;

      if (_kbHookId != IntPtr.Zero) UnhookWindowsHookEx(_kbHookId);
      if (_mouseHookId != IntPtr.Zero) UnhookWindowsHookEx(_mouseHookId);

      if (_slowTimer is not null) { _slowTimer.Stop(); _slowTimer = null; }
      if (_autoTimer is not null) { _autoTimer.Stop(); _autoTimer = null; }

      _restoreCts?.Cancel(); _restoreCts?.Dispose(); _restoreCts = null;

      // Dispose tray icon first to avoid ObjectDisposedException when NotifyIcon tries to use disposed Icon handles
      if (_notifyIcon is not null)
      {
        try { _notifyIcon.Visible = false; _notifyIcon.Icon = null; } catch { /* ignore */ }
        _notifyIcon.Dispose();
      }

      // Now it's safe to dispose our Icon instances
      _iconOff?.Dispose(); _iconLow?.Dispose(); _iconHigh?.Dispose();

      // Night light polling no longer used (replaced by registry watcher)

      _autoKickCts?.Cancel();
      _autoKickCts?.Dispose();
      _autoKickCts = null;

      try { _singleInstanceMutex?.ReleaseMutex(); } catch { /* ignore */ }
      _singleInstanceMutex?.Dispose();
      _singleInstanceMutex = null;

      base.OnExit(e);
    }

    // ===== Auto toggle =====
    private void ToggleAuto()
    {
      _settings = TraySettingsStore.LoadOrDefaults(); // refresh latest
      _settings.AutoEnabled = !_settings.AutoEnabled;
      TraySettingsStore.Save(_settings);

      _autoItem.Checked = _settings.AutoEnabled;

      if (_settings.AutoEnabled)
      {
        // Immediately evaluate and apply
        EvaluateAuto(applyHardware: true, reason: "manual-toggle-on");
      }
      // When toggling OFF, we just stop suppressing learn; nothing else to do.
    }

    private void OnNightLightChanged(object? sender, EventArgs e)
    {
      // Only care when Auto is enabled AND Night light mode is chosen.
      if (_settings.AutoEnabled && _settings.Mode == OperatingMode.NightLight)
      {
        // Re-evaluate immediately and apply to hardware so the keyboard switches with the screen tint
        EvaluateAuto(applyHardware: true, reason: "nightlight-registry-change");
      }
    }

    private void KickAutoSoon(string reason, params int[] delaysMs)
    {
      if (!AutoEnabled) return;

      var old = Interlocked.Exchange(ref _autoKickCts, new CancellationTokenSource());
      try { old?.Cancel(); } catch { }
      old?.Dispose();

      var cts = _autoKickCts!;
      _ = Task.Run(async () =>
      {
        try
        {
          foreach (var d in delaysMs)
          {
            await Task.Delay(d, cts.Token);
            EvaluateAuto(applyHardware: true, reason: $"{reason}+{d}ms");
          }
        }
        catch (TaskCanceledException) { /* fine */ }
        finally
        {
          cts.Dispose();
          Interlocked.CompareExchange(ref _autoKickCts, null, cts);
        }
      });
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

            // Auto-at-start behavior:
            if (AutoEnabled)
            {
              if (_settings.Mode == OperatingMode.NightLight)
              {
                // Kick re-evaluation to apply Night light based level
                KickAutoSoon("resume-nightlight", 300, 1500, 6000);
              }
              else
              {
                // Time-based: safe to seed immediately.
                int desired = ComputeDesiredLevelByDayNight();
                if (desired != PreferredLevelStore.ReadPreferredLevel())
                  PreferredLevelStore.SavePreferredLevel(desired);
                _preferredCached = desired;
              }
            }
            else
            {
              _preferredCached = PreferredLevelStore.ReadPreferredLevel();
            }

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

    // ===== Auto engine =====
    private void EvaluateAuto(bool applyHardware, string reason)
    {
      if (!AutoEnabled) return;

      // Night light mode: do not defer; ComputeDesiredLevelByDayNight treats unknown as OFF.

      int desired = ComputeDesiredLevelByDayNight();
      if (desired != _preferredCached)
      {
        PreferredLevelStore.SavePreferredLevel(desired);
        _preferredCached = desired;
        Debug.WriteLine($"[Auto] Persisted preferred={desired} ({reason})");

        if (applyHardware)
        {
          try
          {
            _controller.ResetStatus(desired);
            _lastLevel = desired;
            Dispatcher.Invoke(() => UpdateCheckedItemUIOnly(desired));
            Debug.WriteLine($"[Auto] Applied hardware level={desired} ({reason})");
          }
          catch (Exception ex)
          {
            Debug.WriteLine("[Auto] Apply failed: " + ex.Message);
          }
        }
      }
    }

    private int ComputeDesiredLevelByDayNight()
    {
      // Reload settings lightly in case the JSON changed externally
      var s = _settings;

      bool timeBased = s.Mode == OperatingMode.TimeBased;
      bool isNight;

      if (timeBased)
      {
        var now = DateTime.Now.TimeOfDay;
        var dayStart = s.DayStart;
        var dayEnd = s.DayEnd;

        // Day: [dayStart, dayEnd) possibly crossing midnight
        // Night: complement
        if (dayStart <= dayEnd)
        {
          // Simple case: same day
          bool isDay = now >= dayStart && now < dayEnd;
          isNight = !isDay;
        }
        else
        {
          // Wrap-around (e.g. 20:00 → 08:00)
          bool isDay = now >= dayStart || now < dayEnd;
          isNight = !isDay;
        }
      }
      else
      {
        // Night light mode: default to OFF when unknown
        bool en = _nightLight.Enabled;
        isNight = en;
        try { System.Diagnostics.Debug.WriteLine($"[Auto] NightLight.Enabled={en}"); } catch { }
      }

      return isNight ? s.NightLevel : s.DayLevel;
    }

    // ===== Explicit user intent (tray menu) =====
    private void SetLevelUserIntent(int level)
    {
      // In Auto mode we DO NOT persist; just apply to hardware and let auto own the preference on day/night
      if (!AutoEnabled)
      {
        PreferredLevelStore.SavePreferredLevel(level); // persist now
        _preferredCached = level;
      }

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

                    // Suppress *all* learning when Auto is ON
                    if (!AutoEnabled && CanPersistLearned(level))
                    {
                      PreferredLevelStore.SavePreferredLevel(level);
                      _preferredCached = level;
                      Debug.WriteLine($"[Tray] Learned preferred={level}");
                    }
                    else
                    {
                      Debug.WriteLine($"[Tray] Suppressed learning (lvl={level}, auto={AutoEnabled}, guard={_postResumeGuard}, quiesce={(DateTime.UtcNow < _quiesceUntilUtc)})");
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
      _autoItem.Checked = AutoEnabled; // keep auto tick visible regardless of level
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

                // Suppress learning in Auto mode
                if (!AutoEnabled && CanPersistLearned(current))
                {
                  PreferredLevelStore.SavePreferredLevel(current);
                  _preferredCached = current;
                  Debug.WriteLine($"[Tray] Learned preferred={current} (burst)");
                }
                else
                {
                  Debug.WriteLine($"[Tray] Suppressed learning (burst, lvl={current}, auto={AutoEnabled})");
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

    // ===== Restart Manager integration =====
    private void InitializeRestartManagerSink()
    {
      // Create a native WS_POPUP window that is visible and unowned, off-screen.
      // It won't appear on the taskbar, yet it qualifies as a candidate main window for Process APIs.
      var p = new HwndSourceParameters("BacklightTrayApp");
      p.Width = 1;
      p.Height = 1;
      p.PositionX = -10000;
      p.PositionY = -10000;
      p.ParentWindow = IntPtr.Zero; // unowned
      p.WindowStyle = WS_POPUP | WS_VISIBLE;
      // Make it a tool window to avoid taskbar/Alt-Tab, and avoid activation.
      p.ExtendedWindowStyle = WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;

      _rmSource = new HwndSource(p);
      _rmSource.AddHook(RestartManagerWndProc);
      _rmHwnd = _rmSource.Handle;
    }

    // Restart Manager sends WM_QUERYENDSESSION with ENDSESSION_CLOSEAPP, followed by WM_ENDSESSION.
    private const int WM_CLOSE = 0x0010;
    private const int WM_QUERYENDSESSION = 0x0011;
    private const int WM_ENDSESSION = 0x0016;
    private const int ENDSESSION_CLOSEAPP = 0x00000001;
    private const int WS_POPUP = unchecked((int)0x80000000);
    private const int WS_VISIBLE = 0x10000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    private IntPtr RestartManagerWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
      switch (msg)
      {
        case WM_CLOSE:
        case WM_QUERYENDSESSION:
        case WM_ENDSESSION:
          // Let default close processing proceed; also request app exit immediately.
          RequestAppExitFromRM();
          // Do NOT mark handled; allow window to actually close.
          handled = false;
          return IntPtr.Zero;
      }
      return IntPtr.Zero;
    }

    // Idempotent, aggressive shutdown path used when requested by Restart Manager
    private void RequestAppExitFromRM()
    {
      if (_rmExitInitiated) return;
      _rmExitInitiated = true;

      try
      {
        // Make closing any remaining window sufficient to exit
        try { this.ShutdownMode = ShutdownMode.OnLastWindowClose; } catch { }

        // Stop timers early
        try { _slowTimer?.Stop(); } catch { }
        _slowTimer = null;
        try { _autoTimer?.Stop(); } catch { }
        _autoTimer = null;

        // Cancel any pending work
        try { _restoreCts?.Cancel(); } catch { }
        try { _restoreCts?.Dispose(); } catch { }
        _restoreCts = null;
        try { _autoKickCts?.Cancel(); } catch { }
        try { _autoKickCts?.Dispose(); } catch { }
        _autoKickCts = null;

        // Unhook low-level hooks ASAP
        try { if (_kbHookId != IntPtr.Zero) UnhookWindowsHookEx(_kbHookId); } catch { }
        _kbHookId = IntPtr.Zero;
        try { if (_mouseHookId != IntPtr.Zero) UnhookWindowsHookEx(_mouseHookId); } catch { }
        _mouseHookId = IntPtr.Zero;

        // Power watcher
        try { _powerWatcher?.Dispose(); } catch { }
        _powerWatcher = null;

        // Night light polling
        // Night light polling no longer used (replaced by registry watcher)

        // Tray icon first, then icon handles
        if (_notifyIcon is not null)
        {
          try { _notifyIcon.Visible = false; _notifyIcon.Icon = null; } catch { }
          try { _notifyIcon.Dispose(); } catch { }
          _notifyIcon = null;
        }
        try { _iconOff?.Dispose(); } catch { }
        try { _iconLow?.Dispose(); } catch { }
        try { _iconHigh?.Dispose(); } catch { }
        _iconOff = _iconLow = _iconHigh = null;

        // Close hidden window (and any other windows)
        try { _restartManagerWindow?.Close(); } catch { }
        _restartManagerWindow = null;
        try { _rmSource?.Dispose(); } catch { }
        _rmSource = null;
        _rmHwnd = IntPtr.Zero;
        try { Current.MainWindow?.Close(); } catch { }

        // Finally request shutdown
        try { Shutdown(); } catch { }

        // Ensure process termination in case message pumps are blocked
        _ = Task.Run(async () => { try { await Task.Delay(2000); Environment.Exit(0); } catch { } });
      }
      catch
      {
        // If anything unexpected occurs, guarantee process exit
        try { Environment.Exit(0); } catch { }
      }
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

    // (RegisterApplicationRestart removed per requirements)
  }
}
