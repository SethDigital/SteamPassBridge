using System.Runtime.InteropServices;

namespace SteamPassBridge
{
    [StructLayout(LayoutKind.Sequential)]
    struct XInputGamepad
    {
        public ushort Buttons;
        public byte LeftTrigger, RightTrigger;
        public short ThumbLX, ThumbLY, ThumbRX, ThumbRY;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct XInputState { public uint PacketNumber; public XInputGamepad Gamepad; }

    [StructLayout(LayoutKind.Sequential)]
    struct XInputVibration { public ushort LeftMotor, RightMotor; }

    [StructLayout(LayoutKind.Sequential)]
    struct XInputCapabilitiesEx
    {
        public byte Type, SubType; public ushort Flags;
        public XInputGamepad Gamepad; public XInputVibration Vibration;
        public ushort VendorId, ProductId, ProductVersion, Unk1; public uint Unk2;
    }

    static class XInput
    {
        public const ushort ValveVendorId = 0x28DE;

        [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
        public static extern uint GetState(uint userIndex, out XInputState state);

        [DllImport("xinput1_4.dll", EntryPoint = "XInputSetState")]
        public static extern uint SetState(uint userIndex, ref XInputVibration vibration);

        // Undocumented but stable export (ordinal 108) that also reports VID/PID.
        [DllImport("xinput1_4.dll", EntryPoint = "#108")]
        static extern uint GetCapabilitiesEx(uint one, uint userIndex, uint flags, out XInputCapabilitiesEx caps);

        [StructLayout(LayoutKind.Sequential)]
        public struct BatteryInfo { public byte Type, Level; }

        [DllImport("xinput1_4.dll", EntryPoint = "XInputGetBatteryInformation")]
        public static extern uint GetBattery(uint userIndex, byte devType, out BatteryInfo info);

        public static bool TryGetCaps(uint slot, out XInputCapabilitiesEx caps)
        {
            return GetCapabilitiesEx(1, slot, 0, out caps) == 0;
        }

        public static bool TryGetIds(uint slot, out ushort vid, out ushort pid)
        {
            XInputCapabilitiesEx c;
            vid = pid = 0;
            if (GetCapabilitiesEx(1, slot, 0, out c) != 0) return false;
            vid = c.VendorId; pid = c.ProductId;
            return true;
        }

        public static bool IsConnected(uint slot)
        {
            XInputState s;
            return GetState(slot, out s) == 0;
        }
    }
}
