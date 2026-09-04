using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

namespace AgentUsage.Windows
{
    internal sealed class AgentUsageForm : Form
    {
        public const string Version = "0.5.0";
        private const int CanvasWidth = 360;
        private const int CollapsedHeight = 418;
        private const int ExpandedHeight = 638;
        private static readonly int[] RefreshIntervals = { 5, 15, 30, 60, 300, 900, 1800, 3600 };

        private readonly SettingsStore settings;
        private readonly CodexClient client = new CodexClient();
        private readonly Timer refreshTimer = new Timer();
        private readonly Timer animationTimer = new Timer();
        private readonly Timer updateTimer = new Timer();
        private UsageSnapshot snapshot;
        private string error;
        private bool refreshing;
        private bool optionsExpanded;
        private string latestVersion;
        private Point logicalMouse = new Point(-100, -100);
        private string hoverText;
        private NotifyIcon tray;
        private float scale = 1;
        private ActivityPeriod? previewPeriod;

        public event EventHandler ExitRequested;
        public event EventHandler TrayAppearanceChanged;

        public AgentUsageForm(SettingsStore settings) : this(settings, true) { }

        public AgentUsageForm(SettingsStore settings, bool autoStart)
        {
            this.settings = settings;
            optionsExpanded = settings.OptionsExpanded;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            DoubleBuffered = true;
            KeyPreview = true;
            BackColor = Color.FromArgb(246, 246, 242);
            Font = new Font("Segoe UI", 12, FontStyle.Regular, GraphicsUnit.Pixel);
            UpdateScaleAndSize();

            refreshTimer.Tick += delegate { RefreshUsage(); };
            ConfigureRefreshTimer();
            animationTimer.Interval = 700;
            animationTimer.Tick += delegate { if (refreshing) Invalidate(); };

            MouseMove += HandleMouseMove;
            MouseWheel += HandleMouseWheel;
            MouseLeave += delegate { logicalMouse = new Point(-100, -100); hoverText = null; Invalidate(); };
            MouseUp += HandleMouseUp;
            Deactivate += delegate { Hide(); };
            Shown += delegate { NativeMethods.RoundWindow(Handle); };
            KeyDown += delegate(object sender, KeyEventArgs e) { if (e.KeyCode == Keys.Escape) Hide(); };

            SystemEvents.UserPreferenceChanged += HandleUserPreferenceChanged;
            if (autoStart)
            {
                RefreshUsage();
                CheckForUpdates(false);
                updateTimer.Interval = 60 * 60 * 1000;
                updateTimer.Tick += delegate { CheckForUpdates(false); };
                updateTimer.Start();
            }
        }

        public void AttachTray(NotifyIcon value)
        {
            tray = value;
            UpdateTrayAppearance();
        }

        public void SetPreviewData()
        {
            var activity = new CodexActivitySnapshot {
                LifetimeTokens = 27200000000,
                PeakDailyTokens = 1950000000,
                CurrentStreakDays = 20,
                LongestStreakDays = 38,
                LongestRunningTurnSeconds = 5420
            };
            DateTime start = DateTime.Today.AddDays(-167);
            var random = new Random(42);
            for (int i = 0; i < 168; i++)
            {
                double trend = 90000000 + i * 3500000;
                double wave = (Math.Sin(i * .31) + 1) * 110000000;
                long tokens = random.NextDouble() < .18 ? 0 : (long)((trend + wave) * (.35 + random.NextDouble()));
                if (i == 157) tokens = 1950000000;
                activity.DailyUsage.Add(new TokenUsageDay { StartDate = start.AddDays(i).ToString("yyyy-MM-dd"), Tokens = tokens });
            }
            snapshot = new UsageSnapshot {
                Plan = "pro",
                UpdatedAt = DateTime.Now,
                FiveHour = new RateWindow { UsedPercent = 14, WindowMinutes = 300, ResetsAt = DateTime.Now.AddHours(2.4) },
                SevenDay = new RateWindow { UsedPercent = 4, WindowMinutes = 10080, ResetsAt = DateTime.Now.AddDays(3.2) },
                Credits = new CreditSnapshot { HasCredits = false, Unlimited = false },
                AvailableResets = 4,
                Activity = activity
            };
        }

        public void RenderPreview(string path, ActivityPeriod period, bool expanded)
        {
            previewPeriod = period;
            optionsExpanded = expanded;
            UpdateScaleAndSize();
            CreateControl();
            using (var bitmap = new Bitmap(ClientSize.Width, ClientSize.Height))
            {
                DrawToBitmap(bitmap, new Rectangle(Point.Empty, ClientSize));
                bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                refreshTimer.Dispose();
                animationTimer.Dispose();
                updateTimer.Dispose();
                SystemEvents.UserPreferenceChanged -= HandleUserPreferenceChanged;
            }
            base.Dispose(disposing);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                return;
            }
            base.OnFormClosing(e);
        }

        private void HandleUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            if (IsDisposed) return;
            BeginInvoke((MethodInvoker)delegate { Invalidate(); UpdateTrayAppearance(); });
        }

        private void UpdateScaleAndSize()
        {
            using (Graphics graphics = CreateGraphics()) scale = Math.Max(1, graphics.DpiX / 96f);
            ClientSize = new Size((int)Math.Ceiling(CanvasWidth * scale),
                (int)Math.Ceiling((optionsExpanded ? ExpandedHeight : CollapsedHeight) * scale));
        }

        public void ToggleNearTaskbar()
        {
            if (Visible) { Hide(); return; }
            UpdateScaleAndSize();
            Screen screen = Screen.FromPoint(Cursor.Position);
            Rectangle work = screen.WorkingArea;
            Location = new Point(work.Right - Width - (int)(8 * scale), work.Bottom - Height - (int)(8 * scale));
            Show();
            Activate();
            BringToFront();
        }

        public void RefreshUsage()
        {
            if (refreshing) return;
            refreshing = true;
            error = null;
            animationTimer.Start();
            Invalidate();
            client.FetchAsync().ContinueWith(task =>
            {
                if (IsDisposed) return;
                BeginInvoke((MethodInvoker)delegate
                {
                    refreshing = false;
                    animationTimer.Stop();
                    if (task.IsFaulted)
                    {
                        Exception issue = task.Exception == null ? null : task.Exception.GetBaseException();
                        error = issue == null ? "Unable to fetch Codex usage." : issue.Message;
                    }
                    else
                    {
                        snapshot = task.Result;
                        error = null;
                    }
                    UpdateTrayAppearance();
                    Invalidate();
                });
            });
        }

        private void ConfigureRefreshTimer()
        {
            refreshTimer.Stop();
            refreshTimer.Interval = Math.Max(1000, settings.RefreshSeconds * 1000);
            refreshTimer.Start();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            g.ScaleTransform(scale, scale);
            Theme theme = Theme.Current;
            g.Clear(theme.Background);

            DrawHeader(g, theme);
            DrawDivider(g, theme, 48);
            DrawProvider(g, theme);
            DrawDivider(g, theme, 374);
            DrawOptions(g, theme);
            DrawBorder(g, theme);
            DrawHover(g, theme);
        }

        private void DrawHeader(Graphics g, Theme theme)
        {
            DrawText(g, "AgentUsage", theme.Text, 12, 13, 105, 22, 14, FontStyle.Bold, StringAlignment.Near);
            DrawText(g, "v" + Version, theme.Secondary, 124, 16, 80, 18, 10, FontStyle.Regular, StringAlignment.Near);
            DrawRefreshGlyph(g, theme, new RectangleF(327, 12, 22, 22), refreshing);
        }

        private void DrawProvider(Graphics g, Theme theme)
        {
            DrawText(g, "Codex", theme.Text, 12, 61, 70, 22, 14, FontStyle.Bold, StringAlignment.Near);
            string plan = snapshot == null || string.IsNullOrEmpty(snapshot.Plan) ? "" : FriendlyPlan(snapshot.Plan);
            DrawText(g, plan, theme.Text, 77, 64, 105, 18, 10, FontStyle.Regular, StringAlignment.Near);
            string updated = snapshot != null ? "Updated " + snapshot.UpdatedAt.ToString("h:mm tt") : refreshing ? "Refreshing..." : "No data";
            DrawText(g, updated, theme.Secondary, 184, 64, 139, 18, 10, FontStyle.Regular, StringAlignment.Far);
            DrawRefreshGlyph(g, theme, new RectangleF(327, 59, 22, 22), refreshing);

            string streak = "Current streak --  ·  Longest --";
            if (snapshot != null && snapshot.Activity != null)
            {
                streak = "Current streak " + Days(snapshot.Activity.CurrentStreakDays) + "  ·  Longest " + Days(snapshot.Activity.LongestStreakDays);
            }
            DrawText(g, streak, theme.Secondary, 12, 84, 336, 18, 10, FontStyle.Regular, StringAlignment.Near);

            float y = 108;
            if (snapshot != null && snapshot.FiveHour != null) { DrawUsageBar(g, theme, snapshot.FiveHour, y); y += 34; }
            if (snapshot != null && snapshot.SevenDay != null) { DrawUsageBar(g, theme, snapshot.SevenDay, y); y += 34; }
            if (snapshot == null || (snapshot.FiveHour == null && snapshot.SevenDay == null))
            {
                DrawEmptyUsageBar(g, theme, y);
                y += 34;
            }

            CodexActivitySnapshot activity = snapshot == null ? null : snapshot.Activity;
            string tokens = activity == null ? "-- all time  ·  -- peak" : TokenSummary(activity);
            DrawText(g, "Tokens", theme.Secondary, 12, 178, 60, 18, 10, FontStyle.Regular, StringAlignment.Near);
            DrawText(g, tokens, theme.Text, 70, 178, 278, 18, 10, FontStyle.Regular, StringAlignment.Far);
            string resets = snapshot != null && snapshot.AvailableResets.HasValue
                ? "Resets: " + snapshot.AvailableResets.Value + " available" : "Resets: --";
            DrawText(g, resets, theme.Text, 12, 198, 180, 18, 10, FontStyle.Regular, StringAlignment.Near);
            DrawText(g, "Credits: " + DisplayFormatter.Credits(snapshot == null ? null : snapshot.Credits),
                theme.Text, 195, 198, 153, 18, 10, FontStyle.Regular, StringAlignment.Far);

            if (!string.IsNullOrEmpty(error))
            {
                string compact = error.Length > 75 ? error.Substring(0, 72) + "..." : error;
                DrawText(g, compact, theme.Error, 12, 218, 336, 18, 9, FontStyle.Regular, StringAlignment.Near);
            }

            DrawSegments(g, theme, new RectangleF(12, 238, 336, 24),
                new[] { "Day", "Week", "Cumulative" }, (int)CurrentPeriod);
            DrawChart(g, theme, new RectangleF(12, 272, 336, 91), activity);
        }

        private void DrawUsageBar(Graphics g, Theme theme, RateWindow window, float y)
        {
            string reset = window.ResetsAt.HasValue ? "Refreshes " + window.ResetsAt.Value.ToString("hh:mm tt, MMM d") : "Refresh time unavailable";
            DrawText(g, window.DurationLabel, theme.Secondary, 12, y, 28, 15, 10, FontStyle.Regular, StringAlignment.Near);
            DrawText(g, reset, theme.Text, 40, y, 210, 15, 10, FontStyle.Regular, StringAlignment.Near);
            DrawText(g, DisplayFormatter.Percent(window.RemainingPercent) + " remaining", theme.Text, 252, y, 96, 15, 10, FontStyle.Regular, StringAlignment.Far);
            RectangleF track = new RectangleF(12, y + 19, 336, 6);
            using (var brush = new SolidBrush(theme.Track)) g.FillRoundedRectangle(brush, track, 3);
            RectangleF fill = track;
            fill.Width *= (float)(window.RemainingPercent / 100.0);
            if (fill.Width > 0) using (var brush = new SolidBrush(theme.Accent)) g.FillRoundedRectangle(brush, fill, 3);
        }

        private void DrawEmptyUsageBar(Graphics g, Theme theme, float y)
        {
            DrawText(g, "Limits", theme.Secondary, 12, y, 50, 15, 10, FontStyle.Regular, StringAlignment.Near);
            DrawText(g, "-- remaining", theme.Text, 252, y, 96, 15, 10, FontStyle.Regular, StringAlignment.Far);
            using (var brush = new SolidBrush(theme.Track)) g.FillRoundedRectangle(brush, new RectangleF(12, y + 19, 336, 6), 3);
        }

        private void DrawChart(Graphics g, Theme theme, RectangleF bounds, CodexActivitySnapshot activity)
        {
            if (activity == null || activity.DailyUsage.Count == 0)
            {
                DrawText(g, refreshing ? "Loading activity…" : "Activity unavailable", theme.Secondary,
                    bounds.X, bounds.Y + 30, bounds.Width, 20, 10, FontStyle.Regular, StringAlignment.Center);
                return;
            }
            switch (CurrentPeriod)
            {
                case ActivityPeriod.Week: DrawWeeklyChart(g, theme, bounds, activity); break;
                case ActivityPeriod.Cumulative: DrawCumulativeChart(g, theme, bounds, activity); break;
                default: DrawHeatmap(g, theme, bounds, activity); break;
            }
        }

        private void DrawHeatmap(Graphics g, Theme theme, RectangleF bounds, CodexActivitySnapshot activity)
        {
            var byDate = new Dictionary<DateTime, long>();
            long maximum = 1;
            foreach (TokenUsageDay item in activity.DailyUsage)
            {
                if (!item.Date.HasValue) continue;
                byDate[item.Date.Value] = item.Tokens;
                maximum = Math.Max(maximum, item.Tokens);
            }
            DateTime latest = activity.DailyUsage[activity.DailyUsage.Count - 1].Date ?? DateTime.Today;
            DateTime end = latest.AddDays(6 - WeekdayMonday(latest)).AddDays(-7 * settings.ChartOffsetWeeks);
            const int columns = 24;
            DateTime start = end.AddDays(-(columns * 7 - 1));
            float labelWidth = 16;
            float cell = Math.Min(11, (bounds.Width - labelWidth - 4) / columns);
            float left = bounds.X + labelWidth;
            float top = bounds.Y + 1;
            DrawText(g, "M", theme.Secondary, bounds.X, top + cell - 5, 12, 12, 8, FontStyle.Regular, StringAlignment.Near);
            DrawText(g, "W", theme.Secondary, bounds.X, top + cell * 3 - 5, 12, 12, 8, FontStyle.Regular, StringAlignment.Near);
            DrawText(g, "F", theme.Secondary, bounds.X, top + cell * 5 - 5, 12, 12, 8, FontStyle.Regular, StringAlignment.Near);
            for (int column = 0; column < columns; column++)
            {
                for (int row = 0; row < 7; row++)
                {
                    DateTime date = start.AddDays(column * 7 + row);
                    long value;
                    byDate.TryGetValue(date, out value);
                    float intensity = value == 0 ? 0 : (float)(0.18 + 0.82 * Math.Sqrt(value / (double)maximum));
                    Color color = value == 0 ? theme.CellEmpty : Blend(theme.CellEmpty, theme.Accent, intensity);
                    RectangleF cellRect = new RectangleF(left + column * cell + 1, top + row * cell + 1, cell - 2, cell - 2);
                    using (var brush = new SolidBrush(color)) g.FillRoundedRectangle(brush, cellRect, 2);
                    if (cellRect.Contains(logicalMouse)) hoverText = date.ToString("MMM d") + "  ·  " + DisplayFormatter.CompactTokens(value) + " tokens";
                }
            }
            DrawText(g, start.ToString("MMM d"), theme.Secondary, left, bounds.Bottom - 13, 70, 13, 8, FontStyle.Regular, StringAlignment.Near);
            DrawText(g, latest.ToString("MMM d"), theme.Secondary, bounds.Right - 70, bounds.Bottom - 13, 70, 13, 8, FontStyle.Regular, StringAlignment.Far);
        }

        private void DrawWeeklyChart(Graphics g, Theme theme, RectangleF bounds, CodexActivitySnapshot activity)
        {
            var allWeeks = MakeWeeks(activity.DailyUsage, 14);
            int endIndex = Math.Max(0, allWeeks.Count - settings.ChartOffsetWeeks);
            int startIndex = Math.Max(0, endIndex - 14);
            var weeks = allWeeks.GetRange(startIndex, endIndex - startIndex);
            long maximumValue = 1;
            foreach (WeekBucket week in weeks) maximumValue = Math.Max(maximumValue, week.Tokens);
            long axisMaximum = DisplayFormatter.RoundedMaximum(maximumValue);
            RectangleF plot = new RectangleF(bounds.X + 27, bounds.Y + 2, bounds.Width - 28, bounds.Height - 18);
            DrawText(g, DisplayFormatter.CompactAxisTokens(axisMaximum), theme.Secondary, bounds.X, plot.Y - 2, 25, 12, 8, FontStyle.Regular, StringAlignment.Near);
            DrawText(g, "0", theme.Secondary, bounds.X, plot.Bottom - 8, 25, 12, 8, FontStyle.Regular, StringAlignment.Near);
            using (var pen = new Pen(theme.Rule, 1)) g.DrawLine(pen, plot.Left, plot.Top, plot.Left, plot.Bottom);
            float slot = plot.Width / weeks.Count;
            for (int i = 0; i < weeks.Count; i++)
            {
                float height = (float)(plot.Height * weeks[i].Tokens / (double)axisMaximum);
                RectangleF bar = new RectangleF(plot.Left + i * slot + 2, plot.Bottom - height, Math.Max(2, slot - 4), height);
                using (var brush = new SolidBrush(theme.Accent)) g.FillRoundedRectangle(brush, bar, 2);
                RectangleF hit = new RectangleF(plot.Left + i * slot, plot.Top, slot, plot.Height);
                if (hit.Contains(logicalMouse)) hoverText = weeks[i].Start.ToString("MMM d") + "  ·  " + DisplayFormatter.CompactTokens(weeks[i].Tokens) + " tokens";
            }
            DrawText(g, weeks[0].Start.ToString("MMM d"), theme.Secondary, plot.Left, bounds.Bottom - 13, 70, 13, 8, FontStyle.Regular, StringAlignment.Near);
            DrawText(g, weeks[weeks.Count - 1].Start.ToString("MMM d"), theme.Secondary, plot.Right - 70, bounds.Bottom - 13, 70, 13, 8, FontStyle.Regular, StringAlignment.Far);
        }

        private void DrawCumulativeChart(Graphics g, Theme theme, RectangleF bounds, CodexActivitySnapshot activity)
        {
            var points = new List<TokenUsageDay>(activity.DailyUsage);
            long cumulative = 0;
            foreach (TokenUsageDay item in points) cumulative += Math.Max(0, item.Tokens);
            long maximum = DisplayFormatter.RoundedMaximum(cumulative);
            RectangleF plot = new RectangleF(bounds.X + 27, bounds.Y + 2, bounds.Width - 28, bounds.Height - 18);
            DrawText(g, DisplayFormatter.CompactAxisTokens(maximum), theme.Secondary, bounds.X, plot.Y - 2, 25, 12, 8, FontStyle.Regular, StringAlignment.Near);
            DrawText(g, "0", theme.Secondary, bounds.X, plot.Bottom - 8, 25, 12, 8, FontStyle.Regular, StringAlignment.Near);
            using (var pen = new Pen(theme.Rule, 1)) g.DrawLine(pen, plot.Left, plot.Top, plot.Left, plot.Bottom);
            if (points.Count > 1)
            {
                var line = new PointF[points.Count];
                long running = 0;
                for (int i = 0; i < points.Count; i++)
                {
                    running += Math.Max(0, points[i].Tokens);
                    line[i] = new PointF(plot.Left + plot.Width * i / (points.Count - 1f), plot.Bottom - plot.Height * running / maximum);
                    if (Math.Abs(logicalMouse.X - line[i].X) < 4 && logicalMouse.Y >= plot.Top && logicalMouse.Y <= plot.Bottom)
                        hoverText = points[i].StartDate + "  ·  " + DisplayFormatter.CompactTokens(running) + " total";
                }
                using (var pen = new Pen(theme.Accent, 2)) { pen.LineJoin = LineJoin.Round; g.DrawLines(pen, line); }
                DrawText(g, points[0].Date.HasValue ? points[0].Date.Value.ToString("MMM d") : "", theme.Secondary,
                    plot.Left, bounds.Bottom - 13, 70, 13, 8, FontStyle.Regular, StringAlignment.Near);
                TokenUsageDay last = points[points.Count - 1];
                DrawText(g, last.Date.HasValue ? last.Date.Value.ToString("MMM d") : "", theme.Secondary,
                    plot.Right - 70, bounds.Bottom - 13, 70, 13, 8, FontStyle.Regular, StringAlignment.Far);
            }
        }

        private void DrawOptions(Graphics g, Theme theme)
        {
            DrawText(g, "Options", theme.Text, 12, 387, 90, 20, 13, FontStyle.Bold, StringAlignment.Near);
            using (var pen = new Pen(theme.Secondary, 1.6f))
            {
                PointF[] arrow = optionsExpanded
                    ? new[] { new PointF(330, 392), new PointF(336, 398), new PointF(342, 392) }
                    : new[] { new PointF(333, 389), new PointF(339, 395), new PointF(333, 401) };
                g.DrawLines(pen, arrow);
            }
            if (!optionsExpanded) return;

            DrawOptionLabel(g, theme, "Metric", 431);
            DrawSegments(g, theme, new RectangleF(70, 427, 278, 27),
                new[] { "5h%", "7d%", "All limits", "Billing $" }, (int)settings.Metric);
            DrawOptionLabel(g, theme, "Display", 469);
            DrawSegments(g, theme, new RectangleF(70, 465, 278, 27),
                new[] { "Ring", "Percentage", "Ring + Percentage" }, (int)settings.DisplayMode);
            DrawOptionLabel(g, theme, "Refresh", 508);
            DrawRefreshSlider(g, theme, new RectangleF(80, 501, 218, 28));
            DrawText(g, RefreshLabel(settings.RefreshSeconds), theme.Text, 301, 506, 47, 18, 11, FontStyle.Regular, StringAlignment.Far);

            DrawText(g, "Open on Startup", theme.Text, 12, 545, 180, 20, 11, FontStyle.Bold, StringAlignment.Near);
            DrawSwitch(g, theme, new RectangleF(308, 541, 40, 22), SettingsStore.OpenOnStartup);
            DrawText(g, latestVersion == null ? "Check for Updates" : "Update v" + latestVersion + " Available - Open",
                theme.Text, 12, 578, 336, 22, 11, FontStyle.Bold, StringAlignment.Near);
            DrawText(g, "Quit AgentUsage", theme.Text, 12, 609, 336, 22, 11, FontStyle.Bold, StringAlignment.Near);
        }

        private ActivityPeriod CurrentPeriod { get { return previewPeriod ?? settings.Period; } }

        private void DrawRefreshSlider(Graphics g, Theme theme, RectangleF bounds)
        {
            int index = NearestRefreshIndex(settings.RefreshSeconds);
            float left = bounds.Left + 5;
            float right = bounds.Right - 5;
            float y = bounds.Top + bounds.Height / 2;
            using (var pen = new Pen(theme.Track, 2)) g.DrawLine(pen, left, y, right, y);
            float x = left + (right - left) * index / (RefreshIntervals.Length - 1f);
            using (var pen = new Pen(theme.Accent, 2)) g.DrawLine(pen, left, y, x, y);
            for (int i = 0; i < RefreshIntervals.Length; i++)
            {
                float tick = left + (right - left) * i / (RefreshIntervals.Length - 1f);
                using (var pen = new Pen(i <= index ? theme.Accent : theme.Secondary, 1)) g.DrawLine(pen, tick, y - 3, tick, y + 3);
            }
            using (var brush = new SolidBrush(theme.Accent)) g.FillRoundedRectangle(brush, new RectangleF(x - 4, y - 9, 8, 18), 4);
        }

        private void DrawSegments(Graphics g, Theme theme, RectangleF bounds, string[] labels, int selected)
        {
            using (var brush = new SolidBrush(theme.Control)) g.FillRoundedRectangle(brush, bounds, 7);
            float width = bounds.Width / labels.Length;
            RectangleF selectedRect = new RectangleF(bounds.Left + width * selected, bounds.Top, width, bounds.Height);
            using (var brush = new SolidBrush(theme.Selected)) g.FillRoundedRectangle(brush, selectedRect, 7);
            for (int i = 1; i < labels.Length; i++)
            {
                using (var pen = new Pen(theme.Rule, 1)) g.DrawLine(pen, bounds.Left + width * i, bounds.Top + 5, bounds.Left + width * i, bounds.Bottom - 5);
            }
            for (int i = 0; i < labels.Length; i++)
            {
                Color color = i == selected ? theme.SelectedText : theme.Text;
                DrawText(g, labels[i], color, bounds.Left + width * i, bounds.Top + 4, width, bounds.Height - 6,
                    10, i == selected ? FontStyle.Bold : FontStyle.Regular, StringAlignment.Center);
            }
        }

        private void DrawSwitch(Graphics g, Theme theme, RectangleF bounds, bool enabled)
        {
            using (var brush = new SolidBrush(enabled ? theme.Accent : theme.Track)) g.FillRoundedRectangle(brush, bounds, bounds.Height / 2);
            float diameter = bounds.Height - 4;
            float x = enabled ? bounds.Right - diameter - 2 : bounds.Left + 2;
            using (var brush = new SolidBrush(Color.White)) g.FillEllipse(brush, x, bounds.Top + 2, diameter, diameter);
        }

        private void DrawRefreshGlyph(Graphics g, Theme theme, RectangleF bounds, bool spinning)
        {
            float phase = spinning ? (Environment.TickCount / 80) % 360 : 35;
            using (var pen = new Pen(theme.Secondary, 1.7f))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                g.DrawArc(pen, bounds.X + 4, bounds.Y + 4, bounds.Width - 8, bounds.Height - 8, phase, 285);
                double angle = (phase + 285) * Math.PI / 180;
                float cx = bounds.X + bounds.Width / 2;
                float cy = bounds.Y + bounds.Height / 2;
                float radius = (bounds.Width - 8) / 2;
                PointF tip = new PointF(cx + radius * (float)Math.Cos(angle), cy + radius * (float)Math.Sin(angle));
                g.DrawLine(pen, tip, new PointF(tip.X - 4, tip.Y - 1));
                g.DrawLine(pen, tip, new PointF(tip.X - 1, tip.Y + 4));
            }
        }

        private void DrawHover(Graphics g, Theme theme)
        {
            if (new RectangleF(12, 195, 180, 22).Contains(logicalMouse) && snapshot != null)
                hoverText = DisplayFormatter.ResetHelp(snapshot);
            if (string.IsNullOrEmpty(hoverText)) return;
            string[] lines = hoverText.Split(new[] { Environment.NewLine }, StringSplitOptions.None);
            float width = 0;
            using (Font font = PixelFont(10, FontStyle.Regular))
                foreach (string line in lines) width = Math.Max(width, g.MeasureString(line, font).Width);
            width = Math.Min(320, width + 18);
            float height = lines.Length * 16 + 10;
            float x = Math.Max(6, Math.Min(CanvasWidth - width - 6, logicalMouse.X - width / 2));
            float y = logicalMouse.Y - height - 10;
            if (y < 6) y = logicalMouse.Y + 14;
            RectangleF bubble = new RectangleF(x, y, width, height);
            using (var brush = new SolidBrush(theme.Tooltip)) g.FillRoundedRectangle(brush, bubble, 7);
            for (int i = 0; i < lines.Length; i++)
                DrawText(g, lines[i], theme.TooltipText, bubble.X + 9, bubble.Y + 5 + i * 16,
                    bubble.Width - 18, 16, 9, FontStyle.Regular, StringAlignment.Near);
        }

        private void HandleMouseMove(object sender, MouseEventArgs e)
        {
            logicalMouse = new Point((int)(e.X / scale), (int)(e.Y / scale));
            hoverText = null;
            Cursor = HitAction(logicalMouse) != null ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }

        private void HandleMouseWheel(object sender, MouseEventArgs e)
        {
            Point point = new Point((int)(e.X / scale), (int)(e.Y / scale));
            if (!new Rectangle(12, 272, 336, 91).Contains(point) || snapshot == null || snapshot.Activity == null) return;
            int maximum = Math.Max(0, (snapshot.Activity.DailyUsage.Count + 6) / 7 - (CurrentPeriod == ActivityPeriod.Day ? 24 : 14));
            int next = Math.Max(0, Math.Min(maximum, settings.ChartOffsetWeeks + (e.Delta > 0 ? 1 : -1)));
            if (next != settings.ChartOffsetWeeks) { settings.ChartOffsetWeeks = next; Invalidate(); }
        }

        private void HandleMouseUp(object sender, MouseEventArgs e)
        {
            Point point = new Point((int)(e.X / scale), (int)(e.Y / scale));
            string action = HitAction(point);
            if (action == null) return;
            if (action == "refresh") RefreshUsage();
            else if (action == "options")
            {
                optionsExpanded = !optionsExpanded;
                settings.OptionsExpanded = optionsExpanded;
                UpdateScaleAndSize();
                ToggleNearTaskbar();
                ToggleNearTaskbar();
            }
            else if (action.StartsWith("period:")) { settings.Period = (ActivityPeriod)int.Parse(action.Substring(7)); Invalidate(); }
            else if (action.StartsWith("metric:")) { settings.Metric = (MenuMetric)int.Parse(action.Substring(7)); UpdateTrayAppearance(); Invalidate(); }
            else if (action.StartsWith("display:")) { settings.DisplayMode = (MenuDisplayMode)int.Parse(action.Substring(8)); UpdateTrayAppearance(); Invalidate(); }
            else if (action == "slider")
            {
                float fraction = Math.Max(0, Math.Min(1, (point.X - 85) / 208f));
                int index = (int)Math.Round(fraction * (RefreshIntervals.Length - 1));
                settings.RefreshSeconds = RefreshIntervals[index];
                ConfigureRefreshTimer();
                Invalidate();
            }
            else if (action == "startup") { SettingsStore.OpenOnStartup = !SettingsStore.OpenOnStartup; Invalidate(); }
            else if (action == "updates")
            {
                if (latestVersion != null) Process.Start("https://github.com/Rock-Z/AgentUsage/releases/latest");
                else CheckForUpdates(true);
            }
            else if (action == "quit" && ExitRequested != null) ExitRequested(this, EventArgs.Empty);
        }

        private string HitAction(Point p)
        {
            if (new Rectangle(322, 5, 35, 39).Contains(p) || new Rectangle(322, 54, 35, 35).Contains(p)) return "refresh";
            if (new Rectangle(12, 238, 336, 24).Contains(p)) return "period:" + Math.Min(2, (p.X - 12) / 112);
            if (new Rectangle(0, 375, 360, 43).Contains(p)) return "options";
            if (!optionsExpanded) return null;
            if (new Rectangle(70, 427, 278, 27).Contains(p)) return "metric:" + Math.Min(3, (p.X - 70) * 4 / 278);
            if (new Rectangle(70, 465, 278, 27).Contains(p)) return "display:" + Math.Min(2, (p.X - 70) * 3 / 278);
            if (new Rectangle(75, 498, 230, 34).Contains(p)) return "slider";
            if (new Rectangle(0, 535, 360, 37).Contains(p)) return "startup";
            if (new Rectangle(0, 571, 360, 32).Contains(p)) return "updates";
            if (new Rectangle(0, 603, 360, 35).Contains(p)) return "quit";
            return null;
        }

        private void CheckForUpdates(bool interactive)
        {
            var client = new WebClient();
            client.Headers[HttpRequestHeader.UserAgent] = "AgentUsage-Windows";
            client.DownloadStringCompleted += delegate(object sender, DownloadStringCompletedEventArgs e)
            {
                client.Dispose();
                if (e.Error != null)
                {
                    if (interactive && tray != null) tray.ShowBalloonTip(2500, "AgentUsage", "Could not check for updates.", ToolTipIcon.Warning);
                    return;
                }
                try
                {
                    var serializer = new JavaScriptSerializer();
                    var root = serializer.DeserializeObject(e.Result) as IDictionary<string, object>;
                    string tag = JsonValue.String(root, "tag_name");
                    if (!string.IsNullOrEmpty(tag)) tag = tag.TrimStart('v');
                    if (IsNewer(tag, Version)) latestVersion = tag;
                    else if (interactive && tray != null) tray.ShowBalloonTip(2200, "AgentUsage", "You’re up to date.", ToolTipIcon.Info);
                    Invalidate();
                }
                catch { }
            };
            client.DownloadStringAsync(new Uri("https://api.github.com/repos/Rock-Z/AgentUsage/releases/latest"));
        }

        private void UpdateTrayAppearance()
        {
            if (tray == null) return;
            Icon previous = tray.Icon;
            tray.Icon = TrayIconRenderer.Render(snapshot, settings.Metric, settings.DisplayMode, Theme.Current);
            if (previous != null) previous.Dispose();
            tray.Text = TrayIconRenderer.Tooltip(snapshot, refreshing, error);
            if (TrayAppearanceChanged != null) TrayAppearanceChanged(this, EventArgs.Empty);
        }

        private static bool IsNewer(string candidate, string current)
        {
            Version a, b;
            return System.Version.TryParse(candidate, out a) && System.Version.TryParse(current, out b) && a > b;
        }

        private static string FriendlyPlan(string plan)
        {
            if (string.IsNullOrEmpty(plan)) return "";
            string cleaned = plan.Replace('_', ' ');
            return CultureInfo.CurrentCulture.TextInfo.ToTitleCase(cleaned.ToLowerInvariant());
        }

        private static string Days(long? value) { return value.HasValue ? value.Value + "d" : "--"; }

        private static string TokenSummary(CodexActivitySnapshot activity)
        {
            string text = DisplayFormatter.CompactTokens(activity.LifetimeTokens) + " all time  ·  " + DisplayFormatter.CompactTokens(activity.PeakDailyTokens) + " peak";
            if (activity.PeakDailyTokens.HasValue)
            {
                TokenUsageDay match = activity.DailyUsage.Find(delegate(TokenUsageDay item) { return item.Tokens == activity.PeakDailyTokens.Value; });
                if (match != null && match.Date.HasValue) text += " on " + match.Date.Value.ToString("MMM d");
            }
            return text;
        }

        private static int WeekdayMonday(DateTime date) { return ((int)date.DayOfWeek + 6) % 7; }

        private static List<WeekBucket> MakeWeeks(List<TokenUsageDay> days, int minimum)
        {
            var dated = new Dictionary<DateTime, long>();
            DateTime latest = DateTime.Today;
            foreach (TokenUsageDay day in days) if (day.Date.HasValue) { dated[day.Date.Value] = day.Tokens; if (day.Date.Value > latest) latest = day.Date.Value; }
            DateTime latestMonday = latest.AddDays(-WeekdayMonday(latest));
            DateTime earliest = days.Count > 0 && days[0].Date.HasValue ? days[0].Date.Value : latest;
            DateTime earliestMonday = earliest.AddDays(-WeekdayMonday(earliest));
            int count = Math.Max(minimum, (int)((latestMonday - earliestMonday).TotalDays / 7) + 1);
            var result = new List<WeekBucket>();
            for (int i = 0; i < count; i++)
            {
                DateTime start = latestMonday.AddDays(-7 * (count - 1 - i));
                long tokens = 0;
                for (int day = 0; day < 7; day++) { long value; dated.TryGetValue(start.AddDays(day), out value); tokens += value; }
                result.Add(new WeekBucket { Start = start, Tokens = tokens });
            }
            return result;
        }

        private static int NearestRefreshIndex(int seconds)
        {
            int best = 0;
            for (int i = 1; i < RefreshIntervals.Length; i++)
                if (Math.Abs(RefreshIntervals[i] - seconds) < Math.Abs(RefreshIntervals[best] - seconds)) best = i;
            return best;
        }

        private static string RefreshLabel(int seconds)
        {
            if (seconds < 60) return seconds + "s";
            if (seconds < 3600) return (seconds / 60) + "m";
            return "1h";
        }

        private static Color Blend(Color from, Color to, float amount)
        {
            amount = Math.Max(0, Math.Min(1, amount));
            return Color.FromArgb((int)(from.R + (to.R - from.R) * amount),
                (int)(from.G + (to.G - from.G) * amount), (int)(from.B + (to.B - from.B) * amount));
        }

        private static Font PixelFont(float size, FontStyle style) { return new Font("Segoe UI", size, style, GraphicsUnit.Pixel); }

        private static void DrawText(Graphics g, string text, Color color, float x, float y, float width, float height,
            float size, FontStyle style, StringAlignment alignment)
        {
            if (string.IsNullOrEmpty(text)) return;
            using (Font font = PixelFont(size, style))
            using (var brush = new SolidBrush(color))
            using (var format = new StringFormat { Alignment = alignment, LineAlignment = StringAlignment.Near, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
                g.DrawString(text, font, brush, new RectangleF(x, y, width, height), format);
        }

        private static void DrawDivider(Graphics g, Theme theme, float y) { using (var pen = new Pen(theme.Rule, 1)) g.DrawLine(pen, 0, y, CanvasWidth, y); }
        private void DrawBorder(Graphics g, Theme theme) { using (var pen = new Pen(theme.Rule, 1)) g.DrawRectangle(pen, .5f, .5f, CanvasWidth - 1, (optionsExpanded ? ExpandedHeight : CollapsedHeight) - 1); }
        private static void DrawOptionLabel(Graphics g, Theme theme, string text, float y) { DrawText(g, text, theme.Text, 12, y, 52, 20, 11, FontStyle.Bold, StringAlignment.Near); }

        private sealed class WeekBucket { public DateTime Start; public long Tokens; }
    }

    internal sealed class Theme
    {
        public Color Background, Text, Secondary, Accent, Track, Rule, Control, Selected, SelectedText, CellEmpty, Error, Tooltip, TooltipText;

        public static Theme Current
        {
            get
            {
                bool dark = false;
                try
                {
                    object value = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1);
                    dark = Convert.ToInt32(value) == 0;
                }
                catch { }
                return dark ? Dark() : Light();
            }
        }

        private static Theme Light()
        {
            return new Theme {
                Background = Color.FromArgb(246, 246, 242), Text = Color.FromArgb(35, 35, 37), Secondary = Color.FromArgb(91, 91, 96),
                Accent = Color.FromArgb(66, 137, 246), Track = Color.FromArgb(217, 217, 210), Rule = Color.FromArgb(205, 205, 199),
                Control = Color.FromArgb(227, 227, 222), Selected = Color.FromArgb(151, 151, 145), SelectedText = Color.White,
                CellEmpty = Color.FromArgb(226, 228, 224), Error = Color.FromArgb(190, 42, 42), Tooltip = Color.FromArgb(39, 39, 42), TooltipText = Color.White
            };
        }

        private static Theme Dark()
        {
            return new Theme {
                Background = Color.FromArgb(43, 43, 48), Text = Color.FromArgb(241, 241, 243), Secondary = Color.FromArgb(190, 190, 195),
                Accent = Color.FromArgb(79, 143, 247), Track = Color.FromArgb(76, 76, 82), Rule = Color.FromArgb(84, 84, 90),
                Control = Color.FromArgb(67, 67, 73), Selected = Color.FromArgb(112, 108, 120), SelectedText = Color.White,
                CellEmpty = Color.FromArgb(65, 67, 71), Error = Color.FromArgb(255, 120, 112), Tooltip = Color.FromArgb(235, 235, 239), TooltipText = Color.FromArgb(28, 28, 31)
            };
        }
    }

    internal static class TrayIconRenderer
    {
        public static Icon Render(UsageSnapshot snapshot, MenuMetric metric, MenuDisplayMode mode, Theme theme)
        {
            using (var bitmap = new Bitmap(32, 32, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
            using (Graphics g = Graphics.FromImage(bitmap))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                RateWindow outer = null, inner = null;
                if (snapshot != null)
                {
                    outer = metric == MenuMetric.SevenDay ? snapshot.SevenDay : snapshot.FiveHour;
                    if (metric == MenuMetric.AllLimits) inner = snapshot.SevenDay;
                }
                string text = "--";
                if (metric == MenuMetric.Billing) text = "$";
                else if (outer != null) text = Math.Round(outer.RemainingPercent).ToString("0");
                bool drawRing = mode != MenuDisplayMode.Percentage && metric != MenuMetric.Billing;
                bool drawText = mode != MenuDisplayMode.Ring || metric == MenuMetric.Billing;
                if (drawRing)
                {
                    DrawRing(g, new RectangleF(2.5f, 2.5f, 27, 27), outer == null ? 0 : outer.RemainingPercent, Color.White, 3.3f);
                    if (metric == MenuMetric.AllLimits) DrawRing(g, new RectangleF(8, 8, 16, 16), inner == null ? 0 : inner.RemainingPercent, Color.White, 2.3f);
                }
                if (drawText)
                {
                    float size = mode == MenuDisplayMode.RingAndPercentage ? 9 : 13;
                    using (var font = new Font("Segoe UI", size, FontStyle.Bold, GraphicsUnit.Pixel))
                    using (var brush = new SolidBrush(Color.White))
                    using (var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                        g.DrawString(text, font, brush, new RectangleF(1, 1, 30, 30), format);
                }
                IntPtr handle = bitmap.GetHicon();
                try { return (Icon)Icon.FromHandle(handle).Clone(); }
                finally { NativeMethods.DestroyIcon(handle); }
            }
        }

        private static void DrawRing(Graphics g, RectangleF rect, double percent, Color color, float width)
        {
            using (var track = new Pen(Color.FromArgb(90, color), width)) g.DrawEllipse(track, rect);
            using (var pen = new Pen(color, width)) { pen.StartCap = LineCap.Round; pen.EndCap = LineCap.Round; g.DrawArc(pen, rect, -90, (float)(360 * Math.Max(0, Math.Min(100, percent)) / 100)); }
        }

        public static string Tooltip(UsageSnapshot snapshot, bool refreshing, string error)
        {
            if (refreshing && snapshot == null) return "AgentUsage - Refreshing";
            if (snapshot == null) return "AgentUsage - " + (string.IsNullOrEmpty(error) ? "No data" : "Codex unavailable");
            var lines = new List<string> { "AgentUsage" };
            if (snapshot.FiveHour != null) lines.Add(snapshot.FiveHour.DurationLabel + ": " + DisplayFormatter.Percent(snapshot.FiveHour.RemainingPercent) + " remaining");
            if (snapshot.SevenDay != null) lines.Add(snapshot.SevenDay.DurationLabel + ": " + DisplayFormatter.Percent(snapshot.SevenDay.RemainingPercent) + " remaining");
            string value = string.Join("\n", lines.ToArray());
            return value.Length > 63 ? value.Substring(0, 63) : value;
        }
    }

    internal static class GraphicsExtensions
    {
        public static void FillRoundedRectangle(this Graphics graphics, Brush brush, RectangleF bounds, float radius)
        {
            float diameter = radius * 2;
            using (var path = new GraphicsPath())
            {
                path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
                path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
                path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
                path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
                path.CloseFigure();
                graphics.FillPath(brush, path);
            }
        }
    }

    internal static class NativeMethods
    {
        [DllImport("user32.dll", SetLastError = true)] public static extern bool DestroyIcon(IntPtr handle);
        [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        public static void RoundWindow(IntPtr handle)
        {
            try { int preference = 2; DwmSetWindowAttribute(handle, 33, ref preference, sizeof(int)); }
            catch { }
        }
    }
}
