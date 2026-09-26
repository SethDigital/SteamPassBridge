using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace SteamPassBridge
{
    class MainForm : Form
    {
        enum StepKind { GetSteam, InstallApp, InstallDriver, RestartPending, Repoint }

        class Step
        {
            public StepKind Kind;
            public string Title, Detail, Glyph;
            public FlatButton Button;
        }

        enum Mood { Idle, Pending, Working, Success, Error }

        readonly GameList list = new GameList();
        readonly FlatButton save = new FlatButton("Update Steam library", ButtonKind.Primary, null);
        readonly FlatButton refresh = new FlatButton("Refresh", ButtonKind.Ghost, Theme.GlyphRefresh);
        readonly FlatButton selectAll = new FlatButton("Select all", ButtonKind.Ghost, Theme.GlyphSelectAll);
        readonly FlatButton more = new FlatButton("", ButtonKind.Ghost, Theme.GlyphMore);
        readonly ContextMenuStrip menu = new ContextMenuStrip();
        readonly System.Windows.Forms.Timer spinner = new System.Windows.Forms.Timer { Interval = 16 };

        List<Step> steps = new List<Step>();
        string stepSignature = "";
        List<string> alreadyInSteam = new List<string>();
        bool working, installingDriver, loaded;
        Mood mood;
        string statusTitle, statusDetail;
        float spin;

        // Layout results used by OnPaint.
        Rectangle markRect, titleRect, subtitleRect, setupHeading, setupBox, gamesHeading, footerRect, statusIcon, statusText;
        readonly List<Rectangle> stepRows = new List<Rectangle>();

        int Pad { get { return Theme.S(28); } }

        public MainForm()
        {
            Text = "SteamPassBridge";
            Icon = Icon.ExtractAssociatedIcon(Program.ExePath);
            AutoScaleMode = AutoScaleMode.None;
            Font = Theme.Body;
            ClientSize = new Size(Theme.S(720), Theme.S(700));
            MinimumSize = new Size(Theme.S(560), Theme.S(560));
            Theme.PlaceWindow(this);
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
            BackColor = Theme.P.Bg;

            more.AccessibleName = "More options";
            Controls.AddRange(new Control[] { refresh, selectAll, list, save, more });

            BuildMenu();
            save.Click += delegate { Save(); };
            refresh.Click += delegate { Reload(); };
            selectAll.Click += delegate { list.SetAll(!list.AllOn); };
            more.Click += delegate { menu.Show(more, new Point(more.Width - menu.PreferredSize.Width, more.Height + Theme.S(4))); };
            list.Changed += delegate { OnSelectionChanged(); };
            spinner.Tick += delegate { spin = (spin + 9f) % 360f; Invalidate(statusIcon); };

            EventHandler themeChanged = (s, e) => BeginInvoke(new Action(ApplyTheme));
            Theme.Changed += themeChanged;
            FormClosed += delegate { Theme.Changed -= themeChanged; };
            Shown += delegate { Reload(); loaded = true; };
            Activated += delegate { if (loaded && !working && !installingDriver) { RefreshSteps(); UpdateStatus(); } };
        }

        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Theme.ApplyChrome(this); ApplySurfaces(); }

        // Controls that sit on the footer band or setup tiles take that surface for their rounded corners.
        void ApplySurfaces()
        {
            save.BackColor = Theme.P.Band;
            foreach (var s in steps) if (s.Button != null) s.Button.BackColor = Theme.P.Tile;
        }
        protected override bool ShowWithoutActivation { get { return Theme.Offscreen; } }

        void ApplyTheme()
        {
            Theme.ApplyChrome(this);
            ApplySurfaces();
            list.ApplyTheme();
            menu.Renderer = new ThemedMenuRenderer();
            Invalidate(true);
        }

        void BuildMenu()
        {
            menu.Renderer = new ThemedMenuRenderer();
            menu.ShowImageMargin = false;
            menu.Font = Theme.Body;
            menu.Padding = new Padding(Theme.S(4));
            var version = new ToolStripMenuItem("SteamPassBridge " + Program.Version) { Enabled = false };
            var log = new ToolStripMenuItem("Open log file", null, delegate
            {
                if (System.IO.File.Exists(Program.LogPath)) Process.Start(Program.LogPath);
                else SetStatus(Mood.Idle, "No log yet", "Nothing has been logged on this PC so far.");
            });
            var uninstall = new ToolStripMenuItem("Uninstall SteamPassBridge…", null, delegate { RunUninstall(); });
            menu.Items.AddRange(new ToolStripItem[] { version, new ToolStripSeparator(), log, uninstall });
            menu.Opening += delegate { uninstall.Visible = Setup.RunningFromInstallDir; };
        }

        // ---- data ----

        void Reload()
        {
            alreadyInSteam = Steam.IsInstalled ? Steam.ExistingOurAppNames() : new List<string>();
            list.SetGames(Games.Find(), alreadyInSteam);
            ClearOutcome();
            RefreshSteps();
            UpdateStatus();
        }

        // A finished result stays visible until the user changes something.
        void ClearOutcome()
        {
            if (mood == Mood.Success || mood == Mood.Error) { mood = Mood.Idle; statusTitle = null; statusDetail = null; }
        }

        void OnSelectionChanged()
        {
            ClearOutcome();
            selectAll.Text = list.AllOn ? "Clear all" : "Select all";
            UpdateStatus();
        }

        void RefreshSteps()
        {
            var next = new List<Step>();
            if (!Steam.IsInstalled)
                next.Add(new Step { Kind = StepKind.GetSteam, Glyph = Theme.GlyphLink, Title = "Install Steam",
                    Detail = "Your games are added to Steam, so install Steam and sign in once." });
            if (!Setup.RunningFromInstallDir)
                next.Add(new Step { Kind = StepKind.InstallApp, Glyph = Theme.GlyphDownload,
                    Title = Setup.IsInstalled ? "Update the installed app" : "Install SteamPassBridge",
                    Detail = Setup.IsInstalled ? "Replace the installed copy with version " + Program.Version + "."
                                               : "Keeps your Steam shortcuts working after you delete this download." });
            if (Steam.IsInstalled && !ViGEmBus.IsInstalled)
            {
                if (DriverSetup.RestartPending)
                    next.Add(new Step { Kind = StepKind.RestartPending, Glyph = Theme.GlyphRestart, Title = "Restart Windows to finish",
                        Detail = "The controller driver finishes installing after a restart." });
                else
                    next.Add(new Step { Kind = StepKind.InstallDriver, Glyph = Theme.GlyphGamepad, Title = "Install the controller driver",
                        Detail = "Lets games see your controller as an Xbox controller. Windows asks for permission once." });
            }
            if (Setup.RunningFromInstallDir && Steam.IsInstalled && Steam.ShortcutsPointElsewhere(Program.ExePath))
                next.Add(new Step { Kind = StepKind.Repoint, Glyph = Theme.GlyphRefresh, Title = "Update your game shortcuts",
                    Detail = "Some games in Steam still point to another copy of this app." });

            var signature = string.Join(",", next.Select(s => s.Kind.ToString()).ToArray());
            if (signature == stepSignature) return;
            stepSignature = signature;

            foreach (var s in steps) if (s.Button != null) { Controls.Remove(s.Button); s.Button.Dispose(); }
            for (int i = 0; i < next.Count; i++)
            {
                var s = next[i];
                string label = null;
                switch (s.Kind)
                {
                    case StepKind.GetSteam: label = "Get Steam"; break;
                    case StepKind.InstallApp: label = Setup.IsInstalled ? "Update" : "Install"; break;
                    case StepKind.InstallDriver: label = DriverSetup.IsBundled ? "Install driver" : "Download"; break;
                    case StepKind.Repoint: label = "Update"; break;
                }
                if (label == null) continue;
                s.Button = new FlatButton(label, i == 0 ? ButtonKind.Primary : ButtonKind.Secondary, null) { BackColor = Theme.P.Tile };
                var step = s;
                s.Button.Click += delegate { RunStep(step); };
                Controls.Add(s.Button);
                Controls.SetChildIndex(s.Button, 0);
            }
            steps = next;
            // Tab order: setup actions first, then the games, then saving.
            int tab = 0;
            foreach (var s in steps) if (s.Button != null) s.Button.TabIndex = tab++;
            refresh.TabIndex = tab++; selectAll.TabIndex = tab++; list.TabIndex = tab++; save.TabIndex = tab++; more.TabIndex = tab++;
            PerformLayout();
            Invalidate();
        }

        void RunStep(Step s)
        {
            switch (s.Kind)
            {
                case StepKind.GetSteam: Process.Start("https://store.steampowered.com/about/"); break;
                case StepKind.InstallApp: InstallApp(); break;
                case StepKind.InstallDriver:
                    if (DriverSetup.IsBundled) InstallDriver(s.Button); else Process.Start(ViGEmBus.DownloadUrl);
                    break;
                case StepKind.Repoint:
                    RunSync(list.Chosen.Where(g => alreadyInSteam.Contains(g.Name)).ToList(), null, "Game shortcuts updated", "They now use this installed copy.");
                    break;
            }
        }

        void InstallApp()
        {
            Program.Log("install requested from setup step");
            try
            {
                Setup.Install();
                Process.Start(Setup.InstalledExe);
                Close();
            }
            catch (Exception ex)
            {
                Program.Log("ERROR install: " + ex);
                SetStatus(Mood.Error, "Couldn't install SteamPassBridge", ex.Message);
            }
        }

        void InstallDriver(FlatButton button)
        {
            if (installingDriver) return;
            installingDriver = true;
            button.SetBusy(true, "Installing…");
            SetStatus(Mood.Working, "Installing the controller driver…", "Approve the Windows permission prompt to continue.");
            PerformLayout();
            new Thread(() =>
            {
                DriverSetup.Result result = DriverSetup.Result.Cancelled; Exception error = null;
                try { result = DriverSetup.Install(); } catch (Exception ex) { error = ex; Program.Log("ERROR driver install: " + ex); }
                BeginInvoke(new Action(() =>
                {
                    installingDriver = false;
                    if (!button.IsDisposed) button.SetBusy(false, null);
                    RefreshSteps();
                    if (error != null) SetStatus(Mood.Error, "The driver didn't install", error.Message);
                    else if (result == DriverSetup.Result.Installed)
                        SetStatus(Mood.Success, steps.Count == 0 ? "Setup complete" : "Controller driver installed",
                            "Your controllers can now appear in games as an Xbox controller.");
                    else if (result == DriverSetup.Result.RestartRequired)
                        SetStatus(Mood.Idle, "Almost there", "Restart Windows to finish installing the controller driver.");
                    else SetStatus(Mood.Idle, "Driver install cancelled", "Install it whenever you're ready. Games can't see the controller until then.");
                }));
            }) { IsBackground = true }.Start();
        }

        void Save()
        {
            Program.Log("save clicked");
            var added = list.ToAdd;
            var removed = list.ToRemove;
            string title = added.Count > 0
                ? Plural(added.Count, "game") + " added to your Steam library"
                : "Steam library updated";
            string detail = added.Count > 0
                ? "Launch " + (added.Count == 1 ? "it" : "them") + " from Steam and your controller shows up as an Xbox controller."
                : Plural(removed.Count, "game") + " removed from Steam.";
            RunSync(list.Chosen, removed, title, detail);
        }

        void RunSync(List<GamePassGame> chosen, List<string> removed, string doneTitle, string doneDetail)
        {
            Program.Log("sync " + chosen.Count + " game(s), removing " + (removed == null ? 0 : removed.Count));
            working = true;
            list.Enabled = false;
            save.SetBusy(true, "Updating…");
            SetStatus(Mood.Working, "Updating your Steam library…",
                Steam.IsRunning ? "Steam closes and reopens. This takes a few seconds." : "This takes a moment.");
            PerformLayout();
            new Thread(() =>
            {
                Exception error = null;
                try { Program.SyncToSteam(chosen, removed, true); }
                catch (Exception ex) { error = ex; Program.Log("ERROR " + ex); }
                BeginInvoke(new Action(() =>
                {
                    working = false;
                    list.Enabled = true;
                    save.SetBusy(false, null);
                    if (error == null)
                    {
                        list.MarkSaved();
                        alreadyInSteam = Steam.ExistingOurAppNames();
                        RefreshSteps();
                        SetStatus(Mood.Success, doneTitle, doneDetail);
                    }
                    else SetStatus(Mood.Error, "Couldn't update your Steam library", error.Message);
                    UpdateStatus();
                }));
            }) { IsBackground = true }.Start();
        }

        void RunUninstall()
        {
            if (MessageBox.Show(this, "Remove SteamPassBridge and the games it added to your Steam library?" +
                    (Steam.IsRunning ? "\n\nSteam will close and reopen." : ""), "Uninstall SteamPassBridge",
                    MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            try
            {
                Setup.Uninstall();
                MessageBox.Show(this, "SteamPassBridge has been removed.", "SteamPassBridge");
                Close();
            }
            catch (Exception ex)
            {
                Program.Log("ERROR uninstall: " + ex);
                SetStatus(Mood.Error, "Uninstall didn't finish", ex.Message);
            }
        }

        static string Plural(int n, string noun) { return n + " " + noun + (n == 1 ? "" : "s"); }

        // ---- footer status ----

        void SetStatus(Mood m, string title, string detail)
        {
            mood = m; statusTitle = title; statusDetail = detail;
            if (m == Mood.Working) spinner.Start(); else spinner.Stop();
            UpdateStatus();
        }

        void UpdateStatus()
        {
            var add = list.ToAdd.Count; var remove = list.ToRemove.Count;
            bool pending = add + remove > 0;
            save.Enabled = pending && !working && Steam.IsInstalled;
            selectAll.Enabled = list.Count > 0 && !working;
            selectAll.Text = list.AllOn ? "Clear all" : "Select all";

            if (mood == Mood.Idle || mood == Mood.Pending)
            {
                if (pending)
                {
                    mood = Mood.Pending;
                    var parts = new List<string>();
                    if (add > 0) parts.Add(add + " to add");
                    if (remove > 0) parts.Add(remove + " to remove");
                    statusTitle = string.Join(" · ", parts.ToArray());
                    statusDetail = Steam.IsRunning ? "Steam closes and reopens to apply this." : "Applies the next time Steam starts.";
                }
                else if (mood == Mood.Pending || statusTitle == null)
                {
                    mood = Mood.Idle;
                    if (list.Count == 0) { statusTitle = null; statusDetail = null; }
                    else if (list.InSteamCount > 0)
                    {
                        statusTitle = "Your Steam library is up to date";
                        statusDetail = "Launch these games from Steam to play with your controller.";
                    }
                    else
                    {
                        statusTitle = "Pick the games you want to play";
                        statusDetail = "Switch a game on, then update your Steam library.";
                    }
                }
            }
            PerformLayout();
            Invalidate();
        }

        // ---- layout ----

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            int pad = Pad, w = ClientSize.Width, h = ClientSize.Height;
            int y = Theme.S(24);

            markRect = new Rectangle(pad, y, Theme.S(44), Theme.S(44));
            more.Location = new Point(w - pad - more.Width + Theme.S(6), y + Theme.S(5));
            titleRect = new Rectangle(markRect.Right + Theme.S(14), y - Theme.S(2), w, Theme.S(30));
            subtitleRect = new Rectangle(titleRect.X, y + Theme.S(28), w, Theme.S(20));
            y = markRect.Bottom + Theme.S(28);

            stepRows.Clear();
            if (steps.Count > 0)
            {
                setupHeading = new Rectangle(pad, y, w - pad * 2, Theme.S(24));
                y += Theme.S(34);
                int rowH = Theme.S(76);
                setupBox = new Rectangle(pad, y, w - pad * 2, rowH * steps.Count);
                for (int i = 0; i < steps.Count; i++)
                {
                    var r = new Rectangle(setupBox.X, y + i * rowH, setupBox.Width, rowH);
                    stepRows.Add(r);
                    var b = steps[i].Button;
                    if (b != null) b.Location = new Point(r.Right - Theme.S(18) - b.Width, r.Y + (rowH - b.Height) / 2);
                }
                y = setupBox.Bottom + Theme.S(30);
            }
            else setupBox = setupHeading = Rectangle.Empty;

            gamesHeading = new Rectangle(pad, y, w - pad * 2, Theme.S(26));
            selectAll.Location = new Point(w - pad - selectAll.Width + Theme.S(8), y - Theme.S(4));
            refresh.Location = new Point(selectAll.Left - refresh.Width - Theme.S(2), y - Theme.S(4));
            y += Theme.S(40);

            int fh = Theme.S(88);
            footerRect = new Rectangle(0, h - fh, w, fh);
            save.Location = new Point(w - pad - save.Width, footerRect.Y + (fh - save.Height) / 2);
            statusIcon = new Rectangle(pad, footerRect.Y + Theme.S(24), Theme.S(22), Theme.S(22));
            statusText = new Rectangle(statusIcon.Right + Theme.S(12), footerRect.Y + Theme.S(21), save.Left - statusIcon.Right - Theme.S(28), Theme.S(48));

            list.Bounds = new Rectangle(pad - Theme.S(2), y, w - pad * 2 + Theme.S(4), Math.Max(Theme.S(80), footerRect.Y - Theme.S(14) - y));
        }

        // ---- painting ----

        protected override void OnPaintBackground(PaintEventArgs e) { e.Graphics.Clear(Theme.P.Bg); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var p = Theme.P;
            var g = e.Graphics;
            Theme.Smooth(g);

            // Brand mark + title
            Theme.Fill(g, markRect, Theme.S(12), p.Accent);
            Theme.Glyph(g, Theme.GlyphGamepad, 17f, p.OnAccent, markRect);
            Theme.Text(g, "SteamPassBridge", Theme.Title, p.Text, titleRect, TextFormatFlags.Left);
            Theme.Text(g, "Play Game Pass with your Steam or PlayStation controller", Theme.Body, p.Muted, subtitleRect, TextFormatFlags.Left);
            PaintChips(g);

            if (steps.Count > 0)
            {
                Theme.Text(g, "Finish setup", Theme.Heading, p.Text, setupHeading, TextFormatFlags.Left);
                Theme.Fill(g, setupBox, Theme.S(12), p.Tile);
                for (int i = 0; i < steps.Count; i++) PaintStep(g, steps[i], stepRows[i], i > 0);
            }

            Theme.Text(g, "Your Game Pass games", Theme.Heading, p.Text, gamesHeading, TextFormatFlags.Left);
            if (list.Count > 0)
            {
                int hw = Theme.Measure("Your Game Pass games", Theme.Heading).Width;
                string count = list.Count + " installed · " + list.InSteamCount + " in Steam";
                Theme.Text(g, count, Theme.Small, p.Muted,
                    new Rectangle(gamesHeading.X + hw + Theme.S(12), gamesHeading.Y + Theme.S(5), refresh.Left - gamesHeading.X - hw - Theme.S(20), Theme.S(18)),
                    TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
            }

            PaintFooter(g);
        }

        void PaintChips(Graphics g)
        {
            var p = Theme.P;
            var chips = new List<Tuple<string, Color>>();
            chips.Add(Tuple.Create(Steam.IsInstalled ? "Steam" : "Steam missing", Steam.IsInstalled ? p.Accent : p.Warn));
            bool driver = ViGEmBus.IsInstalled;
            chips.Add(Tuple.Create(driver ? "Controller driver" : DriverSetup.RestartPending ? "Restart needed" : "Driver needed", driver ? p.Accent : p.Warn));

            int x = more.Left - Theme.S(10), hgt = Theme.S(28), y = markRect.Y + Theme.S(8);
            foreach (var c in Enumerable.Reverse(chips))
            {
                int tw = Theme.Measure(c.Item1, Theme.Small).Width;
                int cw = tw + Theme.S(12 + 8 + 8 + 12);
                x -= cw;
                if (x < titleRect.X + Theme.Measure("SteamPassBridge", Theme.Title).Width + Theme.S(12)) break; // not enough room
                var r = new Rectangle(x, y, cw, hgt);
                Theme.Fill(g, r, hgt / 2f, p.Tile);
                int d = Theme.S(8);
                using (var b = new SolidBrush(c.Item2)) g.FillEllipse(b, r.X + Theme.S(12), r.Y + (hgt - d) / 2f, d, d);
                Theme.Text(g, c.Item1, Theme.Small, p.Text, new Rectangle(r.X + Theme.S(28), r.Y, tw + 2, hgt), TextFormatFlags.VerticalCenter);
                x -= Theme.S(8);
            }
        }

        void PaintStep(Graphics g, Step s, Rectangle r, bool separator)
        {
            var p = Theme.P;
            if (separator)
                using (var pen = new Pen(p.Line)) g.DrawLine(pen, r.X + Theme.S(18), r.Y, r.Right - Theme.S(18), r.Y);
            var icon = new Rectangle(r.X + Theme.S(18), r.Y + (r.Height - Theme.S(38)) / 2, Theme.S(38), Theme.S(38));
            Theme.Fill(g, icon, Theme.S(19), p.Dark ? p.Bg : p.Band);
            bool warn = s.Kind == StepKind.RestartPending;
            Theme.Glyph(g, s.Glyph, 12f, warn ? p.Warn : p.Accent, icon);
            int tx = icon.Right + Theme.S(16);
            int tw = (s.Button != null ? s.Button.Left - Theme.S(16) : r.Right - Theme.S(18)) - tx;
            Theme.Text(g, s.Title, Theme.Strong, p.Text, new Rectangle(tx, r.Y + Theme.S(16), tw, Theme.S(22)), TextFormatFlags.EndEllipsis);
            Theme.Text(g, s.Detail, Theme.Small, p.Muted, new Rectangle(tx, r.Y + Theme.S(40), tw, Theme.S(32)), TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis);
        }

        void PaintFooter(Graphics g)
        {
            var p = Theme.P;
            using (var b = new SolidBrush(p.Band)) g.FillRectangle(b, footerRect);
            using (var pen = new Pen(p.Line)) g.DrawLine(pen, 0, footerRect.Y, footerRect.Right, footerRect.Y);
            if (string.IsNullOrEmpty(statusTitle)) return;

            switch (mood)
            {
                case Mood.Working:
                    using (var pen = new Pen(p.Accent, Theme.S(2.5f)) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round })
                        g.DrawArc(pen, RectangleF.Inflate(statusIcon, -Theme.S(3), -Theme.S(3)), spin, 270);
                    break;
                case Mood.Success: Theme.Glyph(g, Theme.GlyphCompleted, 14f, p.Accent, statusIcon); break;
                case Mood.Error: Theme.Glyph(g, Theme.GlyphError, 14f, p.Danger, statusIcon); break;
                case Mood.Pending: Theme.Glyph(g, Theme.GlyphInfo, 14f, p.Accent, statusIcon); break;
                default: Theme.Glyph(g, list.InSteamCount > 0 ? Theme.GlyphCheck : Theme.GlyphGamepad, 13f, p.Muted, statusIcon); break;
            }
            Theme.Text(g, statusTitle, Theme.Strong, p.Text, new Rectangle(statusText.X, statusText.Y, statusText.Width, Theme.S(22)), TextFormatFlags.EndEllipsis);
            if (!string.IsNullOrEmpty(statusDetail))
                Theme.Text(g, statusDetail, Theme.Small, p.Muted, new Rectangle(statusText.X, statusText.Y + Theme.S(24), statusText.Width, Theme.S(34)),
                    TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis);
        }
    }
}
