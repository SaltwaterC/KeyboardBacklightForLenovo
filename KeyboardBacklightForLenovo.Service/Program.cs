using System;
using System.ServiceProcess;

namespace KeyboardBacklightForLenovo
{
  internal static class Program
  {
    public static void Main(string[] args)
    {
      AppDomain.CurrentDomain.UnhandledException += (_, e) =>
          ServiceLogger.LogError("Fatal (domain): " + (e.ExceptionObject?.ToString() ?? "null"));

      // Allow running as a console for dev: BacklightResetService.exe --console
      if (Environment.UserInteractive && args.Length > 0 && args[0].Equals("--console", StringComparison.OrdinalIgnoreCase))
      {
        using var svc = new ResetService();
        svc.StartForConsole();
        Console.WriteLine("Running in console mode. Press Enter to exit.");
        Console.ReadLine();
        svc.StopForConsole();
        return;
      }

      ServiceBase.Run(new ResetService());
    }
  }
}
