using System;
using System.Runtime.InteropServices;

namespace KeyboardBacklightForLenovo
{
    internal static class WorkstationLock
    {
        // Use WTS SessionInfoEx to determine locked/unlocked for the active console session.

        public static bool IsLocked()
        {
            try
            {
                uint activeSessionId = WtsNative.WTSGetActiveConsoleSessionId();
                if (activeSessionId == 0xFFFFFFFF) // no active console session
                    return true;

                if (!WtsNative.WTSQuerySessionInformation(IntPtr.Zero, (int)activeSessionId,
                        WtsNative.WTS_INFO_CLASS.WTSSessionInfoEx, out var pBuf, out var bytes))
                {
                    return true;
                }

                try
                {
                    // The buffer is a WTSINFOEX struct: [DWORD Level][DWORD Reserved][union Data]
                    int level = Marshal.ReadInt32(pBuf);
                    if (level != 1)
                        return true; // unknown -> treat as locked

                    // Data starts after 8 bytes; we only need the first fields of LEVEL1.
                    IntPtr pLevel1 = pBuf + 8;
                    var info1 = Marshal.PtrToStructure<WTSINFOEX_LEVEL1_HEAD>(pLevel1);

                    // SessionFlags: 0=Unknown, 1=Locked, 2=Unlocked (per MSDN)
                    return info1.SessionFlags == 1;
                }
                finally
                {
                    WtsNative.WTSFreeMemory(pBuf);
                }
            }
            catch
            {
                // Conservative default
                return true;
            }
        }

        // Minimal interop needed for WTSSessionInfoEx
        private static class WtsNative
        {
            [DllImport("Wtsapi32.dll")]
            public static extern bool WTSQuerySessionInformation(IntPtr hServer, int SessionId, WTS_INFO_CLASS WTSInfoClass, out IntPtr ppBuffer, out int pBytesReturned);

            [DllImport("Wtsapi32.dll")]
            public static extern void WTSFreeMemory(IntPtr pMemory);

            [DllImport("Kernel32.dll")]
            public static extern uint WTSGetActiveConsoleSessionId();

            public enum WTS_INFO_CLASS
            {
                WTSSessionInfoEx = 24
            }
        }

        // We only need the header of LEVEL1: first three fields.
        [StructLayout(LayoutKind.Sequential)]
        private struct WTSINFOEX_LEVEL1_HEAD
        {
            public int SessionId;
            public WtsApi32.WTS_CONNECTSTATE_CLASS SessionState;
            public int SessionFlags;
        }
    }
}
