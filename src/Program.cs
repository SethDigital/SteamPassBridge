using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace SteamPassBridge
{
    static class Program
    {
        public const string TestShortcutName = "SteamPassBridge Controller Test";

        public static string ExePath { get { return Process.GetCurrentProcess().MainModule.FileName; } }
        public static string ExeDir { get { return Path.GetDirectoryName(ExePath); } }
        public static string LogPath { get { return Path.Combine(ExeDir, "SteamPassBridge.log"); } }

        public static string Version
        {
            get
            {
                var attrs = typeof(Program).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false);
                return attrs.Length > 0 ? ((System.Reflection.AssemblyInformationalVersionAttribute)attrs[0]).InformationalVersion : "0.0.0";
            }
        }

        public static bool OtherInstancesRunning()
        {
            int me = Process.GetCurrentProcess().Id;
            return Process.GetProcessesByName("SteamPassBridge").Any(p => p.Id != me);
        }

        public static int InstancesOf(string exePath)
        {
            int me = Process.GetCurrentProcess().Id, n = 0;
            foreach (var p in Process.GetProcessesByName("SteamPassBridge"))
            {
                try
                {
                    if (p.Id != me && string.Equals(p.MainModule.FileName, exePath, StringComparison.OrdinalIgnoreCase)) n++;
                }
                catch { n++; } // can't inspect it: assume it's in the way
            }
            return n;
        }

        [DllImport("kernel32.dll")] static extern bool AttachConsole(int pid);

        [STAThread]
        static int Main(string[] args)
        {
            string cmd = args.Length > 0 ? args[0].ToLowerInvariant() : "";
            try
            {
                switch (cmd)
                {
                    case "": StartUi(); Application.Run(new MainForm()); return 0;
                    case "launch": return Launch(args.Length > 1 ? args[1] : null);
                    case "test": StartUi(); Application.Run(new TestForm()); return 0;
                    case "probe": return Probe(args.Length > 1 ? int.Parse(args[1]) : 30);
                    case "list": AttachConsole(-1); return List();
                    case "sync": AttachConsole(-1); return Sync(args.Skip(1).ToList());
                    case "art": return RegenerateArt();
                    case "install": AttachConsole(-1); Setup.Install(); Console.WriteLine("Installed to " + Setup.InstallDir); return 0;
                    case "uninstall": return Uninstall(args.Contains("/quiet") || args.Contains("--quiet"));
                    case "install-driver":
                        {
                            AttachConsole(-1);
                            if (ViGEmBus.IsInstalled && !args.Contains("--force"))
                            {
                                // Never upgrade a working driver implicitly: ViGEmBus upgrades need a Windows restart.
                                Console.WriteLine("ViGEmBus is already installed; nothing to do (use --force to reinstall).");
                                return 0;
                            }
                            if (DriverSetup.RestartPending) { Console.WriteLine("Restart Windows to finish the ViGEmBus install."); return 1; }
                            var r = DriverSetup.Install();
                            Console.WriteLine("ViGEmBus " + DriverSetup.Version + ": " + r);
                            return r == DriverSetup.Result.Cancelled ? 1 : 0;
                        }
                    default:
                        AttachConsole(-1);
                        Console.WriteLine();
                        Console.WriteLine("SteamPassBridge " + Version + " - play Game Pass games with a Steam Controller");
                        Console.WriteLine("  (no args)        open the setup window");
                        Console.WriteLine("  list             list installed Game Pass games");
                        Console.WriteLine("  sync [family..]  add games to Steam (all games if none given); restarts Steam");
                        Console.WriteLine("  art              regenerate Steam library artwork for the added games");
                        Console.WriteLine("  launch <family>  used by the Steam shortcuts: bridge controller + run game");
                        Console.WriteLine("  test             live view of what Steam Input sends (launch from Steam)");
                        Console.WriteLine("  install          install for this Windows user (no admin needed)");
                        Console.WriteLine("  install-driver   install the bundled ViGEmBus driver (asks for admin)");
                        Console.WriteLine("  uninstall        remove the Steam shortcuts and this app");
                        return 1;
                }
            }
            catch (Exception ex)
            {
                Log("ERROR " + ex);
                if (cmd == "launch" || cmd == "")
                    MessageBox.Show(ex.Message, "SteamPassBridge", MessageBoxButtons.OK, MessageBoxIcon.Error);
                else Console.Error.WriteLine(ex.Message);
                return 1;
            }
        }

        static void StartUi()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
        }

        public static void Log(string line)
        {
            try
            {
                var fi = new FileInfo(LogPath);
                if (fi.Exists && fi.Length > 1024 * 1024) fi.Delete();
                File.AppendAllText(LogPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " [" + Process.GetCurrentProcess().Id + "] " + line + Environment.NewLine);
            }
            catch { }
        }

        static int Launch(string family)
        {
            var game = family == null ? null : Games.ByFamily(family);
            if (game == null) throw new Exception("Game Pass game not found: " + family + ". Is it still installed?");
            Log("launch " + game.Name + " (" + game.Aumid + "); steam hooks: " + SteamModules());

            // Mutex per game so a double-click in Steam doesn't create two sets of virtual pads.
            bool created;
            using (var mutex = new Mutex(true, @"Local\SteamPassBridge_" + game.FamilyName, out created))
            {
                if (!created) { Launcher.Activate(game.Aumid); return 0; }

                if (!EnsureDriverForLaunch(game.Name))
                {
                    // Still start the game; it just won't see the Steam Controller.
                    Launcher.Activate(game.Aumid);
                    Launcher.WaitForGame(game.FamilyName, TimeSpan.FromMinutes(3));
                    return 0;
                }

                using (var bridge = new Bridge())
                {
                    // Give the bridge a moment to create the virtual pad so the game sees it at startup.
                    var until = DateTime.Now.AddSeconds(3);
                    while (bridge.PadCount == 0 && DateTime.Now < until) Thread.Sleep(50);
                    Thread.Sleep(bridge.PadCount > 0 ? 500 : 0);
                    if (bridge.PadCount == 0) Log("no Steam virtual pad yet (controller off?); will bridge when it appears");

                    Launcher.Activate(game.Aumid);
                    Launcher.WaitForGame(game.FamilyName, TimeSpan.FromMinutes(3));
                }
            }
            return 0;
        }

        static bool EnsureDriverForLaunch(string gameName)
        {
            if (ViGEmBus.IsInstalled) return true;
            if (DriverSetup.RestartPending)
            {
                MessageBox.Show("Restart Windows to finish installing the controller driver. Until then " + gameName +
                    " can't see your Steam Controller.", "SteamPassBridge", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return false;
            }
            if (!DriverSetup.IsBundled ||
                MessageBox.Show("The ViGEmBus controller driver isn't installed, so " + gameName + " can't see your Steam Controller.\n\n" +
                    "Install it now? Windows will ask for permission once.", "SteamPassBridge",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return false;
            try
            {
                var result = DriverSetup.Install();
                if (result == DriverSetup.Result.RestartRequired)
                    MessageBox.Show("The driver is installed but Windows needs a restart before games can see the controller.", "SteamPassBridge");
                return result == DriverSetup.Result.Installed;
            }
            catch (Exception ex)
            {
                Log("ERROR driver install: " + ex);
                MessageBox.Show(ex.Message, "SteamPassBridge", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }
        }

        static int List()
        {
            Console.WriteLine();
            foreach (var g in Games.Find()) Console.WriteLine(g.Name.PadRight(40) + " " + g.FamilyName);
            return 0;
        }

        static int Sync(List<string> families)
        {
            var all = Games.Find();
            var chosen = families.Count == 0 ? all
                : all.Where(g => families.Any(f => string.Equals(f, g.FamilyName, StringComparison.OrdinalIgnoreCase))).ToList();
            var added = SyncToSteam(chosen, null, true);
            Console.WriteLine();
            Console.WriteLine("Added/updated " + added + " shortcut(s) in Steam.");
            return 0;
        }

        // Writes shortcuts for the given games (plus the controller-test entry), removing any of our
        // shortcuts listed in `remove`. Steam is closed while the file is edited and reopened afterwards.
        public static int SyncToSteam(IList<GamePassGame> games, IList<string> remove, bool includeTest)
        {
            var gridDirs = Steam.GridDirs();
            var specs = games.Select(g =>
            {
                uint appId = Steam.ShortcutAppId("\"" + ExePath + "\"", g.Name);
                string icon = null;
                try { icon = Artwork.WriteForGame(g, appId, gridDirs, false); } catch (Exception ex) { Log("artwork: " + ex.Message); }
                return new ShortcutSpec
                {
                    AppName = g.Name,
                    Exe = ExePath,
                    StartDir = ExeDir,
                    LaunchOptions = "launch " + g.FamilyName,
                    Icon = icon,
                };
            }).ToList();
            if (includeTest)
                specs.Add(new ShortcutSpec { AppName = TestShortcutName, Exe = ExePath, StartDir = ExeDir, LaunchOptions = "test" });

            bool wasRunning = Steam.IsRunning;
            Steam.Shutdown();
            var removeList = new List<string>(remove ?? new string[0]) { "SteamPassBridge Probe" };
            int n = Steam.WriteShortcuts(specs, removeList, ExePath, false);
            if (wasRunning) Steam.Start();
            Log("synced " + n + " shortcut(s)");
            return n;
        }

        static int Uninstall(bool quiet)
        {
            if (!quiet && MessageBox.Show("Remove SteamPassBridge and the games it added to your Steam library?" +
                    (Steam.IsRunning ? "\n\nSteam will close and reopen." : ""),
                    "Uninstall SteamPassBridge", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
                return 1;
            Setup.Uninstall();
            if (!quiet) MessageBox.Show("SteamPassBridge has been removed.", "Uninstall SteamPassBridge");
            return 0;
        }

        // Regenerates Steam library artwork for the games already added, replacing any we made before.
        static int RegenerateArt()
        {
            AttachConsole(-1);
            if (!Steam.IsInstalled) { Console.WriteLine("Steam is not installed."); return 1; }
            var gridDirs = Steam.GridDirs();
            var inSteam = Steam.ExistingOurAppNames();
            int n = 0;
            foreach (var g in Games.Find())
            {
                if (!inSteam.Contains(g.Name)) continue;
                uint appId = Steam.ShortcutAppId("\"" + ExePath + "\"", g.Name);
                Artwork.WriteForGame(g, appId, gridDirs, true);
                Console.WriteLine("  art: " + g.Name);
                n++;
            }
            Console.WriteLine("Regenerated artwork for " + n + " game(s). Restart Steam to see it.");
            return 0;
        }

        static int Probe(int seconds)
        {
            Log("probe start; steam hooks: " + SteamModules());
            var end = DateTime.Now.AddSeconds(seconds);
            while (DateTime.Now < end) { Log("slots: " + DescribeSlots()); Thread.Sleep(1000); }
            return 0;
        }

        public static string SteamModules()
        {
            var names = new List<string>();
            foreach (ProcessModule m in Process.GetCurrentProcess().Modules)
                if (m.ModuleName.ToLowerInvariant().Contains("overlay")) names.Add(m.ModuleName);
            return names.Count == 0 ? "(none - not launched from Steam)" : string.Join(", ", names.ToArray());
        }

        // Everything XInput reveals about one slot, for the log (helps tell controller types apart).
        public static string DescribeSlot(uint slot)
        {
            XInputCapabilitiesEx c;
            if (!XInput.TryGetCaps(slot, out c)) return "no capabilities";
            XInput.BatteryInfo bat;
            string battery = XInput.GetBattery(slot, 0, out bat) == 0 ? "battery type " + bat.Type + " level " + bat.Level : "no battery info";
            return string.Format("{0:X4}:{1:X4} ver {2:X4} type {3} sub {4} flags {5:X4} extra {6:X4}/{7:X8}, {8}",
                c.VendorId, c.ProductId, c.ProductVersion, c.Type, c.SubType, c.Flags, c.Unk1, c.Unk2, battery);
        }

        public static string DescribeSlots()
        {
            var parts = new List<string>();
            for (uint i = 0; i < 4; i++)
            {
                XInputState s;
                if (XInput.GetState(i, out s) != 0) { parts.Add("Slot " + i + ": empty"); continue; }
                ushort vid, pid;
                string ids = XInput.TryGetIds(i, out vid, out pid) ? vid.ToString("X4") + ":" + pid.ToString("X4") : "?";
                string kind = vid == XInput.ValveVendorId ? "Steam Input" : vid == 0x045E ? "Xbox pad" : "other";
                var g = s.Gamepad;
                parts.Add(string.Format("Slot {0}: {1} ({2})  buttons={3:X4}  L=({4,6},{5,6})  R=({6,6},{7,6})  LT={8,3} RT={9,3}",
                    i, kind, ids, g.Buttons, g.ThumbLX, g.ThumbLY, g.ThumbRX, g.ThumbRY, g.LeftTrigger, g.RightTrigger));
            }
            return string.Join(Environment.NewLine, parts.ToArray());
        }
    }
}
