using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml.Linq;
using Microsoft.Win32;

namespace SteamPassBridge
{
    enum Store { GamePass, Epic }

    class Game
    {
        public Store Store;
        public string Name;          // display name, e.g. "Crimson Desert"
        public string Id;            // what `launch` takes: the package family name, or "epic:<AppName>"
        public string InstallDir;
        public string Version;
        public string LogoPath;      // square tile art, may be null
        public string BigLogoPath;   // largest square logo (up to 480x480) for the icon and capsule
        public string SplashPath;    // landscape key art, used for hero/capsule backgrounds
        public string PortraitPath;  // portrait key art for the capsule, may be null (Epic only)
        public string LogoBackground; // "#rrggbb" tile colour behind transparent logos, may be null

        // Game Pass
        public string FamilyName;    // e.g. PearlAbyss.CrimsonDesert_bs8qf1ajnj9cw
        public string AppId;         // Application Id from AppxManifest
        public string Aumid { get { return FamilyName + "!" + AppId; } }

        // Epic Games Store
        public string EpicAppName, EpicNamespace, EpicCatalogItemId;

        public string StoreName { get { return Store == Store.Epic ? "Epic Games" : "Game Pass"; } }
        public override string ToString() { return Name; }
    }

    static class Games
    {
        const string RepoKey = @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";

        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
        static extern int SHLoadIndirectString(string source, StringBuilder outBuf, int cch, IntPtr reserved);

        // Every supported game: Game Pass packages plus Epic Games Store installs.
        public static List<Game> Find()
        {
            var all = FindGamePass();
            var names = new HashSet<string>(all.Select(g => g.Name), StringComparer.CurrentCultureIgnoreCase);
            List<Game> epic;
            try { epic = EpicGames.Find(); }
            catch (Exception ex) { Program.Log("skipping Epic games: " + ex.Message); epic = new List<Game>(); }
            foreach (var g in epic)
            {
                // Our Steam shortcuts are matched by name, so a game installed from both stores needs its own.
                if (names.Contains(g.Name)) g.Name += " (Epic)";
                names.Add(g.Name);
                all.Add(g);
            }
            return all.OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        public static Game ById(string id)
        {
            return Find().FirstOrDefault(g => string.Equals(g.Id, id, StringComparison.OrdinalIgnoreCase));
        }

        // Game Pass / Microsoft Store PC games are the packages that carry a MicrosoftGame.config (GDK titles).
        static List<Game> FindGamePass()
        {
            var byFamily = new Dictionary<string, Game>(StringComparer.OrdinalIgnoreCase);
            using (var repo = Registry.CurrentUser.OpenSubKey(RepoKey))
            {
                if (repo == null) return new List<Game>();
                foreach (var fullName in repo.GetSubKeyNames())
                {
                    try
                    {
                        using (var k = repo.OpenSubKey(fullName))
                        {
                            var root = k.GetValue("PackageRootFolder") as string;
                            if (string.IsNullOrEmpty(root)) continue;
                            if (!File.Exists(Path.Combine(root, "MicrosoftGame.config"))) continue;
                            var manifest = Path.Combine(root, "AppxManifest.xml");
                            if (!File.Exists(manifest)) continue;

                            var doc = XDocument.Load(manifest);
                            var app = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Application");
                            if (app == null || app.Attribute("Id") == null) continue; // DLC / content-only package

                            var parts = fullName.Split('_'); // Name_Version_Arch_ResourceId_PublisherId
                            if (parts.Length < 5) continue;
                            var family = parts[0] + "_" + parts[parts.Length - 1];
                            var game = new Game
                            {
                                Store = Store.GamePass,
                                Id = family,
                                FamilyName = family,
                                Version = parts[1],
                                AppId = app.Attribute("Id").Value,
                                InstallDir = root,
                                Name = ResolveName(k.GetValue("DisplayName") as string, root, parts[0]),
                            };
                            ReadVisuals(game, root);

                            Game existing;
                            if (!byFamily.TryGetValue(game.FamilyName, out existing) || NewerThan(game.Version, existing.Version))
                                byFamily[game.FamilyName] = game;
                        }
                    }
                    catch (Exception ex) { Program.Log("skipping package " + fullName + ": " + ex.Message); }
                }
            }
            return byFamily.Values.ToList();
        }

        static string ResolveName(string registryName, string root, string fallback)
        {
            if (!string.IsNullOrEmpty(registryName))
            {
                if (!registryName.StartsWith("@")) return registryName;
                var sb = new StringBuilder(512);
                if (SHLoadIndirectString(registryName, sb, sb.Capacity, IntPtr.Zero) == 0 && sb.Length > 0) return sb.ToString();
            }
            try
            {
                var cfg = XDocument.Load(Path.Combine(root, "MicrosoftGame.config"));
                var sv = cfg.Descendants().FirstOrDefault(e => e.Name.LocalName == "ShellVisuals");
                var n = sv == null ? null : sv.Attribute("DefaultDisplayName");
                if (n != null && !n.Value.StartsWith("ms-resource")) return n.Value;
            }
            catch { }
            return fallback;
        }

        static void ReadVisuals(Game game, string root)
        {
            try
            {
                var cfg = XDocument.Load(Path.Combine(root, "MicrosoftGame.config"));
                var sv = cfg.Descendants().FirstOrDefault(e => e.Name.LocalName == "ShellVisuals");
                if (sv == null) return;
                var bg = sv.Attribute("BackgroundColor");
                if (bg != null && bg.Value.StartsWith("#") && bg.Value.Length == 7) game.LogoBackground = bg.Value;

                var splash = sv.Attribute("SplashScreenImage");
                if (splash != null) game.SplashPath = FindAsset(root, splash.Value);

                foreach (var attr in new[] { "Square150x150Logo", "StoreLogo", "Square44x44Logo" })
                {
                    var a = sv.Attribute(attr);
                    if (a == null) continue;
                    var path = FindAsset(root, a.Value);
                    if (path != null) { game.LogoPath = path; break; }
                }

                // Prefer a large square logo for the icon/capsule; packages ship these next to the manifest logos.
                var sq150 = sv.Attribute("Square150x150Logo");
                if (sq150 != null)
                {
                    var dir = Path.GetDirectoryName(Path.Combine(root, sq150.Value.Replace('/', '\\')));
                    game.BigLogoPath = LargestSquareLogo(dir) ?? game.LogoPath;
                }
                else game.BigLogoPath = game.LogoPath;
            }
            catch { }
        }

        // Resolves a manifest asset, including the "Name.scale-200.png" variants packages often ship instead.
        static string FindAsset(string root, string relative)
        {
            var path = Path.Combine(root, relative.Replace('/', '\\'));
            if (File.Exists(path)) return path;
            var dir = Path.GetDirectoryName(path);
            if (!Directory.Exists(dir)) return null;
            var stem = Path.GetFileNameWithoutExtension(path);
            return Directory.GetFiles(dir, stem + ".scale-*" + Path.GetExtension(path))
                .OrderByDescending(f => new FileInfo(f).Length).FirstOrDefault();
        }

        // Biggest square logo in the assets folder (Square480x480Logo, default480x480, scale-200 variants…).
        static string LargestSquareLogo(string dir)
        {
            if (dir == null || !Directory.Exists(dir)) return null;
            try
            {
                return Directory.GetFiles(dir, "*.png")
                    .Where(f => { var n = Path.GetFileNameWithoutExtension(f).ToLowerInvariant(); return n.Contains("480") || n.Contains("logo") || n.Contains("default"); })
                    .OrderByDescending(f => new FileInfo(f).Length)
                    .FirstOrDefault();
            }
            catch { return null; }
        }

        static bool NewerThan(string a, string b)
        {
            Version va, vb;
            return Version.TryParse(a, out va) && Version.TryParse(b, out vb) && va > vb;
        }
    }
}
