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
using System.Threading;
using System.Diagnostics;

namespace KeyboardBacklightForLenovo
{
    /// <summary>
    /// Read-only access to Night Light state in Windows 10/11.
    /// Simplified to rely on Settings payload with CB-encoded boolean.
    /// </summary>
    internal class NightLight
    {
        // CloudStore: Settings keys (prefer Cache, then Current)
        private const string KeySettingsCurrent =
            "Software\\Microsoft\\Windows\\CurrentVersion\\CloudStore\\Store\\DefaultAccount\\Current\\default$windows.data.bluelightreduction.settings\\windows.data.bluelightreduction.settings";
        private const string KeySettingsCacheCurrent =
            "Software\\Microsoft\\Windows\\CurrentVersion\\CloudStore\\Store\\Cache\\DefaultAccount\\$$windows.data.bluelightreduction.settings\\Current";
        private const string KeySettingsCacheLeaf =
            "Software\\Microsoft\\Windows\\CurrentVersion\\CloudStore\\Store\\Cache\\DefaultAccount\\$$windows.data.bluelightreduction.settings\\windows.data.bluelightreduction.settings";

        // CloudStore: State key (reflects active Night light now)
        private const string KeyStateCurrent =
            "Software\\Microsoft\\Windows\\CurrentVersion\\CloudStore\\Store\\DefaultAccount\\Current\\default$windows.data.bluelightreduction.bluelightreductionstate\\windows.data.bluelightreduction.bluelightreductionstate";

        private readonly bool _supported;
        public bool Supported
        {
            get
            {
                using var a = Registry.CurrentUser.OpenSubKey(KeySettingsCacheCurrent, false);
                if (a is not null) return true;
                using var b = Registry.CurrentUser.OpenSubKey(KeySettingsCacheLeaf, false);
                if (b is not null) return true;
                using var c = Registry.CurrentUser.OpenSubKey(KeySettingsCurrent, false);
                if (c is not null) return true;
                return _supported;
            }
        }

        private bool? _inferredActiveFromState;
        private byte[]? _lastStateBlob;

        public bool Enabled
        {
            get
            {
                // Prefer State (parsed/inferred) so manual toggles reflect instantly
                if (TryReadEnabled(KeyStateCurrent, out bool activeNow))
                    return activeNow;
                if (_inferredActiveFromState.HasValue)
                    return _inferredActiveFromState.Value;

                // Fall back to Settings (Cache then Current); open fresh each time
                if (TryReadEnabled(KeySettingsCacheCurrent, out bool enabledSettingsCache))
                    return enabledSettingsCache;
                if (TryReadEnabled(KeySettingsCacheLeaf, out bool enabledSettingsCache2))
                    return enabledSettingsCache2;
                if (TryReadEnabled(KeySettingsCurrent, out bool enabledSettingsCurrent))
                    return enabledSettingsCurrent;
                return false;
            }
        }

        private bool TryReadEnabled(string subKey, out bool enabled)
        {
            enabled = false;
            try
            {
                using var rk = Registry.CurrentUser.OpenSubKey(subKey, false);
                if (rk is null) return false;
                var data = rk.GetValue("Data") as byte[];
                if (data is null || data.Length == 0) return false;

                // CloudStore CB boolean: 0E 15 00 (true) or 0E 14 00 (false)
                for (int i = 0; i + 2 < data.Length; i++)
                {
                    if (data[i] == 0x0E && data[i + 1] == 0x15 && data[i + 2] == 0x00)
                    {
                        enabled = true;
                        Debug.WriteLine($"[NightLight] Enabled via {subKey} [CB bool true at {i}] len={data.Length}");
                        return true;
                    }
                    if (data[i] == 0x0E && data[i + 1] == 0x14 && data[i + 2] == 0x00)
                    {
                        enabled = false;
                        Debug.WriteLine($"[NightLight] Enabled=false via {subKey} [CB bool false at {i}] len={data.Length}");
                        return true;
                    }
                }

                // Unknown layout for this key
                return false;
            }
            catch
            {
                return false;
            }
        }

        private Thread? _watchThreadSettingsCurrent;
        private Thread? _watchThreadSettingsCache;
        private Thread? _watchThreadStateCurrent;
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
            using var rks1 = Registry.CurrentUser.OpenSubKey(KeySettingsCacheCurrent, false);
            using var rks1b = Registry.CurrentUser.OpenSubKey(KeySettingsCacheLeaf, false);
            using var rks2 = Registry.CurrentUser.OpenSubKey(KeySettingsCurrent, false);
            using var rs = Registry.CurrentUser.OpenSubKey(KeyStateCurrent, false);
            _supported = rks1 is not null || rks1b is not null || rks2 is not null || rs is not null;

            // Seed baseline so the first toggle can be inferred if needed
            SeedStateBaseline();
        }

        private void SeedStateBaseline()
        {
            try
            {
                using var rk = Registry.CurrentUser.OpenSubKey(KeyStateCurrent, false);
                var blob = rk?.GetValue("Data") as byte[];
                if (blob is not null)
                {
                    _lastStateBlob = blob;
                    if (TryParseCbBool(blob, out bool val))
                    {
                        _inferredActiveFromState = val;
                    }
                    else if (TryReadSettingsEnabled(out bool settingsVal))
                    {
                        _inferredActiveFromState = settingsVal;
                    }
                }
                else if (TryReadSettingsEnabled(out bool settingsVal2))
                {
                    _inferredActiveFromState = settingsVal2;
                }
            }
            catch { /* ignore */ }
        }

        public void StartWatching()
        {
            if (!Supported) return;

            StopWatching(); // idempotent

            try
            {
                _watchCts = new CancellationTokenSource();
                var token = _watchCts.Token;

                Thread StartWatcher(string subKey, string name)
                {
                    var rk = Registry.CurrentUser.OpenSubKey(
                        subKey,
                        RegistryKeyPermissionCheck.ReadSubTree,
                        RegistryRights.ReadKey | RegistryRights.Notify);
                    if (rk is null) return null!;

                    var t = new Thread(() =>
                    {
                        using (rk)
                        {
                            var h = rk.Handle;
                            while (!token.IsCancellationRequested)
                            {
                                int hr = RegNotifyChangeKeyValue(
                                    h,
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

                                    // If this is the State key, attempt to parse/infer active flag
                                    if (string.Equals(subKey, KeyStateCurrent, System.StringComparison.OrdinalIgnoreCase))
                                    {
                                        try
                                        {
                                            using var rkRead = Registry.CurrentUser.OpenSubKey(subKey, false);
                                            var blob = rkRead?.GetValue("Data") as byte[];
                                            if (blob is not null)
                                            {
                                                bool parsed;
                                                if (TryParseCbBool(blob, out bool val))
                                                {
                                                    _inferredActiveFromState = val;
                                                    parsed = true;
                                                }
                                                else
                                                {
                                                    parsed = false;
                                                    if (_lastStateBlob is not null && !AreEqual(_lastStateBlob, blob))
                                                    {
                                                        // Toggle inference fallback if we saw a genuine change
                                                        _inferredActiveFromState = !(_inferredActiveFromState ?? false);
                                                    }
                                                }
                                                _lastStateBlob = blob;
                                                Debug.WriteLine($"[NightLight] State change: parsed={parsed}, inferredActive={_inferredActiveFromState}");
                                            }
                                        }
                                        catch { /* ignore */ }
                                    }

                                    Changed?.Invoke(this, EventArgs.Empty);
                                }
                                catch { /* ignore */ }
                            }
                        }
                    })
                    { IsBackground = true, Name = name };
                    t.Start();
                    return t;
                }

                _watchThreadSettingsCache = StartWatcher(KeySettingsCacheCurrent, "NightLightRegistryWatcher(Settings/CacheCurrent)");
                if (_watchThreadSettingsCache == null)
                    _watchThreadSettingsCache = StartWatcher(KeySettingsCacheLeaf, "NightLightRegistryWatcher(Settings/CacheLeaf)");
                _watchThreadSettingsCurrent = StartWatcher(KeySettingsCurrent, "NightLightRegistryWatcher(Settings/Current)");
                _watchThreadStateCurrent = StartWatcher(KeyStateCurrent, "NightLightRegistryWatcher(State/Current)");

                if (_watchThreadSettingsCache == null && _watchThreadSettingsCurrent == null && _watchThreadStateCurrent == null)
                {
                    StopWatching();
                }
            }
            catch
            {
                StopWatching();
            }
        }

        public void StopWatching()
        {
            try { _watchCts?.Cancel(); } catch { }
            try
            {
                if (_watchThreadSettingsCache != null && _watchThreadSettingsCache.IsAlive) _watchThreadSettingsCache.Join(200);
                if (_watchThreadSettingsCurrent != null && _watchThreadSettingsCurrent.IsAlive) _watchThreadSettingsCurrent.Join(200);
                if (_watchThreadStateCurrent != null && _watchThreadStateCurrent.IsAlive) _watchThreadStateCurrent.Join(200);
            }
            catch { }
            finally
            {
                _watchCts?.Dispose();
                _watchCts = null;
                _watchThreadSettingsCache = null;
                _watchThreadSettingsCurrent = null;
                _watchThreadStateCurrent = null;
            }
        }

        private static bool TryParseCbBool(byte[] data, out bool value)
        {
            value = false;
            for (int i = 0; i + 2 < data.Length; i++)
            {
                if (data[i] == 0x0E && data[i + 1] == 0x15 && data[i + 2] == 0x00) { value = true; return true; }
                if (data[i] == 0x0E && data[i + 1] == 0x14 && data[i + 2] == 0x00) { value = false; return true; }
            }
            return false;
        }

        private bool TryReadSettingsEnabled(out bool value)
        {
            value = false;
            // Try cache/current, then cache/leaf, then current
            if (TryReadEnabled(KeySettingsCacheCurrent, out var v1)) { value = v1; return true; }
            if (TryReadEnabled(KeySettingsCacheLeaf, out var v2)) { value = v2; return true; }
            if (TryReadEnabled(KeySettingsCurrent, out var v3)) { value = v3; return true; }
            return false;
        }

        private static bool AreEqual(byte[] a, byte[] b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a is null || b is null) return false;
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }
    }
}
