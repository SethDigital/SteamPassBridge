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

        const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        public static uint Activate(string aumid)
        {
            var mgr = (IApplicationActivationManager)new ApplicationActivationManager();
            uint pid;
            int hr = mgr.ActivateApplication(aumid, null, 0, out pid);
            if (hr < 0) throw new Exception("Windows could not start " + aumid + " (HRESULT 0x" + hr.ToString("X8") + ")");
            return pid;
        }

        // True if any process belonging to the package is alive. Works for gamelaunchhelper and the real game exe alike.
        public static bool IsPackageRunning(string familyName)
        {
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, p.Id);
                    if (h == IntPtr.Zero) continue;
                    try
                    {
                        uint len = 256;
                        var sb = new StringBuilder((int)len);
                        if (GetPackageFamilyName(h, ref len, sb) == 0 &&
                            string.Equals(sb.ToString(), familyName, StringComparison.OrdinalIgnoreCase))
                            return true;
                    }
                    finally { CloseHandle(h); }
                }
                finally { p.Dispose(); }
            }
            return false;
        }

        // Blocks until the game has started and then fully exited, so Steam keeps treating it as "running".
        public static void WaitForGame(string familyName, TimeSpan startTimeout)
        {
            var deadline = DateTime.Now + startTimeout;
            while (!IsPackageRunning(familyName))
            {
                if (DateTime.Now > deadline) { Program.Log("game never appeared: " + familyName); return; }
                Thread.Sleep(1000);
            }
            Program.Log("game is running");

            // Launch helpers hand off to the real exe, so require several consecutive empty checks.
            int misses = 0;
            while (misses < 4)
            {
                Thread.Sleep(1500);
                misses = IsPackageRunning(familyName) ? 0 : misses + 1;
            }
            Program.Log("game exited");
        }
    }
}
