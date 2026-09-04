using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

[assembly: AssemblyTitle("AgentUsage")]
[assembly: AssemblyProduct("AgentUsage for Windows")]
[assembly: AssemblyDescription("Codex usage in the Windows notification area")]
[assembly: AssemblyCompany("AgentUsage")]
[assembly: AssemblyVersion("0.5.0.0")]
[assembly: AssemblyFileVersion("0.5.0.0")]
[assembly: ComVisible(false)]

namespace AgentUsage.Windows
{
    internal static class Program
    {
        private const string MutexName = @"Local\AgentUsage.Windows";
        private const string ShowEventName = @"Local\AgentUsage.Windows.Show";

        [STAThread]
        public static void Main(string[] args)
        {
            string previewArgument = Array.Find(args, delegate(string item) { return item.StartsWith("--render-previews=", StringComparison.OrdinalIgnoreCase); });
            if (previewArgument != null)
            {
                string previewRoot = previewArgument.Substring(previewArgument.IndexOf('=') + 1).Trim('"');
                Directory.CreateDirectory(previewRoot);
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                using (var preview = new AgentUsageForm(new SettingsStore(previewRoot), false))
                {
                    preview.SetPreviewData();
                    preview.RenderPreview(Path.Combine(previewRoot, "day.png"), ActivityPeriod.Day, true);
                    preview.RenderPreview(Path.Combine(previewRoot, "week.png"), ActivityPeriod.Week, true);
                    preview.RenderPreview(Path.Combine(previewRoot, "cumulative.png"), ActivityPeriod.Cumulative, true);
                }
                return;
            }
            bool created;
            using (var mutex = new Mutex(true, MutexName, out created))
            {
                if (!created)
                {
                    try { using (EventWaitHandle signal = EventWaitHandle.OpenExisting(ShowEventName)) signal.Set(); }
                    catch { }
                    return;
                }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                bool show = Array.IndexOf(args, "--show") >= 0;
                using (var signal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName))
                using (var context = new TrayApplicationContext(show, signal)) Application.Run(context);
            }
        }
    }

    internal sealed class TrayApplicationContext : ApplicationContext
    {
        private readonly NotifyIcon tray;
        private readonly AgentUsageForm form;
        private readonly RegisteredWaitHandle showRegistration;

        public TrayApplicationContext(bool show, EventWaitHandle showSignal)
        {
            var settings = new SettingsStore();
            form = new AgentUsageForm(settings);
            tray = new NotifyIcon();
            tray.Visible = true;
            tray.Text = "AgentUsage";
            tray.MouseClick += delegate(object sender, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left) form.ToggleNearTaskbar();
            };
            var menu = new ContextMenuStrip();
            menu.Items.Add("Open AgentUsage", null, delegate { form.ToggleNearTaskbar(); });
            menu.Items.Add("Refresh", null, delegate { form.RefreshUsage(); });
            menu.Items.Add(new ToolStripSeparator());
            var startup = new ToolStripMenuItem("Open on Startup");
            startup.Checked = SettingsStore.OpenOnStartup;
            startup.Click += delegate { SettingsStore.OpenOnStartup = !SettingsStore.OpenOnStartup; startup.Checked = SettingsStore.OpenOnStartup; };
            menu.Items.Add(startup);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Quit AgentUsage", null, delegate { Exit(); });
            menu.Opening += delegate { startup.Checked = SettingsStore.OpenOnStartup; };
            tray.ContextMenuStrip = menu;
            form.ExitRequested += delegate { Exit(); };
            form.AttachTray(tray);
            IntPtr ignored = form.Handle;
            showRegistration = ThreadPool.RegisterWaitForSingleObject(showSignal, delegate
            {
                if (!form.IsDisposed) form.BeginInvoke((MethodInvoker)delegate { if (!form.Visible) form.ToggleNearTaskbar(); });
            }, null, Timeout.Infinite, false);
            if (show) form.ToggleNearTaskbar();
        }

        private void Exit()
        {
            tray.Visible = false;
            ExitThread();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                tray.Visible = false;
                if (showRegistration != null) showRegistration.Unregister(null);
                tray.Dispose();
                form.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
