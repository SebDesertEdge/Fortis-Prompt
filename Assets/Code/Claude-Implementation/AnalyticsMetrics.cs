using System.Threading;

namespace Fortis.Analytics
{
    public class AnalyticsMetrics
    {
        private long _totalEnqueued;
        private long _totalSucceeded;
        private long _totalFailed;
        private long _totalDropped;
        private long _totalRetried;
        private long _savedHitchTimeMs;
        private long _circuitOpenCount;
        private long _retryBufferSize;

        public long TotalEnqueued    => Interlocked.Read(ref _totalEnqueued);
        public long TotalSucceeded   => Interlocked.Read(ref _totalSucceeded);
        public long TotalFailed      => Interlocked.Read(ref _totalFailed);
        public long TotalDropped     => Interlocked.Read(ref _totalDropped);
        public long TotalRetried     => Interlocked.Read(ref _totalRetried);
        public long SavedHitchTimeMs => Interlocked.Read(ref _savedHitchTimeMs);
        public long CircuitOpenCount => Interlocked.Read(ref _circuitOpenCount);
        public long RetryBufferSize  => Interlocked.Read(ref _retryBufferSize);

        public float SuccessRate
        {
            get
            {
                var succeeded = TotalSucceeded;
                var failed = TotalFailed;
                var total = succeeded + failed;
                return total == 0 ? 1f : (float)succeeded / total;
            }
        }

        public void RecordEnqueue()                 => Interlocked.Increment(ref _totalEnqueued);
        public void RecordSuccess()                 => Interlocked.Increment(ref _totalSucceeded);
        public void RecordFailure()                 => Interlocked.Increment(ref _totalFailed);
        public void RecordDrop()                    => Interlocked.Increment(ref _totalDropped);
        public void RecordRetry()                   => Interlocked.Increment(ref _totalRetried);
        public void RecordCircuitOpen()             => Interlocked.Increment(ref _circuitOpenCount);
        public void AddSavedHitchTime(long ms)      => Interlocked.Add(ref _savedHitchTimeMs, ms);
        public void SetRetryBufferSize(long size)    => Interlocked.Exchange(ref _retryBufferSize, size);

        public void Reset()
        {
            Interlocked.Exchange(ref _totalEnqueued, 0);
            Interlocked.Exchange(ref _totalSucceeded, 0);
            Interlocked.Exchange(ref _totalFailed, 0);
            Interlocked.Exchange(ref _totalDropped, 0);
            Interlocked.Exchange(ref _totalRetried, 0);
            Interlocked.Exchange(ref _savedHitchTimeMs, 0);
            Interlocked.Exchange(ref _circuitOpenCount, 0);
            Interlocked.Exchange(ref _retryBufferSize, 0);
        }
    }
}
