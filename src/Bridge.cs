using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace SteamPassBridge
{
    // Turns physical controllers into system-wide ViGEm Xbox 360 pads that Game Pass games can see.
    //
    // Two independent sources feed the same virtual pads:
    //   * Steam Input slots (28DE) — the Steam Controller and anything else Steam hands us. Mirrored while
    //     this process is Steam-launched. Subject to Steam's desktop-config switching in real games.
    //   * DualSense / DualShock 4 read directly over HID — immune to Steam's focus handling, so PlayStation
    //     controllers keep working inside a focused game. See DualShock.cs.
    // A Steam Input slot that turns out to be the same physical pad as a directly-read PlayStation controller
    // is suppressed, so a controller is never bridged twice.
    sealed class Bridge : IDisposable
    {
        class SteamBinding
        {
            public uint Slot, Serial;
            public uint LastPacket = uint.MaxValue;
            public bool Connected = true, Shadowed;
            public XInputGamepad Last;
        }

        class DsBinding
        {
            public DualShock Ds;
            public uint Serial;
            public int LastVer = -1;
            public XInputGamepad Last;
        }

        [DllImport("winmm.dll")] static extern uint timeBeginPeriod(uint ms);
        [DllImport("winmm.dll")] static extern uint timeEndPeriod(uint ms);

        static readonly TimeSpan Quarantine = TimeSpan.FromSeconds(3);

        readonly ViGEmBus bus;
        readonly List<SteamBinding> steam = new List<SteamBinding>();
        readonly List<DsBinding> ds = new List<DsBinding>();
        readonly Dictionary<uint, bool> suspect = new Dictionary<uint, bool>();
        readonly Thread pump;
        DateTime lastPlug = DateTime.MinValue, nextDsScan = DateTime.MinValue;
        HashSet<uint> presentAtPlug = new HashSet<uint>();
        volatile bool stopping;

        public int PadCount { get { lock (steam) lock (ds) return steam.Count(b => !b.Shadowed) + ds.Count; } }
        public List<uint> BoundSlots { get { lock (steam) return steam.Where(b => b.Connected && !b.Shadowed).Select(b => b.Slot).ToList(); } }

        public Bridge()
        {
            bus = new ViGEmBus();
            pump = new Thread(Run) { IsBackground = true, Name = "bridge", Priority = ThreadPriority.AboveNormal };
            pump.Start();
        }

        // Snapshot of directly-read PlayStation controllers, for the Controller Test display.
        public List<KeyValuePair<string, XInputGamepad>> DirectControllers()
        {
            lock (ds) return ds.Select(b => new KeyValuePair<string, XInputGamepad>(
                b.Ds.Name + (b.Ds.Bluetooth ? " (Bluetooth)" : " (USB)"), b.Last)).ToList();
        }

        // Manually buzz a directly-read controller by its index in DirectControllers(), for the test window.
        public void RumbleDirect(int index, byte large, byte small)
        {
            DsBinding b = null;
            lock (ds) if (index >= 0 && index < ds.Count) b = ds[index];
            if (b != null) try { b.Ds.SetRumble(large, small); } catch { }
        }

        void Run()
        {
            timeBeginPeriod(1);
            try
            {
                var nextScan = DateTime.MinValue;
                while (!stopping)
                {
                    if (DateTime.Now >= nextScan) { ScanSteam(); nextScan = DateTime.Now.AddMilliseconds(250); }
                    if (DateTime.Now >= nextDsScan) { ScanDualShock(); nextDsScan = DateTime.Now.AddMilliseconds(1000); }
                    Pump();
                    Thread.Sleep(1);
                }
            }
            catch (Exception ex) { Program.Log("bridge error: " + ex); }
            finally { timeEndPeriod(1); }
        }

        // ---- PlayStation controllers, read directly ----

        void ScanDualShock()
        {
            List<HidDevice> devices;
            try { devices = DualShock.FindAll(); } catch { return; }

            lock (ds)
            {
                // Drop controllers that went away.
                for (int i = ds.Count - 1; i >= 0; i--)
                {
                    var b = ds[i];
                    bool stillThere = devices.Any(d => d.Path == b.Ds.Path);
                    if (b.Ds.Alive && stillThere) continue;
                    Program.Log(b.Ds.Name + " disconnected; removing its virtual pad #" + b.Serial);
                    bus.Submit(b.Serial, new XInputGamepad());
                    bus.Unplug(b.Serial);
                    b.Ds.Dispose();
                    ds.RemoveAt(i);
                }

                foreach (var dev in devices)
                {
                    if (ds.Any(b => b.Ds.Path == dev.Path)) continue;
                    DualShock reader;
                    try { reader = new DualShock(dev); }
                    catch (Exception ex) { Program.Log("could not open " + dev.Product.ToString("X4") + ": " + ex.Message); continue; }
                    int player = ds.Count;
                    var binding = new DsBinding { Ds = reader, Serial = bus.PlugXbox360() };
                    ds.Add(binding);
                    ApplyPlayerLight(reader, player);
                    Program.Log("bridging " + reader.Name + " (player " + (player + 1) + ") -> virtual Xbox 360 pad #" + binding.Serial);
                    new Thread(() => DsRumbleLoop(binding)) { IsBackground = true, Name = "ds-rumble" + binding.Serial }.Start();
                }
            }
        }

        // Per-player lightbar colour and player-number dot LEDs, so each controller is distinguishable.
        static readonly byte[][] PlayerColors =
        {
            new byte[] { 0, 40, 160 },   // 1 - blue
            new byte[] { 200, 0, 0 },    // 2 - red
            new byte[] { 0, 150, 40 },   // 3 - green
            new byte[] { 150, 0, 150 },  // 4 - purple
        };
        static readonly byte[] PlayerDots = { 0x04, 0x0A, 0x15, 0x1B };

        static void ApplyPlayerLight(DualShock reader, int player)
        {
            var c = PlayerColors[player % PlayerColors.Length];
            byte dots = PlayerDots[Math.Min(player, PlayerDots.Length - 1)];
            try { reader.SetLightbar(c[0], c[1], c[2], dots); } catch { }
        }

        void DsRumbleLoop(DsBinding b)
        {
            byte large, small;
            while (!stopping && bus.WaitForRumble(b.Serial, out large, out small))
            {
                try { b.Ds.SetRumble(large, small); } catch { }
            }
        }

        // ---- Steam Input slots ----

        void ScanSteam()
        {
            var valveNow = new HashSet<uint>();
            for (uint i = 0; i < 4; i++) if (IsConnectedValveSlot(i)) valveNow.Add(i);

            for (uint slot = 0; slot < 4; slot++)
            {
                XInputState s;
                bool connected = XInput.GetState(slot, out s) == 0;
                SteamBinding bound;
                lock (steam) bound = steam.FirstOrDefault(b => b.Slot == slot);

                if (!connected) { suspect.Remove(slot); continue; }
                if (bound != null && bound.Connected) continue;

                ushort vid, pid;
                if (!XInput.TryGetIds(slot, out vid, out pid) || vid != XInput.ValveVendorId) continue;

                if (suspect.ContainsKey(slot))
                {
                    if (!IsIndependentInput(s.Gamepad)) continue;
                    Program.Log("Steam slot " + slot + " has its own input; treating it as a real controller");
                    suspect.Remove(slot);
                }
                else if (bound == null && DateTime.Now - lastPlug < Quarantine && !presentAtPlug.Contains(slot))
                {
                    suspect[slot] = true;
                    Program.Log("Steam slot " + slot + " appeared right after a virtual pad was created; watching it before bridging");
                    continue;
                }

                if (bound != null) { bound.Connected = true; Program.Log("Steam slot " + slot + " reconnected -> virtual pad #" + bound.Serial); continue; }

                SteamBinding idle;
                lock (steam) idle = steam.FirstOrDefault(b => !b.Connected && !IsConnectedValveSlot(b.Slot));
                if (idle != null)
                {
                    Program.Log("Steam slot " + slot + " takes over virtual pad #" + idle.Serial + " (was slot " + idle.Slot + ")");
                    idle.Slot = slot; idle.Connected = true; idle.LastPacket = uint.MaxValue;
                    continue;
                }

                var nb = new SteamBinding { Slot = slot, Serial = bus.PlugXbox360() };
                lastPlug = DateTime.Now;
                presentAtPlug = valveNow;
                lock (steam) steam.Add(nb);
                Program.Log("bridging Steam slot " + slot + " -> virtual Xbox 360 pad #" + nb.Serial + " (" + Program.DescribeSlot(slot) + ")");
                new Thread(() => SteamRumbleLoop(nb)) { IsBackground = true, Name = "rumble" + nb.Serial }.Start();
            }
        }

        static bool IsConnectedValveSlot(uint slot)
        {
            ushort vid, pid;
            return XInput.IsConnected(slot) && XInput.TryGetIds(slot, out vid, out pid) && vid == XInput.ValveVendorId;
        }

        bool IsIndependentInput(XInputGamepad g)
        {
            if (IsNeutral(g)) return false;
            lock (steam) return steam.All(b => !Similar(b.Last, g));
        }

        static bool IsNeutral(XInputGamepad g)
        {
            const int dz = 9000;
            return g.Buttons == 0 && g.LeftTrigger < 40 && g.RightTrigger < 40 &&
                   Math.Abs((int)g.ThumbLX) < dz && Math.Abs((int)g.ThumbLY) < dz && Math.Abs((int)g.ThumbRX) < dz && Math.Abs((int)g.ThumbRY) < dz;
        }

        static bool Similar(XInputGamepad a, XInputGamepad b)
        {
            const int tol = 6000;
            return a.Buttons == b.Buttons && Math.Abs(a.LeftTrigger - b.LeftTrigger) < 40 && Math.Abs(a.RightTrigger - b.RightTrigger) < 40 &&
                   Math.Abs(a.ThumbLX - b.ThumbLX) < tol && Math.Abs(a.ThumbLY - b.ThumbLY) < tol &&
                   Math.Abs(a.ThumbRX - b.ThumbRX) < tol && Math.Abs(a.ThumbRY - b.ThumbRY) < tol;
        }

        // A Steam Input slot that mirrors a directly-read PlayStation pad (Steam Input PlayStation support left
        // on) is the same physical controller reaching us twice. Suppress it so the game sees one pad.
        bool ShadowsDualShock(XInputGamepad g)
        {
            if (IsNeutral(g)) return false;
            lock (ds) return ds.Any(b => Similar(b.Last, g));
        }

        void Pump()
        {
            DsBinding[] dsSnap;
            lock (ds) dsSnap = ds.ToArray();
            foreach (var b in dsSnap)
            {
                int ver;
                var g = b.Ds.Read(out ver);
                if (ver != b.LastVer) { bus.Submit(b.Serial, g); b.LastVer = ver; b.Last = g; }
            }

            SteamBinding[] steamSnap;
            lock (steam) steamSnap = steam.ToArray();
            foreach (var b in steamSnap)
            {
                XInputState s;
                if (b.Connected && XInput.GetState(b.Slot, out s) == 0)
                {
                    if (s.PacketNumber == b.LastPacket) continue;
                    b.LastPacket = s.PacketNumber;
                    b.Last = s.Gamepad;
                    if (!b.Shadowed && ShadowsDualShock(s.Gamepad))
                    {
                        b.Shadowed = true;
                        bus.Submit(b.Serial, new XInputGamepad());
                        bus.Unplug(b.Serial);
                        Program.Log("Steam slot " + b.Slot + " is the same controller as a directly-read PlayStation pad; suppressing the duplicate");
                        continue;
                    }
                    if (!b.Shadowed) bus.Submit(b.Serial, s.Gamepad);
                }
                else if (b.Connected)
                {
                    if (!b.Shadowed) bus.Submit(b.Serial, new XInputGamepad());
                    b.Last = new XInputGamepad();
                    b.Connected = false;
                    Program.Log("Steam slot " + b.Slot + " disconnected; virtual pad #" + b.Serial + " kept for reconnect");
                }
            }
        }

        void SteamRumbleLoop(SteamBinding b)
        {
            byte large, small;
            while (!stopping && bus.WaitForRumble(b.Serial, out large, out small))
            {
                var v = new XInputVibration { LeftMotor = (ushort)(large * 257), RightMotor = (ushort)(small * 257) };
                XInput.SetState(b.Slot, ref v);
            }
        }

        public void Dispose()
        {
            stopping = true;
            pump.Join(2000);
            lock (ds) foreach (var b in ds) { try { b.Ds.Dispose(); } catch { } if (b.Serial != 0) bus.Unplug(b.Serial); }
            lock (steam) foreach (var b in steam) if (b.Serial != 0 && !b.Shadowed) bus.Unplug(b.Serial);
            bus.Dispose();
        }
    }
}
