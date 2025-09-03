using System.Runtime.InteropServices;

namespace KeyboardBacklightForLenovo
{
    public sealed class SessionWatcher
    {
        /// <summary>
        /// True if there is any HUMAN user logged on (locked or unlocked).
        ///
        /// We rely on WTSSessionInfoEx as WTSUserName may return the last logged-on
        /// user even when nobody is currently signed in. A human user session is
        /// considered present when:
        ///   * SessionId != 0 (services),
        ///   * SessionState == WTSActive,
        ///   * SessionFlags indicate Locked or Unlocked, and
        ///   * The embedded UserName from WTSSessionInfoEx is non-empty and not a
        ///     well-known service account.
        /// </summary>
        public bool IsAnyUserLoggedOn()
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

                    // Only consider sessions that are active on the console.
                    if (info.State != WtsApi32.WTS_CONNECTSTATE_CLASS.WTSActive)
                        continue;

                    if (!WtsApi32.WTSQuerySessionInformation(IntPtr.Zero, info.SessionId,
                            WtsApi32.WTS_INFO_CLASS.WTSSessionInfoEx, out var pInfo, out _))
                        continue;

                    try
                    {
                        var infoEx = Marshal.PtrToStructure<WtsApi32.WTSINFOEX>(pInfo);
                        if (infoEx.Level != 1)
                            continue;
                        var level1 = infoEx.Data;

                        // Only Locked or Unlocked sessions with a valid logon time count as human.
                        if (level1.SessionFlags != WtsApi32.WTS_SESSIONSTATE_LOCK && level1.SessionFlags != WtsApi32.WTS_SESSIONSTATE_UNLOCK)
                            continue;
                        if (level1.LogonTime == 0)
                            continue;

                        string? user = level1.UserName;
                        if (!string.IsNullOrWhiteSpace(user) && !IsServiceOrSystemUser(user))
                            return true;
                    }
                    finally
                    {
                        WtsApi32.WTSFreeMemory(pInfo);
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
        public const int WTS_SESSIONSTATE_LOCK = 0x1;
        public const int WTS_SESSIONSTATE_UNLOCK = 0x2;
        [DllImport("Wtsapi32.dll")]
        public static extern bool WTSEnumerateSessions(IntPtr hServer, int Reserved, int Version, out IntPtr ppSessionInfo, out int pCount);

        [DllImport("Wtsapi32.dll")]
        public static extern void WTSFreeMemory(IntPtr pMemory);

        [DllImport("Wtsapi32.dll")]
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

        [StructLayout(LayoutKind.Sequential)]
        public struct WTSINFOEX
        {
            public int Level;
            public int Reserved;
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
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string UserName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string DomainName;
            public long LogonTime;
            public long ConnectTime;
            public long DisconnectTime;
            public long LastInputTime;
            public long CurrentTime;
            public int IncomingBytes;
            public int OutgoingBytes;
            public int IncomingFrames;
            public int OutgoingFrames;
            public int IncomingCompressedBytes;
            public int OutgoingCompressedBytes;
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
    }
}
