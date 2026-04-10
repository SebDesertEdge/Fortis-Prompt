using System;
using System.Collections.Generic;
using Claude.Analytics;
using NUnit.Framework;

namespace Tests.EditMode
{
    [TestFixture]
    public class CircuitBreakerTests
    {
        private long _fakeTime;
        private CircuitBreaker _cb;

        private long FakeTimestamp() => _fakeTime;

        [SetUp]
        public void SetUp()
        {
            _fakeTime = 0;
            _cb = new CircuitBreaker(failureThreshold: 3, openDurationMs: 1000, getTimestamp: FakeTimestamp);
        }

        // --- Construction ---

        [Test]
        public void Constructor_DefaultState_IsClosed()
        {
            Assert.AreEqual(CircuitState.Closed, _cb.State);
        }

        [Test]
        public void Constructor_SetsThresholdAndDuration()
        {
            Assert.AreEqual(3, _cb.FailureThreshold);
            Assert.AreEqual(1000, _cb.OpenDurationMs);
        }

        [Test]
        public void Constructor_ThrowsOnInvalidThreshold()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new CircuitBreaker(failureThreshold: 0));
        }

        [Test]
        public void Constructor_ThrowsOnNegativeDuration()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new CircuitBreaker(openDurationMs: -1));
        }

        // --- Closed state ---

        [Test]
        public void Closed_AllowsRequests()
        {
            Assert.IsTrue(_cb.AllowRequest());
        }

        [Test]
        public void Closed_FailuresBelowThreshold_StaysClosed()
        {
            _cb.RecordFailure();
            _cb.RecordFailure();
            Assert.AreEqual(CircuitState.Closed, _cb.State);
            Assert.AreEqual(2, _cb.ConsecutiveFailures);
        }

        [Test]
        public void Closed_SuccessResetsFailureCount()
        {
            _cb.RecordFailure();
            _cb.RecordFailure();
            _cb.RecordSuccess();
            Assert.AreEqual(0, _cb.ConsecutiveFailures);
        }

        // --- Tripping to Open ---

        [Test]
        public void Closed_ReachingThreshold_TripsToOpen()
        {
            for (int i = 0; i < 3; i++)
                _cb.RecordFailure();

            Assert.AreEqual(CircuitState.Open, _cb.State);
        }

        [Test]
        public void Open_BlocksRequests()
        {
            TripCircuit();
            Assert.IsFalse(_cb.AllowRequest());
        }

        // --- Open -> HalfOpen transition ---

        [Test]
        public void Open_AfterDurationExpires_TransitionsToHalfOpen()
        {
            TripCircuit();

            // Advance time past the open duration
            _fakeTime += TimeSpan.FromMilliseconds(1001).Ticks;

            Assert.IsTrue(_cb.AllowRequest());
            Assert.AreEqual(CircuitState.HalfOpen, _cb.State);
        }

        [Test]
        public void Open_BeforeDurationExpires_StaysOpen()
        {
            TripCircuit();

            _fakeTime += TimeSpan.FromMilliseconds(500).Ticks;

            Assert.IsFalse(_cb.AllowRequest());
            Assert.AreEqual(CircuitState.Open, _cb.State);
        }

        // --- HalfOpen state ---

        [Test]
        public void HalfOpen_AllowsProbeRequest()
        {
            TransitionToHalfOpen();
            // Already transitioned via AllowRequest, so a second call should still be allowed
            Assert.IsTrue(_cb.AllowRequest());
        }

        [Test]
        public void HalfOpen_SuccessTransitionsToClosed()
        {
            TransitionToHalfOpen();
            _cb.RecordSuccess();
            Assert.AreEqual(CircuitState.Closed, _cb.State);
        }

        [Test]
        public void HalfOpen_FailureTripsBackToOpen()
        {
            TransitionToHalfOpen();
            _cb.RecordFailure();
            Assert.AreEqual(CircuitState.Open, _cb.State);
        }

        // --- State change events ---

        [Test]
        public void OnStateChanged_FiresOnTrip()
        {
            var states = new List<CircuitState>();
            _cb.OnStateChanged += s => states.Add(s);

            TripCircuit();

            Assert.AreEqual(1, states.Count);
            Assert.AreEqual(CircuitState.Open, states[0]);
        }

        [Test]
        public void OnStateChanged_FiresOnFullCycle()
        {
            var states = new List<CircuitState>();
            _cb.OnStateChanged += s => states.Add(s);

            TripCircuit();

            _fakeTime += TimeSpan.FromMilliseconds(1001).Ticks;
            _cb.AllowRequest(); // -> HalfOpen

            _cb.RecordSuccess(); // -> Closed

            Assert.AreEqual(3, states.Count);
            Assert.AreEqual(CircuitState.Open, states[0]);
            Assert.AreEqual(CircuitState.HalfOpen, states[1]);
            Assert.AreEqual(CircuitState.Closed, states[2]);
        }

        [Test]
        public void OnStateChanged_HandlerExceptionDoesNotCrash()
        {
            _cb.OnStateChanged += _ => throw new InvalidOperationException("test");

            Assert.DoesNotThrow(() => TripCircuit());
            Assert.AreEqual(CircuitState.Open, _cb.State);
        }

        // --- PercentageOpenStateCompletion ---

        [Test]
        public void PercentageOpenStateCompletion_WhenClosed_Returns100()
        {
            Assert.AreEqual(100f, _cb.PercentageOpenStateCompletion);
        }

        [Test]
        public void PercentageOpenStateCompletion_WhenOpen_ReflectsElapsedTime()
        {
            TripCircuit();
            _fakeTime += TimeSpan.FromMilliseconds(500).Ticks;

            // Should be ~50%
            float pct = _cb.PercentageOpenStateCompletion;
            Assert.Greater(pct, 40f);
            Assert.Less(pct, 60f);
        }

        // --- Helpers ---

        private void TripCircuit()
        {
            for (int i = 0; i < _cb.FailureThreshold; i++)
                _cb.RecordFailure();
        }

        private void TransitionToHalfOpen()
        {
            TripCircuit();
            _fakeTime += TimeSpan.FromMilliseconds(1001).Ticks;
            _cb.AllowRequest(); // triggers Open -> HalfOpen
        }
    }
}
