using System.Diagnostics;

namespace KeyboardBacklightForLenovo
{
  internal static class ServiceLogger
  {
    // Use the service name as the event source to keep it consistent.
    private const string Source = "BacklightResetService";
    private const string Log = "Application";

    // IMPORTANT: Do NOT attempt to create the source here (requires elevation).
    // Assume the WiX installer (or dev script) has created it already.

    public static void LogInfo(string message)
    {
      try
      {
        EventLog.WriteEntry(Source, message, System.Diagnostics.EventLogEntryType.Information);
      }
      catch
      {
        // Fall back to stderr to aid dev runs
        try { Console.Error.WriteLine($"[INFO] {message}"); } catch { }
      }
    }

    public static void LogError(string message)
    {
      try
      {
        EventLog.WriteEntry(Source, message, System.Diagnostics.EventLogEntryType.Error);
      }
      catch
      {
        try { Console.Error.WriteLine($"[ERROR] {message}"); } catch { }
      }
    }
  }
}
