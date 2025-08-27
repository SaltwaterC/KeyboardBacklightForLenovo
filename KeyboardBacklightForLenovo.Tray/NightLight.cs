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

        public NightLight()
        {
            _registryKey = Registry.CurrentUser.OpenSubKey(_key, false);
        }

        ~NightLight()
        {
            _registryKey?.Close();
        }

    }
}
