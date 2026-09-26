using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SteamPassBridge
{
    struct HidDevice
    {
        public string Path;
        public ushort Vendor, Product;
        public int InputLength, OutputLength, FeatureLength;
        public bool Bluetooth;
    }

    // Thin wrapper over the Windows HID API: enumerate game-controller interfaces and open one for
    // reading input reports and writing output (rumble) reports. No third-party dependency.
    static class Hid
    {
        public static List<HidDevice> Enumerate(ushort vendor)
        {
            var list = new List<HidDevice>();
            Guid g; HidD_GetHidGuid(out g);
            IntPtr set = SetupDiGetClassDevs(ref g, IntPtr.Zero, IntPtr.Zero, 0x12 /* PRESENT | DEVICEINTERFACE */);
            if (set == new IntPtr(-1)) return list;
            try
            {
                var data = new SP_DEVICE_INTERFACE_DATA();
                data.cbSize = Marshal.SizeOf(typeof(SP_DEVICE_INTERFACE_DATA));
                for (uint i = 0; SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref g, i, ref data); i++)
                {
                    uint req;
                    SetupDiGetDeviceInterfaceDetail(set, ref data, IntPtr.Zero, 0, out req, IntPtr.Zero);
                    if (req == 0) continue;
                    IntPtr detail = Marshal.AllocHGlobal((int)req);
                    string path = null;
                    try
                    {
                        Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                        if (SetupDiGetDeviceInterfaceDetail(set, ref data, detail, req, out req, IntPtr.Zero))
                            path = Marshal.PtrToStringUni(new IntPtr(detail.ToInt64() + 4));
                    }
                    finally { Marshal.FreeHGlobal(detail); }
                    if (path == null) continue;

                    var h = CreateFile(path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero); // 0 access: identify without grabbing
                    if (h.IsInvalid) continue;
                    try
                    {
                        var attr = new HIDD_ATTRIBUTES { Size = Marshal.SizeOf(typeof(HIDD_ATTRIBUTES)) };
                        if (!HidD_GetAttributes(h, ref attr) || attr.Vendor != vendor) continue;
                        IntPtr pp;
                        if (!HidD_GetPreparsedData(h, out pp)) continue;
                        HIDP_CAPS caps;
                        HidP_GetCaps(pp, out caps);
                        HidD_FreePreparsedData(pp);
                        if (caps.UsagePage != 0x01 || caps.Usage != 0x05) continue; // generic-desktop / gamepad only
                        var p = path.ToLowerInvariant();
                        list.Add(new HidDevice
                        {
                            Path = path, Vendor = attr.Vendor, Product = attr.Product,
                            InputLength = caps.InputReportByteLength, OutputLength = caps.OutputReportByteLength,
                            FeatureLength = caps.FeatureReportByteLength,
                            Bluetooth = p.Contains("{00001124") || p.Contains("bthenum") || p.Contains("bthle") || p.Contains("_bthhid"),
                        });
                    }
                    finally { h.Dispose(); }
                }
            }
            finally { SetupDiDestroyDeviceInfoList(set); }
            return list;
        }

        public static FileStream Open(string path, out SafeFileHandle handle)
        {
            handle = CreateFile(path, 0xC0000000 /* GENERIC_READ|WRITE */, 3 /* share R|W */, IntPtr.Zero, 3 /* OPEN_EXISTING */, 0x40000000 /* OVERLAPPED */, IntPtr.Zero);
            if (handle.IsInvalid)
            {
                int err = Marshal.GetLastWin32Error();
                // Fall back to read-only (some Bluetooth stacks deny write sharing); rumble then won't work.
                handle = CreateFile(path, 0x80000000, 3, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero);
                if (handle.IsInvalid) throw new IOException("Could not open controller (error " + err + ")");
            }
            return new FileStream(handle, FileAccess.ReadWrite, 64, true);
        }

        public static bool GetFeature(SafeFileHandle h, byte[] buffer)
        {
            return HidD_GetFeature(h, buffer, buffer.Length);
        }

        [StructLayout(LayoutKind.Sequential)] struct HIDD_ATTRIBUTES { public int Size; public ushort Vendor, Product, Version; }
        [StructLayout(LayoutKind.Sequential)] struct SP_DEVICE_INTERFACE_DATA { public int cbSize; public Guid g; public int Flags; public IntPtr Reserved; }
        [StructLayout(LayoutKind.Sequential)]
        struct HIDP_CAPS
        {
            public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
            public ushort LinkCollectionNodes, a, b, c, d, e, f, g, h, i;
        }

        [DllImport("hid.dll")] static extern void HidD_GetHidGuid(out Guid g);
        [DllImport("hid.dll")] static extern bool HidD_GetAttributes(SafeFileHandle h, ref HIDD_ATTRIBUTES a);
        [DllImport("hid.dll")] static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr pp);
        [DllImport("hid.dll")] static extern bool HidD_FreePreparsedData(IntPtr pp);
        [DllImport("hid.dll")] static extern int HidP_GetCaps(IntPtr pp, out HIDP_CAPS caps);
        [DllImport("hid.dll")] static extern bool HidD_GetFeature(SafeFileHandle h, [Out] byte[] buffer, int length);
        [DllImport("setupapi.dll")] static extern IntPtr SetupDiGetClassDevs(ref Guid g, IntPtr e, IntPtr p, int flags);
        [DllImport("setupapi.dll")] static extern bool SetupDiEnumDeviceInterfaces(IntPtr s, IntPtr d, ref Guid g, uint i, ref SP_DEVICE_INTERFACE_DATA data);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode)] static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr s, ref SP_DEVICE_INTERFACE_DATA d, IntPtr detail, uint size, out uint req, IntPtr dev);
        [DllImport("setupapi.dll")] static extern bool SetupDiDestroyDeviceInfoList(IntPtr s);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern SafeFileHandle CreateFile(string n, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr t);
    }
}
