using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using Microsoft.Win32;

namespace SteamPassBridge
{
    // Per-user install (no admin needed): copies the exe to a stable location so Steam shortcuts
    // keep working after the download is deleted, and registers an entry in "Installed apps".
    static class Setup
    {
        static readonly string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\SteamPassBridge" + (Sandbox.On ? "-Sandbox" : "");

        public static string InstallDir
        {
            get { return Path.Combine(Sandbox.On ? Sandbox.Root : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "SteamPassBridge"); }
        }

        public static string InstalledExe { get { return Path.Combine(InstallDir, "SteamPassBridge.exe"); } }

        public static bool IsInstalled { get { return File.Exists(InstalledExe); } }

        public static bool RunningFromInstallDir
        {
            get { return string.Equals(Program.ExePath, InstalledExe, StringComparison.OrdinalIgnoreCase); }
        }

        static string StartMenuShortcut
        {
            get { return Path.Combine(Sandbox.On ? Path.Combine(Sandbox.Root, "StartMenu") : Environment.GetFolderPath(Environment.SpecialFolder.Programs), "SteamPassBridge.lnk"); }
        }

        public static void Install()
        {
            if (RunningFromInstallDir) return;
            if (Program.InstancesOf(InstalledExe) > 0)
                throw new Exception("The installed SteamPassBridge is running (maybe a game is open). Close it first, then try again.");

            Directory.CreateDirectory(InstallDir);
            Directory.CreateDirectory(Path.GetDirectoryName(StartMenuShortcut));
            File.Copy(Program.ExePath, InstalledExe, true);
            foreach (var extra in new[] { "README.md", "LICENSE.txt", "THIRD-PARTY-NOTICES.txt" })
            {
                var src = Path.Combine(Program.ExeDir, extra);
                if (File.Exists(src)) File.Copy(src, Path.Combine(InstallDir, extra), true);
            }

            CreateShortcut(StartMenuShortcut, InstalledExe);

            using (var k = Registry.CurrentUser.CreateSubKey(UninstallKey))
            {
                k.SetValue("DisplayName", "SteamPassBridge");
                k.SetValue("DisplayVersion", Program.Version);
                k.SetValue("Publisher", "SteamPassBridge");
                k.SetValue("DisplayIcon", InstalledExe);
                k.SetValue("InstallLocation", InstallDir);
                k.SetValue("UninstallString", "\"" + InstalledExe + "\" uninstall");
                k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                k.SetValue("EstimatedSize", (int)(new FileInfo(InstalledExe).Length / 1024) + 1, RegistryValueKind.DWord);
            }
            Program.Log("installed " + Program.Version + " to " + InstallDir);
        }

        // Removes our Steam shortcuts, the Start menu entry, the "Installed apps" entry and the install folder.
        public static void Uninstall()
        {
            if (Steam.IsInstalled && Steam.ExistingOurAppNames().Count > 0)
            {
                bool wasRunning = Steam.IsRunning;
                Steam.Shutdown();
                Steam.WriteShortcuts(new ShortcutSpec[0], null, Program.ExePath, true);
                if (wasRunning) Steam.Start();
            }
            try { File.Delete(StartMenuShortcut); } catch { }
            Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false);

            if (Directory.Exists(InstallDir))
            {
                // We may be running from the folder we're deleting, so let cmd remove it after we exit.
                var psi = new ProcessStartInfo("cmd.exe", "/c ping 127.0.0.1 -n 3 >nul & rmdir /s /q \"" + InstallDir + "\"")
                { CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden };
                Process.Start(psi);
            }
        }

        static void CreateShortcut(string lnkPath, string target)
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            object shell = Activator.CreateInstance(shellType);
            object lnk = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { lnkPath });
            var t = lnk.GetType();
            t.InvokeMember("TargetPath", BindingFlags.SetProperty, null, lnk, new object[] { target });
            t.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, lnk, new object[] { Path.GetDirectoryName(target) });
            t.InvokeMember("Description", BindingFlags.SetProperty, null, lnk, new object[] { "Play Game Pass games with a Steam Controller" });
            t.InvokeMember("Save", BindingFlags.InvokeMethod, null, lnk, null);
        }
    }
}
