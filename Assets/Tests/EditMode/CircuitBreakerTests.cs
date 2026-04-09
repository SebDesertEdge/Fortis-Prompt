using System;
using System.Collections.Generic;
using NUnit.Framework;
using Fortis.Analytics;

namespace Fortis.Analytics.Tests
{
    [TestFixture]
    public class CircuitBreakerTests
    {
        private long _fakeTicks;
        private long FakeClock() => _fakeTicks;
        private void AdvanceTime(int ms) => _fakeTicks += TimeSpan.FromMilliseconds(ms).Ticks;

        [SetUp]
        public void SetUp()
        {
            _fakeTicks = DateTime.UtcNow.Ticks;
        }

        private CircuitBreaker MakeBreaker(int failureThreshold = 5, int openDurationMs = 10000)
        {
            return new CircuitBreaker(failureThreshold, openDurationMs, getTimestamp: FakeClock);
        }

        [Test]
        public void InitialState_IsClosed()
        {
            var cb = MakeBreaker();
            Assert.AreEqual(CircuitState.Closed, cb.State);
        }

        [Test]
        public void AllowRequest_InClosed_ReturnsTrue()
        {
            var cb = MakeBreaker();
            Assert.IsTrue(cb.AllowRequest());
        }

        [Test]
        public void RecordFailure_BelowThreshold_StaysClosed()
        {
            var cb = MakeBreaker(failureThreshold: 5);
            for (int i = 0; i < 4; i++)
                cb.RecordFailure();
            Assert.AreEqual(CircuitState.Closed, cb.State);
        }

        [Test]
        public void RecordFailure_AtThreshold_TransitionsToOpen()
        {
            var cb = MakeBreaker(failureThreshold: 5);
            for (int i = 0; i < 5; i++)
                cb.RecordFailure();
            Assert.AreEqual(CircuitState.Open, cb.State);
        }

        [Test]
        public void AllowRequest_InOpen_ReturnsFalse()
        {
            var cb = MakeBreaker(failureThreshold: 1, openDurationMs: 60000);
            cb.RecordFailure();
            Assert.IsFalse(cb.AllowRequest());
        }

        [Test]
        public void AllowRequest_AfterTimeout_TransitionsToHalfOpen()
        {
            var cb = MakeBreaker(failureThreshold: 1, openDurationMs: 1000);
            cb.RecordFailure();
            AdvanceTime(1001);
            Assert.IsTrue(cb.AllowRequest());
            Assert.AreEqual(CircuitState.HalfOpen, cb.State);
        }

        [Test]
        public void AllowRequest_BeforeTimeout_StaysOpen()
        {
            var cb = MakeBreaker(failureThreshold: 1, openDurationMs: 1000);
            cb.RecordFailure();
            AdvanceTime(500);
            Assert.IsFalse(cb.AllowRequest());
            Assert.AreEqual(CircuitState.Open, cb.State);
        }

        [Test]
        public void RecordSuccess_InHalfOpen_TransitionsToClosed()
        {
            var cb = MakeBreaker(failureThreshold: 1, openDurationMs: 1000);
            cb.RecordFailure();
            AdvanceTime(1001);
            cb.AllowRequest();
            cb.RecordSuccess();
            Assert.AreEqual(CircuitState.Closed, cb.State);
        }

        [Test]
        public void RecordFailure_InHalfOpen_TransitionsToOpen()
        {
            var cb = MakeBreaker(failureThreshold: 1, openDurationMs: 1000);
            cb.RecordFailure();
            AdvanceTime(1001);
            cb.AllowRequest();
            cb.RecordFailure();
            Assert.AreEqual(CircuitState.Open, cb.State);
        }

        [Test]
        public void RecordSuccess_ResetsConsecutiveFailures()
        {
            var cb = MakeBreaker(failureThreshold: 5);
            for (int i = 0; i < 4; i++)
                cb.RecordFailure();
            cb.RecordSuccess();
            for (int i = 0; i < 4; i++)
                cb.RecordFailure();
            Assert.AreEqual(CircuitState.Closed, cb.State);
        }

        [Test]
        public void OnStateChanged_FiresOnTransition()
        {
            var cb = MakeBreaker(failureThreshold: 1);
            var transitions = new List<CircuitState>();
            cb.OnStateChanged += s => transitions.Add(s);

            cb.RecordFailure();
            Assert.AreEqual(1, transitions.Count);
            Assert.AreEqual(CircuitState.Open, transitions[0]);
        }

        [Test]
        public void OnStateChanged_DoesNotFireDuplicateForSameState()
        {
            var cb = MakeBreaker(failureThreshold: 1);
            var transitions = new List<CircuitState>();
            cb.OnStateChanged += s => transitions.Add(s);

            cb.RecordFailure();
            cb.RecordFailure();
            Assert.AreEqual(1, transitions.Count);
        }

        [Test]
        public void Constructor_ThrowsOnInvalidThreshold()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new CircuitBreaker(failureThreshold: 0));
        }

        [Test]
        public void Constructor_ThrowsOnNegativeDuration()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new CircuitBreaker(openDurationMs: -1));
        }

        [Test]
        public void FullCycle_Closed_Open_HalfOpen_Closed()
        {
            var cb = MakeBreaker(failureThreshold: 3, openDurationMs: 5000);
            var transitions = new List<CircuitState>();
            cb.OnStateChanged += s => transitions.Add(s);

            cb.RecordFailure();
            cb.RecordFailure();
            cb.RecordFailure();
            Assert.AreEqual(CircuitState.Open, cb.State);

            AdvanceTime(5001);
            Assert.IsTrue(cb.AllowRequest());
            Assert.AreEqual(CircuitState.HalfOpen, cb.State);

            cb.RecordSuccess();
            Assert.AreEqual(CircuitState.Closed, cb.State);

            Assert.AreEqual(3, transitions.Count);
            Assert.AreEqual(CircuitState.Open, transitions[0]);
            Assert.AreEqual(CircuitState.HalfOpen, transitions[1]);
            Assert.AreEqual(CircuitState.Closed, transitions[2]);
        }
    }
}
