using System;
using Claude.Analytics;

namespace Code
{
    public interface IAnalyticsService
    {
        public CircuitBreaker CircuitBreaker { get; }
        public AnalyticsMetrics Metrics { get; }
        public bool IsReady { get; }

        public void SendEvent(string eventName, Action<bool> onComplete = null);
    }
}