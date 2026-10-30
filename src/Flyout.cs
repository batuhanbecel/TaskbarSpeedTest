using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace TaskbarSpeedTest
{
    // The panel that opens from the taskbar, styled like the Windows 11 taskbar flyouts: a gauge with the live
    // speed and the start / stop button, cards for ping, download and upload, the recent tests, and a footer
    // with the settings button.
    class Flyout : Form
    {
        public event Action StartClicked;              // start a test, or stop the running one
        public event Action<Point> SettingsClicked;
        public int Unit;                               // 0 = Mbps, 1 = MB/s

        TestStatus status;                             // null: no test since the app started
        List<Result> history = new List<Result>();
        string link = "";
        float s = 1f;                                  // DPI scale
        int hover;                                     // HitStart / HitSettings / 0
        int historyRows;
        double needle;                                 // gauge position on screen (0..1), eased toward the target
        double peak;                                   // fastest live value of the running test, picks the scale
        Rectangle gaugeCard, startButton, settingsButton;
        readonly Timer slide = new Timer { Interval = 10 };
        readonly Timer anim = new Timer { Interval = 15 };
        int slideFrom, slideTo, slideStep;
        DateTime hiddenAt;

        const int HitStart = 1, HitSettings = 2, MaxHistoryRows = 5;
        const int Width0 = 380, Margin0 = 12, Header0 = 48, Gauge0 = 236, Gap0 = 8, Metric0 = 80, Info0 = 36;
        const int HistoryHead0 = 30, Row0 = 28, Footer0 = 48;
        const float StartAngle = 150, SweepAngle = 240;

        public Flyout()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            KeyPreview = true;
            Text = "TaskbarSpeedTest";
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            slide.Tick += (o, e) => SlideStep();
            anim.Tick += (o, e) => Animate();
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= WS_EX_TOOLWINDOW;
                cp.ClassStyle |= CS_DROPSHADOW;
                return cp;
            }
        }

        // A click on the taskbar text while the flyout is open closes it (deactivation) and then
        // arrives as a fresh click; this tells the caller to leave it closed.
        public bool JustClosed { get { return DateTime.UtcNow - hiddenAt < TimeSpan.FromMilliseconds(300); } }

        public void SetStatus(TestStatus st)
        {
            if (st.Running && st.Phase == Phase.Preparing) peak = 0;
            if (st.Phase == Phase.Download || st.Phase == Phase.Upload) peak = Math.Max(peak, st.Live);
            status = st;
            if (!string.IsNullOrEmpty(st.Result.Link)) link = st.Result.Link;
            if (Visible) Invalidate();
        }

        public void SetHistory(List<Result> list)
        {
            history = list;
            if (!Visible) return;
            Relayout();
            Invalidate();
        }

        // anchor: the taskbar text or the tray icon (screen coordinates).
        public void ShowAt(Rectangle anchor)
        {
            Screen screen = Screen.FromPoint(new Point(anchor.Left + anchor.Width / 2, anchor.Top + anchor.Height / 2));
            s = MonitorDpi(screen) / 96f;
            workArea = screen.WorkingArea;
            int margin = Px(Margin0);
            int w = Px(Width0);
            anchorRight = Math.Min(workArea.Right - margin, Math.Max(workArea.Left + margin + w, anchor.Left + anchor.Width / 2 + w / 2));
            if (anchor.IsEmpty) anchorRight = workArea.Right - margin;
            anchorTop = anchor.Bottom <= workArea.Top + 1 && !anchor.IsEmpty;   // taskbar at the top
            anchorEdge = anchorTop ? workArea.Top + margin : workArea.Bottom - margin;
            string now = Network.Describe();
            if (now.Length > 0) link = now;
            Relayout();

            slideTo = Top;
            slideFrom = anchorTop ? slideTo - Px(16) : slideTo + Px(16);
            slideStep = 0;
            Top = slideFrom;
            hover = 0;
            needle = 0;   // the gauge sweeps up to the value on every open
            Show();
            Activate();
            ApplyDwm();
            slide.Start();
            anim.Start();
        }

        Rectangle workArea;
        int anchorRight, anchorEdge;
        bool anchorTop;

        void Relayout()
        {
            int w = Px(Width0);
            int fixedPart = Px(Header0) + Px(Gauge0) + Px(Gap0) + Px(Metric0) + Px(Info0) + Px(Footer0);
            // As many recent tests as fit on the screen, up to five.
            historyRows = Math.Min(history.Count, MaxHistoryRows);
            while (historyRows > 0 && fixedPart + HistoryHeight(historyRows) > workArea.Height - 2 * Px(Margin0)) historyRows--;
            int h = fixedPart + HistoryHeight(historyRows);
            SetBounds(anchorRight - w, anchorTop ? anchorEdge : anchorEdge - h, w, h);
        }

        int HistoryHeight(int rows)
        {
            return rows == 0 ? 0 : Px(HistoryHead0) + rows * Px(Row0) + Px(Gap0);
        }

        void SlideStep()
        {
            slideStep++;
            double t = Math.Min(1.0, slideStep / 12.0);
            double ease = 1 - Math.Pow(1 - t, 3);
            Top = (int)Math.Round(slideFrom + (slideTo - slideFrom) * ease);
            if (t >= 1) slide.Stop();
        }

        void Animate()
        {
            if (!Visible) { anim.Stop(); return; }
            Color color;
            double target = Target(out color);
            double next = needle + (target - needle) * 0.16;
            if (Math.Abs(target - next) < 0.001) next = target;
            if (next == needle) return;
            needle = next;
            Invalidate(gaugeCard);
        }

        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            if (ContextMenuOpen) return;
            CloseFlyout();
        }

        public bool ContextMenuOpen;

        public void CloseFlyout()
        {
            if (!Visible) return;
            slide.Stop();
            anim.Stop();
            hiddenAt = DateTime.UtcNow;
            Hide();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Escape) CloseFlyout();
            else if ((e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space) && StartClicked != null) StartClicked();
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_DPICHANGED) return;   // layout is recomputed on every show
            base.WndProc(ref m);
        }

        void ApplyDwm()
        {
            int round = DWMWCP_ROUND;
            DwmSetWindowAttribute(Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, 4);
            Color c = Theme.Light ? Color.FromArgb(0xD0, 0xD0, 0xD0) : Color.FromArgb(0x40, 0x40, 0x40);
            int colorRef = c.R | (c.G << 8) | (c.B << 16);
            DwmSetWindowAttribute(Handle, DWMWA_BORDER_COLOR, ref colorRef, 4);
            int dark = Theme.Light ? 0 : 1;
            DwmSetWindowAttribute(Handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, 4);
        }

        int Px(int v) { return (int)Math.Round(v * s); }

        // --- what is on show ---

        bool Running { get { return status != null && status.Running; } }

        // The values on show: the test in progress, or the last one that went through.
        Result Displayed
        {
            get
            {
                if (status != null && (status.Running || status.Result.Complete)) return status.Result;
                return history.Count > 0 ? history[0] : null;
            }
        }

        // Why the last test failed (nothing when the user stopped it).
        string Failure
        {
            get { return status != null && !status.Running && status.Result.Error != null && !status.Cancelled ? status.Result.Error : null; }
        }

        // Where the gauge arc should end (0..1) and its colour.
        double Target(out Color color)
        {
            color = Theme.Accent;
            if (Failure != null) return 0;
            if (Running)
            {
                if (status.Phase == Phase.Upload) color = Theme.Upload;
                return status.Phase == Phase.Download || status.Phase == Phase.Upload ? Position(status.Live) : 0;
            }
            Result r = Displayed;
            return r != null && r.DownMbps > 0 ? Position(r.DownMbps) : 0;
        }

        // Scale marks in the chosen unit; a wider scale once speeds pass a gigabit.
        double[] Marks()
        {
            Result r = Displayed;
            bool big = Running ? peak > 1000 : r != null && Math.Max(r.DownMbps, r.UpMbps) > 1000;
            if (Unit == 1) return big ? new double[] { 0, 1, 10, 50, 100, 250, 500, 1250 } : new double[] { 0, 1, 5, 10, 25, 50, 100, 125 };
            return big ? new double[] { 0, 10, 100, 500, 1000, 2500, 5000, 10000 } : new double[] { 0, 5, 10, 50, 100, 250, 500, 1000 };
        }

        // Evenly spaced marks, linear in between (like other speed tests): small speeds still move the gauge.
        double Position(double mbps)
        {
            double v = Unit == 1 ? mbps / 8 : mbps;
            double[] m = Marks();
            if (v <= 0) return 0;
            for (int i = 1; i < m.Length; i++)
                if (v <= m[i]) return (i - 1 + (v - m[i - 1]) / (m[i] - m[i - 1])) / (m.Length - 1);
            return 1;
        }

        static string MarkText(double v)
        {
            if (v >= 1000 && v % 1000 == 0) return (v / 1000) + "G";
            if (v >= 2500) return (v / 1000).ToString("0.#", L.Culture) + "G";
            return v.ToString("0", L.Culture);
        }

        static string PhaseName(Phase p)
        {
            switch (p)
            {
                case Phase.Ping: return L.T("Ping", "Ping");
                case Phase.Download: return L.T("Download", "İndirme");
                case Phase.Upload: return L.T("Upload", "Yükleme");
                default: return L.T("Connecting…", "Bağlanılıyor…");
            }
        }

        // --- painting ---

        const TextFormatFlags Center = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
        const TextFormatFlags LeftLine = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis;
        const TextFormatFlags RightLine = TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Theme.Background);
            int x = Px(Margin0), inner = Width - 2 * Px(Margin0);

            PaintHeader(g);
            int y = Px(Header0);
            gaugeCard = new Rectangle(x, y, inner, Px(Gauge0));
            PaintGauge(g, gaugeCard);
            y = gaugeCard.Bottom + Px(Gap0);
            PaintMetrics(g, new Rectangle(x, y, inner, Px(Metric0)));
            y += Px(Metric0);
            PaintInfo(g, new Rectangle(x + Px(4), y, inner - Px(8), Px(Info0)));
            y += Px(Info0);
            if (historyRows > 0) PaintHistory(g, new Rectangle(x, y, inner, HistoryHeight(historyRows) - Px(Gap0)));
            PaintFooter(g);
        }

        void PaintHeader(Graphics g)
        {
            int x = Px(Margin0 + 4), right = Width - Px(Margin0 + 4);
            using (Font f = Theme.Strong(14 * s, false))
                TextRenderer.DrawText(g, L.T("Speed test", "Hız testi"), f, new Rectangle(x, 0, Width / 2, Px(Header0)), Theme.Text, LeftLine);
            if (link.Length == 0) return;
            using (Font f = Theme.NewFont(Theme.TextFont, 12 * s))
            {
                int w = TextRenderer.MeasureText(g, link, f, Size.Empty, TextFormatFlags.NoPadding).Width;
                TextRenderer.DrawText(g, link, f, new Rectangle(right - w, 0, w + Px(2), Px(Header0)), Theme.SecondaryText, LeftLine);
                Icons.DrawGlyph(g, Theme.LinkGlyph(link), 14 * s, new RectangleF(right - w - Px(22), 0, Px(16), Px(Header0)), Theme.SecondaryText);
            }
        }

        void PaintGauge(Graphics g, Rectangle card)
        {
            FillCard(g, card, null);
            float r = 92 * s, thick = 10 * s;
            float cx = card.X + card.Width / 2f, cy = card.Y + 20 * s + r;
            var arc = new RectangleF(cx - r, cy - r, 2 * r, 2 * r);
            Color color;
            Target(out color);
            using (Pen pen = RoundPen(Theme.Track, thick)) g.DrawArc(pen, arc, StartAngle, SweepAngle);
            if (needle > 0.002)
                using (Pen pen = RoundPen(color, thick)) g.DrawArc(pen, arc, StartAngle, (float)(SweepAngle * needle));

            double[] marks = Marks();
            float lr = r - thick / 2 - 13 * s;
            using (Font f = Theme.NewFont(Theme.TextFont, 10 * s))
                for (int i = 0; i < marks.Length; i++)
                {
                    double a = (StartAngle + SweepAngle * i / (marks.Length - 1)) * Math.PI / 180;
                    int lx = (int)(cx + Math.Cos(a) * lr), ly = (int)(cy + Math.Sin(a) * lr);
                    TextRenderer.DrawText(g, MarkText(marks[i]), f, new Rectangle(lx - Px(20), ly - Px(8), Px(40), Px(16)), Theme.TertiaryText, Center);
                }

            int icx = (int)cx, icy = (int)cy;
            var labelBox = new Rectangle(icx - Px(90), icy - Px(50), Px(180), Px(18));
            var numberBox = new Rectangle(icx - Px(90), icy - Px(32), Px(180), Px(50));
            var unitBox = new Rectangle(icx - Px(90), icy + Px(18), Px(180), Px(18));
            string failure = Failure;
            Result shown = Displayed;
            using (Font label = Theme.NewFont(Theme.TextFont, 12 * s))
            using (Font number = Theme.Strong(40 * s))
            {
                if (failure != null)
                {
                    Icons.DrawGlyph(g, Glyphs.Error, 22 * s, new RectangleF(icx - Px(20), icy - Px(52), Px(40), Px(28)), Theme.Critical);
                    using (Font f = Theme.Strong(14 * s, false))
                        TextRenderer.DrawText(g, L.T("Test failed", "Test başarısız"), f, new Rectangle(icx - Px(90), icy - Px(22), Px(180), Px(20)), Theme.Text, Center);
                    using (Font f = Theme.NewFont(Theme.TextFont, 11 * s))
                        TextRenderer.DrawText(g, failure, f, new Rectangle(icx - Px(66), icy, Px(132), Px(44)), Theme.SecondaryText,
                            TextFormatFlags.HorizontalCenter | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
                }
                else if (Running && status.Phase == Phase.Preparing)
                {
                    TextRenderer.DrawText(g, PhaseName(Phase.Preparing), label, labelBox, Theme.SecondaryText, Center);
                    TextRenderer.DrawText(g, "–", number, numberBox, Theme.TertiaryText, Center);
                }
                else if (Running)
                {
                    bool ping = status.Phase == Phase.Ping;
                    TextRenderer.DrawText(g, PhaseName(status.Phase), label, labelBox, Theme.SecondaryText, Center);
                    TextRenderer.DrawText(g, ping ? L.Ms(status.Live) : L.Speed(status.Live, Unit), number, numberBox, Theme.Text, Center);
                    TextRenderer.DrawText(g, ping ? "ms" : L.UnitName(Unit), label, unitBox, Theme.SecondaryText, Center);
                }
                else if (shown != null)
                {
                    TextRenderer.DrawText(g, PhaseName(Phase.Download), label, labelBox, Theme.SecondaryText, Center);
                    TextRenderer.DrawText(g, L.Speed(shown.DownMbps, Unit), number, numberBox, Theme.Text, Center);
                    TextRenderer.DrawText(g, L.UnitName(Unit), label, unitBox, Theme.SecondaryText, Center);
                }
                else
                {
                    Icons.DrawGlyph(g, Glyphs.Speed, 34 * s, new RectangleF(icx - Px(30), icy - Px(44), Px(60), Px(44)), Theme.TertiaryText);
                    TextRenderer.DrawText(g, L.T("Ready to test", "Teste hazır"), label, new Rectangle(icx - Px(90), icy + Px(6), Px(180), Px(18)), Theme.SecondaryText, Center);
                }
            }
            PaintStartButton(g, icx, card.Bottom - Px(20) - Px(36));
        }

        void PaintStartButton(Graphics g, int cx, int top)
        {
            bool running = Running, again = Displayed != null || Failure != null;
            bool hot = hover == HitStart;
            string glyph = running ? Glyphs.Stop : again ? Glyphs.Again : Glyphs.Play;
            string text = running ? L.T("Stop", "Durdur") : again ? L.T("Test again", "Tekrar test et") : L.T("Start", "Başlat");
            using (Font f = Theme.NewFont(Theme.TextFont, 14 * s))
            {
                int textWidth = TextRenderer.MeasureText(g, text, f, Size.Empty, TextFormatFlags.NoPadding).Width;
                int glyphWidth = Px(16), gap = Px(8);
                int content = glyphWidth + gap + textWidth;
                int w = Math.Max(Px(140), content + Px(48)), h = Px(36);
                startButton = new Rectangle(cx - w / 2, top, w, h);

                using (GraphicsPath p = Rounded(startButton, h / 2))
                {
                    if (running)
                    {
                        using (var b = new SolidBrush(hot ? Theme.SubtleStrong : Theme.Subtle)) g.FillPath(b, p);
                        using (var pen = new Pen(Theme.Divider)) g.DrawPath(pen, p);
                    }
                    else
                    {
                        Color fill = hot ? Theme.Mix(Theme.Accent, Theme.Card, 0.12f) : Theme.Accent;
                        using (var b = new SolidBrush(fill)) g.FillPath(b, p);
                    }
                }
                Color fg = running ? Theme.Text : Theme.OnAccent;
                int x = startButton.X + (w - content) / 2;
                Icons.DrawGlyph(g, glyph, 13 * s, new RectangleF(x, top, glyphWidth, h), fg);
                TextRenderer.DrawText(g, text, f, new Rectangle(x + glyphWidth + gap, top, textWidth + Px(4), h), fg, LeftLine);
            }
        }

        void PaintMetrics(Graphics g, Rectangle area)
        {
            int gap = Px(Gap0);
            int w = (area.Width - 2 * gap) / 3;
            Result r = Displayed;
            for (int i = 0; i < 3; i++)
            {
                Phase phase = i == 0 ? Phase.Ping : i == 1 ? Phase.Download : Phase.Upload;
                int x = area.X + i * (w + gap);
                var card = new Rectangle(x, area.Y, i == 2 ? area.Right - x : w, area.Height);
                bool active = Running && status.Phase == phase;
                Color tone = phase == Phase.Upload ? Theme.Upload : phase == Phase.Download ? Theme.Accent : Theme.SecondaryText;
                FillCard(g, card, active ? (Color?)tone : null);

                string glyph = phase == Phase.Ping ? Glyphs.Ping : phase == Phase.Download ? Glyphs.Download : Glyphs.Upload;
                int pad = Px(12);
                Icons.DrawGlyph(g, glyph, 12 * s, new RectangleF(card.X + pad, card.Y + Px(10), Px(14), Px(18)), tone);
                using (Font f = Theme.NewFont(Theme.TextFont, 12 * s))
                    TextRenderer.DrawText(g, PhaseName(phase), f, new Rectangle(card.X + pad + Px(20), card.Y + Px(10), card.Width - pad * 2 - Px(20), Px(18)), Theme.SecondaryText, LeftLine);

                double value = active ? status.Live
                    : r == null ? -1 : phase == Phase.Ping ? r.PingMs : phase == Phase.Download ? r.DownMbps : r.UpMbps;
                bool pending = Running && value < 0;
                string text = phase == Phase.Ping ? L.Ms(value) : L.Speed(value, Unit);
                string unit = phase == Phase.Ping ? "ms" : L.UnitName(Unit);
                var valueBox = new Rectangle(card.X + pad, card.Y + Px(30), card.Width - pad, Px(30));
                using (Font f = Theme.Strong(22 * s))
                {
                    TextRenderer.DrawText(g, text, f, valueBox, pending || value < 0 ? Theme.TertiaryText : Theme.Text, LeftLine);
                    if (value >= 0)
                    {
                        int tw = TextRenderer.MeasureText(g, text, f, Size.Empty, TextFormatFlags.NoPadding).Width;
                        using (Font u = Theme.NewFont(Theme.TextFont, 12 * s))
                            TextRenderer.DrawText(g, unit, u, new Rectangle(valueBox.X + tw + Px(4), valueBox.Y + Px(9), card.Right - valueBox.X - tw - Px(4), Px(18)),
                                Theme.SecondaryText, LeftLine);
                    }
                }

                if (phase == Phase.Ping && r != null && r.JitterMs >= 0 && !active)
                    using (Font f = Theme.NewFont(Theme.TextFont, 11 * s))
                        TextRenderer.DrawText(g, "Jitter " + L.Ms(r.JitterMs) + " ms", f,
                            new Rectangle(card.X + pad, card.Bottom - Px(22), card.Width - pad * 2, Px(16)), Theme.TertiaryText, LeftLine);

                if (active)
                {
                    var track = new Rectangle(card.X + pad, card.Bottom - Px(12), card.Width - 2 * pad, Px(3));
                    using (GraphicsPath p = Rounded(track, track.Height / 2))
                    using (var b = new SolidBrush(Theme.Track)) g.FillPath(b, p);
                    int fw = (int)Math.Round(track.Width * Math.Max(0, Math.Min(1, status.Progress)));
                    if (fw >= track.Height)
                        using (GraphicsPath p = Rounded(new Rectangle(track.X, track.Y, fw, track.Height), track.Height / 2))
                        using (var b = new SolidBrush(tone)) g.FillPath(b, p);
                }
            }
        }

        void PaintInfo(Graphics g, Rectangle r)
        {
            Result shown = Displayed;
            var parts = new List<string>();
            if (shown != null && shown.Isp.Length > 0) parts.Add(shown.Isp);
            parts.Add("Cloudflare" + (shown != null && shown.Server.Length > 0 ? " " + shown.Server : ""));
            Icons.DrawGlyph(g, Glyphs.Globe, 12 * s, new RectangleF(r.X, r.Y, Px(14), r.Height), Theme.TertiaryText);
            using (Font f = Theme.NewFont(Theme.TextFont, 12 * s))
                TextRenderer.DrawText(g, string.Join(" · ", parts), f, new Rectangle(r.X + Px(22), r.Y, r.Width - Px(22), r.Height), Theme.SecondaryText, LeftLine);
        }

        void PaintHistory(Graphics g, Rectangle area)
        {
            FillCard(g, area, null);
            int pad = Px(12);
            // Columns from the right: ping, upload, download.
            int pingW = Px(52), speedW = Px(76);
            var pingCol = new Rectangle(area.Right - pad - pingW, 0, pingW, 0);
            var upCol = new Rectangle(pingCol.X - speedW, 0, speedW, 0);
            var downCol = new Rectangle(upCol.X - speedW, 0, speedW, 0);

            int y = area.Y;
            using (Font head = Theme.Strong(12 * s, false))
                TextRenderer.DrawText(g, L.T("Recent tests", "Son testler"), head, new Rectangle(area.X + pad, y, downCol.X - area.X - pad, Px(HistoryHead0)), Theme.Text, LeftLine);
            using (Font f = Theme.NewFont(Theme.TextFont, 11 * s))
            {
                ColumnHead(g, f, Glyphs.Download, L.UnitName(Unit), Theme.Accent, downCol.X, downCol.Right, y);
                ColumnHead(g, f, Glyphs.Upload, L.UnitName(Unit), Theme.Upload, upCol.X, upCol.Right, y);
                ColumnHead(g, f, Glyphs.Ping, "ms", Theme.SecondaryText, pingCol.X, pingCol.Right, y);
            }
            y += Px(HistoryHead0);

            using (Font f = Theme.NewFont(Theme.TextFont, 12 * s))
            using (var line = new Pen(Theme.Divider))
                for (int i = 0; i < historyRows; i++)
                {
                    Result h = history[i];
                    g.DrawLine(line, area.X + pad, y, area.Right - pad, y);
                    int rh = Px(Row0);
                    TextRenderer.DrawText(g, L.When(h.Time), f, new Rectangle(area.X + pad, y, downCol.X - area.X - pad, rh), Theme.SecondaryText, LeftLine);
                    TextRenderer.DrawText(g, L.Speed(h.DownMbps, Unit), f, new Rectangle(downCol.X, y, downCol.Width, rh), Theme.Text, RightLine);
                    TextRenderer.DrawText(g, L.Speed(h.UpMbps, Unit), f, new Rectangle(upCol.X, y, upCol.Width, rh), Theme.Text, RightLine);
                    TextRenderer.DrawText(g, L.Ms(h.PingMs), f, new Rectangle(pingCol.X, y, pingCol.Width, rh), Theme.Text, RightLine);
                    y += rh;
                }
        }

        // A glyph and a unit, right-aligned in a history column.
        void ColumnHead(Graphics g, Font f, string glyph, string unit, Color tone, int left, int right, int y)
        {
            int w = TextRenderer.MeasureText(g, unit, f, Size.Empty, TextFormatFlags.NoPadding).Width;
            TextRenderer.DrawText(g, unit, f, new Rectangle(right - w - Px(1), y, w + Px(2), Px(HistoryHead0)), Theme.TertiaryText, RightLine);
            Icons.DrawGlyph(g, glyph, 11 * s, new RectangleF(right - w - Px(18), y, Px(14), Px(HistoryHead0)), tone);
        }

        void PaintFooter(Graphics g)
        {
            var r = new Rectangle(0, Height - Px(Footer0), Width, Px(Footer0));
            using (var b = new SolidBrush(Theme.Footer)) g.FillRectangle(b, r);
            using (var pen = new Pen(Theme.Divider)) g.DrawLine(pen, 0, r.Y, Width, r.Y);

            Result shown = Displayed;
            string text = Running ? L.T("Testing…", "Test ediliyor…")
                : history.Count > 0 ? L.T("Last test ", "Son test: ") + L.Ago(history[0].Time)
                : shown == null ? L.T("No tests yet", "Henüz test yok") : "";
            using (Font f = Theme.NewFont(Theme.TextFont, 12 * s))
                TextRenderer.DrawText(g, text, f, new Rectangle(Px(Margin0 + 4), r.Y, Width - Px(80), r.Height), Theme.SecondaryText, LeftLine);

            int bs = Px(32);
            settingsButton = new Rectangle(Width - Px(Margin0) - bs, r.Y + (r.Height - bs) / 2, bs, bs);
            if (hover == HitSettings)
                using (GraphicsPath p = Rounded(settingsButton, Px(4)))
                using (var b = new SolidBrush(Theme.Subtle)) g.FillPath(b, p);
            Icons.DrawGlyph(g, Glyphs.Settings, 16 * s, settingsButton, Theme.Text);
        }

        void FillCard(Graphics g, Rectangle r, Color? border)
        {
            using (GraphicsPath p = Rounded(r, Px(8)))
            {
                using (var b = new SolidBrush(Theme.Card)) g.FillPath(b, p);
                using (var pen = new Pen(border ?? Theme.CardBorder, border.HasValue ? Math.Max(1f, 1.5f * s) : 1f)) g.DrawPath(pen, p);
            }
        }

        static Pen RoundPen(Color c, float width)
        {
            return new Pen(c, width) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        }

        static GraphicsPath Rounded(Rectangle r, int radius)
        {
            int d = Math.Max(1, radius * 2);
            var p = new GraphicsPath();
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        // --- mouse ---

        int HitTest(Point p)
        {
            if (startButton.Contains(p)) return HitStart;
            if (settingsButton.Contains(p)) return HitSettings;
            return 0;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int h = HitTest(e.Location);
            if (h == hover) return;
            hover = h;
            Cursor = h != 0 ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (hover == 0) return;
            hover = 0;
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left) return;
            int h = HitTest(e.Location);
            if (h == HitStart && StartClicked != null) StartClicked();
            else if (h == HitSettings && SettingsClicked != null) SettingsClicked(PointToScreen(new Point(settingsButton.Left, settingsButton.Top)));
        }

        // --- native ---

        static uint MonitorDpi(Screen screen)
        {
            try
            {
                IntPtr mon = MonitorFromPoint(new POINT { X = screen.Bounds.Left + 1, Y = screen.Bounds.Top + 1 }, 2);
                uint x, y;
                if (GetDpiForMonitor(mon, 0, out x, out y) == 0) return x;
            }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
            return 96;
        }

        const int WS_EX_TOOLWINDOW = 0x80, CS_DROPSHADOW = 0x20000, WM_DPICHANGED = 0x02E0;
        const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20, DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWA_BORDER_COLOR = 34, DWMWCP_ROUND = 2;

        [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
        [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
        [DllImport("shcore.dll")] static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint x, out uint y);
    }
}
