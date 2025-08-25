using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace KeyboardBacklightForLenovo
{
    public sealed class KeyboardBacklightController : IDisposable
    {
        public const int LevelOff = 0;
        public const int LevelLow = 1;
        public const int LevelHigh = 2;

        private readonly DriverConfig _driver;
        private readonly SafeFileHandle _handle;

        public string Principal => _driver.Principal;
        public string Description => _driver.Description ?? string.Empty;

        public KeyboardBacklightController(string? configPath = null)
        {
            var configs = LoadDriverConfigs(configPath);

            Exception? lastError = null;
            foreach (var cfg in configs)
            {
                try
                {
                    var h = CreateFile(
                        cfg.Principal,
                        FileAccess.ReadWrite,
                        FileShare.ReadWrite,
                        IntPtr.Zero,
                        FileMode.Open,
                        0,
                        IntPtr.Zero);

                    if (!h.IsInvalid)
                    {
                        _driver = cfg;
                        _handle = h;
                        return;
                    }

                    int err = Marshal.GetLastWin32Error();
                    lastError = new Win32Exception(err, $"Open {cfg.Principal} failed");
                    h.Dispose();
                }
                catch (Exception ex)
                {
                    lastError = ex;
                }
            }

            throw new InvalidOperationException(
                $"No supported keyboard backlight driver found. Tried: {string.Join(", ", configs.Select(c => c.Principal))}",
                lastError
            );
        }

        public int GetStatus()
        {
            // For EnergyDrv, GET requires a 4-byte function code in the input buffer.
            byte[]? inBuf = null;
            int inLen = 0;
            if (_driver.GetIn.HasValue)
            {
                inBuf = BitConverter.GetBytes(_driver.GetIn.Value);
                inLen = inBuf.Length; // 4 bytes
            }

            // NOTE: Allocate a larger out buffer (16 bytes) like the working probe.
            byte[] outBuf = new byte[16];

            if (!DeviceIoControl(_handle, _driver.GetIoctl, inBuf, inLen, outBuf, outBuf.Length, out int br, IntPtr.Zero))
            {
                var err = Marshal.GetLastWin32Error();
                throw new Win32Exception(err, $"DeviceIoControl(GET 0x{_driver.GetIoctl:X8}) failed");
            }

            if (br < 4)
                throw new InvalidOperationException("GET did not return a 4-byte payload; cannot map status.");

            uint raw = BitConverter.ToUInt32(outBuf, 0);

            if (raw == _driver.GetOff) return LevelOff;
            if (raw == _driver.GetLow) return LevelLow;
            if (raw == _driver.GetHigh) return LevelHigh;

            throw new InvalidOperationException($"Unknown GET status value: 0x{raw:X8}");
        }

        public void SetStatus(int level)
        {
            if ((uint)level > 2)
                throw new ArgumentOutOfRangeException(nameof(level), "Backlight level must be 0 (Off), 1 (Low), or 2 (High).");

            uint payload = level switch
            {
                LevelOff => _driver.SetOff,
                LevelLow => _driver.SetLow,
                LevelHigh => _driver.SetHigh,
                _ => throw new ArgumentOutOfRangeException(nameof(level))
            };

            byte[] inBuf = BitConverter.GetBytes(payload); // always 4 bytes

            // NOTE: EnergyDrv fails when lpOutBuffer is null. Provide a small out buffer and ignore the content.
            byte[] outBuf = new byte[16];

            if (!DeviceIoControl(_handle, _driver.SetIoctl, inBuf, inBuf.Length, outBuf, outBuf.Length, out _, IntPtr.Zero))
            {
                var err = Marshal.GetLastWin32Error();
                throw new Win32Exception(err, $"DeviceIoControl(SET 0x{_driver.SetIoctl:X8}) failed");
            }

            System.Threading.Thread.Sleep(40);
            int now = GetStatus();
            if (now != level)
                throw new InvalidOperationException($"Set did not take effect. Requested {level} but device reports {now}.");
        }

        public void ResetStatus(int expectedStatus)
        {
            int current = GetStatus();
            if (current != expectedStatus)
                SetStatus(expectedStatus);
        }

        public void Dispose() => _handle?.Dispose();

        #region Config + P/Invoke

        private static IReadOnlyList<DriverConfig> LoadDriverConfigs(string? configPath)
        {
            string path;
            if (!string.IsNullOrWhiteSpace(configPath))
            {
                path = configPath;
            }
            else
            {
                string exeDir = AppContext.BaseDirectory;
                path = Path.Combine(exeDir, "DriversConfig.json");
            }

            if (!File.Exists(path))
                throw new FileNotFoundException("Driver configuration JSON not found.", path);

            string json = File.ReadAllText(path);
            json = RemoveDanglingCommas(json);

            var opts = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            };

            var raw = JsonSerializer.Deserialize<List<DriverConfigJson>>(json, opts)
                      ?? throw new InvalidOperationException("Empty/invalid driver config JSON.");

            return raw.Select(Parse).ToList();

            static DriverConfig Parse(DriverConfigJson j)
            {
                if (string.IsNullOrWhiteSpace(j.Principal))
                    throw new InvalidOperationException("Driver entry missing 'principal'.");

                return new DriverConfig
                {
                    Principal = j.Principal!,
                    Description = j.Description ?? "",
                    GetIoctl = ParseU32(j.GetIoctl, nameof(j.GetIoctl)),
                    GetIn = ParseU32Nullable(j.GetIn),
                    SetIoctl = ParseU32(j.SetIoctl, nameof(j.SetIoctl)),
                    GetOff = ParseU32(j.GetOff, nameof(j.GetOff)),
                    GetLow = ParseU32(j.GetLow, nameof(j.GetLow)),
                    GetHigh = ParseU32(j.GetHigh, nameof(j.GetHigh)),
                    SetOff = ParseU32(j.SetOff, nameof(j.SetOff)),
                    SetLow = ParseU32(j.SetLow, nameof(j.SetLow)),
                    SetHigh = ParseU32(j.SetHigh, nameof(j.SetHigh)),
                };
            }
        }

        private static string RemoveDanglingCommas(string s) =>
            Regex.Replace(s, @",(\s*[}\]])", "$1", RegexOptions.Multiline);

        private static uint ParseU32(string? s, string field)
        {
            if (string.IsNullOrWhiteSpace(s))
                throw new InvalidOperationException($"Missing hex/decimal value for '{field}'.");
            s = s.Trim();
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) return Convert.ToUInt32(s, 16);
            return Convert.ToUInt32(s, 10);
        }

        private static uint? ParseU32Nullable(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            s = s.Trim();
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) return Convert.ToUInt32(s, 16);
            return Convert.ToUInt32(s, 10);
        }

        private class DriverConfigJson
        {
            public string? Principal { get; set; }
            public string? Description { get; set; }
            public string? GetIoctl { get; set; }
            public string? GetIn { get; set; }       // optional GET input payload (EnergyDrv)
            public string? GetOff { get; set; }
            public string? GetLow { get; set; }
            public string? GetHigh { get; set; }
            public string? SetIoctl { get; set; }
            public string? SetOff { get; set; }
            public string? SetLow { get; set; }
            public string? SetHigh { get; set; }
        }

        private sealed class DriverConfig
        {
            public string Principal { get; set; } = "";
            public string Description { get; set; } = "";
            public uint GetIoctl { get; set; }
            public uint? GetIn { get; set; }
            public uint SetIoctl { get; set; }
            public uint GetOff { get; set; }
            public uint GetLow { get; set; }
            public uint GetHigh { get; set; }
            public uint SetOff { get; set; }
            public uint SetLow { get; set; }
            public uint SetHigh { get; set; }
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern SafeFileHandle CreateFile(
            string lpFileName, FileAccess dwDesiredAccess, FileShare dwShareMode,
            IntPtr lpSecurityAttributes, FileMode dwCreationDisposition,
            FileAttributes dwFlagsAndAttributes, IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(
            SafeFileHandle hDevice, uint dwIoControlCode,
            byte[]? lpInBuffer, int nInBufferSize,
            byte[]? lpOutBuffer, int nOutBufferSize,
            out int lpBytesReturned, IntPtr lpOverlapped);

        #endregion
    }
}
