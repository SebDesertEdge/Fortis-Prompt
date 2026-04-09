using System;
using Fortis.Analytics;

namespace Fortis
{
    public interface IAnalyticsService
    {
        public CircuitBreaker CircuitBreaker { get; }
        public AnalyticsMetrics Metrics { get; }
        public bool IsReady { get; }
        public int MaxRetryBufferSize { get; }

        public void SendEvent(string eventName, Action<bool> onComplete = null);
    }
}