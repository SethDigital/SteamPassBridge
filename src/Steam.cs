using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using Microsoft.Win32;

namespace SteamPassBridge
{
    class ShortcutSpec
    {
        public string AppName, Exe, StartDir, LaunchOptions, Icon;
    }

    static class Steam
    {
        public static string InstallDir
        {
            get
            {
                if (Sandbox.On) return System.IO.Path.Combine(Sandbox.Root, "Steam");
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
                {
                    var p = k == null ? null : k.GetValue("SteamPath") as string;
                    if (string.IsNullOrEmpty(p) || !File.Exists(Path.Combine(p, "steam.exe"))) return null;
                    return p.Replace('/', '\\');
                }
            }
        }

        public static bool IsInstalled { get { return InstallDir != null && (!Sandbox.On || System.IO.Directory.Exists(InstallDir)); } }

        static string RequireInstallDir()
        {
            var dir = InstallDir;
            if (dir == null) throw new Exception("Steam is not installed. Install Steam from https://store.steampowered.com/about/ first.");
            return dir;
        }

        public static string Exe { get { return Path.Combine(RequireInstallDir(), "steam.exe"); } }

        public static List<string> ShortcutFiles()
        {
            var result = new List<string>();
            var userdata = Path.Combine(RequireInstallDir(), "userdata");
            if (!Directory.Exists(userdata)) return result;
            foreach (var dir in Directory.GetDirectories(userdata))
            {
                var name = Path.GetFileName(dir);
                if (name == "0" || name == "anonymous") continue;
                var config = Path.Combine(dir, "config");
                Directory.CreateDirectory(config);
                result.Add(Path.Combine(config, "shortcuts.vdf"));
            }
            return result;
        }

        // config\grid folder for each Steam user, where custom shortcut artwork lives.
        public static List<string> GridDirs()
        {
            var result = new List<string>();
            var userdata = Path.Combine(RequireInstallDir(), "userdata");
            if (!Directory.Exists(userdata)) return result;
            foreach (var dir in Directory.GetDirectories(userdata))
            {
                var name = Path.GetFileName(dir);
                if (name == "0" || name == "anonymous") continue;
                result.Add(Path.Combine(dir, "config", "grid"));
            }
            return result;
        }

        public static bool IsRunning { get { return !Sandbox.On && Process.GetProcessesByName("steam").Length > 0; } }

        public static void Shutdown()
        {
            if (!IsRunning) return;
            if (Program.OtherInstancesRunning())
                throw new Exception("A game started through SteamPassBridge is still running. Close it first, then try again.");
            Process.Start(Exe, "-shutdown");
            var deadline = DateTime.Now.AddSeconds(60);
            while (IsRunning)
            {
                if (DateTime.Now > deadline) throw new Exception("Steam did not exit within 60 seconds.");
                System.Threading.Thread.Sleep(500);
            }
            System.Threading.Thread.Sleep(1500); // let Steam finish flushing its files
        }

        public static void Start() { Process.Start(Exe); }

        // Same id Steam itself derives for non-Steam shortcuts; also used for grid artwork names.
        public static uint ShortcutAppId(string exe, string appName)
        {
            return Crc32(Encoding.UTF8.GetBytes(exe + appName)) | 0x80000000;
        }

        public static ulong RunGameId(uint appId) { return ((ulong)appId << 32) | 0x02000000; }

        // Adds or updates the given shortcuts and removes our shortcuts named in `remove` (or all of ours if
        // removeAll). Every remaining shortcut of ours is repointed at `ourExe`, keeping its appid so
        // per-game controller layouts survive the exe moving. Steam must not be running while this runs.
        public static int WriteShortcuts(IList<ShortcutSpec> specs, ICollection<string> remove, string ourExe, bool removeAll)
        {
            int changed = 0;
            foreach (var file in ShortcutFiles())
            {
                if (File.Exists(file))
                {
                    var backup = file + ".spb-backup";
                    if (!File.Exists(backup)) File.Copy(file, backup);
                }
                var root = VdfNode.Load(file);

                root.Children.RemoveAll(e => IsOurs(e) && (removeAll || (remove != null && remove.Contains(AppName(e)))));

                foreach (var e in root.Children)
                    if (IsOurs(e)) Point(e, ourExe);

                foreach (var s in specs)
                {
                    VdfNode entry = null;
                    foreach (var e in root.Children)
                        if (IsOurs(e) && string.Equals(AppName(e), s.AppName, StringComparison.Ordinal)) { entry = e; break; }

                    if (entry == null)
                    {
                        entry = new VdfNode(VdfNode.Map, "0", new List<VdfNode>());
                        root.Children.Add(entry);
                        entry.Set("appid", VdfNode.Int, unchecked((int)ShortcutAppId(Quote(ourExe), s.AppName)));
                        entry.Set("AppName", VdfNode.Str, s.AppName);
                        entry.Set("Exe", VdfNode.Str, Quote(ourExe));
                        entry.Set("StartDir", VdfNode.Str, Quote(Path.GetDirectoryName(ourExe)));
                        entry.Set("icon", VdfNode.Str, s.Icon ?? "");
                        entry.Set("ShortcutPath", VdfNode.Str, "");
                        entry.Set("LaunchOptions", VdfNode.Str, s.LaunchOptions);
                        entry.Set("IsHidden", VdfNode.Int, 0);
                        entry.Set("AllowDesktopConfig", VdfNode.Int, 0);
                        entry.Set("AllowOverlay", VdfNode.Int, 1);
                        entry.Set("OpenVR", VdfNode.Int, 0);
                        entry.Set("Devkit", VdfNode.Int, 0);
                        entry.Set("DevkitGameID", VdfNode.Str, "");
                        entry.Set("DevkitOverrideAppID", VdfNode.Int, 0);
                        entry.Set("LastPlayTime", VdfNode.Int, 0);
                        entry.Set("FlatpakAppID", VdfNode.Str, "");
                        entry.Set("tags", VdfNode.Map, new List<VdfNode> { new VdfNode(VdfNode.Str, "0", "Game Pass") });
                    }
                    else
                    {
                        entry.Set("LaunchOptions", VdfNode.Str, s.LaunchOptions);
                        if (!string.IsNullOrEmpty(s.Icon)) entry.Set("icon", VdfNode.Str, s.Icon);
                        // Desktop config must stay off: the game window isn't a child of our process,
                        // so Steam would otherwise think a "launcher" is focused and swap to mouse/keyboard.
                        entry.Set("AllowDesktopConfig", VdfNode.Int, 0);
                        entry.Set("AllowOverlay", VdfNode.Int, 1);
                    }
                    changed++;
                }

                // Steam expects the entries keyed "0".."n-1".
                for (int i = 0; i < root.Children.Count; i++) root.Children[i].Key = i.ToString();
                root.Save(file);
            }
            return changed;
        }

        public static List<string> ExistingOurAppNames()
        {
            var names = new List<string>();
            foreach (var e in OurEntries())
            {
                var n = AppName(e);
                if (n != null && !names.Contains(n)) names.Add(n);
            }
            return names;
        }

        // True if some of our shortcuts point at a different copy of the exe (e.g. one run from Downloads).
        public static bool ShortcutsPointElsewhere(string ourExe)
        {
            foreach (var e in OurEntries())
                if (!string.Equals(ExePathOf(e), ourExe, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        static IEnumerable<VdfNode> OurEntries()
        {
            if (!IsInstalled) yield break;
            foreach (var file in ShortcutFiles())
            {
                if (!File.Exists(file)) continue;
                foreach (var e in VdfNode.Load(file).Children)
                    if (IsOurs(e)) yield return e;
            }
        }

        static bool IsOurs(VdfNode entry)
        {
            if (entry.Type != VdfNode.Map) return false;
            var exe = ExePathOf(entry);
            if (!exe.EndsWith("\\SteamPassBridge.exe", StringComparison.OrdinalIgnoreCase) &&
                !exe.Equals("SteamPassBridge.exe", StringComparison.OrdinalIgnoreCase)) return false;
            var opts = (entry.GetString("LaunchOptions") ?? "").Trim();
            return opts.StartsWith("launch ", StringComparison.OrdinalIgnoreCase) || opts == "test" || opts.StartsWith("probe");
        }

        static void Point(VdfNode entry, string ourExe)
        {
            entry.Set("Exe", VdfNode.Str, Quote(ourExe));
            entry.Set("StartDir", VdfNode.Str, Quote(Path.GetDirectoryName(ourExe)));
        }

        static string ExePathOf(VdfNode e)
        {
            return (e.GetString("Exe") ?? e.GetString("exe") ?? "").Trim().Trim('"').Replace('/', '\\');
        }

        static string AppName(VdfNode e) { return e.GetString("AppName") ?? e.GetString("appname"); }

        static string Quote(string s) { return "\"" + s + "\""; }

        static uint Crc32(byte[] data)
        {
            uint crc = 0xFFFFFFFF;
            foreach (byte b in data)
            {
                crc ^= b;
                for (int k = 0; k < 8; k++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
            }
            return ~crc;
        }
    }
}
