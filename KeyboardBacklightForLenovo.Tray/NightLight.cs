/*
MIT License

Copyright (c) 2022 MIkhail Kozlov

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
using System.Security.Principal;
using System.Threading;

namespace KeyboardBacklightForLenovo
{
    /// <summary>
    /// Read-only access to Night Light state in Windows 10/11 based on TinyScreen's implementation
    /// </summary>
    internal class NightLight
    {
        private string _key =
        "Software\\Microsoft\\Windows\\CurrentVersion\\CloudStore\\Store\\DefaultAccount\\Current\\default$windows.data.bluelightreduction.bluelightreductionstate\\windows.data.bluelightreduction.bluelightreductionstate";

        private readonly RegistryKey? _registryKey;

        public bool Supported => _registryKey is not null;

        public bool Enabled
        {
            get
            {
                if (_registryKey is null) return false;
                var data = _registryKey.GetValue("Data") as byte[];
                return data is not null && data.Length > 18 && data[18] == 0x15;
            }
        }

        private Thread? _watchThread;
        private CancellationTokenSource? _watchCts;
        public event EventHandler? Changed;   // raised on any NL state blob change

        // P/Invoke
        private const int KEY_NOTIFY = 0x0010;
        private const int REG_NOTIFY_CHANGE_NAME = 0x1;
        private const int REG_NOTIFY_CHANGE_LAST_SET = 0x4;

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int RegNotifyChangeKeyValue(
            SafeRegistryHandle hKey,
            bool bWatchSubtree,
            int dwNotifyFilter,
            IntPtr hEvent,
            bool fAsynchronous);


        public NightLight()
        {
            _registryKey = Registry.CurrentUser.OpenSubKey(_key, false);
        }

        public void StartWatching()
        {
            // Only relevant if the feature works and the key is present.
            if (!Supported) return;

            StopWatching(); // idempotent

            try
            {
                // Re-open with KEY_NOTIFY so RegNotifyChangeKeyValue can be used.
                // .NET 8+: OpenSubKey with rights.
                var rk = Registry.CurrentUser.OpenSubKey(
                    _key,
                    RegistryKeyPermissionCheck.ReadSubTree,
                    RegistryRights.ReadKey | RegistryRights.Notify);

                if (rk == null)
                    return;

                _watchCts = new CancellationTokenSource();
                var token = _watchCts.Token;

                _watchThread = new Thread(() =>
                {
                    using (rk)
                    {
                        var h = rk.Handle;

                        while (!token.IsCancellationRequested)
                        {
                            // Block until something changes under this key (value or name)
                            int hr = RegNotifyChangeKeyValue(
                                h,
                                bWatchSubtree: false,
                                dwNotifyFilter: REG_NOTIFY_CHANGE_LAST_SET | REG_NOTIFY_CHANGE_NAME,
                                hEvent: IntPtr.Zero,
                                fAsynchronous: false);

                            // If cancelled or error, break politely
                            if (token.IsCancellationRequested || hr != 0)
                                break;

                            try
                            {
                                // Debounce tiny bursts (multiple writes per UI toggle)
                                Thread.Sleep(50);
                                Changed?.Invoke(this, EventArgs.Empty);
                            }
                            catch { /* swallow handler exceptions */ }
                        }
                    }
                })
                {
                    IsBackground = true,
                    Name = "NightLightRegistryWatcher"
                };

                _watchThread.Start();
            }
            catch
            {
                // If anything fails, we just fall back to your periodic checks.
                StopWatching();
            }
        }

        public void StopWatching()
        {
            try { _watchCts?.Cancel(); } catch { }
            try
            {
                if (_watchThread != null && _watchThread.IsAlive)
                    _watchThread.Join(200);
            }
            catch { }
            finally
            {
                _watchCts?.Dispose();
                _watchCts = null;
                _watchThread = null;
            }
        }

        ~NightLight()
        {
            _registryKey?.Close();
        }

    }
}
