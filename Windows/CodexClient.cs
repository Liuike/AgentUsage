using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace AgentUsage.Windows
{
    internal sealed class CodexClient
    {
        private readonly JavaScriptSerializer json = new JavaScriptSerializer();

        public Task<UsageSnapshot> FetchAsync()
        {
            return Task.Factory.StartNew<UsageSnapshot>(delegate { return Fetch(); }, TaskCreationOptions.LongRunning);
        }

        private UsageSnapshot Fetch()
        {
            string binary = FindCodex();
            if (binary == null) throw new InvalidOperationException("Codex was not found. AgentUsage checked PATH, Codex Desktop, npm/NVM, and Windows App Paths. Set AGENTUSAGE_CODEX_PATH if Codex is installed elsewhere.");
            try { return FetchOnce(binary); }
            catch (TimeoutException)
            {
                TryCredentialRefresh(binary);
                return FetchOnce(binary);
            }
        }

        private UsageSnapshot FetchOnce(string binary)
        {
            using (var rpc = new RpcSession(binary, json))
            {
                rpc.Start();
                rpc.Request("initialize", new Dictionary<string, object> {
                    { "clientInfo", new Dictionary<string, object> { { "name", "agentusage-windows" }, { "version", "0.5" } } }
                }, 8000);
                rpc.Notify("initialized", new Dictionary<string, object>());

                IDictionary<string, object> rateResult = rpc.Request("account/rateLimits/read", null, 4000);
                IDictionary<string, object> accountResult = TryRequest(rpc, "account/read", 4000);
                IDictionary<string, object> activityResult = TryRequest(rpc, "account/usage/read", 8000);

                var snapshot = ParseSnapshot(rateResult, accountResult, activityResult);
                TryFetchResetExpirationDetails(snapshot);
                return snapshot;
            }
        }

        private static IDictionary<string, object> TryRequest(RpcSession rpc, string method, int timeout)
        {
            try { return rpc.Request(method, null, timeout); }
            catch { return null; }
        }

        internal UsageSnapshot ParseSnapshot(IDictionary<string, object> rateRoot,
            IDictionary<string, object> accountRoot, IDictionary<string, object> activityRoot)
        {
            IDictionary<string, object> limits = JsonValue.Dict(rateRoot, "rateLimits") ?? rateRoot;
            var windows = new List<RateWindow>();
            RateWindow primary = ParseWindow(JsonValue.Dict(limits, "primary"));
            RateWindow secondary = ParseWindow(JsonValue.Dict(limits, "secondary"));
            if (primary != null) windows.Add(primary);
            if (secondary != null) windows.Add(secondary);
            windows.Sort(delegate(RateWindow a, RateWindow b)
            {
                return (a.WindowMinutes ?? int.MaxValue).CompareTo(b.WindowMinutes ?? int.MaxValue);
            });

            var snapshot = new UsageSnapshot();
            if (windows.Count == 1 && windows[0].WindowMinutes.HasValue && windows[0].WindowMinutes.Value >= 1440)
                snapshot.SevenDay = windows[0];
            else if (windows.Count > 0)
            {
                snapshot.FiveHour = windows[0];
                if (windows.Count > 1) snapshot.SevenDay = windows[windows.Count - 1];
            }
            snapshot.Plan = JsonValue.String(limits, "planType");
            snapshot.Credits = ParseCredits(JsonValue.Dict(limits, "credits"));
            IDictionary<string, object> resetSummary = JsonValue.Dict(rateRoot, "rateLimitResetCredits");
            if (resetSummary != null) snapshot.AvailableResets = JsonValue.Int(resetSummary, "availableCount");
            ParseAccount(snapshot, accountRoot);
            snapshot.Activity = ParseActivity(activityRoot);
            snapshot.UpdatedAt = DateTime.Now;
            return snapshot;
        }

        private static RateWindow ParseWindow(IDictionary<string, object> value)
        {
            if (value == null || !value.ContainsKey("usedPercent")) return null;
            var window = new RateWindow();
            window.UsedPercent = JsonValue.Double(value, "usedPercent") ?? 0;
            window.WindowMinutes = JsonValue.Int(value, "windowDurationMins");
            long? reset = JsonValue.Long(value, "resetsAt");
            if (reset.HasValue)
                window.ResetsAt = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(reset.Value).ToLocalTime();
            return window;
        }

        private static CreditSnapshot ParseCredits(IDictionary<string, object> value)
        {
            if (value == null) return null;
            var credits = new CreditSnapshot();
            credits.Balance = JsonValue.Double(value, "balance");
            credits.HasCredits = JsonValue.Bool(value, "hasCredits") ?? false;
            credits.Unlimited = JsonValue.Bool(value, "unlimited") ?? false;
            return credits;
        }

        private static void ParseAccount(UsageSnapshot snapshot, IDictionary<string, object> root)
        {
            if (root == null) return;
            IDictionary<string, object> account = JsonValue.Dict(root, "account");
            IDictionary<string, object> chatgpt = account == null ? null : JsonValue.Dict(account, "chatgpt");
            IDictionary<string, object> source = chatgpt ?? root;
            snapshot.AccountEmail = JsonValue.String(source, "email");
            string plan = JsonValue.String(source, "plan");
            if (!string.IsNullOrEmpty(plan)) snapshot.Plan = plan;
        }

        private static CodexActivitySnapshot ParseActivity(IDictionary<string, object> root)
        {
            if (root == null) return null;
            IDictionary<string, object> summary = JsonValue.Dict(root, "summary");
            if (summary == null) return null;
            var activity = new CodexActivitySnapshot();
            activity.LifetimeTokens = JsonValue.Long(summary, "lifetimeTokens");
            activity.PeakDailyTokens = JsonValue.Long(summary, "peakDailyTokens");
            activity.LongestRunningTurnSeconds = JsonValue.Long(summary, "longestRunningTurnSec");
            activity.CurrentStreakDays = JsonValue.Long(summary, "currentStreakDays");
            activity.LongestStreakDays = JsonValue.Long(summary, "longestStreakDays");
            foreach (object item in JsonValue.Array(root, "dailyUsageBuckets"))
            {
                var bucket = item as IDictionary<string, object>;
                if (bucket == null) continue;
                activity.DailyUsage.Add(new TokenUsageDay {
                    StartDate = JsonValue.String(bucket, "startDate"),
                    Tokens = JsonValue.Long(bucket, "tokens") ?? 0
                });
            }
            activity.DailyUsage.Sort(delegate(TokenUsageDay a, TokenUsageDay b) {
                return string.CompareOrdinal(a.StartDate, b.StartDate);
            });
            return activity;
        }

        private void TryFetchResetExpirationDetails(UsageSnapshot snapshot)
        {
            try
            {
                string authPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "auth.json");
                IDictionary<string, object> auth = json.DeserializeObject(File.ReadAllText(authPath)) as IDictionary<string, object>;
                IDictionary<string, object> tokens = JsonValue.Dict(auth, "tokens");
                string accessToken = JsonValue.String(tokens, "access_token");
                string accountId = JsonValue.String(tokens, "account_id");
                if (string.IsNullOrEmpty(accessToken) || string.IsNullOrEmpty(accountId)) return;

                var request = (HttpWebRequest)WebRequest.Create("https://chatgpt.com/backend-api/wham/rate-limit-reset-credits");
                request.Method = "GET";
                request.Timeout = 4000;
                request.UserAgent = "AgentUsage-Windows";
                request.Accept = "application/json";
                request.Headers[HttpRequestHeader.Authorization] = "Bearer " + accessToken;
                request.Headers["ChatGPT-Account-Id"] = accountId;
                using (var response = (HttpWebResponse)request.GetResponse())
                using (var reader = new StreamReader(response.GetResponseStream()))
                {
                    IDictionary<string, object> root = json.DeserializeObject(reader.ReadToEnd()) as IDictionary<string, object>;
                    foreach (object item in JsonValue.Array(root, "credits"))
                    {
                        var source = item as IDictionary<string, object>;
                        if (source == null) continue;
                        DateTime parsed;
                        string expires = JsonValue.String(source, "expires_at");
                        snapshot.ResetCredits.Add(new ResetCredit {
                            Status = JsonValue.String(source, "status"),
                            Title = JsonValue.String(source, "title"),
                            ExpiresAt = DateTime.TryParse(expires, CultureInfo.InvariantCulture,
                                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out parsed) ? (DateTime?)parsed : null
                        });
                    }
                }
            }
            catch { }
        }

        internal static string FindCodex()
        {
            var candidates = new List<string>();
            string configured = Environment.GetEnvironmentVariable("AGENTUSAGE_CODEX_PATH");
            if (!string.IsNullOrEmpty(configured)) candidates.Add(configured.Trim('"'));

            AddPathCandidates(candidates, Environment.GetEnvironmentVariable("PATH"));
            try { AddPathCandidates(candidates, Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.User)); }
            catch { }
            try { AddPathCandidates(candidates, Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.Machine)); }
            catch { }

            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            candidates.Add(Path.Combine(appData, "npm", "codex.cmd"));
            candidates.Add(Path.Combine(localAppData, "npm", "codex.cmd"));
            candidates.Add(Path.Combine(profile, ".local", "bin", "codex.exe"));

            AddVersionedCandidates(candidates, Path.Combine(localAppData, "OpenAI", "Codex", "bin"), "codex.exe");
            AddVersionedCandidates(candidates, Path.Combine(localAppData, "nvm"), "codex.cmd");
            AddVersionedCandidates(candidates, Path.Combine(appData, "nvm"), "codex.cmd");
            candidates.Add(Path.Combine(localAppData, "Programs", "Codex", "codex.exe"));
            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "OpenAI", "Codex", "codex.exe"));
            candidates.Add(Path.Combine(profile, ".codex", ".sandbox-bin", "codex.exe"));

            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\App Paths\codex.exe"))
                {
                    if (key != null && key.GetValue(null) != null) candidates.Add(Convert.ToString(key.GetValue(null)));
                }
            }
            catch { }
            try
            {
                using (RegistryKey key = Registry.LocalMachine.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\App Paths\codex.exe"))
                {
                    if (key != null && key.GetValue(null) != null) candidates.Add(Convert.ToString(key.GetValue(null)));
                }
            }
            catch { }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string candidate in candidates)
            {
                if (string.IsNullOrWhiteSpace(candidate)) continue;
                string normalized;
                try { normalized = Path.GetFullPath(Environment.ExpandEnvironmentVariables(candidate.Trim('"'))); }
                catch { continue; }
                if (seen.Add(normalized) && File.Exists(normalized)) return normalized;
            }
            return null;
        }

        private static void AddPathCandidates(List<string> candidates, string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            foreach (string directory in path.Split(Path.PathSeparator))
            {
                string clean = directory.Trim().Trim('"');
                if (clean.Length == 0) continue;
                candidates.Add(Path.Combine(clean, "codex.exe"));
                candidates.Add(Path.Combine(clean, "codex.cmd"));
                candidates.Add(Path.Combine(clean, "codex.bat"));
            }
        }

        private static void AddVersionedCandidates(List<string> candidates, string root, string fileName)
        {
            try
            {
                if (!Directory.Exists(root)) return;
                var directories = new List<DirectoryInfo>(new DirectoryInfo(root).GetDirectories());
                directories.Sort(delegate(DirectoryInfo left, DirectoryInfo right)
                {
                    return right.LastWriteTimeUtc.CompareTo(left.LastWriteTimeUtc);
                });
                foreach (DirectoryInfo directory in directories) candidates.Add(Path.Combine(directory.FullName, fileName));
                candidates.Add(Path.Combine(root, fileName));
            }
            catch { }
        }

        private static void TryCredentialRefresh(string binary)
        {
            try
            {
                var start = RpcSession.StartInfo(binary, "login status");
                using (Process process = Process.Start(start)) process.WaitForExit(5000);
            }
            catch { }
        }

        private sealed class RpcSession : IDisposable
        {
            private readonly string binary;
            private readonly JavaScriptSerializer json;
            private Process process;
            private int nextId = 1;

            public RpcSession(string binary, JavaScriptSerializer json)
            {
                this.binary = binary;
                this.json = json;
            }

            public static ProcessStartInfo StartInfo(string binary, string arguments)
            {
                bool script = binary.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
                    || binary.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);
                string fileName = script ? Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe" : binary;
                string args = script
                    ? "/d /s /c \"\"" + binary + "\" " + arguments + "\""
                    : arguments;
                var info = new ProcessStartInfo(fileName, args);
                info.UseShellExecute = false;
                info.CreateNoWindow = true;
                info.RedirectStandardInput = true;
                info.RedirectStandardOutput = true;
                info.RedirectStandardError = true;
                string directory = Path.Combine(Path.GetTempPath(), "AgentUsage-CodexProbe");
                Directory.CreateDirectory(directory);
                info.WorkingDirectory = directory;
                return info;
            }

            public void Start()
            {
                process = new Process();
                process.StartInfo = StartInfo(binary, "-s read-only -a never app-server");
                process.Start();
                process.ErrorDataReceived += delegate { };
                process.BeginErrorReadLine();
            }

            public IDictionary<string, object> Request(string method, IDictionary<string, object> parameters, int timeoutMilliseconds)
            {
                int id = nextId++;
                var payload = new Dictionary<string, object>();
                payload["id"] = id;
                payload["method"] = method;
                payload["params"] = parameters ?? new Dictionary<string, object>();
                Send(payload);
                Stopwatch clock = Stopwatch.StartNew();
                while (clock.ElapsedMilliseconds < timeoutMilliseconds)
                {
                    Task<string> read = process.StandardOutput.ReadLineAsync();
                    int remaining = Math.Max(1, timeoutMilliseconds - (int)clock.ElapsedMilliseconds);
                    if (!read.Wait(remaining)) throw new TimeoutException("Timed out: " + method);
                    string line = read.Result;
                    if (line == null) throw new InvalidOperationException("Codex app-server closed unexpectedly.");
                    IDictionary<string, object> message;
                    try { message = json.DeserializeObject(line) as IDictionary<string, object>; }
                    catch { continue; }
                    if (message == null || !message.ContainsKey("id") || Convert.ToInt32(message["id"]) != id) continue;
                    IDictionary<string, object> error = JsonValue.Dict(message, "error");
                    if (error != null) throw new InvalidOperationException(JsonValue.String(error, "message") ?? "Codex app-server error.");
                    return JsonValue.Dict(message, "result") ?? new Dictionary<string, object>();
                }
                throw new TimeoutException("Timed out: " + method);
            }

            public void Notify(string method, IDictionary<string, object> parameters)
            {
                Send(new Dictionary<string, object> { { "method", method }, { "params", parameters } });
            }

            private void Send(IDictionary<string, object> payload)
            {
                process.StandardInput.WriteLine(json.Serialize(payload));
                process.StandardInput.Flush();
            }

            public void Dispose()
            {
                try { if (process != null && !process.HasExited) process.Kill(); }
                catch { }
                if (process != null) process.Dispose();
            }
        }
    }

    internal static class JsonValue
    {
        public static IDictionary<string, object> Dict(IDictionary<string, object> value, string key)
        {
            object item;
            return value != null && value.TryGetValue(key, out item) ? item as IDictionary<string, object> : null;
        }

        public static string String(IDictionary<string, object> value, string key)
        {
            object item;
            return value != null && value.TryGetValue(key, out item) && item != null ? Convert.ToString(item, CultureInfo.InvariantCulture) : null;
        }

        public static long? Long(IDictionary<string, object> value, string key)
        {
            object item;
            long parsed;
            if (value == null || !value.TryGetValue(key, out item) || item == null) return null;
            return long.TryParse(Convert.ToString(item, CultureInfo.InvariantCulture), NumberStyles.Any,
                CultureInfo.InvariantCulture, out parsed) ? (long?)parsed : null;
        }

        public static int? Int(IDictionary<string, object> value, string key)
        {
            long? number = Long(value, key);
            return number.HasValue ? (int?)number.Value : null;
        }

        public static double? Double(IDictionary<string, object> value, string key)
        {
            object item;
            double parsed;
            if (value == null || !value.TryGetValue(key, out item) || item == null) return null;
            return double.TryParse(Convert.ToString(item, CultureInfo.InvariantCulture), NumberStyles.Any,
                CultureInfo.InvariantCulture, out parsed) ? (double?)parsed : null;
        }

        public static bool? Bool(IDictionary<string, object> value, string key)
        {
            object item;
            bool parsed;
            if (value == null || !value.TryGetValue(key, out item) || item == null) return null;
            return bool.TryParse(Convert.ToString(item, CultureInfo.InvariantCulture), out parsed) ? (bool?)parsed : null;
        }

        public static IEnumerable Array(IDictionary<string, object> value, string key)
        {
            object item;
            if (value != null && value.TryGetValue(key, out item) && item is IEnumerable) return (IEnumerable)item;
            return new object[0];
        }
    }
}
