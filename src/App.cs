using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;
using Timer = System.Windows.Forms.Timer;

namespace TaskbarSpeedTest
{
    // The app: an invisible window that owns the taskbar widget, the flyout, the menu, the optional tray icon,
    // the speed test and the automatic tests.
    class App : Form
    {
        public const string AppName = "TaskbarSpeedTest";
        public const string WindowTitle = "TaskbarSpeedTest.Main";
        public const int ShowFlyoutMessage = 0x8000 + 0x43;   // sent by a second instance
        public const int RunTestMessage = 0x8000 + 0x44;      // a second instance started with --run
        const uint TrayIconId = 1;
        readonly bool runAtStart;

        readonly Settings settings = Settings.Load();
        readonly SpeedTest test = new SpeedTest();
        readonly Flyout flyout = new Flyout();
        readonly ContextMenuStrip menu = new ContextMenuStrip();
        readonly Updater updater = new Updater();
        readonly Timer timer = new Timer { Interval = 1000 };
        readonly DateTime started = DateTime.UtcNow;
        readonly uint taskbarCreated = RegisterWindowMessage("TaskbarCreated");

        List<Result> history;
        TestStatus status;                    // latest progress report; null until the first test
        TaskbarWidget widget;
        TrayIcon trayIcon;
        DateTime lastAttempt = DateTime.MinValue;
        DateTime nextUpdateCheck = DateTime.UtcNow.AddSeconds(30);
        volatile string pendingInstaller;
        int updateBusy;

        public App(bool runAtStart)
        {
            this.runAtStart = runAtStart;
            Text = WindowTitle;
            FormBorderStyle = FormBorderStyle.FixedToolWindow;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Location = new Point(-32000, -32000);
            Size = new Size(1, 1);
            Opacity = 0;

            L.Apply(settings.Language);
            Log.Enabled = settings.DebugLog;
            history = settings.LoadHistory();
            Win11Menu.Apply(menu);
            menu.Closed += (s, e) =>
            {
                if (!flyout.ContextMenuOpen) return;
                flyout.ContextMenuOpen = false;
                // Opened from the flyout: a click elsewhere closes both, otherwise the flyout takes focus back
                // so that the next outside click still closes it.
                if (e.CloseReason == ToolStripDropDownCloseReason.AppFocusChange) flyout.CloseFlyout();
                else if (flyout.Visible) flyout.Activate();
            };

            flyout.Unit = settings.Unit;
            flyout.SetHistory(history);
            flyout.StartClicked += () => ToggleTest();
            flyout.SettingsClicked += p => ShowMenu(p, true);

            test.Changed += st => BeginInvokeSafe(() => OnStatus(st));
            timer.Tick += (s, e) => Tick();
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            Autostart.Repair();
            SyncWidget();
            SyncTrayIcon();
            timer.Start();
            if (runAtStart) StartTest();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            timer.Stop();
            test.Cancel();
            if (trayIcon != null) trayIcon.Dispose();
            base.OnFormClosed(e);
        }

        void BeginInvokeSafe(Action a)
        {
            try { if (IsHandleCreated && !IsDisposed) BeginInvoke(a); }
            catch (InvalidOperationException) { }
        }

        // --- the test ---

        void ToggleTest()
        {
            if (test.Running) test.Cancel();
            else StartTest();
        }

        void StartTest()
        {
            if (test.Running) return;
            lastAttempt = DateTime.UtcNow;
            test.Seconds = settings.Seconds;
            test.Start();
        }

        void OnStatus(TestStatus st)
        {
            status = st;
            flyout.SetStatus(st);
            if (!st.Running && st.Result.Complete)
            {
                history.Insert(0, st.Result);
                if (history.Count > Settings.HistoryLength) history.RemoveRange(Settings.HistoryLength, history.Count - Settings.HistoryLength);
                settings.SaveHistory(history);
                settings.Save();
                flyout.SetHistory(history);
            }
            UpdateWidget();
            if (!st.Running) UpdateTrayIcon();
        }

        // Runs a test when the last one is older than the chosen interval. Waits a minute after start-up, and
        // skips while a game (or anything fullscreen) is in front or the network is down.
        void MaybeAutoTest()
        {
            if (settings.AutoTestHours <= 0 || test.Running) return;
            DateTime now = DateTime.UtcNow;
            if (now - started < TimeSpan.FromMinutes(1)) return;
            DateTime last = history.Count > 0 ? history[0].Time : DateTime.MinValue;
            if (now - last < TimeSpan.FromHours(settings.AutoTestHours)) return;
            if (now - lastAttempt < TimeSpan.FromMinutes(30)) return;   // failed or stopped not long ago
            if (FullscreenAppInFront() || !NetworkInterface.GetIsNetworkAvailable()) return;
            Log.Write("automatic test");
            StartTest();
        }

        // The latest values to show: the running test, or the last one that went through.
        Result Latest
        {
            get
            {
                if (status != null && (status.Running || status.Result.Complete)) return status.Result;
                return history.Count > 0 ? history[0] : null;
            }
        }

        // 10 % for the ping, then 45 % for each direction.
        static double Overall(TestStatus st)
        {
            switch (st.Phase)
            {
                case Phase.Ping: return 0.1 * st.Progress;
                case Phase.Download: return 0.1 + 0.45 * st.Progress;
                case Phase.Upload: return 0.55 + 0.45 * st.Progress;
                default: return 0;
            }
        }

        string Tooltip()
        {
            var lines = new List<string>();
            if (status != null && status.Running)
                lines.Add(L.T("Testing… ", "Test ediliyor… ") + (int)Math.Round(Overall(status) * 100) + "%");
            Result r = Latest;
            if (r == null)
                lines.Add(L.T("No speed test yet. Click to start one.", "Henüz hız testi yok. Başlatmak için tıkla."));
            else
            {
                lines.Add(L.T("Download: ", "İndirme: ") + L.SpeedWithUnit(r.DownMbps, settings.Unit));
                lines.Add(L.T("Upload: ", "Yükleme: ") + L.SpeedWithUnit(r.UpMbps, settings.Unit));
                lines.Add("Ping: " + L.Ms(r.PingMs) + " ms (jitter " + L.Ms(r.JitterMs) + " ms)");
                if (r.Server.Length > 0) lines.Add("Cloudflare " + r.Server);
                if (!(status != null && status.Running)) lines.Add(L.T("Tested ", "Test: ") + L.Ago(r.Time));
            }
            if (status != null && !status.Running && status.Result.Error != null && !status.Cancelled)
                lines.Add(L.T("Last test failed: ", "Son test başarısız: ") + status.Result.Error);
            return string.Join("\n", lines);
        }

        // --- taskbar widget ---

        // Creates, recreates (force: the taskbar was rebuilt) or removes the widget to match the settings.
        void SyncWidget(bool force = false)
        {
            if (widget != null && (force || !settings.ShowWidget || widget.Dead || widget.IsDisposed))
            {
                if (!widget.IsDisposed) widget.Close();
                widget.Dispose();
                widget = null;
            }
            if (settings.ShowWidget && widget == null)
            {
                widget = new TaskbarWidget();
                widget.Clicked += r => ToggleFlyout(r);
                widget.RightClicked += p => ShowMenu(p, false);
                widget.Moved += () =>
                {
                    settings.WidgetOffset = widget.Offset;
                    settings.Save();
                };
                widget.Show();
            }
            if (widget != null)
            {
                widget.Side = settings.WidgetAnchor;
                widget.Offset = settings.WidgetOffset;
                widget.TwoLines = settings.TwoLines;
            }
            UpdateWidget();
        }

        void UpdateWidget()
        {
            if (widget == null) return;
            bool running = status != null && status.Running;
            Result r = Latest;
            var items = new List<TaskbarWidget.Item>();
            if (r == null && !running)
                items.Add(new TaskbarWidget.Item { Glyph = Glyphs.Speed, Text = L.T("Speed test", "Hız testi") });
            else
            {
                // While testing, the phase being measured shows its live value and the ones still to come are dimmed.
                Phase phase = running ? status.Phase : Phase.Done;
                string unit = settings.WidgetUnits ? " " + L.UnitName(settings.Unit) : "";
                string ms = settings.WidgetUnits ? " ms" : "";
                if (settings.WidgetDownload) items.Add(Value(Glyphs.Download, Phase.Download, phase, r.DownMbps, L.Speed(r.DownMbps, settings.Unit) + unit));
                if (settings.WidgetUpload) items.Add(Value(Glyphs.Upload, Phase.Upload, phase, r.UpMbps, L.Speed(r.UpMbps, settings.Unit) + unit));
                if (settings.WidgetPing) items.Add(Value(Glyphs.Ping, Phase.Ping, phase, r.PingMs, L.Ms(r.PingMs) + ms));
                if (items.Count == 0) items.Add(new TaskbarWidget.Item { Glyph = Glyphs.Speed });
            }
            widget.TwoLines = settings.TwoLines;
            widget.SetItems(items, running ? Overall(status) : -1, Tooltip());
        }

        TaskbarWidget.Item Value(string glyph, Phase own, Phase current, double value, string text)
        {
            var item = new TaskbarWidget.Item { Glyph = glyph, Text = text };
            if (current == Phase.Done) return item;
            bool ping = own == Phase.Ping;
            string unit = settings.WidgetUnits ? ping ? " ms" : " " + L.UnitName(settings.Unit) : "";
            item.MinText = "000" + unit;
            if (own == current)
                item.Text = (ping ? L.Ms(status.Live) : L.Speed(status.Live, settings.Unit)) + unit;
            else if (value < 0)
            {
                item.Text = "–" + unit;
                item.Dim = true;
            }
            return item;
        }

        // --- tray icon (when the widget is off, or asked for) ---

        void SyncTrayIcon()
        {
            bool want = !settings.ShowWidget || settings.ShowTrayIcon;
            if (!want && trayIcon != null)
            {
                trayIcon.Dispose();
                trayIcon = null;
            }
            else if (want && trayIcon == null)
            {
                trayIcon = new TrayIcon(Handle, TrayIconId);
                trayIcon.Click += p => ToggleFlyout(trayIcon.Bounds);
                trayIcon.ContextMenu += p => ShowMenu(Cursor.Position, false);
            }
            UpdateTrayIcon();
        }

        // Redrawn on every call (theme, scaling and tooltip changes are rare: after a test or a setting).
        void UpdateTrayIcon()
        {
            if (trayIcon != null) trayIcon.Update(Icons.Tray(TrayIconSize()), Tooltip());
        }

        int TrayIconSize()
        {
            uint dpi = 96;
            IntPtr tray = FindWindow("Shell_TrayWnd", null);
            try { if (tray != IntPtr.Zero) dpi = GetDpiForWindow(tray); }
            catch (EntryPointNotFoundException) { }
            if (dpi == 0) dpi = 96;
            try { return GetSystemMetricsForDpi(SM_CXSMICON, dpi); }
            catch (EntryPointNotFoundException) { return SystemInformation.SmallIconSize.Width; }
        }

        // --- flyout ---

        void ToggleFlyout(Rectangle anchor)
        {
            if (flyout.Visible || flyout.JustClosed)
            {
                flyout.CloseFlyout();
                return;
            }
            flyout.Unit = settings.Unit;
            flyout.ShowAt(anchor);
        }

        void Tick()
        {
            if (settings.ShowWidget && (widget == null || widget.Dead || widget.IsDisposed)) SyncWidget(true);
            if (widget != null && !(status != null && status.Running)) UpdateWidget();   // follows the taskbar, refreshes "5 min ago"
            MaybeAutoTest();
            if (settings.CheckUpdates && !Program.Dev && DateTime.UtcNow >= nextUpdateCheck)
            {
                nextUpdateCheck = DateTime.UtcNow.AddHours(6);
                ScheduleUpdateCheck(false);
            }
            if (pendingInstaller != null && !test.Running && !FullscreenAppInFront()) RunPendingInstaller();
            if (flyout.Visible && !test.Running) flyout.Invalidate();   // "last test 1 min ago"
        }

        // --- menu ---

        void ShowMenu(Point at, bool fromFlyout)
        {
            menu.Items.Clear();
            BuildMenu();
            Win11Menu.Apply(menu);
            if (fromFlyout)
            {
                flyout.ContextMenuOpen = true;
                menu.Show(at, ToolStripDropDownDirection.AboveLeft);
                return;
            }
            SetForegroundWindow(Handle);
            menu.Show(at);
        }

        void BuildMenu()
        {
            var run = new ToolStripMenuItem(test.Running ? L.T("Stop the test", "Testi durdur") : L.T("Run a speed test", "Hız testi yap"), null, (s, e) => ToggleTest());
            run.Font = new Font(run.Font, FontStyle.Bold);

            var updateItem = new ToolStripMenuItem(L.T("Install update ", "Güncellemeyi yükle: ") + updater.LatestText, null, (s, e) => InstallUpdate());
            updateItem.Font = new Font(updateItem.Font, FontStyle.Bold);
            updateItem.Visible = updater.UpdateAvailable;

            var bar = new ToolStripMenuItem(L.T("Taskbar", "Görev çubuğu"));
            bar.DropDownItems.Add(Toggle(L.T("Show results in the taskbar", "Sonuçları görev çubuğunda göster"), settings.ShowWidget, v => settings.ShowWidget = v));
            bar.DropDownItems.Add(Toggle(L.T("Show the tray icon too", "Bildirim alanı simgesini de göster"), settings.ShowTrayIcon || !settings.ShowWidget, v => settings.ShowTrayIcon = v));
            bar.DropDownItems.Add(new ToolStripSeparator());
            bar.DropDownItems.Add(Toggle(L.T("Download", "İndirme"), settings.WidgetDownload, v => settings.WidgetDownload = v));
            bar.DropDownItems.Add(Toggle(L.T("Upload", "Yükleme"), settings.WidgetUpload, v => settings.WidgetUpload = v));
            bar.DropDownItems.Add(Toggle("Ping", settings.WidgetPing, v => settings.WidgetPing = v));
            bar.DropDownItems.Add(new ToolStripSeparator());
            bar.DropDownItems.Add(Toggle(L.T("Two lines, like the clock", "Saat gibi iki satır"), settings.TwoLines, v => settings.TwoLines = v));
            bar.DropDownItems.Add(Toggle(L.T("Show units", "Birimleri göster"), settings.WidgetUnits, v => settings.WidgetUnits = v));
            bar.DropDownItems.Add(new ToolStripSeparator());
            bar.DropDownItems.Add(Choice(L.T("Next to the clock (right)", "Saatin yanında (sağ)"), settings.WidgetAnchor == 0, () => { settings.WidgetAnchor = 0; settings.WidgetOffset = 8; }));
            bar.DropDownItems.Add(Choice(L.T("Left edge of the taskbar", "Görev çubuğunun solunda"), settings.WidgetAnchor == 1, () => { settings.WidgetAnchor = 1; settings.WidgetOffset = 8; }));
            bar.DropDownItems.Add(new ToolStripSeparator());
            bar.DropDownItems.Add(new ToolStripMenuItem(L.T("Tip: drag it to fine-tune the position", "İpucu: konumu ince ayarlamak için sürükle")) { Enabled = false });
            KeepOpen(bar);

            var unit = new ToolStripMenuItem(L.T("Unit", "Birim"));
            unit.DropDownItems.Add(Choice(L.T("Megabits per second (Mbps)", "Saniyede megabit (Mbps)"), settings.Unit == 0, () => settings.Unit = 0));
            unit.DropDownItems.Add(Choice(L.T("Megabytes per second (MB/s)", "Saniyede megabayt (MB/s)"), settings.Unit == 1, () => settings.Unit = 1));

            var length = new ToolStripMenuItem(L.T("Test length", "Test süresi"));
            foreach (int sec in new[] { 5, 10, 15 })
            {
                int v = sec;
                string name = sec == 5 ? L.T("Quick", "Hızlı") : sec == 10 ? L.T("Standard", "Standart") : L.T("Thorough", "Kapsamlı");
                length.DropDownItems.Add(Choice(name + L.T(" (" + sec + " s each way)", " (her yön " + sec + " sn)"), settings.Seconds == sec, () => settings.Seconds = v));
            }

            var auto = new ToolStripMenuItem(L.T("Automatic test", "Otomatik test"));
            auto.DropDownItems.Add(Choice(L.T("Off", "Kapalı"), settings.AutoTestHours == 0, () => settings.AutoTestHours = 0));
            foreach (int h in new[] { 1, 3, 6, 12, 24 })
            {
                int v = h;
                string text = h == 1 ? L.T("Every hour", "Saatte bir") : h == 24 ? L.T("Once a day", "Günde bir") : L.T("Every " + h + " hours", h + " saatte bir");
                auto.DropDownItems.Add(Choice(text, settings.AutoTestHours == h, () => settings.AutoTestHours = v));
            }
            auto.DropDownItems.Add(new ToolStripSeparator());
            auto.DropDownItems.Add(new ToolStripMenuItem(L.T("Skipped while a fullscreen app is in front", "Tam ekran bir uygulama açıkken atlanır")) { Enabled = false });

            var past = new ToolStripMenuItem(L.T("Results", "Sonuçlar"));
            past.DropDownItems.Add(new ToolStripMenuItem(L.T("Copy the last result", "Son sonucu kopyala"), null, (s, e) => CopyLast()) { Enabled = history.Count > 0 });
            past.DropDownItems.Add(new ToolStripMenuItem(L.T("Copy all results (CSV)", "Tüm sonuçları kopyala (CSV)"), null, (s, e) => CopyCsv()) { Enabled = history.Count > 0 });
            past.DropDownItems.Add(new ToolStripSeparator());
            past.DropDownItems.Add(new ToolStripMenuItem(L.T("Clear history", "Geçmişi temizle"), null, (s, e) => ClearHistory()) { Enabled = history.Count > 0 });

            var language = new ToolStripMenuItem(L.T("Language / Dil", "Dil / Language"));
            language.DropDownItems.Add(Choice(L.T("Windows language", "Windows dili"), settings.Language == 0, () => SetLanguage(0)));
            language.DropDownItems.Add(Choice("Türkçe", settings.Language == 1, () => SetLanguage(1)));
            language.DropDownItems.Add(Choice("English", settings.Language == 2, () => SetLanguage(2)));

            var about = new ToolStripMenuItem(L.T("Updates", "Güncellemeler"));
            about.DropDownItems.Add(new ToolStripMenuItem(AppName + " " + Updater.CurrentText) { Enabled = false });
            about.DropDownItems.Add(Toggle(L.T("Check automatically", "Otomatik denetle"), settings.CheckUpdates, v => settings.CheckUpdates = v));
            about.DropDownItems.Add(Toggle(L.T("Install automatically in the background", "Arka planda otomatik yükle"), settings.AutoInstallUpdates, v => settings.AutoInstallUpdates = v));
            about.DropDownItems.Add(new ToolStripMenuItem(L.T("Check now", "Şimdi denetle"), null, (s, e) => ScheduleUpdateCheck(true)));
            about.DropDownItems.Add(new ToolStripMenuItem(L.T("Open GitHub page", "GitHub sayfasını aç"), null, (s, e) => Open("https://github.com/" + Updater.Repo)));
            about.DropDownItems.Add(new ToolStripSeparator());
            about.DropDownItems.Add(Toggle(L.T("Diagnostic log", "Tanılama günlüğü"), settings.DebugLog, v => { settings.DebugLog = v; Log.Enabled = v; }));
            about.DropDownItems.Add(new ToolStripMenuItem(L.T("Open log file", "Günlük dosyasını aç"), null, (s, e) => Open(Log.FilePath)) { Enabled = System.IO.File.Exists(Log.FilePath) });

            menu.Items.Add(run);
            menu.Items.Add(updateItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(bar);
            menu.Items.Add(unit);
            menu.Items.Add(length);
            menu.Items.Add(auto);
            menu.Items.Add(past);
            menu.Items.Add(language);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(Toggle(L.T("Start with Windows", "Windows ile başlat"), Autostart.IsEnabled(), v => Autostart.Set(v)));
            menu.Items.Add(about);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem(L.T("Exit", "Çıkış"), null, (s, e) => Close()));
        }

        string Describe(Result r)
        {
            return L.T("Download ", "İndirme ") + L.SpeedWithUnit(r.DownMbps, settings.Unit) +
                L.T(" · Upload ", " · Yükleme ") + L.SpeedWithUnit(r.UpMbps, settings.Unit) +
                " · Ping " + L.Ms(r.PingMs) + " ms (jitter " + L.Ms(r.JitterMs) + " ms)" +
                (r.Server.Length > 0 ? " · Cloudflare " + r.Server : "") +
                " · " + r.Time.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        }

        void CopyLast()
        {
            if (history.Count > 0) SetClipboard(Describe(history[0]));
        }

        // Values in Mbps and ms with a dot as decimal separator, for spreadsheets.
        void CopyCsv()
        {
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            var lines = new List<string> { "time,download_mbps,upload_mbps,ping_ms,jitter_ms,server,isp,network" };
            foreach (Result r in history)
                lines.Add(string.Join(",", r.Time.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", ci), r.DownMbps.ToString("0.0", ci), r.UpMbps.ToString("0.0", ci),
                    r.PingMs.ToString("0.0", ci), r.JitterMs.ToString("0.0", ci), Csv(r.Server), Csv(r.Isp), Csv(r.Link)));
            SetClipboard(string.Join("\r\n", lines));
        }

        static string Csv(string s)
        {
            return s.IndexOfAny(new[] { ',', '"' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
        }

        static void SetClipboard(string text)
        {
            try { Clipboard.SetText(text); }
            catch (ExternalException) { }
        }

        void ClearHistory()
        {
            string question = L.T("Delete all saved speed test results?", "Kayıtlı tüm hız testi sonuçları silinsin mi?");
            if (MessageBox.Show(question, AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            history.Clear();
            if (status != null && !status.Running) status = null;
            settings.SaveHistory(history);
            ApplySettings();
        }

        void SetLanguage(int language)
        {
            settings.Language = language;
            L.Apply(language);
        }

        void ApplySettings()
        {
            settings.Save();
            flyout.Unit = settings.Unit;
            flyout.SetHistory(history);
            if (status != null) flyout.SetStatus(status);
            SyncWidget();
            SyncTrayIcon();
        }

        ToolStripMenuItem Toggle(string text, bool value, Action<bool> set)
        {
            var item = new ToolStripMenuItem(text) { Checked = value };
            item.Click += (s, e) =>
            {
                item.Checked = !item.Checked;
                set(item.Checked);
                ApplySettings();
            };
            return item;
        }

        ToolStripMenuItem Choice(string text, bool selected, Action select)
        {
            var item = new ToolStripMenuItem(text) { Checked = selected };
            item.Click += (s, e) =>
            {
                select();
                var parent = item.Owner;
                if (parent != null)
                    foreach (ToolStripItem other in parent.Items)
                    {
                        var mi = other as ToolStripMenuItem;
                        if (mi != null && mi.Checked && mi != item && !mi.HasDropDownItems && IsChoiceGroup(parent, mi, item)) mi.Checked = false;
                    }
                item.Checked = true;
                ApplySettings();
            };
            return item;
        }

        // Choices are grouped by separators within a submenu.
        static bool IsChoiceGroup(ToolStrip strip, ToolStripItem a, ToolStripItem b)
        {
            int ia = strip.Items.IndexOf(a), ib = strip.Items.IndexOf(b);
            for (int i = Math.Min(ia, ib); i <= Math.Max(ia, ib); i++)
                if (strip.Items[i] is ToolStripSeparator) return false;
            return true;
        }

        // Lets several toggles be flipped without the submenu closing after each click.
        static void KeepOpen(ToolStripMenuItem parent)
        {
            parent.DropDown.Closing += (s, e) =>
            {
                if (e.CloseReason == ToolStripDropDownCloseReason.ItemClicked) e.Cancel = true;
            };
        }

        static void Open(string target)
        {
            try { Process.Start(target); }
            catch (Exception) { }
        }

        // --- updates (same flow as TaskbarStatus) ---

        void ScheduleUpdateCheck(bool manual)
        {
            if (Interlocked.CompareExchange(ref updateBusy, 1, 0) != 0) return;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                bool ok = updater.Check();
                if (!ok) nextUpdateCheck = DateTime.UtcNow.AddHours(1);
                string error = ok ? null : L.T("Could not reach GitHub.", "GitHub'a ulaşılamadı.");
                if (ok && updater.UpdateAvailable && (manual || settings.AutoInstallUpdates))
                    error = DownloadUpdate();
                updateBusy = 0;
                if (!manual || (error == null && updater.UpdateAvailable)) return;
                BeginInvokeSafe(() => MessageBox.Show(
                    error ?? L.T("You are up to date (", "Güncelsin (") + Updater.CurrentText + ").",
                    AppName, MessageBoxButtons.OK, error != null ? MessageBoxIcon.Warning : MessageBoxIcon.Information));
            });
        }

        void InstallUpdate()
        {
            if (Interlocked.CompareExchange(ref updateBusy, 1, 0) != 0) return;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                string error = DownloadUpdate();
                updateBusy = 0;
                if (error != null) BeginInvokeSafe(() => MessageBox.Show(error, AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning));
            });
        }

        string DownloadUpdate()
        {
            try
            {
                pendingInstaller = updater.Download();
                return null;
            }
            catch (Exception ex)
            {
                return L.T("The update could not be downloaded: ", "Güncelleme indirilemedi: ") + ex.Message;
            }
        }

        void RunPendingInstaller()
        {
            string installer = pendingInstaller;
            pendingInstaller = null;
            try { Updater.InstallSilently(installer); }
            catch (System.ComponentModel.Win32Exception) { }
        }

        // Installs and automatic tests wait while a game (or anything fullscreen) is in front.
        static bool FullscreenAppInFront()
        {
            IntPtr fg = GetForegroundWindow();
            if (fg == IntPtr.Zero || fg == GetDesktopWindow() || fg == GetShellWindow()) return false;
            RECT r;
            if (!GetWindowRect(fg, out r)) return false;
            Rectangle b = Screen.FromHandle(fg).Bounds;
            return r.Left <= b.Left && r.Top <= b.Top && r.Right >= b.Right && r.Bottom >= b.Bottom;
        }

        // --- window messages ---

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == TrayIcon.CallbackMessage)
            {
                if (trayIcon != null) trayIcon.HandleMessage(m);
                return;
            }
            if (m.Msg == taskbarCreated && taskbarCreated != 0)
            {
                if (trayIcon != null) trayIcon.Recreate();
                if (settings.ShowWidget) SyncWidget(true);
                UpdateTrayIcon();
            }
            else if (m.Msg == ShowFlyoutMessage)
            {
                if (!flyout.Visible) ToggleFlyout(widget != null ? widget.ScreenBounds : trayIcon != null ? trayIcon.Bounds : Rectangle.Empty);
                return;
            }
            else if (m.Msg == RunTestMessage)
            {
                StartTest();
                return;
            }
            switch (m.Msg)
            {
                case WM_SETTINGCHANGE:
                case WM_DISPLAYCHANGE:
                case WM_DPICHANGED:
                    // Theme, accent, scaling: redraw at the new size / colours.
                    BeginInvokeSafe(() => { UpdateTrayIcon(); UpdateWidget(); });
                    break;
            }
            base.WndProc(ref m);
        }

        // --- native ---

        const int WM_SETTINGCHANGE = 0x1A, WM_DISPLAYCHANGE = 0x7E, WM_DPICHANGED = 0x2E0;
        const int SM_CXSMICON = 49;

        [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint RegisterWindowMessage(string name);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string cls, string name);
        [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr hwnd);
        [DllImport("user32.dll")] static extern int GetSystemMetricsForDpi(int index, uint dpi);
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hwnd);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern IntPtr GetDesktopWindow();
        [DllImport("user32.dll")] static extern IntPtr GetShellWindow();
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out RECT r);
    }
}
