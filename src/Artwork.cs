using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

namespace SteamPassBridge
{
    // Builds Steam library artwork for the non-Steam shortcuts from each game's own packaged art:
    // a portrait capsule, a hero banner and a landscape grid from the 1920x1080 splash, plus a
    // multi-size .ico from the square logo. Written into every Steam user's config\grid folder,
    // keyed by the shortcut's (unsigned) app id, the same scheme SteamGridDB tools use.
    static class Artwork
    {
        public static string ArtDir
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SteamPassBridge", "art"); }
        }

        // Returns the icon path to store in shortcuts.vdf, or null if nothing could be generated.
        public static string WriteForGame(GamePassGame game, uint appId, IList<string> gridDirs, bool force)
        {
            string icon = null;
            try
            {
                Directory.CreateDirectory(ArtDir);
                icon = Path.Combine(ArtDir, appId + ".ico");
                if (force || !File.Exists(icon))
                {
                    using (var logo = Load(game.BigLogoPath ?? game.LogoPath))
                        SaveIcon(logo, ParseColor(game.LogoBackground), icon);
                }

                using (var splash = Load(game.SplashPath))
                using (var logo = Load(game.BigLogoPath ?? game.LogoPath))
                {
                    var bg = ParseColor(game.LogoBackground);
                    foreach (var dir in gridDirs)
                    {
                        if (!Directory.Exists(dir)) { try { Directory.CreateDirectory(dir); } catch { continue; } }
                        Write(Path.Combine(dir, appId + "p.png"), force, () => Portrait(splash, logo, bg));
                        Write(Path.Combine(dir, appId + "_hero.png"), force, () => Cover(splash, 1920, 620, bg));
                        Write(Path.Combine(dir, appId + ".png"), force, () => Cover(splash, 920, 430, bg));
                    }
                }
            }
            catch (Exception ex) { Program.Log("artwork for " + game.Name + " failed: " + ex.Message); }
            return icon != null && File.Exists(icon) ? icon : null;
        }

        static void Write(string path, bool force, Func<Bitmap> make)
        {
            if (!force && File.Exists(path)) return;
            using (var bmp = make())
            {
                if (bmp == null) return;
                var tmp = path + ".tmp";
                bmp.Save(tmp, ImageFormat.Png);
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
            }
        }

        // Portrait capsule (600x900): full-bleed key art from the splash. When there's no splash, fall back to
        // a coloured panel with the centred logo so the capsule still reads.
        static Bitmap Portrait(Bitmap splash, Bitmap logo, Color bg)
        {
            int w = 600, h = 900;
            if (splash != null)
            {
                var bmp = Cover(splash, w, h, bg);
                using (var g = Graphics.FromImage(bmp))
                {
                    Smooth(g);
                    // Subtle bottom vignette so Steam's overlaid title stays readable over bright art.
                    using (var grad = new LinearGradientBrush(new Rectangle(0, h / 2, w, h - h / 2), Color.FromArgb(0, 0, 0, 0), Color.FromArgb(150, 0, 0, 0), 90f))
                        g.FillRectangle(grad, 0, h / 2, w, h - h / 2);
                }
                return bmp;
            }

            var panel = new Bitmap(w, h);
            using (var g = Graphics.FromImage(panel))
            {
                Smooth(g);
                using (var b = new LinearGradientBrush(new Rectangle(0, 0, w, h), Lighten(bg, 0.15f), Darken(bg, 0.4f), 90f))
                    g.FillRectangle(b, 0, 0, w, h);
                if (logo != null)
                {
                    int lw = (int)(w * 0.72f);
                    int lh = (int)(lw * (float)logo.Height / logo.Width);
                    if (lh > h * 0.6f) { lh = (int)(h * 0.6f); lw = (int)(lh * (float)logo.Width / logo.Height); }
                    g.DrawImage(logo, (w - lw) / 2, (h - lh) / 2, lw, lh);
                }
            }
            return panel;
        }

        // Scale-to-cover then centre-crop to w×h. Falls back to a flat colour when there's no splash.
        static Bitmap Cover(Bitmap src, int w, int h, Color bg)
        {
            var bmp = new Bitmap(w, h);
            using (var g = Graphics.FromImage(bmp))
            {
                Smooth(g);
                if (src == null) { using (var b = new SolidBrush(Darken(bg, 0.25f))) g.FillRectangle(b, 0, 0, w, h); return bmp; }
                float scale = Math.Max((float)w / src.Width, (float)h / src.Height);
                int sw = (int)Math.Ceiling(src.Width * scale), sh = (int)Math.Ceiling(src.Height * scale);
                g.DrawImage(src, (w - sw) / 2, (h - sh) / 2, sw, sh);
            }
            return bmp;
        }

        // ---- icon (.ico with PNG-compressed entries, Vista+) ----

        static readonly int[] IconSizes = { 16, 24, 32, 48, 64, 128, 256 };

        static void SaveIcon(Bitmap logo, Color bg, string path)
        {
            var pngs = new List<byte[]>();
            foreach (var size in IconSizes)
                using (var frame = IconFrame(logo, bg, size))
                using (var ms = new MemoryStream()) { frame.Save(ms, ImageFormat.Png); pngs.Add(ms.ToArray()); }

            using (var fs = File.Create(path))
            using (var w = new BinaryWriter(fs))
            {
                w.Write((ushort)0); w.Write((ushort)1); w.Write((ushort)IconSizes.Length);
                int offset = 6 + 16 * IconSizes.Length;
                for (int i = 0; i < IconSizes.Length; i++)
                {
                    int s = IconSizes[i];
                    w.Write((byte)(s >= 256 ? 0 : s)); w.Write((byte)(s >= 256 ? 0 : s));
                    w.Write((byte)0); w.Write((byte)0);
                    w.Write((ushort)1); w.Write((ushort)32);
                    w.Write((uint)pngs[i].Length); w.Write((uint)offset);
                    offset += pngs[i].Length;
                }
                foreach (var png in pngs) w.Write(png);
            }
        }

        static Bitmap IconFrame(Bitmap logo, Color bg, int size)
        {
            var bmp = new Bitmap(size, size);
            using (var g = Graphics.FromImage(bmp))
            {
                Smooth(g);
                if (logo == null)
                {
                    using (var b = new SolidBrush(bg.A == 0 ? Color.FromArgb(32, 36, 44) : bg)) g.FillRectangle(b, 0, 0, size, size);
                    return bmp;
                }
                // Opaque square logos already fill the frame; transparent ones keep their own shape.
                float inset = LooksTransparent(logo) ? 0f : 0f;
                g.DrawImage(logo, inset, inset, size - inset * 2, size - inset * 2);
            }
            return bmp;
        }

        static bool LooksTransparent(Bitmap b)
        {
            try { return b.GetPixel(0, 0).A < 32; } catch { return false; }
        }

        // ---- helpers ----

        static Bitmap Load(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            try { using (var ms = new MemoryStream(File.ReadAllBytes(path))) return new Bitmap(ms); }
            catch { return null; }
        }

        static void Smooth(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        }

        static Color ParseColor(string hex)
        {
            if (string.IsNullOrEmpty(hex)) return Color.FromArgb(32, 36, 44);
            try { return ColorTranslator.FromHtml(hex); } catch { return Color.FromArgb(32, 36, 44); }
        }

        static Color Darken(Color c, float t) { return Color.FromArgb(c.A == 0 ? 255 : c.A, (int)(c.R * (1 - t)), (int)(c.G * (1 - t)), (int)(c.B * (1 - t))); }
        static Color Lighten(Color c, float t) { return Color.FromArgb(255, (int)(c.R + (255 - c.R) * t), (int)(c.G + (255 - c.G) * t), (int)(c.B + (255 - c.B) * t)); }
    }
}
