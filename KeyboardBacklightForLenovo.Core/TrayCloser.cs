using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace KeyboardBacklightForLenovo
{
  public static class TrayCloser
  {
    private const uint WM_CLOSE = 0x0010;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    public static bool CloseByWMClose(string processName = "BacklightTrayApp", int timeoutSeconds = 5)
    {
      var procs = Process.GetProcessesByName(processName);
      if (procs == null || procs.Length == 0)
        return false;

      bool anySent = false;
      bool allExited = true;
      foreach (var p in procs)
      {
        try
        {
          var h = p.MainWindowHandle;
          if (h == IntPtr.Zero)
          {
            allExited = false;
            continue;
          }
          anySent = true;
          PostMessage(h, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
          if (!p.WaitForExit(Math.Max(0, timeoutSeconds) * 1000))
            allExited = false;
        }
        catch
        {
          allExited = false;
        }
      }

      return anySent && allExited;
    }
  }
}

