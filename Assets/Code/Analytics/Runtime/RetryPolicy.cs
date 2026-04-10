using System;

namespace Fortis.Analytics.PlayerLoop
{
    [Serializable]
    public class RetryConfig
    {
        public int MaxAttempts = 4;
        public double BaseDelayMs = 500;
        public double MaxDelayMs = 8000;
        public double JitterFactor = 0.1;

        public static RetryConfig Default => new RetryConfig
        {
            MaxAttempts = 4,
            BaseDelayMs = 500,
            MaxDelayMs = 8000,
            JitterFactor = 0.1
        };
    }

    public static class RetryPolicy
    {
        // System.Random is thread-safe for Next() in .NET Core / Unity 6,
        // but we use [ThreadStatic] to be safe across all runtimes.
        [ThreadStatic] private static Random s_random;

        private static Random GetRandom()
        {
            return s_random ??= new Random(
                Environment.TickCount ^ System.Threading.Thread.CurrentThread.ManagedThreadId);
        }

        public static bool ShouldRetry(int attemptCount, RetryConfig config)
        {
            return attemptCount < config.MaxAttempts;
        }

        public static TimeSpan GetDelay(int attemptCount, RetryConfig config)
        {
            double delay = Math.Min(
                config.BaseDelayMs * Math.Pow(2, attemptCount),
                config.MaxDelayMs);

            var rng = GetRandom();
            double jitter = delay * config.JitterFactor;
            double offset = (rng.NextDouble() * 2.0 - 1.0) * jitter; // ±jitter
            delay += offset;

            return TimeSpan.FromMilliseconds(Math.Max(0, delay));
        }
    }
}
