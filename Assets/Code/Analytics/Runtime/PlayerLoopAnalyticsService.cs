using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Claude.Analytics;
using Fortis.Core.DependencyInjection;
using Interview.Mocks;
using Debug = UnityEngine.Debug;

namespace Fortis.Analytics.PlayerLoop
{
    /// <summary>
    /// Frame-budgeted analytics service that drains events on the main thread within a
    /// configurable per-frame time budget. Failed events are retried with exponential
    /// backoff on background threads. Events arriving while the circuit is open are
    /// buffered in a pending queue and replayed when the circuit recovers.
    ///
    /// Key properties:
    /// - All SendEvent() calls to the legacy SDK happen on the main thread (safe for
    ///   UnityEngine.Random / Debug.Log used internally by UnstableLegacyService).
    /// - Retry scheduling runs on ThreadPool threads, re-enqueueing to the lock-free
    ///   ConcurrentQueue for the next frame's drain pass.
    /// - Per-frame processing is bounded by FrameBudgetMs (default 8ms) to prevent
    ///   monopolising the frame when many events are queued.
    /// </summary>
    public class PlayerLoopAnalyticsService : IAnalyticsService, IInitializable, ITickable, Core.DependencyInjection.IDisposable
    {
        [Inject] protected AnalyticsConfig Config;

        public CircuitBreaker CircuitBreaker { get; private set; }
        public AnalyticsMetrics Metrics { get; private set; }
        public bool IsReady => CircuitBreaker?.State != CircuitState.Open;

        private UnstableLegacyService _service;
        private ConcurrentQueue<QueuedEvent> _intakeQueue;
        private Queue<QueuedEvent> _pendingQueue;
        private volatile bool _flushRequested;
        private CancellationTokenSource _cts;
        private IFirebaseReporter _firebaseReporter;
        private RetryConfig _retryConfig;

        private struct QueuedEvent
        {
            public string EventName;
            public Action<bool> Callback;
            public int AttemptCount;
        }

        public void SendEvent(string eventName, Action<bool> onComplete = null)
        {
            Metrics.RecordEnqueue();
            _intakeQueue.Enqueue(new QueuedEvent
            {
                EventName = eventName,
                Callback = onComplete,
                AttemptCount = 0
            });
        }

        public void Initialize()
        {
            _intakeQueue = new ConcurrentQueue<QueuedEvent>();
            _pendingQueue = new Queue<QueuedEvent>();
            _cts = new CancellationTokenSource();

            _service = new UnstableLegacyService();
            Metrics = new AnalyticsMetrics();
            CircuitBreaker = new CircuitBreaker(Config.FailureThreshold, Config.CircuitOpenDurationMs);

#if FIREBASE_CRASHLYTICS
            _firebaseReporter = new FirebaseCrashlyticsReporter();
#else
            _firebaseReporter = new NullFirebaseReporter();
#endif

            _retryConfig = new RetryConfig
            {
                MaxAttempts = RetryConfig.Default.MaxAttempts,
                BaseDelayMs = RetryConfig.Default.BaseDelayMs,
                MaxDelayMs = RetryConfig.Default.MaxDelayMs,
                JitterFactor = RetryConfig.Default.JitterFactor
            };

            CircuitBreaker.OnStateChanged += OnCircuitStateChanged;

            Debug.Log("[PlayerLoopAnalytics] Initialized " +
                      $"(budget={Config.FrameBudgetMs}ms, retries={_retryConfig.MaxAttempts}).");
        }

        /// <summary>
        /// Main-thread drain. Processes queued events within the configured frame budget.
        /// </summary>
        public void Tick()
        {
            var sw = Stopwatch.StartNew();
            float budgetMs = Config.FrameBudgetMs;

            while (sw.Elapsed.TotalMilliseconds < budgetMs && _intakeQueue.TryDequeue(out var evt))
            {
                if (!CircuitBreaker.AllowRequest())
                {
                    BufferForPending(evt);
                    continue;
                }

                ExecuteSend(evt);
            }

            if (_flushRequested)
            {
                _flushRequested = false;
                FlushPendingQueue();
            }
        }

        /// <summary>
        /// Synchronous SDK call on the main thread. Records metrics and schedules retries
        /// for failures on a background thread.
        /// </summary>
        private void ExecuteSend(QueuedEvent evt)
        {
            var sw = Stopwatch.StartNew();
            bool success = false;
            Exception caughtEx = null;

            try
            {
                success = _service.SendEvent(evt.EventName);
            }
            catch (Exception ex)
            {
                caughtEx = ex;
            }
            finally
            {
                sw.Stop();
                Metrics.AddSavedHitchTime(sw.ElapsedMilliseconds);
            }

            if (success)
            {
                CircuitBreaker.RecordSuccess();
                Metrics.RecordSuccess();
                evt.Callback?.Invoke(true);
            }
            else
            {
                CircuitBreaker.RecordFailure();
                Metrics.RecordFailure();

                if (caughtEx != null)
                {
                    _firebaseReporter.ReportException(caughtEx,
                        $"AnalyticsEvent:{evt.EventName} attempt:{evt.AttemptCount}");
                    Debug.LogWarning(
                        $"[PlayerLoopAnalytics] Event '{evt.EventName}' failed: {caughtEx.Message}");
                }

                evt.Callback?.Invoke(false);

                if (RetryPolicy.ShouldRetry(evt.AttemptCount, _retryConfig))
                {
                    ScheduleRetry(evt);
                }
                else
                {
                    Metrics.RecordDrop();
                }
            }
        }

        /// <summary>
        /// Schedules a retry on a ThreadPool thread with exponential backoff.
        /// After the delay, the event is re-enqueued to the intake queue for
        /// the next frame's drain pass.
        /// </summary>
        private void ScheduleRetry(QueuedEvent evt)
        {
            var delay = RetryPolicy.GetDelay(evt.AttemptCount, _retryConfig);
            var token = _cts.Token;

            Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(delay, token);
                    if (!token.IsCancellationRequested)
                    {
                        _intakeQueue.Enqueue(new QueuedEvent
                        {
                            EventName = evt.EventName,
                            Callback = null, // don't re-fire callback on retry
                            AttemptCount = evt.AttemptCount + 1
                        });
                        Metrics.RecordRetry();
                    }
                }
                catch (TaskCanceledException) { }
            }, token);
        }

        /// <summary>
        /// Buffers an event in the pending queue when the circuit is open.
        /// Drops the oldest event if the queue exceeds MaxRetryBufferSize.
        /// </summary>
        private void BufferForPending(QueuedEvent evt)
        {
            evt.Callback?.Invoke(false);

            if (_pendingQueue.Count >= _retryConfig.MaxAttempts)
            {
                _pendingQueue.Dequeue();
                Metrics.RecordDrop();
            }

            _pendingQueue.Enqueue(new QueuedEvent
            {
                EventName = evt.EventName,
                Callback = null,
                AttemptCount = evt.AttemptCount
            });
            Metrics.SetRetryBufferSize(_pendingQueue.Count);
        }

        /// <summary>
        /// Drains the pending queue back into the intake queue when the circuit
        /// transitions from Open → Closed.
        /// </summary>
        private void FlushPendingQueue()
        {
            while (_pendingQueue.Count > 0)
            {
                var evt = _pendingQueue.Dequeue();
                _intakeQueue.Enqueue(evt);
                Metrics.RecordRetry();
            }
            Metrics.SetRetryBufferSize(0);
        }

        private void OnCircuitStateChanged(CircuitState newState)
        {
            if (newState == CircuitState.Open)
                Metrics.RecordCircuitOpen();

            if (newState == CircuitState.Closed)
                _flushRequested = true;

            Debug.Log($"[PlayerLoopAnalytics] Circuit state -> {newState}");
        }

        public void Dispose()
        {
            _cts?.Cancel();
            _cts?.Dispose();

            if (CircuitBreaker != null)
                CircuitBreaker.OnStateChanged -= OnCircuitStateChanged;

            Debug.Log("[PlayerLoopAnalytics] Shut down.");
        }
    }
}
