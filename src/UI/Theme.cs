using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace SteamPassBridge
{
    sealed class Palette
    {
        public bool Dark;
        public Color Bg, Band, Tile, TileHover, Line, Text, Muted, Accent, AccentHover, OnAccent, Warn, Danger, TrackOff, KnobOff;
    }

    // Follows the Windows app theme (Settings > Personalization > Colors) and re-themes live when it changes.
    static class Theme
    {
        public static Palette P { get; private set; }
        public static event EventHandler Changed;

        public static readonly Palette DarkPalette = new Palette
        {
            Dark = true,
            Bg = Hex(0x0B0E13), Band = Hex(0x10141B), Tile = Hex(0x151A22), TileHover = Hex(0x1C222C), Line = Hex(0x252C38),
            Text = Hex(0xEAEFF5), Muted = Hex(0x8C96A8),
            Accent = Hex(0x3BE38F), AccentHover = Hex(0x62EBA7), OnAccent = Hex(0x03140B),
            Warn = Hex(0xF5B849), Danger = Hex(0xFF6B6B), TrackOff = Hex(0x2C3441), KnobOff = Hex(0x8C96A8),
        };

        public static readonly Palette LightPalette = new Palette
        {
            Dark = false,
            Bg = Hex(0xF2F4F7), Band = Hex(0xE9EDF2), Tile = Hex(0xFFFFFF), TileHover = Hex(0xF7F9FB), Line = Hex(0xD9DFE7),
            Text = Hex(0x0F141A), Muted = Hex(0x566173),
            Accent = Hex(0x087A4D), AccentHover = Hex(0x066841), OnAccent = Hex(0xFFFFFF),
            Warn = Hex(0x8F5B00), Danger = Hex(0xC62828), TrackOff = Hex(0xC5CDD8), KnobOff = Hex(0xFFFFFF),
        };

        public static readonly float Scale;
        public static readonly Font Title, Heading, Body, Strong, Small, Mono;
        static readonly string IconFamily;

        static Theme()
        {
            using (var g = Graphics.FromHwnd(IntPtr.Zero)) Scale = g.DpiX / 96f;
            var installed = new InstalledFontCollection();
            Func<string, bool> has = name => Array.Exists(installed.Families, f => f.Name == name);
            string display = has("Segoe UI Variable Display") ? "Segoe UI Variable Display" : "Segoe UI";
            string text = has("Segoe UI Variable Text") ? "Segoe UI Variable Text" : "Segoe UI";
            string small = has("Segoe UI Variable Small") ? "Segoe UI Variable Small" : "Segoe UI";
            IconFamily = has("Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";

            Title = new Font(display, 17f, FontStyle.Bold);
            Heading = new Font(display, 12f, FontStyle.Bold);
            Body = new Font(text, 10f);
            Strong = new Font(text, 10.5f, FontStyle.Bold);
            Small = new Font(small, 9f);
            Mono = new Font("Consolas", 9f);

            P = WindowsIsDark() ? DarkPalette : LightPalette;
            SystemEvents.UserPreferenceChanged += (s, e) =>
            {
                var next = WindowsIsDark() ? DarkPalette : LightPalette;
                if (next == P) return;
                P = next;
                if (Changed != null) Changed(null, EventArgs.Empty);
            };
        }

        static bool WindowsIsDark()
        {
            var forced = Environment.GetEnvironmentVariable("SPB_THEME"); // "light" / "dark" for testing both looks
            if (forced == "light") return false;
            if (forced == "dark") return true;
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    var v = k == null ? null : k.GetValue("AppsUseLightTheme");
                    return v is int && (int)v == 0;
                }
            }
            catch { return false; }
        }

        // SPB_OFFSCREEN=1 opens windows off-screen without taking focus, for automated screenshots.
        public static readonly bool Offscreen = Environment.GetEnvironmentVariable("SPB_OFFSCREEN") == "1";

        public static void PlaceWindow(Form f)
        {
            if (!Offscreen) { f.StartPosition = FormStartPosition.CenterScreen; return; }
            f.StartPosition = FormStartPosition.Manual;
            f.Location = new Point(-6000, 50);
            f.ShowInTaskbar = false;
        }

        public static int S(float px) { return (int)Math.Round(px * Scale); }

        static Color Hex(int rgb) { return Color.FromArgb(255, (rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF); }

        public static Color Mix(Color a, Color b, float t)
        {
            t = Math.Max(0f, Math.Min(1f, t));
            return Color.FromArgb(
                (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
        }

        public static Font Icon(float points) { return new Font(IconFamily, points); }

        // Segoe Fluent Icons code points used across the app.
        public const string GlyphGamepad = "", GlyphCheck = "", GlyphCompleted = "", GlyphWarning = "",
            GlyphError = "", GlyphRefresh = "", GlyphSelectAll = "", GlyphMore = "", GlyphDownload = "",
            GlyphInfo = "", GlyphRestart = "", GlyphVibrate = "", GlyphLink = "";

        // ---- drawing helpers ----

        public static GraphicsPath Round(RectangleF r, float radius)
        {
            var p = new GraphicsPath();
            float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            if (d <= 0.5f) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static void Fill(Graphics g, RectangleF r, float radius, Color c)
        {
            using (var path = Round(r, radius)) using (var b = new SolidBrush(c)) g.FillPath(b, path);
        }

        public static void Stroke(Graphics g, RectangleF r, float radius, Color c, float width)
        {
            using (var path = Round(r, radius)) using (var pen = new Pen(c, width)) g.DrawPath(pen, path);
        }

        public static void Text(Graphics g, string s, Font f, Color c, Rectangle r, TextFormatFlags flags)
        {
            TextRenderer.DrawText(g, s, f, r, c, flags | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
        }

        public static void Glyph(Graphics g, string glyph, float points, Color c, Rectangle r)
        {
            using (var f = Icon(points))
                TextRenderer.DrawText(g, glyph, f, r, c, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        public static Size Measure(string s, Font f)
        {
            return TextRenderer.MeasureText(s, f, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
        }

        // Switch used by the game list and the controller test. pos: 0 = off, 1 = on (animated in between).
        public static void Toggle(Graphics g, Rectangle r, float pos, bool enabled)
        {
            var track = Mix(P.TrackOff, P.Accent, pos);
            if (!enabled) track = Mix(track, P.Tile, 0.5f);
            Fill(g, r, r.Height / 2f, track);
            float pad = S(3), knob = r.Height - pad * 2;
            float x = r.X + pad + (r.Width - pad * 2 - knob) * pos;
            var knobColor = Mix(P.KnobOff, P.OnAccent, pos);
            using (var b = new SolidBrush(knobColor)) g.FillEllipse(b, x, r.Y + pad, knob, knob);
        }

        public static void Smooth(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        }

        // ---- window chrome ----

        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        // Dark/light title bar whose caption colour matches the window, so the header reads as one surface.
        public static void ApplyChrome(Form f)
        {
            if (!f.IsHandleCreated) return;
            int dark = P.Dark ? 1 : 0;
            DwmSetWindowAttribute(f.Handle, 20, ref dark, 4);          // DWMWA_USE_IMMERSIVE_DARK_MODE
            int caption = ColorTranslator.ToWin32(P.Bg);
            DwmSetWindowAttribute(f.Handle, 35, ref caption, 4);       // DWMWA_CAPTION_COLOR (Windows 11)
            int text = ColorTranslator.ToWin32(P.Text);
            DwmSetWindowAttribute(f.Handle, 36, ref text, 4);          // DWMWA_TEXT_COLOR
            f.BackColor = P.Bg;
            f.Invalidate(true);
        }
    }
}
