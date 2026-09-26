using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SteamPassBridge
{
    // Owner-drawn list of Game Pass games: tile art, name, Steam status and a switch per game.
    class GameList : ScrollableControl
    {
        class Row
        {
            public GamePassGame Game;
            public bool InSteam, On;
            public float Knob, Glow;
            public Image Logo;
            public Color LogoBg;
        }

        readonly List<Row> rows = new List<Row>();
        readonly Timer anim = new Timer { Interval = 15 };
        int hover = -1, focus;

        public event EventHandler Changed;

        int RowH { get { return Theme.S(68); } }
        int Gap { get { return Theme.S(8); } }

        public GameList()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            AutoScroll = true;
            TabStop = true;
            AccessibleName = "Game Pass games";
            AccessibleRole = AccessibleRole.List;
            anim.Tick += delegate { Animate(); };
        }

        public int Count { get { return rows.Count; } }
        public int InSteamCount { get { return rows.Count(r => r.InSteam); } }
        public List<GamePassGame> Chosen { get { return rows.Where(r => r.On).Select(r => r.Game).ToList(); } }
        public List<string> ToAdd { get { return rows.Where(r => r.On && !r.InSteam).Select(r => r.Game.Name).ToList(); } }
        public List<string> ToRemove { get { return rows.Where(r => !r.On && r.InSteam).Select(r => r.Game.Name).ToList(); } }
        public bool AllOn { get { return rows.Count > 0 && rows.All(r => r.On); } }

        public void SetGames(IList<GamePassGame> games, ICollection<string> inSteam)
        {
            foreach (var r in rows) if (r.Logo != null) r.Logo.Dispose();
            rows.Clear();
            foreach (var g in games)
            {
                bool on = inSteam.Contains(g.Name);
                rows.Add(new Row { Game = g, InSteam = on, On = on, Knob = on ? 1 : 0, Logo = LoadLogo(g.LogoPath), LogoBg = ParseColor(g.LogoBackground) });
            }
            focus = Math.Min(focus, Math.Max(0, rows.Count - 1));
            hover = -1;
            UpdateScrollSize();
            Invalidate();
        }

        public void SetAll(bool on)
        {
            foreach (var r in rows) r.On = on;
            anim.Start();
            Invalidate();
            if (Changed != null) Changed(this, EventArgs.Empty);
        }

        // After a successful save: rows that just landed in Steam glow briefly in the accent colour.
        public void MarkSaved()
        {
            foreach (var r in rows)
            {
                if (r.On && !r.InSteam) r.Glow = 1f;
                r.InSteam = r.On;
            }
            anim.Start();
            Invalidate();
        }

        void Toggle(int i)
        {
            if (i < 0 || i >= rows.Count || !Enabled) return;
            rows[i].On = !rows[i].On;
            anim.Start();
            Invalidate();
            AccessibilityNotifyClients(AccessibleEvents.StateChange, i);
            if (Changed != null) Changed(this, EventArgs.Empty);
        }

        void Animate()
        {
            bool busy = false;
            foreach (var r in rows)
            {
                float target = r.On ? 1f : 0f;
                if (Math.Abs(r.Knob - target) > 0.01f) { r.Knob += (target - r.Knob) * 0.32f; busy = true; } else r.Knob = target;
                if (r.Glow > 0) { r.Glow = Math.Max(0f, r.Glow - 0.018f); busy = true; }
            }
            if (!busy) anim.Stop();
            Invalidate();
        }

        void UpdateScrollSize()
        {
            AutoScrollMinSize = new Size(0, rows.Count == 0 ? 0 : rows.Count * (RowH + Gap) - Gap + Theme.S(4));
        }

        int IndexAt(Point client)
        {
            int y = client.Y - AutoScrollPosition.Y - Theme.S(2);
            if (y < 0) return -1;
            int i = y / (RowH + Gap);
            if (i >= rows.Count || y % (RowH + Gap) >= RowH) return -1;
            return i;
        }

        void ScrollIntoView(int i)
        {
            int top = i * (RowH + Gap), view = -AutoScrollPosition.Y;
            if (top < view) AutoScrollPosition = new Point(0, top);
            else if (top + RowH + Theme.S(4) > view + ClientSize.Height) AutoScrollPosition = new Point(0, top + RowH + Theme.S(4) - ClientSize.Height);
            Invalidate();
        }

        // ---- input ----

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int i = IndexAt(e.Location);
            if (i != hover) { hover = i; Cursor = i >= 0 ? Cursors.Hand : Cursors.Default; Invalidate(); }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e) { hover = -1; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            Focus();
            int i = IndexAt(e.Location);
            if (e.Button == MouseButtons.Left && i >= 0) { focus = i; Toggle(i); }
            base.OnMouseDown(e);
        }

        protected override bool IsInputKey(Keys k)
        {
            return k == Keys.Up || k == Keys.Down || k == Keys.Home || k == Keys.End || k == Keys.Space || base.IsInputKey(k);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (rows.Count == 0) { base.OnKeyDown(e); return; }
            int f = focus;
            if (e.KeyCode == Keys.Down) f = Math.Min(rows.Count - 1, focus + 1);
            else if (e.KeyCode == Keys.Up) f = Math.Max(0, focus - 1);
            else if (e.KeyCode == Keys.Home) f = 0;
            else if (e.KeyCode == Keys.End) f = rows.Count - 1;
            else if (e.KeyCode == Keys.Space) { Toggle(focus); e.Handled = true; }
            if (f != focus) { focus = f; ScrollIntoView(f); AccessibilityNotifyClients(AccessibleEvents.Focus, f); e.Handled = true; }
            base.OnKeyDown(e);
        }

        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
        protected override void OnScroll(ScrollEventArgs se) { Invalidate(); base.OnScroll(se); }
        protected override void OnMouseWheel(MouseEventArgs e) { base.OnMouseWheel(e); Invalidate(); }
        protected override void OnResize(EventArgs e) { UpdateScrollSize(); base.OnResize(e); }

        // ---- painting ----

        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] static extern int SetWindowTheme(IntPtr hwnd, string app, string idList);

        public void ApplyTheme()
        {
            BackColor = Theme.P.Bg;
            if (IsHandleCreated) SetWindowTheme(Handle, Theme.P.Dark ? "DarkMode_Explorer" : "Explorer", null); // themed scrollbar
            Invalidate();
        }

        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); ApplyTheme(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var p = Theme.P;
            var g = e.Graphics;
            g.Clear(p.Bg);
            Theme.Smooth(g);

            if (rows.Count == 0) { PaintEmpty(g); return; }

            int w = ClientSize.Width - Theme.S(4);
            int offset = AutoScrollPosition.Y + Theme.S(2);
            for (int i = 0; i < rows.Count; i++)
            {
                int y = offset + i * (RowH + Gap);
                if (y + RowH < 0 || y > ClientSize.Height) continue;
                PaintRow(g, rows[i], new Rectangle(Theme.S(2), y, w, RowH), i == hover, i == focus && Focused && ShowFocusCues);
            }
        }

        void PaintRow(Graphics g, Row r, Rectangle rect, bool hot, bool focused)
        {
            var p = Theme.P;
            float radius = Theme.S(10);
            var bg = hot ? p.TileHover : p.Tile;
            if (r.Glow > 0) bg = Theme.Mix(bg, p.Accent, 0.22f * EaseOut(r.Glow));
            Theme.Fill(g, rect, radius, bg);
            if (focused) Theme.Stroke(g, RectangleF.Inflate(rect, -1f, -1f), radius - 1, p.Accent, 2f);

            int pad = Theme.S(12), logo = Theme.S(44);
            var logoRect = new Rectangle(rect.X + pad, rect.Y + (rect.Height - logo) / 2, logo, logo);
            using (var path = Theme.Round(logoRect, Theme.S(8)))
            {
                using (var b = new SolidBrush(r.LogoBg.IsEmpty ? p.Line : r.LogoBg)) g.FillPath(b, path);
                if (r.Logo != null)
                {
                    var clip = g.Clip;
                    g.SetClip(path, CombineMode.Intersect);
                    g.DrawImage(r.Logo, logoRect);
                    g.Clip = clip;
                }
                else Theme.Glyph(g, Theme.GlyphGamepad, 14f, p.Muted, logoRect);
            }

            var toggle = new Rectangle(rect.Right - pad - Theme.S(44), rect.Y + (rect.Height - Theme.S(24)) / 2, Theme.S(44), Theme.S(24));
            Theme.Toggle(g, toggle, r.Knob, Enabled);

            int textX = logoRect.Right + Theme.S(14), textW = toggle.X - textX - Theme.S(12);
            Theme.Text(g, r.Game.Name, Theme.Strong, p.Text, new Rectangle(textX, rect.Y + Theme.S(13), textW, Theme.S(22)), TextFormatFlags.EndEllipsis);

            string status; Color color; bool dot = false;
            if (r.On && r.InSteam) { status = "In your Steam library"; color = p.Muted; dot = true; }
            else if (r.On) { status = "Will be added to Steam"; color = p.Accent; }
            else if (r.InSteam) { status = "Will be removed from Steam"; color = p.Warn; }
            else { status = "Not in Steam"; color = p.Muted; }
            int sy = rect.Y + Theme.S(37);
            int sx = textX;
            if (dot)
            {
                int d = Theme.S(7);
                using (var b = new SolidBrush(p.Accent)) g.FillEllipse(b, sx, sy + Theme.S(5), d, d);
                sx += d + Theme.S(7);
            }
            Theme.Text(g, status, Theme.Small, color, new Rectangle(sx, sy, textW - (sx - textX), Theme.S(18)), TextFormatFlags.EndEllipsis);
        }

        void PaintEmpty(Graphics g)
        {
            var p = Theme.P;
            int cy = ClientSize.Height / 2 - Theme.S(50);
            var box = new Rectangle(ClientSize.Width / 2 - Theme.S(28), cy, Theme.S(56), Theme.S(56));
            Theme.Fill(g, box, Theme.S(14), p.Tile);
            Theme.Glyph(g, Theme.GlyphGamepad, 20f, p.Muted, box);
            var r1 = new Rectangle(0, box.Bottom + Theme.S(16), ClientSize.Width, Theme.S(24));
            Theme.Text(g, "No Game Pass PC games installed yet", Theme.Heading, p.Text, r1, TextFormatFlags.HorizontalCenter);
            var r2 = new Rectangle(Theme.S(24), r1.Bottom + Theme.S(6), ClientSize.Width - Theme.S(48), Theme.S(40));
            Theme.Text(g, "Install a PC game from the Xbox app, then press Refresh.", Theme.Body, p.Muted, r2,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.WordBreak);
        }

        static float EaseOut(float t) { return 1f - (1f - t) * (1f - t) * (1f - t); }

        static Color ParseColor(string hex)
        {
            if (string.IsNullOrEmpty(hex)) return Color.Empty;
            try { return ColorTranslator.FromHtml(hex); } catch { return Color.Empty; }
        }

        // Pre-scales the tile art once, so painting stays cheap while animating.
        static Image LoadLogo(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            try
            {
                using (var ms = new MemoryStream(File.ReadAllBytes(path)))
                using (var src = Image.FromStream(ms))
                {
                    int size = Theme.S(44) * 2;
                    var bmp = new Bitmap(size, size);
                    using (var g = Graphics.FromImage(bmp))
                    {
                        Theme.Smooth(g);
                        g.DrawImage(src, 0, 0, size, size);
                    }
                    return bmp;
                }
            }
            catch { return null; }
        }

        // ---- accessibility: each game is exposed as a check button ----

        protected override AccessibleObject CreateAccessibilityInstance() { return new ListAccessible(this); }

        class ListAccessible : ControlAccessibleObject
        {
            readonly GameList list;
            public ListAccessible(GameList l) : base(l) { list = l; }
            public override AccessibleRole Role { get { return AccessibleRole.List; } }
            public override int GetChildCount() { return list.rows.Count; }
            public override AccessibleObject GetChild(int i) { return i >= 0 && i < list.rows.Count ? new RowAccessible(list, i) : null; }
            public override AccessibleObject GetFocused() { return list.Focused && list.rows.Count > 0 ? GetChild(list.focus) : null; }
        }

        class RowAccessible : AccessibleObject
        {
            readonly GameList list; readonly int index;
            public RowAccessible(GameList l, int i) { list = l; index = i; }
            Row R { get { return list.rows[index]; } }
            public override string Name { get { return R.Game.Name; } }
            public override AccessibleRole Role { get { return AccessibleRole.CheckButton; } }
            public override AccessibleObject Parent { get { return list.AccessibilityObject; } }
            public override string DefaultAction { get { return R.On ? "Remove from Steam" : "Add to Steam"; } }
            public override void DoDefaultAction() { list.Toggle(index); }
            public override AccessibleStates State
            {
                get
                {
                    var s = AccessibleStates.Focusable;
                    if (R.On) s |= AccessibleStates.Checked;
                    if (list.Focused && list.focus == index) s |= AccessibleStates.Focused;
                    return s;
                }
            }
            public override Rectangle Bounds
            {
                get
                {
                    int y = list.AutoScrollPosition.Y + Theme.S(2) + index * (list.RowH + list.Gap);
                    return list.RectangleToScreen(new Rectangle(Theme.S(2), y, list.ClientSize.Width - Theme.S(4), list.RowH));
                }
            }
        }
    }
}
