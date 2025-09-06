using System;

internal static class Program
{
  [STAThread]
  private static int Main(string[] args)
  {
    try
    {
      string procName = "BacklightTrayApp";
      int timeoutSec = 5;
      for (int i = 0; i < args.Length; i++)
      {
        if (string.Equals(args[i], "--name", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
        { procName = args[++i]; continue; }
        if (string.Equals(args[i], "--timeout", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length && int.TryParse(args[i + 1], out var t))
        { timeoutSec = t; i++; continue; }
      }

      bool ok = KeyboardBacklightForLenovo.TrayCloser.CloseByWMClose(procName, timeoutSec);
      return ok ? 0 : 1;
    }
    catch
    {
      return 1;
    }
  }
}

