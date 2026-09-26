using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;

namespace SteamPassBridge
{
    // Installs the ViGEmBus driver from the copy embedded in this exe (see vendor\ and build.cmd).
    static class DriverSetup
    {
        public const string Version = "1.22.0";
        const string ResourceName = "ViGEmBusSetup.exe";
        const string Sha256 = "89220A7865076B342892F98865F3499FB7C4CFD673159E89D352C360FD014C6A";

        public enum Result { Installed, RestartRequired, Cancelled }

        public static bool IsBundled
        {
            get { return typeof(DriverSetup).Assembly.GetManifestResourceInfo(ResourceName) != null; }
        }

        // Shows one UAC prompt, installs silently, then waits for the bus to appear. Blocks; call off the UI thread.
        public static Result Install()
        {
            string setup = Extract();
            Program.Log("installing ViGEmBus " + Version);
            Process p;
            try
            {
                p = Process.Start(new ProcessStartInfo(setup, "/exenoui /qn /norestart") { UseShellExecute = true, Verb = "runas" });
            }
            catch (Win32Exception ex)
            {
                if (ex.NativeErrorCode == 1223) { Program.Log("driver install: UAC declined"); return Result.Cancelled; }
                throw;
            }
            p.WaitForExit();
            int code = p.ExitCode;
            Program.Log("driver installer exit code " + code);
            TryDelete(setup);

            if (code == 1602 || code == 1223) return Result.Cancelled;
            // 1604: upgrading an older ViGEmBus removes it, then suspends until Windows restarts and resumes the
            // install (the MSI's ForceReboot step). 3010/1641: plain restart required.
            if (code == 1604 || code == 3010 || code == 1641) { MarkRestartPending(); return Result.RestartRequired; }
            if (code != 0) throw new Exception("The ViGEmBus driver installer failed (code " + code + ").");

            // The device interface can take a moment to show up after the installer returns.
            for (int i = 0; i < 30 && !ViGEmBus.IsInstalled; i++) Thread.Sleep(500);
            if (ViGEmBus.IsInstalled) return Result.Installed;
            MarkRestartPending();
            return Result.RestartRequired;
        }

        [DllImport("kernel32.dll")] static extern ulong GetTickCount64();

        static string PendingMarker
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SteamPassBridge", "driver-restart-pending"); }
        }

        static void MarkRestartPending()
        {
            try { Directory.CreateDirectory(Path.GetDirectoryName(PendingMarker)); File.WriteAllText(PendingMarker, DateTime.Now.ToString("o")); }
            catch { }
        }

        // True between a driver install that needs a restart and the next Windows boot.
        public static bool RestartPending
        {
            get
            {
                if (!File.Exists(PendingMarker)) return false;
                var bootTime = DateTime.Now - TimeSpan.FromMilliseconds(GetTickCount64());
                if (File.GetLastWriteTime(PendingMarker) > bootTime && !ViGEmBus.IsInstalled) return true;
                TryDelete(PendingMarker);
                return false;
            }
        }

        static string Extract()
        {
            var dir = Path.Combine(Path.GetTempPath(), "SteamPassBridge");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "ViGEmBus_" + Version + "_setup.exe");
            using (var res = typeof(DriverSetup).Assembly.GetManifestResourceStream(ResourceName))
            {
                if (res == null) throw new Exception("This build doesn't include the ViGEmBus installer. Download it from " + ViGEmBus.DownloadUrl);
                using (var file = File.Create(path)) res.CopyTo(file);
            }
            using (var sha = SHA256.Create())
            using (var f = File.OpenRead(path))
            {
                var hash = BitConverter.ToString(sha.ComputeHash(f)).Replace("-", "");
                if (!hash.Equals(Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    f.Dispose(); TryDelete(path);
                    throw new Exception("The bundled driver installer failed its integrity check. Please re-download SteamPassBridge.");
                }
            }
            return path;
        }

        static void TryDelete(string path) { try { File.Delete(path); } catch { } }
    }
}
