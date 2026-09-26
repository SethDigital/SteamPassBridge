using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace SteamPassBridge
{
    // Switch with a label; used where a real control (focus, keyboard, screen readers) is needed.
    class ToggleSwitch : Control
    {
        bool on;
        float knob;
        readonly Timer anim = new Timer { Interval = 15 };
        public event EventHandler CheckedChanged;

        public ToggleSwitch(string text, bool initial)
        {
            Text = text; on = initial; knob = on ? 1 : 0;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.Selectable | ControlStyles.StandardClick, true);
            TabStop = true; Cursor = Cursors.Hand;
            AccessibleRole = AccessibleRole.CheckButton;
            anim.Tick += delegate
            {
                float t = on ? 1 : 0;
                knob += (t - knob) * 0.32f;
                if (Math.Abs(knob - t) < 0.01f) { knob = t; anim.Stop(); }
                Invalidate();
            };
            Size = new Size(Theme.S(56) + Theme.Measure(text, Theme.Body).Width, Theme.S(32));
        }

        public bool Checked
        {
            get { return on; }
            set { if (on == value) return; on = value; anim.Start(); Invalidate(); if (CheckedChanged != null) CheckedChanged(this, EventArgs.Empty); }
        }

        protected override void OnClick(EventArgs e) { if (Enabled) Checked = !Checked; base.OnClick(e); }
        protected override bool IsInputKey(Keys k) { return k == Keys.Space || base.IsInputKey(k); }
        protected override void OnKeyUp(KeyEventArgs e) { if (e.KeyCode == Keys.Space && Enabled) { Checked = !Checked; e.Handled = true; } base.OnKeyUp(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var p = Theme.P; var g = e.Graphics;
            g.Clear(BackColor);
            Theme.Smooth(g);
            var track = new Rectangle(Theme.S(2), (Height - Theme.S(24)) / 2, Theme.S(44), Theme.S(24));
            Theme.Toggle(g, track, knob, Enabled);
            if (Focused && ShowFocusCues) Theme.Stroke(g, RectangleF.Inflate(track, 2f, 2f), track.Height / 2f + 2, p.Accent, 2f);
            Theme.Text(g, Text, Theme.Body, Enabled ? p.Text : p.Muted, new Rectangle(track.Right + Theme.S(12), 0, Width, Height), TextFormatFlags.VerticalCenter);
        }

        protected override AccessibleObject CreateAccessibilityInstance() { return new Acc(this); }

        class Acc : ControlAccessibleObject
        {
            readonly ToggleSwitch t;
            public Acc(ToggleSwitch owner) : base(owner) { t = owner; }
            public override AccessibleStates State { get { return base.State | (t.Checked ? AccessibleStates.Checked : AccessibleStates.None); } }
            public override string DefaultAction { get { return "Toggle"; } }
            public override void DoDefaultAction() { t.Checked = !t.Checked; }
        }
    }

    // Launched from Steam via the "SteamPassBridge Controller Test" shortcut: shows live Steam Input state
    // on a drawn controller and mirrors it to a virtual Xbox controller, so the whole chain can be checked.
    class TestForm : Form
    {
        // XInput button bits
        static class Btn
        {
            public const ushort Up = 0x1, Down = 0x2, Left = 0x4, Right = 0x8, Start = 0x10, Back = 0x20, L3 = 0x40, R3 = 0x80,
                                LB = 0x100, RB = 0x200, A = 0x1000, B = 0x2000, X = 0x4000, Y = 0x8000;
        }

        readonly ToggleSwitch mirror;
        readonly FlatButton rumble = new FlatButton("Test rumble", ButtonKind.Secondary, Theme.GlyphVibrate);
        readonly FlatButton next = new FlatButton("Controller 1 of 2", ButtonKind.Secondary, null);
        readonly FlatButton xboxLabels = new FlatButton("Xbox", ButtonKind.Secondary, null);
        readonly FlatButton psLabels = new FlatButton("PlayStation", ButtonKind.Ghost, null);
        readonly Timer tick = new Timer { Interval = 16 };
        readonly List<uint> steamSlots = new List<uint>();
        List<string> psModels = new List<string>();
        DateTime nextHardwareScan;
        string slotSignature = "";
        int selected;
        bool psStyle, styleChosen;
        readonly Dictionary<ushort, float> heat = new Dictionary<ushort, float>();
        readonly bool hooked;
        Bridge bridge;
        string bridgeError;
        int slot = -1;               // Steam Input XInput slot of the shown source, or -1 for a direct source
        int directIndex = -1;        // index in bridge.DirectControllers() of the shown source, or -1
        string sourceName = "";
        bool sourceMirrored;
        int sourceCount;
        XInputGamepad pad;
        ushort vid, pid;
        float lt, rt, shake;
        DateTime rumbleUntil;
        Rectangle canvas, band;

        public TestForm()
        {
            Text = "SteamPassBridge – Controller Test";
            Icon = Icon.ExtractAssociatedIcon(Program.ExePath);
            AutoScaleMode = AutoScaleMode.None;
            ClientSize = new Size(Theme.S(760), Theme.S(600));
            MinimumSize = new Size(Theme.S(520), Theme.S(460));
            Theme.PlaceWindow(this);
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
            BackColor = Theme.P.Bg;
            hooked = Program.SteamModules().Contains("overlay");

            mirror = new ToggleSwitch("Mirror to a virtual Xbox controller", ViGEmBus.IsInstalled);
            mirror.Enabled = ViGEmBus.IsInstalled;
            mirror.BackColor = rumble.BackColor = xboxLabels.BackColor = psLabels.BackColor = Theme.P.Band;
            Controls.AddRange(new Control[] { next, mirror, xboxLabels, psLabels, rumble });
            next.Visible = false;
            xboxLabels.AccessibleName = "Show Xbox button labels";
            psLabels.AccessibleName = "Show PlayStation button labels";
            next.Click += delegate { if (steamSlots.Count > 0) { selected = (selected + 1) % steamSlots.Count; StopRumble(); } };
            xboxLabels.Click += delegate { styleChosen = true; SetLabelStyle(false); };
            psLabels.Click += delegate { styleChosen = true; SetLabelStyle(true); };
            foreach (var b in new[] { Btn.Up, Btn.Down, Btn.Left, Btn.Right, Btn.Start, Btn.Back, Btn.L3, Btn.R3, Btn.LB, Btn.RB, Btn.A, Btn.B, Btn.X, Btn.Y }) heat[b] = 0f;

            mirror.CheckedChanged += delegate { SetBridge(mirror.Checked); };
            rumble.Click += delegate { Rumble(); };
            tick.Tick += delegate { Poll(); };

            EventHandler themeChanged = (s, e) => BeginInvoke(new Action(() =>
            {
                Theme.ApplyChrome(this);
                mirror.BackColor = rumble.BackColor = xboxLabels.BackColor = psLabels.BackColor = Theme.P.Band;
                Invalidate(true);
            }));
            Theme.Changed += themeChanged;
            Shown += delegate { SetBridge(mirror.Checked); tick.Start(); };
            FormClosed += delegate { Theme.Changed -= themeChanged; tick.Stop(); StopRumble(); if (bridge != null) bridge.Dispose(); };
        }

        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Theme.ApplyChrome(this); }
        protected override bool ShowWithoutActivation { get { return Theme.Offscreen; } }

        void SetBridge(bool on)
        {
            try
            {
                bridgeError = null;
                if (on && bridge == null) bridge = new Bridge();
                else if (!on && bridge != null) { bridge.Dispose(); bridge = null; }
            }
            catch (Exception ex) { bridgeError = ex.Message; mirror.Checked = false; }
            Invalidate();
        }

        void SetLabelStyle(bool playStation)
        {
            psStyle = playStation;
            xboxLabels.Kind = playStation ? ButtonKind.Ghost : ButtonKind.Secondary;
            psLabels.Kind = playStation ? ButtonKind.Secondary : ButtonKind.Ghost;
            xboxLabels.Invalidate(); psLabels.Invalidate();
            Invalidate();
        }

        void Poll()
        {
            // Build the list of sources: Steam Input pads (28DE), then any directly-read PlayStation controller.
            // Our own virtual Xbox pad (045E) is never shown - only the physical inputs feeding it.
            steamSlots.Clear();
            for (uint i = 0; i < 4; i++)
            {
                ushort v, pr;
                if (XInput.IsConnected(i) && XInput.TryGetIds(i, out v, out pr) && v == XInput.ValveVendorId) steamSlots.Add(i);
            }
            var direct = bridge != null ? bridge.DirectControllers() : new List<KeyValuePair<string, XInputGamepad>>();
            int count = steamSlots.Count + direct.Count;

            var signature = string.Join(",", steamSlots.Select(x => x.ToString()).ToArray()) + "|" + string.Join(",", direct.Select(x => x.Key).ToArray());
            if (signature != slotSignature)
            {
                slotSignature = signature;
                Program.Log("test: Steam Input slots [" + string.Join(",", steamSlots.Select(x => x.ToString()).ToArray()) + "], direct [" + string.Join(", ", direct.Select(x => x.Key).ToArray()) + "]");
                if (selected >= count) selected = 0;
                next.Visible = count > 1;
                PerformLayout();
            }
            if (next.Visible) next.Text = "Controller " + (Math.Min(selected, Math.Max(0, count - 1)) + 1) + " of " + count;
            sourceCount = count;

            if (DateTime.Now >= nextHardwareScan)
            {
                nextHardwareScan = DateTime.Now.AddSeconds(2);
                psModels = Controllers.ConnectedPlayStation();
                if (!styleChosen && psStyle != (psModels.Count > 0)) SetLabelStyle(psModels.Count > 0);
            }

            slot = -1; directIndex = -1; sourceName = ""; sourceMirrored = false;
            XInputState s = new XInputState();
            if (count > 0)
            {
                int sel = Math.Min(selected, count - 1);
                if (sel < steamSlots.Count)
                {
                    slot = (int)steamSlots[sel];
                    if (XInput.GetState((uint)slot, out s) == 0) { XInput.TryGetIds((uint)slot, out vid, out pid); sourceMirrored = bridge != null && bridge.BoundSlots.Contains((uint)slot); }
                    else slot = -1;
                    sourceName = "Steam Input";
                }
                else
                {
                    directIndex = sel - steamSlots.Count;
                    var entry = direct[directIndex];
                    s.Gamepad = entry.Value;
                    sourceName = entry.Key + " · direct";
                    sourceMirrored = true; // a directly-read controller is, by definition, mirrored to a virtual pad
                }
            }
            pad = (slot >= 0 || directIndex >= 0) ? s.Gamepad : new XInputGamepad();

            foreach (var key in new List<ushort>(heat.Keys))
            {
                bool down = (pad.Buttons & key) != 0;
                heat[key] = down ? 1f : heat[key] * 0.72f;   // snap on, ease off
            }
            lt += (pad.LeftTrigger / 255f - lt) * 0.5f;
            rt += (pad.RightTrigger / 255f - rt) * 0.5f;
            if (shake > 0) shake = Math.Max(0f, shake - 0.05f);
            if (rumbleUntil != DateTime.MinValue && DateTime.Now > rumbleUntil) StopRumble();
            rumble.Enabled = slot >= 0 || directIndex >= 0;
            Invalidate();
        }

        void Rumble()
        {
            if (directIndex >= 0 && bridge != null) bridge.RumbleDirect(directIndex, 220, 220);
            else if (slot >= 0) { var v = new XInputVibration { LeftMotor = 42000, RightMotor = 42000 }; XInput.SetState((uint)slot, ref v); }
            else return;
            rumbleUntil = DateTime.Now.AddMilliseconds(450);
            shake = 1f;
        }

        void StopRumble()
        {
            rumbleUntil = DateTime.MinValue;
            if (directIndex >= 0 && bridge != null) bridge.RumbleDirect(directIndex, 0, 0);
            else if (slot >= 0) { var v = new XInputVibration(); XInput.SetState((uint)slot, ref v); }
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            if (mirror == null) return; // layout also runs from the base constructor, before controls exist
            int pad_ = Theme.S(28), w = ClientSize.Width, h = ClientSize.Height;
            int bandH = Theme.S(96);
            band = new Rectangle(0, h - bandH, w, bandH);
            canvas = new Rectangle(pad_, Theme.S(92), w - pad_ * 2, band.Y - Theme.S(92) - Theme.S(16));
            mirror.Location = new Point(pad_, band.Y + Theme.S(18));
            rumble.Location = new Point(w - pad_ - rumble.Width, band.Y + Theme.S(16));
            psLabels.Location = new Point(rumble.Left - Theme.S(16) - psLabels.Width, rumble.Top);
            xboxLabels.Location = new Point(psLabels.Left - Theme.S(4) - xboxLabels.Width, rumble.Top);
            next.Location = new Point(w - pad_ - next.Width, Theme.S(26));
        }

        protected override void OnPaintBackground(PaintEventArgs e) { e.Graphics.Clear(Theme.P.Bg); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var p = Theme.P; var g = e.Graphics;
            Theme.Smooth(g);
            int pad_ = Theme.S(28);

            Theme.Text(g, "Controller test", Theme.Title, p.Text, new Rectangle(pad_, Theme.S(22), ClientSize.Width, Theme.S(32)), TextFormatFlags.Left);
            string line; Color dot;
            bool haveSource = slot >= 0 || directIndex >= 0;
            if (!haveSource && !hooked) { line = "Open this from your Steam library. Steam only shares your controller with apps it starts."; dot = p.Warn; }
            else if (!haveSource) { line = "Waiting for your controller. Turn it on or press any button."; dot = p.Warn; }
            else
            {
                line = (directIndex >= 0 ? sourceName.Replace(" · direct", " read directly") : "Steam Input connected");
                if (sourceMirrored) line += " · mirrored to a virtual Xbox controller";
                dot = p.Accent;
            }
            int d = Theme.S(8);
            using (var b = new SolidBrush(dot)) g.FillEllipse(b, pad_, Theme.S(64), d, d);
            Theme.Text(g, line, Theme.Body, p.Muted, new Rectangle(pad_ + d + Theme.S(10), Theme.S(58), ClientSize.Width - pad_ * 2, Theme.S(20)), TextFormatFlags.EndEllipsis);

            PaintController(g);

            using (var b = new SolidBrush(p.Band)) g.FillRectangle(b, band);
            using (var pen = new Pen(p.Line)) g.DrawLine(pen, 0, band.Y, band.Right, band.Y);
            string source = slot >= 0 ? "Slot " + slot + " · " + vid.ToString("X4") + ":" + pid.ToString("X4") : directIndex >= 0 ? sourceName : null;
            string readout = source == null ? (bridgeError ?? (ViGEmBus.IsInstalled ? "No controller detected yet" : "Controller driver not installed: mirroring is off"))
                : string.Format("{0} · LX {1} LY {2} · RX {3} RY {4} · LT {5} RT {6} · buttons {7:X4}",
                    source, pad.ThumbLX, pad.ThumbLY, pad.ThumbRX, pad.ThumbRY, pad.LeftTrigger, pad.RightTrigger, pad.Buttons);
            Theme.Text(g, readout, Theme.Mono, p.Muted, new Rectangle(pad_, band.Y + Theme.S(60), ClientSize.Width - pad_ * 2, Theme.S(20)), TextFormatFlags.EndEllipsis);
        }

        // Controller drawn in a 600×350 design space, scaled to fit the canvas.
        void PaintController(Graphics g)
        {
            var p = Theme.P;
            float scale = Math.Min(canvas.Width / 600f, canvas.Height / 350f);
            float ox = canvas.X + (canvas.Width - 600 * scale) / 2, oy = canvas.Y + (canvas.Height - 350 * scale) / 2;
            float jitter = shake > 0 ? (float)Math.Sin(shake * 40) * 3f * shake : 0f;
            var saved = g.Save();
            g.TranslateTransform(ox + jitter * scale, oy);
            g.ScaleTransform(scale, scale);

            Color idle = p.Dark ? p.Bg : p.Band, stroke = p.Line;
            using (var body = BodyPath())
            {
                using (var b = new SolidBrush(p.Tile)) g.FillPath(b, body);
                using (var pen = new Pen(stroke, 2f)) g.DrawPath(pen, body);
            }

            using (var label = new Font(Theme.Strong.FontFamily, 13f, FontStyle.Bold, GraphicsUnit.Pixel))
            using (var small = new Font(Theme.Small.FontFamily, 11f, FontStyle.Bold, GraphicsUnit.Pixel))
            {
                Trigger(g, new RectangleF(110, 8, 110, 22), lt, psStyle ? "L2" : "LT", small, idle, stroke);
                Trigger(g, new RectangleF(380, 8, 110, 22), rt, psStyle ? "R2" : "RT", small, idle, stroke);
                Pill(g, new RectangleF(120, 40, 100, 20), heat[Btn.LB], psStyle ? "L1" : "LB", small, idle, stroke);
                Pill(g, new RectangleF(380, 40, 100, 20), heat[Btn.RB], psStyle ? "R1" : "RB", small, idle, stroke);

                Stick(g, new PointF(175, 145), 36, pad.ThumbLX, pad.ThumbLY, heat[Btn.L3], idle, stroke);
                Stick(g, new PointF(365, 205), 32, pad.ThumbRX, pad.ThumbRY, heat[Btn.R3], idle, stroke);

                // D-pad
                Pill(g, new RectangleF(224, 172, 22, 22), heat[Btn.Up], null, small, idle, stroke);
                Pill(g, new RectangleF(224, 216, 22, 22), heat[Btn.Down], null, small, idle, stroke);
                Pill(g, new RectangleF(202, 194, 22, 22), heat[Btn.Left], null, small, idle, stroke);
                Pill(g, new RectangleF(246, 194, 22, 22), heat[Btn.Right], null, small, idle, stroke);

                Pill(g, new RectangleF(252, 133, 26, 14), heat[Btn.Back], null, small, idle, stroke);
                Pill(g, new RectangleF(322, 133, 26, 14), heat[Btn.Start], null, small, idle, stroke);
                using (var pen = new Pen(stroke, 2f)) g.DrawEllipse(pen, 286, 91, 28, 28);   // guide: reserved by Steam

                if (psStyle)
                {
                    // Same positions as Xbox: Y=Triangle, X=Square, B=Circle, A=Cross.
                    FacePs(g, new PointF(425, 117), heat[Btn.Y], PsShape.Triangle, idle, stroke);
                    FacePs(g, new PointF(397, 145), heat[Btn.X], PsShape.Square, idle, stroke);
                    FacePs(g, new PointF(453, 145), heat[Btn.B], PsShape.Circle, idle, stroke);
                    FacePs(g, new PointF(425, 173), heat[Btn.A], PsShape.Cross, idle, stroke);
                }
                else
                {
                    Face(g, new PointF(425, 117), heat[Btn.Y], "Y", label, idle, stroke);
                    Face(g, new PointF(397, 145), heat[Btn.X], "X", label, idle, stroke);
                    Face(g, new PointF(453, 145), heat[Btn.B], "B", label, idle, stroke);
                    Face(g, new PointF(425, 173), heat[Btn.A], "A", label, idle, stroke);
                }
            }
            g.Restore(saved);
        }

        static GraphicsPath BodyPath()
        {
            var path = new GraphicsPath();
            path.AddLine(170, 70, 430, 70);
            path.AddBezier(430, 70, 480, 70, 520, 82, 540, 110);
            path.AddBezier(540, 110, 565, 145, 585, 220, 590, 270);
            path.AddBezier(590, 270, 596, 320, 570, 342, 540, 338);
            path.AddBezier(540, 338, 510, 334, 490, 310, 470, 282);
            path.AddBezier(470, 282, 455, 260, 440, 252, 420, 252);
            path.AddLine(420, 252, 180, 252);
            path.AddBezier(180, 252, 160, 252, 145, 260, 130, 282);
            path.AddBezier(130, 282, 110, 310, 90, 334, 60, 338);
            path.AddBezier(60, 338, 30, 342, 4, 320, 10, 270);
            path.AddBezier(10, 270, 15, 220, 35, 145, 60, 110);
            path.AddBezier(60, 110, 80, 82, 120, 70, 170, 70);
            path.CloseFigure();
            return path;
        }

        static void Pill(Graphics g, RectangleF r, float h, string text, Font f, Color idle, Color stroke)
        {
            var p = Theme.P;
            Theme.Fill(g, r, Math.Min(r.Width, r.Height) / 2f * 0.6f, Theme.Mix(idle, p.Accent, h));
            if (h < 0.5f) Theme.Stroke(g, r, Math.Min(r.Width, r.Height) / 2f * 0.6f, stroke, 1.5f);
            if (text != null) CenterText(g, text, f, Theme.Mix(p.Muted, p.OnAccent, h), r);
        }

        static void Trigger(Graphics g, RectangleF r, float value, string text, Font f, Color idle, Color stroke)
        {
            var p = Theme.P;
            Theme.Fill(g, r, 8, idle);
            if (value > 0.02f)
            {
                var fill = new RectangleF(r.X, r.Y, Math.Max(16f, r.Width * value), r.Height);
                Theme.Fill(g, fill, 8, p.Accent);
            }
            Theme.Stroke(g, r, 8, stroke, 1.5f);
            CenterText(g, text, f, value > 0.5f ? p.OnAccent : p.Muted, r);
        }

        enum PsShape { Cross, Circle, Square, Triangle }

        // The classic PlayStation symbol colours, deepened for the light theme so they keep their contrast.
        static Color PsColor(PsShape s)
        {
            bool dark = Theme.P.Dark;
            switch (s)
            {
                case PsShape.Triangle: return dark ? Color.FromArgb(64, 224, 176) : Color.FromArgb(10, 138, 106);
                case PsShape.Circle: return dark ? Color.FromArgb(255, 105, 110) : Color.FromArgb(206, 48, 56);
                case PsShape.Square: return dark ? Color.FromArgb(240, 150, 214) : Color.FromArgb(178, 64, 146);
                default: return dark ? Color.FromArgb(128, 170, 255) : Color.FromArgb(52, 98, 214);
            }
        }

        static void FacePs(Graphics g, PointF c, float h, PsShape shape, Color idle, Color stroke)
        {
            var color = PsColor(shape);
            var r = new RectangleF(c.X - 14, c.Y - 14, 28, 28);
            using (var b = new SolidBrush(Theme.Mix(idle, color, h))) g.FillEllipse(b, r);
            if (h < 0.5f) using (var pen = new Pen(stroke, 1.5f)) g.DrawEllipse(pen, r);
            using (var pen = new Pen(Theme.Mix(color, Theme.P.Tile, h), 2.2f) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                switch (shape)
                {
                    case PsShape.Cross:
                        g.DrawLine(pen, c.X - 6, c.Y - 6, c.X + 6, c.Y + 6);
                        g.DrawLine(pen, c.X + 6, c.Y - 6, c.X - 6, c.Y + 6);
                        break;
                    case PsShape.Circle: g.DrawEllipse(pen, c.X - 6.5f, c.Y - 6.5f, 13, 13); break;
                    case PsShape.Square: g.DrawRectangle(pen, c.X - 5.5f, c.Y - 5.5f, 11, 11); break;
                    default: g.DrawPolygon(pen, new[] { new PointF(c.X, c.Y - 7), new PointF(c.X + 7, c.Y + 5), new PointF(c.X - 7, c.Y + 5) }); break;
                }
            }
        }
        static void Face(Graphics g, PointF c, float h, string text, Font f, Color idle, Color stroke)
        {
            var p = Theme.P;
            var r = new RectangleF(c.X - 14, c.Y - 14, 28, 28);
            using (var b = new SolidBrush(Theme.Mix(idle, p.Accent, h))) g.FillEllipse(b, r);
            if (h < 0.5f) using (var pen = new Pen(stroke, 1.5f)) g.DrawEllipse(pen, r);
            CenterText(g, text, f, Theme.Mix(p.Muted, p.OnAccent, h), r);
        }

        static void Stick(Graphics g, PointF c, float radius, short sx, short sy, float click, Color idle, Color stroke)
        {
            var p = Theme.P;
            var ring = new RectangleF(c.X - radius, c.Y - radius, radius * 2, radius * 2);
            using (var b = new SolidBrush(idle)) g.FillEllipse(b, ring);
            using (var pen = new Pen(stroke, 1.5f)) g.DrawEllipse(pen, ring);
            float nx = sx / 32767f, ny = -sy / 32767f;
            float mag = Math.Min(1f, (float)Math.Sqrt(nx * nx + ny * ny));
            float travel = radius * 0.5f, k = radius * 0.5f;
            var knob = new RectangleF(c.X + nx * travel - k, c.Y + ny * travel - k, k * 2, k * 2);
            var color = Theme.Mix(Theme.Mix(p.TrackOff, p.Accent, mag > 0.12f ? mag : 0f), p.AccentHover, click);
            using (var b = new SolidBrush(color)) g.FillEllipse(b, knob);
        }

        static void CenterText(Graphics g, string s, Font f, Color c, RectangleF r)
        {
            using (var b = new SolidBrush(c))
            using (var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            {
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                g.DrawString(s, f, b, r, fmt);
            }
        }
    }
}
