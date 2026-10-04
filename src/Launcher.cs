using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace SteamPassBridge
{
    static class Launcher
    {
        [ComImport, Guid("2e941141-7f97-4756-ba1d-9decde894a3d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IApplicationActivationManager
        {
            [PreserveSig] int ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
                [MarshalAs(UnmanagedType.LPWStr)] string arguments, int options, out uint processId);
        }

        [ComImport, Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")]
        class ApplicationActivationManager { }

        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern int GetPackageFamilyName(IntPtr hProcess, ref uint length, StringBuilder name);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageName(IntPtr hProcess, int flags, StringBuilder name, ref int size);

        const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        public static void Start(Game game)
        {
            if (game.Store == Store.Epic) EpicGames.Launch(game);
            else Activate(game.Aumid);
        }

        public static uint Activate(string aumid)
        {
            var mgr = (IApplicationActivationManager)new ApplicationActivationManager();
            uint pid;
            int hr = mgr.ActivateApplication(aumid, null, 0, out pid);
            if (hr < 0) throw new Exception("Windows could not start " + aumid + " (HRESULT 0x" + hr.ToString("X8") + ")");
            return pid;
        }

        public static bool IsRunning(Game game)
        {
            return game.Store == Store.Epic ? IsRunningFrom(game.InstallDir) : IsPackageRunning(game.FamilyName);
        }

        // True if any process belonging to the package is alive. Works for gamelaunchhelper and the real game exe alike.
        public static bool IsPackageRunning(string familyName)
        {
            return AnyProcess(h =>
            {
                uint len = 256;
                var sb = new StringBuilder((int)len);
                return GetPackageFamilyName(h, ref len, sb) == 0 &&
                       string.Equals(sb.ToString(), familyName, StringComparison.OrdinalIgnoreCase);
            });
        }

        // True if any process runs an exe from inside the folder: bootstrappers, anti-cheat launchers and the game itself.
        public static bool IsRunningFrom(string dir)
        {
            var prefix = dir.TrimEnd('\\') + "\\";
            return AnyProcess(h =>
            {
                int len = 1024;
                var sb = new StringBuilder(len);
                return QueryFullProcessImageName(h, 0, sb, ref len) &&
                       sb.ToString().StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
            });
        }

        static bool AnyProcess(Func<IntPtr, bool> match)
        {
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, p.Id);
                    if (h == IntPtr.Zero) continue;
                    try { if (match(h)) return true; }
                    finally { CloseHandle(h); }
                }
                finally { p.Dispose(); }
            }
            return false;
        }

        // How long to wait for the game to appear. The Epic launcher may have to start, sign in or sync cloud saves first.
        public static TimeSpan StartTimeout(Game game)
        {
            return TimeSpan.FromMinutes(game.Store == Store.Epic ? 5 : 3);
        }

        // Blocks until the game has started and then fully exited, so Steam keeps treating it as "running".
        public static void WaitForGame(Game game)
        {
            var deadline = DateTime.Now + StartTimeout(game);
            while (!IsRunning(game))
            {
                if (DateTime.Now > deadline) { Program.Log("game never appeared: " + game.Id); return; }
                Thread.Sleep(1000);
            }
            Program.Log("game is running");

            // Launch helpers hand off to the real exe, so require several consecutive empty checks.
            int misses = 0;
            while (misses < 4)
            {
                Thread.Sleep(1500);
                misses = IsRunning(game) ? 0 : misses + 1;
            }
            Program.Log("game exited");
        }
    }
}
