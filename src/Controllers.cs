using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace SteamPassBridge
{
    // Finds physically connected PlayStation controllers (USB or Bluetooth) from their HID interfaces.
    // Steam Input hides which physical controller sits behind each virtual slot, so this is used for
    // presentation (button labels) and diagnostics only.
    static class Controllers
    {
        static readonly Dictionary<string, string> SonyModels = new Dictionary<string, string>
        {
            { "05c4", "DualShock 4" }, { "09cc", "DualShock 4" }, { "0ba0", "DualShock 4" },
            { "0ce6", "DualSense" }, { "0df2", "DualSense Edge" },
        };

        public static List<string> ConnectedPlayStation()
        {
            var found = new List<string>();
            foreach (var path in HidPaths())
            {
                var p = path.ToLowerInvariant();
                // USB: "vid_054c&pid_0ce6"; Bluetooth: "vid&0002054c_pid&0ce6"
                int i = p.IndexOf("vid_054c&pid_");
                int j = p.IndexOf("vid&0002054c_pid&");
                string pid = i >= 0 ? p.Substring(i + 13, 4) : j >= 0 ? p.Substring(j + 17, 4) : null;
                string model;
                if (pid != null && SonyModels.TryGetValue(pid, out model) && !found.Contains(model)) found.Add(model);
            }
            return found;
        }

        static IEnumerable<string> HidPaths()
        {
            var guid = new Guid("4D1E55B2-F16F-11CF-88CB-001111000030"); // GUID_DEVINTERFACE_HID
            IntPtr set = SetupDiGetClassDevs(ref guid, IntPtr.Zero, IntPtr.Zero, 0x12 /* PRESENT | DEVICEINTERFACE */);
            if (set == new IntPtr(-1)) yield break;
            try
            {
                var data = new SP_DEVICE_INTERFACE_DATA();
                data.cbSize = Marshal.SizeOf(typeof(SP_DEVICE_INTERFACE_DATA));
                for (uint i = 0; SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, i, ref data); i++)
                {
                    uint required;
                    SetupDiGetDeviceInterfaceDetail(set, ref data, IntPtr.Zero, 0, out required, IntPtr.Zero);
                    if (required == 0) continue;
                    IntPtr detail = Marshal.AllocHGlobal((int)required);
                    string path = null;
                    try
                    {
                        Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                        if (SetupDiGetDeviceInterfaceDetail(set, ref data, detail, required, out required, IntPtr.Zero))
                            path = Marshal.PtrToStringUni(new IntPtr(detail.ToInt64() + 4));
                    }
                    finally { Marshal.FreeHGlobal(detail); }
                    if (path != null) yield return path;
                }
            }
            finally { SetupDiDestroyDeviceInfoList(set); }
        }

        [StructLayout(LayoutKind.Sequential)]
        struct SP_DEVICE_INTERFACE_DATA { public int cbSize; public Guid InterfaceClassGuid; public int Flags; public IntPtr Reserved; }

        [DllImport("setupapi.dll", SetLastError = true)]
        static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, uint flags);
        [DllImport("setupapi.dll", SetLastError = true)]
        static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr devInfo, ref Guid guid, uint index, ref SP_DEVICE_INTERFACE_DATA data);
        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SP_DEVICE_INTERFACE_DATA data, IntPtr detail, uint size, out uint required, IntPtr devInfo);
        [DllImport("setupapi.dll")]
        static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    }
}
