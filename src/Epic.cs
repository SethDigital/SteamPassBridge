using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace SteamPassBridge
{
    // Epic Games Store games. Found from the launcher's install manifests, started through the launcher's
    // URI handler (so ownership checks, updates and cloud saves still happen), and given the same key art
    // the Epic library shows, from the image URLs listed in the launcher's own catalog cache.
    static class EpicGames
    {
        public const string IdPrefix = "epic:";

        // Tried in order; catalog items don't all carry the same image types.
        static readonly string[] WideTypes = { "DieselGameBox", "DieselStoreFrontWide", "OfferImageWide" };
        static readonly string[] TallTypes = { "DieselGameBoxTall", "DieselStoreFrontTall", "OfferImageTall", "Thumbnail" };

        static string DataDir
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Epic", "EpicGamesLauncher", "Data"); }
        }

        static string CacheDir { get { return Path.Combine(Artwork.ArtDir, "epic"); } }

        public static bool LauncherInstalled
        {
            get { using (var k = Registry.ClassesRoot.OpenSubKey(@"com.epicgames.launcher\shell\open\command")) return k != null; }
        }

        public static List<Game> Find()
        {
            var byApp = new Dictionary<string, Game>(StringComparer.OrdinalIgnoreCase);
            var dir = Path.Combine(DataDir, "Manifests");
            if (!Directory.Exists(dir)) return new List<Game>();
            foreach (var file in Directory.GetFiles(dir, "*.item"))
            {
                try
                {
                    var m = Parse(File.ReadAllText(file)) as Dictionary<string, object>;
                    if (m == null || Bool(m, "bIsIncompleteInstall") || !Bool(m, "bIsExecutable")) continue;
                    if (!Strings(m, "AppCategories").Contains("games", StringComparer.OrdinalIgnoreCase)) continue; // engine, plugins, add-ons
                    var appName = Str(m, "AppName");
                    var mainGame = Str(m, "MainGameAppName");
                    if (string.IsNullOrEmpty(appName) || (!string.IsNullOrEmpty(mainGame) && mainGame != appName)) continue; // DLC
                    var install = Str(m, "InstallLocation");
                    if (string.IsNullOrEmpty(install) || !Directory.Exists(install)) continue;

                    var game = new Game
                    {
                        Store = Store.Epic,
                        Id = IdPrefix + appName,
                        Name = Str(m, "DisplayName") ?? appName,
                        InstallDir = install.Replace('/', '\\').TrimEnd('\\'),
                        Version = Str(m, "AppVersionString"),
                        EpicAppName = appName,
                        EpicNamespace = Str(m, "CatalogNamespace"),
                        EpicCatalogItemId = Str(m, "CatalogItemId"),
                    };
                    var exe = Str(m, "LaunchExecutable");
                    if (!string.IsNullOrEmpty(exe)) game.LogoPath = game.BigLogoPath = IconFor(game, Path.Combine(game.InstallDir, exe.Replace('/', '\\')));
                    UseCachedArt(game);
                    byApp[appName] = game;
                }
                catch (Exception ex) { Program.Log("skipping Epic manifest " + Path.GetFileName(file) + ": " + ex.Message); }
            }
            return byApp.Values.ToList();
        }

        // ---- launching ----

        public static string LaunchUri(Game g)
        {
            return "com.epicgames.launcher://apps/" + Uri.EscapeDataString(g.EpicNamespace + ":" + g.EpicCatalogItemId + ":" + g.EpicAppName) +
                   "?action=launch&silent=true";
        }

        // Handed to Explorer rather than started directly, so a launcher that has to start up isn't our child:
        // Steam treats a shortcut's child processes as the game, and the launcher stays open after the game exits.
        public static void Launch(Game g)
        {
            if (!LauncherInstalled) throw new Exception("The Epic Games Launcher isn't installed, so " + g.Name + " can't start.");
            var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
            Process.Start(new ProcessStartInfo(explorer, "\"" + LaunchUri(g) + "\"") { UseShellExecute = false });
        }

        // ---- artwork ----

        // Downloads the game's key art once (or again when forced). Returns true if new art arrived.
        public static bool FetchArt(Game g, bool force)
        {
            if (g.Store != Store.Epic) return false;
            string wide = ArtPath(g, "wide"), tall = ArtPath(g, "tall");
            if (!force && File.Exists(wide) && File.Exists(tall)) return false;
            var images = KeyImages(g);
            bool got = false;
            if (force || !File.Exists(wide)) got |= Download(Pick(images, WideTypes), wide);
            if (force || !File.Exists(tall)) got |= Download(Pick(images, TallTypes), tall);
            UseCachedArt(g);
            return got;
        }

        static void UseCachedArt(Game g)
        {
            string wide = ArtPath(g, "wide"), tall = ArtPath(g, "tall");
            g.SplashPath = File.Exists(wide) ? wide : null;
            g.PortraitPath = File.Exists(tall) ? tall : null;
        }

        static string ArtPath(Game g, string kind) { return Path.Combine(CacheDir, SafeName(g.EpicAppName) + "-" + kind + ".img"); }

        static string SafeName(string s)
        {
            foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return s;
        }

        static string Pick(Dictionary<string, string> images, string[] types)
        {
            string url;
            foreach (var t in types) if (images.TryGetValue(t, out url)) return url;
            return null;
        }

        static List<Dictionary<string, object>> catalog;
        static DateTime catalogStamp;

        // The launcher keeps its catalog as base64-encoded JSON: one item per owned game, with keyImages.
        static Dictionary<string, string> KeyImages(Game g)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var file = Path.Combine(DataDir, "Catalog", "catcache.bin");
            var stamp = File.Exists(file) ? File.GetLastWriteTimeUtc(file) : DateTime.MinValue;
            if (catalog == null || stamp != catalogStamp) // re-read after the launcher updates it (a newly installed game)
            {
                catalog = new List<Dictionary<string, object>>();
                catalogStamp = stamp;
                try
                {
                    if (File.Exists(file))
                    {
                        var json = Encoding.UTF8.GetString(Convert.FromBase64String(File.ReadAllText(file).Trim()));
                        var items = Parse(json) as object[];
                        if (items != null) catalog.AddRange(items.OfType<Dictionary<string, object>>());
                    }
                }
                catch (Exception ex) { Program.Log("Epic catalog unreadable: " + ex.Message); }
            }
            var item = catalog.FirstOrDefault(i => Str(i, "id") == g.EpicCatalogItemId && Str(i, "namespace") == g.EpicNamespace)
                    ?? catalog.FirstOrDefault(i => Str(i, "id") == g.EpicCatalogItemId);
            object images;
            if (item == null || !item.TryGetValue("keyImages", out images) || !(images is object[])) return result;
            foreach (var img in ((object[])images).OfType<Dictionary<string, object>>())
            {
                string type = Str(img, "type"), url = Str(img, "url");
                if (type != null && url != null && url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) && !result.ContainsKey(type))
                    result[type] = url;
            }
            return result;
        }

        static bool Download(string url, string path)
        {
            if (url == null) return false;
            try
            {
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.Timeout = req.ReadWriteTimeout = 20000;
                req.UserAgent = "SteamPassBridge/" + Program.Version;
                byte[] data;
                using (var resp = req.GetResponse())
                using (var s = resp.GetResponseStream())
                using (var ms = new MemoryStream()) { s.CopyTo(ms); data = ms.ToArray(); }
                using (var check = new MemoryStream(data)) using (Image.FromStream(check)) { } // must be a real image

                Directory.CreateDirectory(CacheDir);
                var tmp = path + ".tmp";
                File.WriteAllBytes(tmp, data);
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
                return true;
            }
            catch (Exception ex) { Program.Log("Epic art download failed (" + url + "): " + ex.Message); return false; }
        }

        // ---- the game's own icon, for the list tile and the Steam shortcut icon ----

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern uint PrivateExtractIcons(string file, int index, int cx, int cy, IntPtr[] icons, uint[] ids, uint count, uint flags);
        [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);
        [DllImport("user32.dll")] static extern bool GetIconInfo(IntPtr hIcon, out IconInfo info);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr h);
        [DllImport("gdi32.dll")] static extern int GetObject(IntPtr h, int size, ref BitmapHeader bm);
        [DllImport("gdi32.dll")] static extern int GetDIBits(IntPtr hdc, IntPtr hbm, uint start, uint lines, byte[] bits, ref BitmapInfoHeader bmi, uint usage);
        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

        [StructLayout(LayoutKind.Sequential)]
        struct IconInfo { public bool IsIcon; public int HotX, HotY; public IntPtr Mask, Color; }

        [StructLayout(LayoutKind.Sequential)]
        struct BitmapHeader { public int Type, Width, Height, WidthBytes; public short Planes, BitsPixel; public IntPtr Bits; }

        [StructLayout(LayoutKind.Sequential)]
        struct BitmapInfoHeader { public int Size, Width, Height; public short Planes, BitCount; public int Compression, SizeImage, XPerMeter, YPerMeter, ClrUsed, ClrImportant; }

        // Extracted once into the art cache as a 256px PNG.
        static string IconFor(Game g, string exe)
        {
            var path = Path.Combine(CacheDir, SafeName(g.EpicAppName) + "-icon.png");
            if (File.Exists(path)) return path;
            if (!File.Exists(exe)) return null;
            var handles = new IntPtr[1];
            uint n = PrivateExtractIcons(exe, 0, 256, 256, handles, new uint[1], 1, 0);
            if (n == 0 || n == uint.MaxValue || handles[0] == IntPtr.Zero) return null;
            try
            {
                using (var bmp = IconBitmap(handles[0]))
                {
                    Directory.CreateDirectory(CacheDir);
                    bmp.Save(path, ImageFormat.Png);
                }
                return path;
            }
            catch (Exception ex) { Program.Log("icon for " + g.Name + " failed: " + ex.Message); return null; }
            finally { DestroyIcon(handles[0]); }
        }

        // Icon.ToBitmap drops the alpha channel of handle-based icons, so read the colour bitmap's bits directly.
        static Bitmap IconBitmap(IntPtr hIcon)
        {
            IconInfo info;
            if (!GetIconInfo(hIcon, out info)) throw new Exception("GetIconInfo failed");
            try
            {
                var bm = new BitmapHeader();
                if (info.Color != IntPtr.Zero && GetObject(info.Color, Marshal.SizeOf(bm), ref bm) != 0 && bm.Width > 0 && bm.Height > 0)
                {
                    // Ask for 32bpp top-down rows so the layout matches a Format32bppArgb bitmap exactly.
                    int w = bm.Width, h = bm.Height;
                    var bmi = new BitmapInfoHeader { Size = Marshal.SizeOf(typeof(BitmapInfoHeader)), Width = w, Height = -h, Planes = 1, BitCount = 32 };
                    var bytes = new byte[w * h * 4];
                    var dc = GetDC(IntPtr.Zero);
                    int lines;
                    try { lines = GetDIBits(dc, info.Color, 0, (uint)h, bytes, ref bmi, 0); }
                    finally { ReleaseDC(IntPtr.Zero, dc); }

                    bool hasAlpha = false;
                    for (int i = 3; i < bytes.Length && !hasAlpha; i += 4) hasAlpha = bytes[i] != 0;
                    if (lines == h && hasAlpha)
                    {
                        var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
                        var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                        try { for (int y = 0; y < h; y++) Marshal.Copy(bytes, y * w * 4, data.Scan0 + y * data.Stride, w * 4); }
                        finally { bmp.UnlockBits(data); }
                        return bmp;
                    }
                }
                using (var icon = Icon.FromHandle(hIcon)) return icon.ToBitmap(); // old icons without alpha: mask-based
            }
            finally
            {
                if (info.Color != IntPtr.Zero) DeleteObject(info.Color);
                if (info.Mask != IntPtr.Zero) DeleteObject(info.Mask);
            }
        }

        // ---- JSON helpers ----

        static object Parse(string json) { return new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(json); }

        static string Str(Dictionary<string, object> m, string key)
        {
            object v;
            return m.TryGetValue(key, out v) && v is string && ((string)v).Length > 0 ? (string)v : null;
        }

        static bool Bool(Dictionary<string, object> m, string key)
        {
            object v;
            return m.TryGetValue(key, out v) && v is bool && (bool)v;
        }

        static List<string> Strings(Dictionary<string, object> m, string key)
        {
            object v;
            return m.TryGetValue(key, out v) && v is IEnumerable && !(v is string) ? ((IEnumerable)v).OfType<string>().ToList() : new List<string>();
        }
    }
}
