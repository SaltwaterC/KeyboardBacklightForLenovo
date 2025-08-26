using Microsoft.Win32;

namespace KeyboardBacklightForLenovo
{
    public static class PreferredLevelStore
    {
        private const string KeyPath = @"HKEY_CURRENT_USER\Software\KeyboardBacklightForLenovo";
        private const string ValueName = "PreferredLevel";

        /// <summary>
        /// Returns 0,1,2 with default of 2 (High) if missing/invalid.
        /// </summary>
        public static int ReadPreferredLevel()
        {
            object? v = Registry.GetValue(KeyPath, ValueName, 2);
            if (v is int i && i >= 0 && i <= 2) return i;
            return 2;
        }

        /// <summary>
        /// Saves 0,1,2 (clamped) to HKCU.
        /// </summary>
        public static void SavePreferredLevel(int level)
        {
            if (level < 0) level = 0;
            if (level > 2) level = 2;
            Registry.SetValue(KeyPath, ValueName, level, RegistryValueKind.DWord);
        }
    }
}
