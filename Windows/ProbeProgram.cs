using System;
using System.Collections.Generic;
using System.Web.Script.Serialization;

namespace AgentUsage.Windows
{
    internal static class ProbeProgram
    {
        public static int Main(string[] args)
        {
            if (Array.IndexOf(args, "--self-test") >= 0) return SelfTest();
            if (Array.IndexOf(args, "--locate") >= 0)
            {
                string located = CodexClient.FindCodex();
                if (located == null) { Console.Error.WriteLine("Codex not found"); return 1; }
                Console.WriteLine(located);
                return 0;
            }
            if (Array.IndexOf(args, "--check-update") >= 0)
            {
                try { Console.WriteLine(ReleaseUpdateChecker.Check()); return 0; }
                catch (Exception error) { Console.Error.WriteLine(error.Message); return 1; }
            }
            try
            {
                UsageSnapshot snapshot = new CodexClient().FetchAsync().Result;
                Console.WriteLine("Codex probe passed");
                Console.WriteLine("Plan: " + (snapshot.Plan ?? "--"));
                Console.WriteLine("5h remaining: " + (snapshot.FiveHour == null ? "--" : DisplayFormatter.Percent(snapshot.FiveHour.RemainingPercent)));
                Console.WriteLine("7d remaining: " + (snapshot.SevenDay == null ? "--" : DisplayFormatter.Percent(snapshot.SevenDay.RemainingPercent)));
                Console.WriteLine("Activity days: " + (snapshot.Activity == null ? 0 : snapshot.Activity.DailyUsage.Count));
                Console.WriteLine("Available resets: " + (snapshot.AvailableResets.HasValue ? snapshot.AvailableResets.Value.ToString() : "--"));
                return 0;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine("Codex probe failed: " + error.GetBaseException().Message);
                return 1;
            }
        }

        private static int SelfTest()
        {
            try
            {
                const string rates = "{\"rateLimits\":{\"primary\":{\"usedPercent\":24,\"windowDurationMins\":300,\"resetsAt\":1788552000},\"secondary\":{\"usedPercent\":8,\"windowDurationMins\":10080,\"resetsAt\":1789000000},\"planType\":\"pro\",\"credits\":{\"balance\":\"12.50\",\"hasCredits\":true,\"unlimited\":false}},\"rateLimitResetCredits\":{\"availableCount\":3}}";
                const string account = "{\"account\":{\"type\":\"chatgpt\",\"email\":\"test@example.com\",\"planType\":\"plus\"},\"requiresOpenaiAuth\":true}";
                const string activity = "{\"summary\":{\"lifetimeTokens\":1234567,\"peakDailyTokens\":45678,\"currentStreakDays\":4,\"longestStreakDays\":9},\"dailyUsageBuckets\":[{\"startDate\":\"2026-09-03\",\"tokens\":45678}]}";
                var json = new JavaScriptSerializer();
                var snapshot = new CodexClient().ParseSnapshot(
                    (IDictionary<string, object>)json.DeserializeObject(rates),
                    (IDictionary<string, object>)json.DeserializeObject(account),
                    (IDictionary<string, object>)json.DeserializeObject(activity));
                Expect(snapshot.FiveHour != null && snapshot.FiveHour.WindowMinutes == 300, "short window classification");
                Expect(snapshot.SevenDay != null && snapshot.SevenDay.WindowMinutes == 10080, "long window classification");
                Expect(snapshot.Plan == "plus", "account plan precedence");
                Expect(snapshot.AccountEmail == "test@example.com", "current account response shape");
                Expect(snapshot.Credits != null && snapshot.Credits.Balance == 12.5, "string credit balance");
                Expect(snapshot.AvailableResets == 3, "reset credits");
                Expect(snapshot.Activity != null && snapshot.Activity.DailyUsage.Count == 1, "activity buckets");
                Expect(DisplayFormatter.CompactTokens(snapshot.Activity.LifetimeTokens) == "1.23M", "token formatting");
                Console.WriteLine("Windows self-test passed");
                return 0;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine("Windows self-test failed: " + error.Message);
                return 1;
            }
        }

        private static void Expect(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("Failed: " + name);
        }
    }
}
