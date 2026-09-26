using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SteamPassBridge
{
    // Talks to the ViGEmBus driver directly (no ViGEmClient.dll needed) to create virtual Xbox 360 pads.
    // Pads are owned by this handle: if the process dies, the driver unplugs them automatically.
    sealed class ViGEmBus : IDisposable
    {
        static Guid InterfaceGuid = new Guid("96E42B22-F5E9-42F8-B043-ED0F932F014F");

        const uint IOCTL_PLUGIN_TARGET = 0x2AA004;
        const uint IOCTL_UNPLUG_TARGET = 0x2AA008;
        const uint IOCTL_CHECK_VERSION = 0x2AA00C;
        const uint IOCTL_WAIT_DEVICE_READY = 0x2AA010;
        const uint IOCTL_XUSB_REQUEST_NOTIFICATION = 0x2AE804;
        const uint IOCTL_XUSB_SUBMIT_REPORT = 0x2AA808;

        readonly SafeFileHandle handle;

        public ViGEmBus()
        {
            string path = FindDevicePath();
            if (path == null) throw new Exception("The ViGEmBus driver is not installed. SteamPassBridge needs it to create the virtual Xbox controller.\n\nDownload it from " + DownloadUrl);
            handle = CreateFile(path, 0xC0000000, 3, IntPtr.Zero, 3, 0x80 | 0x20000000 | 0x80000000 | 0x40000000, IntPtr.Zero);
            if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not open ViGEmBus");
            if (!Ioctl(IOCTL_CHECK_VERSION, Pack(8, 1), 0)) throw new Exception("Unsupported ViGEmBus version");
        }

        public uint PlugXbox360()
        {
            for (uint serial = 1; serial <= 32; serial++)
            {
                var req = new byte[16];
                Put(req, 0, 16); Put(req, 4, serial); Put(req, 8, 0 /* Xbox360Wired */);
                BitConverter.GetBytes((ushort)0x045E).CopyTo(req, 12);
                BitConverter.GetBytes((ushort)0x028E).CopyTo(req, 14);
                if (!Ioctl(IOCTL_PLUGIN_TARGET, req, 0)) continue; // serial in use, try next
                Ioctl(IOCTL_WAIT_DEVICE_READY, Pack(8, serial), 0);
                return serial;
            }
            throw new Exception("Could not create a virtual Xbox 360 controller");
        }

        public void Unplug(uint serial) { Ioctl(IOCTL_UNPLUG_TARGET, Pack(8, serial), 0); }

        public bool Submit(uint serial, XInputGamepad g)
        {
            var req = new byte[20];
            Put(req, 0, 20); Put(req, 4, serial);
            BitConverter.GetBytes(g.Buttons).CopyTo(req, 8);
            req[10] = g.LeftTrigger; req[11] = g.RightTrigger;
            BitConverter.GetBytes(g.ThumbLX).CopyTo(req, 12);
            BitConverter.GetBytes(g.ThumbLY).CopyTo(req, 14);
            BitConverter.GetBytes(g.ThumbRX).CopyTo(req, 16);
            BitConverter.GetBytes(g.ThumbRY).CopyTo(req, 18);
            return Ioctl(IOCTL_XUSB_SUBMIT_REPORT, req, 0);
        }

        // Blocks until the game changes rumble on this pad. Returns false once the pad/handle goes away.
        public bool WaitForRumble(uint serial, out byte large, out byte small)
        {
            var req = new byte[12];
            Put(req, 0, 12); Put(req, 4, serial);
            byte[] reply;
            large = small = 0;
            if (!Ioctl(IOCTL_XUSB_REQUEST_NOTIFICATION, req, 12, out reply)) return false;
            large = reply[8]; small = reply[9];
            return true;
        }

        public void Dispose()
        {
            if (!handle.IsInvalid && !handle.IsClosed) { CancelIoEx(handle, IntPtr.Zero); handle.Dispose(); }
        }

        bool Ioctl(uint code, byte[] input, int outLen)
        {
            byte[] ignored;
            return Ioctl(code, input, outLen, out ignored);
        }

        // Overlapped DeviceIoControl with unmanaged buffers, so concurrent calls (reports + rumble wait) are safe.
        bool Ioctl(uint code, byte[] input, int outLen, out byte[] output)
        {
            output = null;
            if (handle.IsClosed) return false;
            IntPtr inBuf = Marshal.AllocHGlobal(input.Length);
            IntPtr outBuf = outLen > 0 ? Marshal.AllocHGlobal(outLen) : IntPtr.Zero;
            IntPtr ovl = Marshal.AllocHGlobal(32);
            IntPtr evt = CreateEvent(IntPtr.Zero, true, false, null);
            try
            {
                Marshal.Copy(input, 0, inBuf, input.Length);
                for (int i = 0; i < 32; i += 8) Marshal.WriteInt64(ovl, i, 0);
                Marshal.WriteIntPtr(ovl, 24, evt);
                uint ret;
                bool ok = DeviceIoControl(handle, code, inBuf, (uint)input.Length, outBuf, (uint)outLen, out ret, ovl);
                if (!ok)
                {
                    if (Marshal.GetLastWin32Error() != 997 /* ERROR_IO_PENDING */) return false;
                    ok = GetOverlappedResult(handle, ovl, out ret, true);
                }
                if (ok && outLen > 0)
                {
                    output = new byte[outLen];
                    Marshal.Copy(outBuf, output, 0, outLen);
                }
                return ok;
            }
            catch (ObjectDisposedException) { return false; }
            finally
            {
                CloseHandle(evt);
                Marshal.FreeHGlobal(ovl);
                Marshal.FreeHGlobal(inBuf);
                if (outBuf != IntPtr.Zero) Marshal.FreeHGlobal(outBuf);
            }
        }

        static byte[] Pack(uint size, uint value)
        {
            var b = new byte[8];
            Put(b, 0, size); Put(b, 4, value);
            return b;
        }

        static void Put(byte[] b, int offset, uint v) { BitConverter.GetBytes(v).CopyTo(b, offset); }

        public const string DownloadUrl = "https://github.com/nefarius/ViGEmBus/releases/latest";

        public static bool IsInstalled { get { return FindDevicePath() != null; } }

        static string FindDevicePath()
        {
            IntPtr set = SetupDiGetClassDevs(ref InterfaceGuid, IntPtr.Zero, IntPtr.Zero, 0x12 /* PRESENT | DEVICEINTERFACE */);
            if (set == new IntPtr(-1)) return null;
            try
            {
                var data = new SP_DEVICE_INTERFACE_DATA();
                data.cbSize = Marshal.SizeOf(typeof(SP_DEVICE_INTERFACE_DATA));
                if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref InterfaceGuid, 0, ref data)) return null;
                uint required;
                SetupDiGetDeviceInterfaceDetail(set, ref data, IntPtr.Zero, 0, out required, IntPtr.Zero);
                IntPtr detail = Marshal.AllocHGlobal((int)required);
                try
                {
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                    if (!SetupDiGetDeviceInterfaceDetail(set, ref data, detail, required, out required, IntPtr.Zero)) return null;
                    return Marshal.PtrToStringUni(new IntPtr(detail.ToInt64() + 4));
                }
                finally { Marshal.FreeHGlobal(detail); }
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
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sec, uint disposition, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool DeviceIoControl(SafeFileHandle h, uint code, IntPtr inBuf, uint inLen, IntPtr outBuf, uint outLen, out uint returned, IntPtr overlapped);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool GetOverlappedResult(SafeFileHandle h, IntPtr overlapped, out uint transferred, bool wait);
        [DllImport("kernel32.dll")]
        static extern bool CancelIoEx(SafeFileHandle h, IntPtr overlapped);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr CreateEvent(IntPtr sec, bool manualReset, bool initialState, string name);
        [DllImport("kernel32.dll")]
        static extern bool CloseHandle(IntPtr h);
    }
}
