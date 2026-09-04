using System;
using System.Collections.Generic;
using System.Globalization;

namespace AgentUsage.Windows
{
    internal sealed class RateWindow
    {
        public double UsedPercent;
        public int? WindowMinutes;
        public DateTime? ResetsAt;

        public double RemainingPercent
        {
            get { return Math.Max(0, 100 - UsedPercent); }
        }

        public string DurationLabel
        {
            get
            {
                if (!WindowMinutes.HasValue || WindowMinutes.Value <= 0) return "Limit";
                int minutes = WindowMinutes.Value;
                int days = minutes / 1440;
                int hours = (minutes % 1440) / 60;
                int remainder = minutes % 60;
                var parts = new List<string>();
                if (days > 0) parts.Add(days + "d");
                if (hours > 0) parts.Add(hours + "h");
                if (remainder > 0 || parts.Count == 0) parts.Add(remainder + "m");
                return string.Join(" ", parts.ToArray());
            }
        }
    }

    internal sealed class CreditSnapshot
    {
        public double? Balance;
        public bool HasCredits;
        public bool Unlimited;
    }

    internal sealed class ResetCredit
    {
        public string Status;
        public DateTime? ExpiresAt;
        public string Title;
    }

    internal sealed class TokenUsageDay
    {
        public string StartDate;
        public long Tokens;

        public DateTime? Date
        {
            get
            {
                DateTime value;
                if (DateTime.TryParseExact(StartDate, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeLocal, out value)) return value.Date;
                return null;
            }
        }
    }

    internal sealed class CodexActivitySnapshot
    {
        public long? LifetimeTokens;
        public long? PeakDailyTokens;
        public long? LongestRunningTurnSeconds;
        public long? CurrentStreakDays;
        public long? LongestStreakDays;
        public readonly List<TokenUsageDay> DailyUsage = new List<TokenUsageDay>();
    }

    internal sealed class UsageSnapshot
    {
        public RateWindow FiveHour;
        public RateWindow SevenDay;
        public CreditSnapshot Credits;
        public int? AvailableResets;
        public readonly List<ResetCredit> ResetCredits = new List<ResetCredit>();
        public string AccountEmail;
        public string Plan;
        public CodexActivitySnapshot Activity;
        public DateTime UpdatedAt;
    }

    internal enum ActivityPeriod
    {
        Day,
        Week,
        Cumulative
    }

    internal enum MenuMetric
    {
        FiveHour,
        SevenDay,
        AllLimits,
        Billing
    }

    internal enum MenuDisplayMode
    {
        Ring,
        Percentage,
        RingAndPercentage
    }

    internal static class DisplayFormatter
    {
        public static string Percent(double value)
        {
            return Math.Round(Math.Max(0, Math.Min(100, value))).ToString("0", CultureInfo.InvariantCulture) + "%";
        }

        public static string CompactTokens(long? value)
        {
            if (!value.HasValue) return "--";
            double absolute = Math.Abs((double)value.Value);
            double divisor = 1;
            string suffix = "";
            if (absolute >= 1000000000) { divisor = 1000000000; suffix = "B"; }
            else if (absolute >= 1000000) { divisor = 1000000; suffix = "M"; }
            else if (absolute >= 1000) { divisor = 1000; suffix = "K"; }
            if (divisor == 1) return value.Value.ToString("N0", CultureInfo.CurrentCulture);
            return (value.Value / divisor).ToString("0.##", CultureInfo.InvariantCulture) + suffix;
        }

        public static string CompactAxisTokens(long value)
        {
            return CompactTokens(value);
        }

        public static string Credits(CreditSnapshot snapshot)
        {
            if (snapshot == null) return "--";
            if (snapshot.Unlimited) return "Unlimited";
            if (!snapshot.HasCredits || !snapshot.Balance.HasValue) return "--";
            return snapshot.Balance.Value < 100
                ? "$" + snapshot.Balance.Value.ToString("0.00", CultureInfo.InvariantCulture)
                : "$" + snapshot.Balance.Value.ToString("0", CultureInfo.InvariantCulture);
        }

        public static string ResetHelp(UsageSnapshot snapshot)
        {
            if (snapshot == null || !snapshot.AvailableResets.HasValue) return "Reset expiration data unavailable";
            var available = snapshot.ResetCredits.FindAll(delegate(ResetCredit item) { return item.Status == "available"; });
            available.Sort(delegate(ResetCredit a, ResetCredit b)
            {
                if (!a.ExpiresAt.HasValue) return 1;
                if (!b.ExpiresAt.HasValue) return -1;
                return a.ExpiresAt.Value.CompareTo(b.ExpiresAt.Value);
            });
            if (available.Count == 0)
                return snapshot.AvailableResets.Value > 0 ? "Expiration details unavailable" : "No resets available";
            var lines = new List<string>();
            for (int i = 0; i < available.Count; i++)
            {
                string date = available[i].ExpiresAt.HasValue
                    ? available[i].ExpiresAt.Value.ToLocalTime().ToString("MMM d, h:mm tt", CultureInfo.CurrentCulture)
                    : "unknown";
                lines.Add("Reset " + (i + 1) + ": expires " + date);
            }
            return string.Join(Environment.NewLine, lines.ToArray());
        }

        public static long RoundedMaximum(long value)
        {
            if (value <= 0) return 1;
            double magnitude = Math.Pow(10, Math.Floor(Math.Log10(value)));
            double[] steps = { 1, 1.5, 2, 2.5, 3, 4, 5, 6, 8, 10, 15 };
            double minimum = value * 1.05;
            for (int i = 0; i < steps.Length; i++)
            {
                double candidate = steps[i] * magnitude;
                if (candidate >= minimum) return (long)Math.Ceiling(candidate);
            }
            return (long)Math.Ceiling(15 * magnitude);
        }
    }
}
