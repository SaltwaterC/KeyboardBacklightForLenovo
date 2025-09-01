/*
MIT License

Copyright (c) 2022 MIkhail Kozlov
Copyright (c) 2025 Ștefan Rusu

Permission is hereby granted, free of charge, to any person obtaining a copy of
this software and associated documentation files (the "Software"), to deal in 
the Software without restriction, including without limitation the rights to 
use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies 
of the Software, and to permit persons to whom the Software is furnished to do 
so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all 
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR 
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, 
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE 
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER 
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, 
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE 
SOFTWARE.
 */

using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Threading;

namespace KeyboardBacklightForLenovo
{
  /// <summary>
  /// Read-only access to Night Light state in Windows 10/11 based on TinyScreen's implementation
  /// </summary>
  public class NightLight
  {
    private string _key =
    "Software\\Microsoft\\Windows\\CurrentVersion\\CloudStore\\Store\\DefaultAccount\\Current\\default$windows.data.bluelightreduction.bluelightreductionstate\\windows.data.bluelightreduction.bluelightreductionstate";

    private readonly RegistryKey? _registryKey;

    // Registry watcher state
    private Thread? _watchThread;
    private CancellationTokenSource? _watchCts;

    public event EventHandler? Changed;

    public bool Supported => _registryKey is not null;

    public bool Enabled
    {
      get
      {
        if (_registryKey is null) return false;
        var data = _registryKey.GetValue("Data") as byte[];
        if (data is null || data.Length < 19) return false;

        // Heuristics for different Windows builds:
        // - Older builds: byte[18] == 0x15 when ON, 0x13 when OFF
        // - Newer builds (observed): presence of 0x10 0x00 at indices 23..24 indicates ON
        byte b = data[18];
        if (b == 0x15) return true;          // legacy ON
        if (b == 0x13) return false;         // legacy OFF

        // Newer layout: check for 0x10 0x00 field around 23..24
        if (data.Length > 24 && data[23] == 0x10 && data[24] == 0x00)
          return true;

        // Fallback: treat as OFF
        return false;
      }
    }

    public NightLight()
    {
      _registryKey = Registry.CurrentUser.OpenSubKey(_key, false);
    }

    ~NightLight()
    {
      try { StopWatching(); } catch { /* ignore */ }
      _registryKey?.Close();
    }

    // ---- Registry watcher ----
    private const int REG_NOTIFY_CHANGE_NAME = 0x00000001;
    private const int REG_NOTIFY_CHANGE_LAST_SET = 0x00000004;

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int RegNotifyChangeKeyValue(
      SafeRegistryHandle hKey,
      bool bWatchSubtree,
      int dwNotifyFilter,
      IntPtr hEvent,
      bool fAsynchronous);

    public void StartWatching()
    {
      if (_registryKey is null) return; // unsupported

      StopWatching(); // idempotent

      try
      {
        _watchCts = new CancellationTokenSource();
        var token = _watchCts.Token;

        // Open a fresh handle with Notify rights
        var rk = Registry.CurrentUser.OpenSubKey(
          _key,
          RegistryKeyPermissionCheck.ReadSubTree,
          RegistryRights.ReadKey | RegistryRights.Notify);
        if (rk is null)
        {
          // Key disappeared; nothing to watch
          return;
        }

        _watchThread = new Thread(() =>
        {
          using (rk)
          {
            var handle = rk.Handle;
            while (!token.IsCancellationRequested)
            {
              int hr = RegNotifyChangeKeyValue(
                handle,
                bWatchSubtree: false,
                dwNotifyFilter: REG_NOTIFY_CHANGE_LAST_SET | REG_NOTIFY_CHANGE_NAME,
                hEvent: IntPtr.Zero,
                fAsynchronous: false);

              if (token.IsCancellationRequested || hr != 0)
                break;

              try
              {
                // Debounce multiple writes per toggle
                Thread.Sleep(50);
                Changed?.Invoke(this, EventArgs.Empty);
              }
              catch { /* ignore */ }
            }
          }
        })
        { IsBackground = true, Name = "NightLightRegistryWatcher" };

        _watchThread.Start();
      }
      catch
      {
        StopWatching();
      }
    }

    public void StopWatching()
    {
      try { _watchCts?.Cancel(); } catch { }
      try { if (_watchThread != null && _watchThread.IsAlive) _watchThread.Join(200); }
      catch { }
      finally
      {
        _watchCts?.Dispose();
        _watchCts = null;
        _watchThread = null;
      }
    }

  }
}
