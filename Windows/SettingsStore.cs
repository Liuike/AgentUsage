using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Microsoft.Win32;

namespace AgentUsage.Windows
{
    internal sealed class SettingsStore
    {
        private readonly string path;
        private readonly Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public SettingsStore() : this(DefaultDirectory()) { }

        public SettingsStore(string directory)
        {
            Directory.CreateDirectory(directory);
            path = Path.Combine(directory, "settings.ini");
            Load();
        }

        private void Load()
        {
            if (!File.Exists(path)) return;
            foreach (string line in File.ReadAllLines(path))
            {
                int split = line.IndexOf('=');
                if (split > 0) values[line.Substring(0, split)] = line.Substring(split + 1);
            }
        }

        private static string DefaultDirectory()
        {
            string custom = Environment.GetEnvironmentVariable("AGENTUSAGE_SETTINGS_DIR");
            return string.IsNullOrEmpty(custom)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AgentUsage")
                : custom;
        }

        private void Save()
        {
            var lines = new List<string>();
            foreach (KeyValuePair<string, string> item in values) lines.Add(item.Key + "=" + item.Value);
            lines.Sort(StringComparer.OrdinalIgnoreCase);
            File.WriteAllLines(path, lines.ToArray());
        }

        public int RefreshSeconds
        {
            get { return GetInt("refreshSeconds", 60); }
            set { Set("refreshSeconds", value.ToString()); }
        }

        public MenuMetric Metric
        {
            get { return (MenuMetric)GetInt("menuMetric", (int)MenuMetric.AllLimits); }
            set { Set("menuMetric", ((int)value).ToString()); }
        }

        public MenuDisplayMode DisplayMode
        {
            get { return (MenuDisplayMode)GetInt("displayMode", (int)MenuDisplayMode.RingAndPercentage); }
            set { Set("displayMode", ((int)value).ToString()); }
        }

        public ActivityPeriod Period
        {
            get { return (ActivityPeriod)GetInt("activityPeriod", (int)ActivityPeriod.Day); }
            set { Set("activityPeriod", ((int)value).ToString()); }
        }

        public bool OptionsExpanded
        {
            get { return GetInt("optionsExpanded", 0) == 1; }
            set { Set("optionsExpanded", value ? "1" : "0"); }
        }

        public int ChartOffsetWeeks
        {
            get { return Math.Max(0, GetInt("chartOffsetWeeks", 0)); }
            set { Set("chartOffsetWeeks", Math.Max(0, value).ToString()); }
        }

        private int GetInt(string key, int fallback)
        {
            string raw;
            int value;
            return values.TryGetValue(key, out raw) && int.TryParse(raw, out value) ? value : fallback;
        }

        private void Set(string key, string value)
        {
            values[key] = value;
            Save();
        }

        public static bool OpenOnStartup
        {
            get
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
                    return key != null && key.GetValue("AgentUsage") != null;
            }
            set
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
                {
                    if (value)
                    {
                        string executable = Assembly.GetExecutingAssembly().Location;
                        key.SetValue("AgentUsage", "\"" + executable + "\" --startup", RegistryValueKind.String);
                    }
                    else key.DeleteValue("AgentUsage", false);
                }
            }
        }
    }
}
