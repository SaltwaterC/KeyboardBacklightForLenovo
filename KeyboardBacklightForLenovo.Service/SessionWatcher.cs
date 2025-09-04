using System.Runtime.InteropServices;

namespace KeyboardBacklightForLenovo
{
  public sealed class SessionWatcher
  {
    /// <summary>
    /// True if there is any HUMAN user logged on (locked or unlocked).
    /// Definition: any session with SessionId != 0 that has a non-empty user name
    /// not belonging to built-in service/system accounts (SYSTEM, LOCAL/NETWORK SERVICE,
    /// DWM-*, UMFD-*, defaultuser0), and in WTSActive state.
    /// </summary>
    public bool IsAnyUserLoggedOn(string reason)
    {
      if (!WtsApi32.WTSEnumerateSessions(IntPtr.Zero, 0, 1, out var pp, out var count))
        return false;

      try
      {
        var size = Marshal.SizeOf<WtsApi32.WTS_SESSION_INFO>();
        for (int i = 0; i < count; i++)
        {
          var p = pp + i * size;
          var info = Marshal.PtrToStructure<WtsApi32.WTS_SESSION_INFO>(p);

          // Exclude Session 0 (services), it's never a human session.
          if (info.SessionId == 0)
            continue;

          // Consider a human user present only when the session is active.
          // This avoids false positives from disconnected/idle sessions during boot.
          if (info.State != WtsApi32.WTS_CONNECTSTATE_CLASS.WTSActive)
            continue;

          var user = WtsApi32.WTSQuerySessionString(IntPtr.Zero, info.SessionId, WtsApi32.WTS_INFO_CLASS.WTSUserName);
          var domain = WtsApi32.WTSQuerySessionString(IntPtr.Zero, info.SessionId, WtsApi32.WTS_INFO_CLASS.WTSDomainName);
          if (!string.IsNullOrWhiteSpace(user) && !IsServiceOrSystemUser(user))
          {
            ServiceLogger.LogInfo($"[{reason}] {user} logged on.");
            return true;
          }
        }
      }
      finally
      {
        WtsApi32.WTSFreeMemory(pp);
      }

      return false;
    }

    private static bool IsServiceOrSystemUser(string user)
    {
      // Fast filter for built-in accounts we never treat as "interactive".
      return user.Equals("SYSTEM", StringComparison.OrdinalIgnoreCase)
          || user.Equals("LOCAL SERVICE", StringComparison.OrdinalIgnoreCase)
          || user.Equals("NETWORK SERVICE", StringComparison.OrdinalIgnoreCase)
          || user.Equals("defaultuser0", StringComparison.OrdinalIgnoreCase)
          || user.StartsWith("DWM-", StringComparison.OrdinalIgnoreCase)
          || user.StartsWith("UMFD-", StringComparison.OrdinalIgnoreCase);
    }
  }

  internal static class WtsApi32
  {
    [DllImport("Wtsapi32.dll", CharSet = CharSet.Unicode)]
    public static extern bool WTSEnumerateSessions(IntPtr hServer, int Reserved, int Version, out IntPtr ppSessionInfo, out int pCount);

    [DllImport("Wtsapi32.dll", CharSet = CharSet.Unicode)]
    public static extern void WTSFreeMemory(IntPtr pMemory);

    [DllImport("Wtsapi32.dll", CharSet = CharSet.Unicode)]
    public static extern bool WTSQuerySessionInformation(IntPtr hServer, int SessionId, WTS_INFO_CLASS WTSInfoClass, out IntPtr ppBuffer, out int pBytesReturned);

    public static string WTSQuerySessionString(IntPtr server, int sessionId, WTS_INFO_CLASS cls)
    {
      if (!WTSQuerySessionInformation(server, sessionId, cls, out var p, out _))
        return string.Empty;
      try { return Marshal.PtrToStringUni(p) ?? string.Empty; }
      finally { WTSFreeMemory(p); }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct WTS_SESSION_INFO
    {
      public int SessionId;
      public IntPtr pWinStationName;
      public WTS_CONNECTSTATE_CLASS State;
    }

    public enum WTS_CONNECTSTATE_CLASS
    {
      WTSActive,
      WTSConnected,
      WTSConnectQuery,
      WTSShadow,
      WTSDisconnected,
      WTSIdle,
      WTSListen,
      WTSReset,
      WTSDown,
      WTSInit
    }

    public enum WTS_INFO_CLASS
    {
      WTSUserName = 5,
      WTSDomainName = 7,
      WTSSessionInfoEx = 24
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WTSINFOEX
    {
      public int Level;
      public WTSINFOEX_LEVEL1 Data;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WTSINFOEX_LEVEL1
    {
      public int SessionId;
      public WTS_CONNECTSTATE_CLASS SessionState;
      public int SessionFlags;

      [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 33)]
      public string WinStationName;

      [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 21)]
      public string UserName;

      [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 18)]
      public string DomainName;

      public long LogonTime;
      public long ConnectTime;
      public long DisconnectTime;
      public long LastInputTime;
      public long CurrentTime;
      public long TimeZoneBias;
    }
  }
}
