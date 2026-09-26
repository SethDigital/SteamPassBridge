using System;
using System.Drawing;
using System.Windows.Forms;

namespace SteamPassBridge
{
    enum ButtonKind { Primary, Secondary, Ghost }

    // Themed button: default, hover, pressed, keyboard focus, disabled and busy states.
    class FlatButton : Control
    {
        public ButtonKind Kind;
        public string Glyph;          // optional Fluent icon shown before the text
        bool hover, pressed, busy;
        string busyText;

        public FlatButton(string text, ButtonKind kind, string glyph)
        {
            Text = text; Kind = kind; Glyph = glyph;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.Selectable | ControlStyles.StandardClick, true);
            Cursor = Cursors.Hand;
            TabStop = true;
            AccessibleRole = AccessibleRole.PushButton;
            Size = GetPreferredSize(Size.Empty);
        }

        public bool Busy { get { return busy; } }

        public void SetBusy(bool on, string text)
        {
            busy = on; busyText = text;
            Size = GetPreferredSize(Size.Empty);
            Invalidate();
        }

        string Label { get { return busy && busyText != null ? busyText : Text; } }

        public override Size GetPreferredSize(Size proposed)
        {
            var t = Theme.Measure(Label, Theme.Strong);
            int glyph = string.IsNullOrEmpty(Glyph) ? 0 : Theme.S(24);
            bool iconOnly = string.IsNullOrEmpty(Label);
            int h = Kind == ButtonKind.Primary ? Theme.S(40) : Theme.S(34);
            return iconOnly ? new Size(h, h) : new Size(t.Width + glyph + Theme.S(Kind == ButtonKind.Primary ? 40 : 28), h);
        }

        protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); AccessibleName = Text; Size = GetPreferredSize(Size.Empty); Invalidate(); }
        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Cursor = Enabled ? Cursors.Hand : Cursors.Default; Invalidate(); }
        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = pressed = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { pressed = true; Invalidate(); } base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

        protected override void OnClick(EventArgs e) { if (busy) return; base.OnClick(e); }

        protected override bool IsInputKey(Keys k) { return k == Keys.Enter || k == Keys.Space || base.IsInputKey(k); }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space) { pressed = true; Invalidate(); e.Handled = true; }
            base.OnKeyDown(e);
        }

        protected override void OnKeyUp(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space) { pressed = false; Invalidate(); if (Enabled) OnClick(EventArgs.Empty); e.Handled = true; }
            base.OnKeyUp(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var p = Theme.P;
            var g = e.Graphics;
            g.Clear(BackColor);
            Theme.Smooth(g);

            Color bg, fg, border = Color.Empty;
            bool active = Enabled && !busy;
            switch (Kind)
            {
                case ButtonKind.Primary:
                    bg = !active ? p.TrackOff : pressed ? Theme.Mix(p.AccentHover, p.Bg, 0.15f) : hover ? p.AccentHover : p.Accent;
                    fg = !active ? p.Muted : p.OnAccent;
                    break;
                case ButtonKind.Secondary:
                    bg = pressed ? p.Line : hover && active ? p.TileHover : p.Tile;
                    fg = active ? p.Text : p.Muted;
                    border = p.Line;
                    break;
                default:
                    bg = pressed ? p.Line : hover && active ? p.TileHover : Color.Transparent;
                    fg = active ? p.Text : p.Muted;
                    break;
            }

            var r = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
            float radius = Theme.S(8);
            if (bg.A > 0) Theme.Fill(g, r, radius, bg);
            if (!border.IsEmpty) Theme.Stroke(g, r, radius, border, 1f);
            if (Focused && ShowFocusCues)
                Theme.Stroke(g, RectangleF.Inflate(r, -1.5f, -1.5f), radius - 1.5f, Kind == ButtonKind.Primary ? p.OnAccent : p.Accent, 2f);

            var label = Label;
            int glyphW = string.IsNullOrEmpty(Glyph) ? 0 : Theme.S(24);
            var textSize = Theme.Measure(label, Theme.Strong);
            int contentW = glyphW + (string.IsNullOrEmpty(label) ? 0 : textSize.Width);
            int x = (Width - contentW) / 2;
            if (glyphW > 0)
                Theme.Glyph(g, Glyph, 11f, fg, new Rectangle(x, 0, string.IsNullOrEmpty(label) ? contentW : Theme.S(18), Height));
            if (!string.IsNullOrEmpty(label))
                Theme.Text(g, label, Theme.Strong, fg, new Rectangle(x + glyphW, 0, textSize.Width + 2, Height), TextFormatFlags.VerticalCenter);
        }
    }

    // Context menu colours that follow the theme instead of the default light-grey renderer.
    class ThemedMenuRenderer : ToolStripProfessionalRenderer
    {
        public ThemedMenuRenderer() : base(new MenuColors()) { RoundedEdges = false; }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? Theme.P.Text : Theme.P.Muted;
            e.TextFont = Theme.Body;
            base.OnRenderItemText(e);
        }

        class MenuColors : ProfessionalColorTable
        {
            public override Color ToolStripDropDownBackground { get { return Theme.P.Tile; } }
            public override Color ImageMarginGradientBegin { get { return Theme.P.Tile; } }
            public override Color ImageMarginGradientMiddle { get { return Theme.P.Tile; } }
            public override Color ImageMarginGradientEnd { get { return Theme.P.Tile; } }
            public override Color MenuBorder { get { return Theme.P.Line; } }
            public override Color MenuItemBorder { get { return Theme.P.TileHover; } }
            public override Color MenuItemSelected { get { return Theme.P.TileHover; } }
            public override Color MenuItemSelectedGradientBegin { get { return Theme.P.TileHover; } }
            public override Color MenuItemSelectedGradientEnd { get { return Theme.P.TileHover; } }
            public override Color SeparatorDark { get { return Theme.P.Line; } }
            public override Color SeparatorLight { get { return Theme.P.Tile; } }
        }
    }
}
