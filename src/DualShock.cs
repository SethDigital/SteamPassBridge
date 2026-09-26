using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace SteamPassBridge
{
    // Reads a DualSense or DualShock 4 directly over HID (USB or Bluetooth) and exposes it as an
    // XInput-style gamepad, independent of Steam Input. This is how PlayStation controllers reach
    // Game Pass games without being affected by Steam's desktop-configuration switching.
    sealed class DualShock : IDisposable
    {
        public enum Model { DualShock4, DualSense }

        static readonly Dictionary<ushort, Model> Products = new Dictionary<ushort, Model>
        {
            { 0x05C4, Model.DualShock4 }, { 0x09CC, Model.DualShock4 }, { 0x0BA0, Model.DualShock4 },
            { 0x0CE6, Model.DualSense }, { 0x0DF2, Model.DualSense },
        };

        public readonly Model Kind;
        public readonly bool Bluetooth;
        public readonly string Path;
        public string Name { get { return Kind == Model.DualSense ? "DualSense" : "DualShock 4"; } }

        readonly FileStream stream;
        readonly SafeFileHandle handle;
        readonly byte[] buffer;
        readonly Thread reader;
        readonly object gate = new object();
        XInputGamepad state;
        int version;
        volatile bool stopping, alive = true;
        byte rumbleLarge, rumbleSmall;
        byte ledR, ledG, ledB, playerLeds;
        bool ledSet;
        volatile bool outputDirty;
        bool firstOutput = true;
        byte outSeq;

        public bool Alive { get { return alive; } }

        public static Model? ModelFor(ushort product)
        {
            Model m;
            return Products.TryGetValue(product, out m) ? (Model?)m : null;
        }

        public static List<HidDevice> FindAll()
        {
            var result = new List<HidDevice>();
            foreach (var d in Hid.Enumerate(0x054C))
                if (Products.ContainsKey(d.Product)) result.Add(d);
            return result;
        }

        public DualShock(HidDevice dev)
        {
            Path = dev.Path;
            Kind = Products[dev.Product];
            Bluetooth = dev.Bluetooth;
            stream = Hid.Open(dev.Path, out handle);
            buffer = new byte[Math.Max(64, dev.InputLength)];

            if (Bluetooth) EnableFullReports();

            reader = new Thread(ReadLoop) { IsBackground = true, Name = "ds-" + Name, Priority = ThreadPriority.AboveNormal };
            reader.Start();
            Program.Log("reading " + Name + " directly over " + (Bluetooth ? "Bluetooth" : "USB") + " (" + dev.Vendor.ToString("X4") + ":" + dev.Product.ToString("X4") + ")");
        }

        // Over Bluetooth both controllers start in a minimal report mode; reading a feature report
        // switches them to the full input report that carries triggers and all buttons.
        void EnableFullReports()
        {
            try
            {
                var feat = new byte[Kind == Model.DualSense ? 41 : 37];
                feat[0] = (byte)(Kind == Model.DualSense ? 0x05 : 0x02);
                Hid.GetFeature(handle, feat);
            }
            catch { }
        }

        public XInputGamepad Read(out int ver)
        {
            lock (gate) { ver = version; return state; }
        }

        public void SetRumble(byte large, byte small)
        {
            lock (gate)
            {
                if (rumbleLarge == large && rumbleSmall == small) return;
                rumbleLarge = large; rumbleSmall = small; outputDirty = true;
            }
            SendOutput();
        }

        // Sets the DualSense lightbar colour and the player-number dot LEDs. DualShock 4 has a lightbar
        // but no dots. Games see only an Xbox pad, so the colour is ours to assign (e.g. per player).
        public void SetLightbar(byte r, byte g, byte b, byte players)
        {
            lock (gate)
            {
                if (ledSet && ledR == r && ledG == g && ledB == b && playerLeds == players) return;
                ledR = r; ledG = g; ledB = b; playerLeds = players; ledSet = true; outputDirty = true;
            }
            SendOutput();
        }

        void ReadLoop()
        {
            while (!stopping)
            {
                int n;
                try { n = stream.Read(buffer, 0, buffer.Length); }
                catch { break; }
                if (n <= 0) break;
                var g = Parse(buffer, n);
                lock (gate) { state = g; version++; }
            }
            alive = false;
        }

        XInputGamepad Parse(byte[] b, int n)
        {
            int ax, ay, rxi, ryi, face, shoulder, misc, lt, rt;
            byte id = b[0];

            if (Kind == Model.DualSense && id == 0x31)          // DualSense, Bluetooth full report
            { ax = 2; ay = 3; rxi = 4; ryi = 5; lt = 6; rt = 7; face = 9; shoulder = 10; misc = 11; }
            else if (Kind == Model.DualSense && id == 0x01 && n >= 64) // DualSense, USB
            { ax = 1; ay = 2; rxi = 3; ryi = 4; lt = 5; rt = 6; face = 8; shoulder = 9; misc = 10; }
            else if (Kind == Model.DualShock4 && id == 0x11)     // DualShock 4, Bluetooth full report
            { ax = 3; ay = 4; rxi = 5; ryi = 6; face = 7; shoulder = 8; misc = 9; lt = 10; rt = 11; }
            else                                                  // DualShock 4 USB, or either in minimal BT mode
            { ax = 1; ay = 2; rxi = 3; ryi = 4; face = 5; shoulder = 6; misc = 7; lt = 8; rt = 9; }

            if (Math.Max(Math.Max(lt, rt), Math.Max(misc, ryi)) >= n) return new XInputGamepad();

            var g = new XInputGamepad
            {
                ThumbLX = Axis(b[ax], false),
                ThumbLY = Axis(b[ay], true),
                ThumbRX = Axis(b[rxi], false),
                ThumbRY = Axis(b[ryi], true),
            };
            if (Kind == Model.DualSense) { g.LeftTrigger = b[lt]; g.RightTrigger = b[rt]; }
            else { g.LeftTrigger = b[lt]; g.RightTrigger = b[rt]; }

            ushort btn = 0;
            int hat = b[face] & 0x0F;
            switch (hat)
            {
                case 0: btn |= Up; break;
                case 1: btn |= (ushort)(Up | Right); break;
                case 2: btn |= Right; break;
                case 3: btn |= (ushort)(Right | Down); break;
                case 4: btn |= Down; break;
                case 5: btn |= (ushort)(Down | Left); break;
                case 6: btn |= Left; break;
                case 7: btn |= (ushort)(Left | Up); break;
            }
            if ((b[face] & 0x20) != 0) btn |= A;   // Cross
            if ((b[face] & 0x40) != 0) btn |= B;   // Circle
            if ((b[face] & 0x10) != 0) btn |= X;   // Square
            if ((b[face] & 0x80) != 0) btn |= Y;   // Triangle
            if ((b[shoulder] & 0x01) != 0) btn |= LB;
            if ((b[shoulder] & 0x02) != 0) btn |= RB;
            if ((b[shoulder] & 0x10) != 0) btn |= Back;    // Share / Create
            if ((b[shoulder] & 0x20) != 0) btn |= Start;   // Options
            if ((b[shoulder] & 0x40) != 0) btn |= LThumb;  // L3
            if ((b[shoulder] & 0x80) != 0) btn |= RThumb;  // R3
            if ((b[misc] & 0x01) != 0) btn |= Guide;       // PS button
            g.Buttons = btn;
            return g;
        }

        static short Axis(byte raw, bool invert)
        {
            int v = (raw - 128) * 257;
            if (v > 32767) v = 32767;
            if (v < -32768) v = -32768;
            if (invert) v = v == -32768 ? 32767 : -v;
            return (short)v;
        }

        // ---- rumble output ----

        void SendOutput()
        {
            if (!outputDirty) return;
            byte large, small, r, g, b, players; bool led, first;
            lock (gate)
            {
                large = rumbleLarge; small = rumbleSmall; r = ledR; g = ledG; b = ledB; players = playerLeds;
                led = ledSet; first = firstOutput; firstOutput = false; outputDirty = false;
            }
            try { WriteOutput(large, small, r, g, b, players, led, first); } catch { }
        }

        // Builds one output report carrying the current rumble, lightbar colour and player LEDs together,
        // since the controller applies the whole report at once.
        void WriteOutput(byte large, byte small, byte r, byte g, byte b, byte players, bool led, bool first)
        {
            byte[] rep;
            if (Kind == Model.DualSense && !Bluetooth)
            {
                rep = new byte[48];
                rep[0] = 0x02;
                rep[1] = 0x03;                                                  // enable rumble
                rep[2] = (byte)((led ? 0x04 : 0) | (players != 0 ? 0x10 : 0) | (first && led ? 0x08 : 0));
                rep[3] = small; rep[4] = large;
                rep[44] = players; rep[45] = r; rep[46] = g; rep[47] = b;
            }
            else if (Kind == Model.DualSense) // Bluetooth
            {
                rep = new byte[78];
                rep[0] = 0x31; rep[1] = (byte)((outSeq++ << 4) & 0xF0);
                rep[2] = 0x10;                                                  // enable rumble (as verified)
                rep[3] = (byte)(0x03 | (led ? 0x04 : 0) | (players != 0 ? 0x10 : 0) | (first && led ? 0x08 : 0));
                rep[4] = small; rep[5] = large;
                rep[45] = players; rep[46] = r; rep[47] = g; rep[48] = b;
                AppendCrc(rep);
            }
            else if (!Bluetooth) // DualShock 4 USB
            {
                rep = new byte[32];
                rep[0] = 0x05; rep[1] = (byte)(0x01 | (led ? 0x02 : 0));
                rep[4] = small; rep[5] = large;
                rep[6] = r; rep[7] = g; rep[8] = b;
            }
            else // DualShock 4 Bluetooth
            {
                rep = new byte[78];
                rep[0] = 0x11; rep[1] = 0xC0; rep[2] = 0xA0;
                rep[3] = (byte)(0x01 | (led ? 0x02 : 0));
                rep[6] = small; rep[7] = large;
                rep[8] = r; rep[9] = g; rep[10] = b;
                AppendCrc(rep);
            }
            stream.Write(rep, 0, rep.Length);
            stream.Flush();
        }

        // Bluetooth output reports carry a CRC32 over 0xA2 + the report, in the last four bytes.
        static void AppendCrc(byte[] rep)
        {
            uint crc = 0xFFFFFFFF;
            crc = Step(crc, 0xA2);
            for (int i = 0; i < rep.Length - 4; i++) crc = Step(crc, rep[i]);
            crc = ~crc;
            rep[rep.Length - 4] = (byte)crc;
            rep[rep.Length - 3] = (byte)(crc >> 8);
            rep[rep.Length - 2] = (byte)(crc >> 16);
            rep[rep.Length - 1] = (byte)(crc >> 24);
        }

        static uint Step(uint crc, byte b)
        {
            crc ^= b;
            for (int k = 0; k < 8; k++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
            return crc;
        }

        public void Dispose()
        {
            stopping = true;
            try { SetRumble(0, 0); } catch { }
            try { handle.Close(); } catch { }   // unblocks the pending Read
            reader.Join(500);
            try { stream.Dispose(); } catch { }
        }

        const ushort Up = 0x1, Down = 0x2, Left = 0x4, Right = 0x8, Start = 0x10, Back = 0x20, LThumb = 0x40, RThumb = 0x80,
                     LB = 0x100, RB = 0x200, Guide = 0x400, A = 0x1000, B = 0x2000, X = 0x4000, Y = 0x8000;
    }
}
