using System;
using System.IO;

namespace KeyboardBacklightForLenovo
{
  internal static class TrayLogger
  {
    private static readonly object Sync = new();
    private static readonly string LogDirectory =
      Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KeyboardBacklightForLenovo");
    private static readonly string LogPath = Path.Combine(LogDirectory, "Tray.log");

    public static void Write(string message)
    {
      try
      {
        string line = $"[{DateTime.Now:O}] {message}{Environment.NewLine}";
        lock (Sync)
        {
          Directory.CreateDirectory(LogDirectory);
          File.AppendAllText(LogPath, line);
        }
      }
      catch
      {
        // Swallow logging errors to avoid impacting app behaviour.
      }
    }
  }
}
