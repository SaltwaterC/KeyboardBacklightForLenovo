using System.Runtime.InteropServices;

namespace KeyboardBacklightForLenovo
{
    public sealed class SessionWatcher
    {
        /// <summary>
        /// True only if there is at least one interactive **unlocked** user session.
        /// Locked console / logon screen returns false.
        /// </summary>
        public bool IsAnyInteractiveUserActive()
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

                    // We only care about sessions that are attached to the console and "Active".
                    if (info.State != WtsApi32.WTS_CONNECTSTATE_CLASS.WTSActive)
                        continue;

                    // Ask for extended session info so we can tell Locked vs Unlocked.
                    if (!WtsApi32.WTSQuerySessionInformation(IntPtr.Zero, info.SessionId,
                            WtsApi32.WTS_INFO_CLASS.WTSSessionInfoEx, out var pInfoEx, out var _))
                        continue;

                    try
                    {
                        // The buffer starts with a WTSINFOEXW header with Level==1, followed by LEVEL1 data.
                        var level = Marshal.ReadInt32(pInfoEx); // WTSINFOEXW.Level
                        if (level != 1)
                            continue;

                        // Skip header (2 ints) to LEVEL1 struct.
                        // WTSINFOEXW {
                        //   DWORD Level; DWORD Reserved;
                        //   union { WTSINFOEX_LEVEL1_W Level1; ... };
                        // }
                        var pLevel1 = pInfoEx + (sizeof(int) * 2);

                        // Offsets inside LEVEL1 we care about:
                        //   DWORD SessionId; WTS_CONNECTSTATE_CLASS SessionState; DWORD SessionFlags;
                        // Layout: [SessionId:int][SessionState:int][SessionFlags:int]
                        int sessionId = Marshal.ReadInt32(pLevel1 + 0);
                        int sessionState = Marshal.ReadInt32(pLevel1 + 4);
                        int sessionFlags = Marshal.ReadInt32(pLevel1 + 8);

                        // Per WTSSessionInfoEx: SessionFlags==0 => Unlocked, 1 => Locked.
                        bool isUnlocked = (sessionFlags == 0);

                        if (isUnlocked)
                        {
                            // Optional: also ensure there’s a real username (not services).
                            var user = WtsApi32.WTSQuerySessionString(IntPtr.Zero, sessionId, WtsApi32.WTS_INFO_CLASS.WTSUserName);
                            if (!string.IsNullOrWhiteSpace(user) &&
                                !IsServiceOrSystemUser(user))
                                return true;
                        }
                    }
                    finally
                    {
                        WtsApi32.WTSFreeMemory(pInfoEx);
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
                || user.StartsWith("DWM-", StringComparison.OrdinalIgnoreCase)
                || user.StartsWith("UMFD-", StringComparison.OrdinalIgnoreCase);
        }
    }

    internal static class WtsApi32
    {
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
            WTSSessionInfoEx = 24
        }
    }
}
