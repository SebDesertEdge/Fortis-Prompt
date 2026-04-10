using System;
using Code.Core.Utilities;
using NUnit.Framework;

namespace Tests.EditMode
{
    [TestFixture]
    public class RetryPolicyTests
    {
        private RetryConfig _config;

        [SetUp]
        public void SetUp()
        {
            _config = RetryConfig.Default;
        }

        // --- ShouldRetry ---

        [Test]
        public void ShouldRetry_AttemptBelowMax_ReturnsTrue()
        {
            Assert.IsTrue(RetryPolicy.ShouldRetry(0, _config));
            Assert.IsTrue(RetryPolicy.ShouldRetry(1, _config));
            Assert.IsTrue(RetryPolicy.ShouldRetry(3, _config));
        }

        [Test]
        public void ShouldRetry_AttemptAtMax_ReturnsFalse()
        {
            Assert.IsFalse(RetryPolicy.ShouldRetry(4, _config));
        }

        [Test]
        public void ShouldRetry_AttemptAboveMax_ReturnsFalse()
        {
            Assert.IsFalse(RetryPolicy.ShouldRetry(10, _config));
        }

        // --- GetDelay ---

        [Test]
        public void GetDelay_FirstAttempt_IsAroundBaseDelay()
        {
            var delay = RetryPolicy.GetDelay(0, _config);

            // BaseDelay=500, jitter=±10% → 450..550
            Assert.GreaterOrEqual(delay.TotalMilliseconds, 400);
            Assert.LessOrEqual(delay.TotalMilliseconds, 600);
        }

        [Test]
        public void GetDelay_ExponentiallyIncreases()
        {
            var delay0 = RetryPolicy.GetDelay(0, _config).TotalMilliseconds;
            var delay1 = RetryPolicy.GetDelay(1, _config).TotalMilliseconds;
            var delay2 = RetryPolicy.GetDelay(2, _config).TotalMilliseconds;

            // Each should roughly double (with jitter)
            Assert.Greater(delay1, delay0 * 1.5);
            Assert.Greater(delay2, delay1 * 1.5);
        }

        [Test]
        public void GetDelay_CappedAtMaxDelay()
        {
            // Attempt 100 should hit the cap
            var delay = RetryPolicy.GetDelay(100, _config);

            // MaxDelay=8000, with jitter could be up to 8800
            Assert.LessOrEqual(delay.TotalMilliseconds, _config.MaxDelayMs * 1.2);
        }

        [Test]
        public void GetDelay_NeverNegative()
        {
            var config = new RetryConfig
            {
                MaxAttempts = 10,
                BaseDelayMs = 1,
                MaxDelayMs = 1,
                JitterFactor = 0.9
            };

            for (int i = 0; i < 100; i++)
            {
                var delay = RetryPolicy.GetDelay(i, config);
                Assert.GreaterOrEqual(delay.TotalMilliseconds, 0);
            }
        }

        [Test]
        public void GetDelay_ZeroJitter_ReturnsExactDelay()
        {
            var config = new RetryConfig
            {
                MaxAttempts = 4,
                BaseDelayMs = 100,
                MaxDelayMs = 10000,
                JitterFactor = 0
            };

            Assert.AreEqual(100, RetryPolicy.GetDelay(0, config).TotalMilliseconds, 0.01);
            Assert.AreEqual(200, RetryPolicy.GetDelay(1, config).TotalMilliseconds, 0.01);
            Assert.AreEqual(400, RetryPolicy.GetDelay(2, config).TotalMilliseconds, 0.01);
            Assert.AreEqual(800, RetryPolicy.GetDelay(3, config).TotalMilliseconds, 0.01);
        }

        // --- Default config ---

        [Test]
        public void DefaultConfig_HasExpectedValues()
        {
            var config = RetryConfig.Default;
            Assert.AreEqual(4, config.MaxAttempts);
            Assert.AreEqual(500, config.BaseDelayMs);
            Assert.AreEqual(8000, config.MaxDelayMs);
            Assert.AreEqual(0.1, config.JitterFactor, 0.001);
        }
    }
}
