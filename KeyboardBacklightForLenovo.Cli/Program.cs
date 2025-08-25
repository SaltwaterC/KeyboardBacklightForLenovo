// Generic, JSON-driven CLI for ThinkPad/ThinkBook keyboard backlight
// Requires a JSON config file on disk (default: KeyboardBacklightDrivers.json next to the EXE).

using System;
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

                    // remove the two tokens so command parsing is clean
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
        Console.WriteLine("keyboard-backligth");
        Console.WriteLine("Usage:");
        Console.WriteLine("  get-level [--drivers <path-to-DriversConfig.json>]");
        Console.WriteLine("  set-level <off|low|high|0|1|2> [--drivers <path-to-DriversConfig.json>]");
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
        Console.WriteLine($"principal={ctrl.Principal} ({ctrl.Description})  mapped={mapped} ({lvl})");
        return 0;
    }

    private static int CmdSetLevel(KeyboardBacklightController ctrl, Level level)
    {
        ctrl.SetStatus((int)level);
        int now = ctrl.GetStatus();
        Console.WriteLine($"Set {level} OK -> principal={ctrl.Principal} ({ctrl.Description}) now={now}");
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
}
