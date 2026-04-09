using NUnit.Framework;
using Fortis.Analytics;

namespace Fortis.Analytics.Tests
{
    [TestFixture]
    public class AnalyticsMetricsTests
    {
        [Test]
        public void InitialValues_AreZero()
        {
            var m = new AnalyticsMetrics();
            Assert.AreEqual(0, m.TotalEnqueued);
            Assert.AreEqual(0, m.TotalSucceeded);
            Assert.AreEqual(0, m.TotalFailed);
            Assert.AreEqual(0, m.SavedHitchTimeMs);
        }

        [Test]
        public void RecordSuccess_IncrementsCounter()
        {
            var m = new AnalyticsMetrics();
            m.RecordSuccess();
            m.RecordSuccess();
            Assert.AreEqual(2, m.TotalSucceeded);
        }

        [Test]
        public void SuccessRate_WithNoEvents_Returns1()
        {
            var m = new AnalyticsMetrics();
            Assert.AreEqual(1f, m.SuccessRate);
        }

        [Test]
        public void SuccessRate_ComputesCorrectly()
        {
            var m = new AnalyticsMetrics();
            m.RecordSuccess();
            m.RecordSuccess();
            m.RecordFailure();
            Assert.AreEqual(2f / 3f, m.SuccessRate, 0.001f);
        }

        [Test]
        public void AddSavedHitchTime_Accumulates()
        {
            var m = new AnalyticsMetrics();
            m.AddSavedHitchTime(100);
            m.AddSavedHitchTime(250);
            Assert.AreEqual(350, m.SavedHitchTimeMs);
        }

        [Test]
        public void SetRetryBufferSize_SetsValue()
        {
            var m = new AnalyticsMetrics();
            m.SetRetryBufferSize(42);
            Assert.AreEqual(42, m.RetryBufferSize);
        }

        [Test]
        public void Reset_ZerosAllCounters()
        {
            var m = new AnalyticsMetrics();
            m.RecordEnqueue();
            m.RecordSuccess();
            m.RecordFailure();
            m.RecordDrop();
            m.AddSavedHitchTime(500);
            m.RecordCircuitOpen();
            m.SetRetryBufferSize(10);

            m.Reset();

            Assert.AreEqual(0, m.TotalEnqueued);
            Assert.AreEqual(0, m.TotalSucceeded);
            Assert.AreEqual(0, m.TotalFailed);
            Assert.AreEqual(0, m.TotalDropped);
            Assert.AreEqual(0, m.SavedHitchTimeMs);
            Assert.AreEqual(0, m.CircuitOpenCount);
            Assert.AreEqual(0, m.RetryBufferSize);
        }
    }
}
