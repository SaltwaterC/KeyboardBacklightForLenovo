// Generic, JSON-driven CLI for ThinkPad/ThinkBook keyboard backlight
// Requires a JSON config file on disk (default: DriversConfig.json next to the EXE).

using System;
using Microsoft.Win32;
using KeyboardBacklightForLenovo;

internal static class Program
{
  private enum Level : int { Off = 0, Low = 1, High = 2 }

  private static int Main(string[] args)
  {
    try
    {
      if (args.Length == 0 || IsHelp(args[0]))
      {
        PrintHelp();
        return 0;
      }

      // Optional: --drivers <path> to a JSON file. If omitted, controller looks next to EXE.
      string? driversPath = null;
      for (int i = 0; i < args.Length; i++)
      {
        if (args[i].Equals("--drivers", StringComparison.OrdinalIgnoreCase))
        {
          if (i + 1 >= args.Length)
            throw new ArgumentException("--drivers requires a file path");
          driversPath = args[i + 1];

          var list = new System.Collections.Generic.List<string>(args);
          list.RemoveAt(i + 1);
          list.RemoveAt(i);
          args = list.ToArray();
          break;
        }
      }

      using var ctrl = new KeyboardBacklightController(driversPath);

      var cmd = args[0].ToLowerInvariant();
      switch (cmd)
      {
        case "get-level":
        case "kbd-get":
          return CmdGetLevel(ctrl);

        case "set-level":
        case "kbd-set":
          if (args.Length < 2)
            throw new ArgumentException("set-level requires a value: off|low|high|0|1|2");
          if (!TryParseLevel(args[1], out var level))
            throw new ArgumentException("Invalid level. Use off|low|high|0|1|2");
          return CmdSetLevel(ctrl, level);

        case "reset":
          return CmdReset(ctrl);

        case "watch":
          return CmdWatch(ctrl);

        case "night-light":
          return CmdNightLight();

        case "close-tray":
        case "tray-close":
          {
            string procName = "BacklightTrayApp";
            int timeoutSec = 5;
            // Parse optional flags: --name <proc>, --timeout <sec>
            for (int i = 1; i < args.Length; i++)
            {
              if (args[i].Equals("--name", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
              { procName = args[++i]; continue; }
              if (args[i].Equals("--timeout", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length && int.TryParse(args[i + 1], out var t))
              { timeoutSec = t; i++; continue; }
            }
            return CmdCloseTray(procName, timeoutSec);
          }

        default:
          Console.Error.WriteLine($"Unknown command: {args[0]}");
          PrintHelp();
          return 2;
      }
    }
    catch (Exception ex)
    {
      Console.Error.WriteLine(ex.Message);
      return 1;
    }
  }

  private static void PrintHelp()
  {
    Console.WriteLine("keyboard-backlight");
    Console.WriteLine("Usage:");
    Console.WriteLine("  get-level [--drivers <path-to-DriversConfig.json>]");
    Console.WriteLine("  set-level <off|low|high|0|1|2> [--drivers <path-to-DriversConfig.json>]");
    Console.WriteLine("  reset [--drivers <path-to-DriversConfig.json>]");
    Console.WriteLine("  watch [--drivers <path-to-DriversConfig.json>]");
    Console.WriteLine("  night-light");
    Console.WriteLine("  close-tray [--name BacklightTrayApp] [--timeout 5]");
    Console.WriteLine();
    Console.WriteLine("A JSON driver config file is REQUIRED.");
    Console.WriteLine("Default: a file named 'DriversConfig.json' located next to the executable.");
    Console.WriteLine("Use --drivers to point at a different JSON file if needed.");
  }

  private static bool IsHelp(string s) =>
      s.Equals("-h", StringComparison.OrdinalIgnoreCase) ||
      s.Equals("--help", StringComparison.OrdinalIgnoreCase) ||
      s.Equals("/h", StringComparison.OrdinalIgnoreCase) ||
      s.Equals("/?");

  private static int CmdGetLevel(KeyboardBacklightController ctrl)
  {
    int lvl = ctrl.GetStatus();
    string mapped = lvl switch
    {
      0 => "Off",
      1 => "Low",
      2 => "High",
      _ => "Unknown"
    };
    Console.WriteLine($"principal={ctrl.Principal} ({ctrl.Description})  mapped={mapped} ({lvl})  store={PreferredLevelStore.StorePath}");
    return 0;
  }

  private static int CmdSetLevel(KeyboardBacklightController ctrl, Level level)
  {
    ctrl.SetStatusNoVerify((int)level);
    int now = ctrl.GetStatus();
    PreferredLevelStore.SavePreferredLevel((int)level);
    Console.WriteLine($"Set {level} OK -> principal={ctrl.Principal} ({ctrl.Description}) now={now}  store={PreferredLevelStore.StorePath}");
    return 0;
  }

  private static int CmdReset(KeyboardBacklightController ctrl)
  {
    int preferred = PreferredLevelStore.ReadPreferredLevel();
    ctrl.ResetStatus(preferred);
    Console.WriteLine($"Reset applied -> preferred={preferred} principal={ctrl.Principal} ({ctrl.Description})  store={PreferredLevelStore.StorePath}");
    return 0;
  }

  private static int CmdWatch(KeyboardBacklightController ctrl)
  {
    Console.WriteLine("Watching power/session/screen events. Press Ctrl+C to exit.");
    Console.WriteLine($"Preference store: {PreferredLevelStore.StorePath}");

    using var watcher = new PowerEventsWatcher(
        onTrigger: evt =>
        {
          int preferred = PreferredLevelStore.ReadPreferredLevel();
          try
          {
            ctrl.ResetStatus(preferred);
            Console.WriteLine($"[{DateTime.Now:T}] {evt}: applied preferred level {preferred}");
          }
          catch (Exception ex)
          {
            Console.Error.WriteLine($"[{DateTime.Now:T}] {evt}: failed to apply preferred level {preferred}: {ex.Message}");
          }
        });

    watcher.Start();

    var exit = new ManualResetEvent(false);
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; exit.Set(); };
    exit.WaitOne();
    return 0;
  }

  private static int CmdNightLight()
  {
    var nightLight = new NightLight();
    Console.WriteLine($"Night light supported: {nightLight.Supported}");
    Console.WriteLine($"Night light state: {nightLight.Enabled}");
    return 0;
  }

  private static bool TryParseLevel(string s, out Level level)
  {
    switch (s.Trim().ToLowerInvariant())
    {
      case "0":
      case "off": level = Level.Off; return true;
      case "1":
      case "low": level = Level.Low; return true;
      case "2":
      case "high": level = Level.High; return true;
    }
    if (int.TryParse(s, out var i) && i >= 0 && i <= 2) { level = (Level)i; return true; }
    level = Level.Off;
    return false;
  }

  private static int CmdCloseTray(string processName, int timeoutSec)
  {
    bool ok = KeyboardBacklightForLenovo.TrayCloser.CloseByWMClose(processName, timeoutSec);
    return ok ? 0 : 1;
  }
}
