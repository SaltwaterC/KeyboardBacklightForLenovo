using System;
using System.Reflection;

namespace ThinkPadKeyboardBacklight
{
    public class KeyboardBacklightController
    {
        private readonly object _instance;
        private readonly MethodInfo _getStatus;
        private readonly MethodInfo _setStatus;

        public KeyboardBacklightController()
        {
            string basePath = @"C:\ProgramData\Lenovo\Vantage\Addins\ThinkKeyboardAddin";

            // Find newest version folder
            var versionDir = new DirectoryInfo(basePath)
                .GetDirectories()
                .OrderByDescending(d => d.Name)
                .FirstOrDefault()
                ?? throw new Exception($"No version directories found in {basePath}");

            string dllPath = Path.Combine(versionDir.FullName, "Keyboard_Core.dll");
            if (!File.Exists(dllPath))
                throw new Exception($"Keyboard_Core.dll not found at {dllPath}");

            var asm = Assembly.LoadFrom(dllPath);
            var controlType = asm.GetType("Keyboard_Core.KeyboardControl")
                ?? throw new Exception("Type 'Keyboard_Core.KeyboardControl' not found.");

            _instance = Activator.CreateInstance(controlType)
                ?? throw new Exception("Failed to create KeyboardControl instance.");

            _getStatus = controlType.GetMethod("GetKeyboardBackLightStatus")
                ?? throw new Exception("Method GetKeyboardBackLightStatus not found.");
            _setStatus = controlType.GetMethod("SetKeyboardBackLightStatus")
                ?? throw new Exception("Method SetKeyboardBackLightStatus not found.");
        }

        public int GetStatus()
        {
#pragma warning disable CS8625 // Cannot convert null literal to non-nullable reference type.
            object[] parameters = [0, null];
#pragma warning restore CS8625 // Cannot convert null literal to non-nullable reference type.
            _getStatus.Invoke(_instance, parameters);
            return Convert.ToInt32(parameters[0]);
        }

        public void SetStatus(int level)
        {
            if (level < 0 || level > 2)
            {
                throw new ArgumentOutOfRangeException(nameof(level), "Backlight level must be between 0 and 2.");
            }

#pragma warning disable CS8625 // Cannot convert null literal to non-nullable reference type.
            object[] parameters = [level, null];
#pragma warning restore CS8625 // Cannot convert null literal to non-nullable reference type.
            _setStatus.Invoke(_instance, parameters);
        }

        public void ResetStatus(int expectedStatus)
        {
            int currentStatus = GetStatus();
            if (currentStatus != expectedStatus)
            {
                SetStatus(expectedStatus);
            }
        }
    }
}
