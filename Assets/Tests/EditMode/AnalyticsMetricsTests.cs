using Claude.Analytics;
using NUnit.Framework;

namespace Tests.EditMode
{
    [TestFixture]
    public class AnalyticsMetricsTests
    {
        private AnalyticsMetrics _metrics;

        [SetUp]
        public void SetUp()
        {
            _metrics = new AnalyticsMetrics();
        }

        // --- Initial state ---

        [Test]
        public void InitialState_AllCountersAreZero()
        {
            Assert.AreEqual(0, _metrics.TotalEnqueued);
            Assert.AreEqual(0, _metrics.TotalSucceeded);
            Assert.AreEqual(0, _metrics.TotalFailed);
            Assert.AreEqual(0, _metrics.TotalDropped);
            Assert.AreEqual(0, _metrics.TotalRetried);
            Assert.AreEqual(0, _metrics.SavedHitchTimeMs);
            Assert.AreEqual(0, _metrics.CircuitOpenCount);
            Assert.AreEqual(0, _metrics.RetryBufferSize);
        }

        [Test]
        public void InitialState_SuccessRateIsOne()
        {
            Assert.AreEqual(1f, _metrics.SuccessRate);
        }

        // --- Recording ---

        [Test]
        public void RecordEnqueue_IncrementsCounter()
        {
            _metrics.RecordEnqueue();
            _metrics.RecordEnqueue();
            Assert.AreEqual(2, _metrics.TotalEnqueued);
        }

        [Test]
        public void RecordSuccess_IncrementsCounter()
        {
            _metrics.RecordSuccess();
            Assert.AreEqual(1, _metrics.TotalSucceeded);
        }

        [Test]
        public void RecordFailure_IncrementsCounter()
        {
            _metrics.RecordFailure();
            Assert.AreEqual(1, _metrics.TotalFailed);
        }

        [Test]
        public void RecordDrop_IncrementsCounter()
        {
            _metrics.RecordDrop();
            Assert.AreEqual(1, _metrics.TotalDropped);
        }

        [Test]
        public void RecordRetry_IncrementsCounter()
        {
            _metrics.RecordRetry();
            Assert.AreEqual(1, _metrics.TotalRetried);
        }

        [Test]
        public void RecordCircuitOpen_IncrementsCounter()
        {
            _metrics.RecordCircuitOpen();
            _metrics.RecordCircuitOpen();
            Assert.AreEqual(2, _metrics.CircuitOpenCount);
        }

        [Test]
        public void AddSavedHitchTime_AccumulatesMs()
        {
            _metrics.AddSavedHitchTime(100);
            _metrics.AddSavedHitchTime(250);
            Assert.AreEqual(350, _metrics.SavedHitchTimeMs);
        }

        [Test]
        public void SetRetryBufferSize_SetsValue()
        {
            _metrics.SetRetryBufferSize(42);
            Assert.AreEqual(42, _metrics.RetryBufferSize);

            _metrics.SetRetryBufferSize(10);
            Assert.AreEqual(10, _metrics.RetryBufferSize);
        }

        // --- SuccessRate ---

        [Test]
        public void SuccessRate_AllSuccesses_ReturnsOne()
        {
            _metrics.RecordSuccess();
            _metrics.RecordSuccess();
            _metrics.RecordSuccess();
            Assert.AreEqual(1f, _metrics.SuccessRate);
        }

        [Test]
        public void SuccessRate_AllFailures_ReturnsZero()
        {
            _metrics.RecordFailure();
            _metrics.RecordFailure();
            Assert.AreEqual(0f, _metrics.SuccessRate);
        }

        [Test]
        public void SuccessRate_Mixed_ReturnsCorrectRatio()
        {
            _metrics.RecordSuccess();
            _metrics.RecordSuccess();
            _metrics.RecordSuccess();
            _metrics.RecordFailure();

            Assert.AreEqual(0.75f, _metrics.SuccessRate, 0.001f);
        }

        // --- Reset ---

        [Test]
        public void Reset_ZerosAllCounters()
        {
            _metrics.RecordEnqueue();
            _metrics.RecordSuccess();
            _metrics.RecordFailure();
            _metrics.RecordDrop();
            _metrics.RecordRetry();
            _metrics.RecordCircuitOpen();
            _metrics.AddSavedHitchTime(500);
            _metrics.SetRetryBufferSize(10);

            _metrics.Reset();

            Assert.AreEqual(0, _metrics.TotalEnqueued);
            Assert.AreEqual(0, _metrics.TotalSucceeded);
            Assert.AreEqual(0, _metrics.TotalFailed);
            Assert.AreEqual(0, _metrics.TotalDropped);
            Assert.AreEqual(0, _metrics.TotalRetried);
            Assert.AreEqual(0, _metrics.SavedHitchTimeMs);
            Assert.AreEqual(0, _metrics.CircuitOpenCount);
            Assert.AreEqual(0, _metrics.RetryBufferSize);
        }

        [Test]
        public void Reset_SuccessRateReturnsOneAfterReset()
        {
            _metrics.RecordFailure();
            _metrics.Reset();
            Assert.AreEqual(1f, _metrics.SuccessRate);
        }
    }
}
