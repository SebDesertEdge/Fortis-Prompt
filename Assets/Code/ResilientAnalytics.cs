using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Tasks;
using Claude.Analytics;
using Fortis;
using Fortis.Analytics.PlayerLoop;
using Fortis.Core.DependencyInjection;
using Interview.Mocks;
using Debug = UnityEngine.Debug;
using IDisposable = System.IDisposable;

namespace Code
{
    public class ResilientAnalytics : IAnalyticsService, IInitializable, IDisposable, ICleanable, ITickable
    {
        public CircuitBreaker CircuitBreaker { get; private set;}
        public AnalyticsMetrics Metrics { get; private set; }
        public bool IsReady => CircuitBreaker?.State != CircuitState.Open;

        [Inject] protected readonly AnalyticsConfig _config;
        
        private UnstableLegacyService _service;
        private ConcurrentQueue<QueuedEvent> _pendingQueue;
        private Stopwatch _stopwatch;
        
        private struct QueuedEvent
        {
            public string EventName;
            public Action<bool> Callback;
            public int AttemptCount;
        }
        
        public void Initialize()
        {
            _pendingQueue = new ConcurrentQueue<QueuedEvent>();
            
            _service = new UnstableLegacyService();
            Metrics = new AnalyticsMetrics();
            
            _stopwatch = Stopwatch.StartNew();
            
            CircuitBreaker = new CircuitBreaker(_config.FailureThreshold, _config.CircuitOpenDurationMs);
            CircuitBreaker.OnStateChanged += OnCircuitStateChanged;
        }
        
        public void Dispose()
        {
            CleanUp();
        }

        public void Cleanup()
        {
            CleanUp();
        }

        public void Tick()
        {
            var start = _stopwatch.ElapsedMilliseconds;
            var budgetMs = _config.FrameBudgetMs;

            if (!CircuitBreaker.AllowRequest())
            {
                return;
            }

            while (_stopwatch.ElapsedMilliseconds - start < budgetMs && _pendingQueue.TryDequeue(out var evt))
            {
                if (!CircuitBreaker.AllowRequest())
                {
                    _pendingQueue.Enqueue(new QueuedEvent
                    {
                        EventName = evt.EventName,
                        Callback = evt.Callback,
                        AttemptCount = evt.AttemptCount
                    });
                    continue;
                }

                TryToSendEvent(evt);
            }
            
        }

        public void SendEvent(string eventName, Action<bool> onComplete = null)
        {
            Metrics.RecordEnqueue();
            _pendingQueue.Enqueue(new QueuedEvent {EventName = eventName, AttemptCount = 0, Callback = onComplete});
        }

        private void CleanUp()
        {
            _stopwatch.Stop();
            if (CircuitBreaker != null)
            {
                CircuitBreaker.OnStateChanged -= OnCircuitStateChanged;
            }
        }

        /// <summary>
        /// Synchronous SDK call on the main thread. Records metrics and schedules retries
        /// for failures on a background thread.
        /// </summary>
        private void TryToSendEvent(QueuedEvent evt)
        {
            var start = _stopwatch.ElapsedMilliseconds;
            bool success = false;
            
            try
            {
                success = _service.SendEvent(evt.EventName);
            }
            catch (Exception ex)
            {
                Debug.LogWarning(ex);
            }
            finally
            {
                Metrics.AddSavedHitchTime(_stopwatch.ElapsedMilliseconds - start);
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

                if (RetryPolicy.ShouldRetry(evt.AttemptCount, _config.RetryConfig))
                {
                    ScheduleRetry(evt);
                }
                else
                {
                    Metrics.RecordDrop();
                    evt.Callback?.Invoke(false);
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
            var delay = RetryPolicy.GetDelay(evt.AttemptCount, _config.RetryConfig);

            Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(delay);
                    Metrics.RecordRetry();
                    _pendingQueue.Enqueue(new QueuedEvent {EventName = evt.EventName, AttemptCount = evt.AttemptCount + 1, Callback = evt.Callback});
                }
                catch (TaskCanceledException) { }
            });
        }

        private void OnCircuitStateChanged(CircuitState newState)
        {
            if (newState == CircuitState.Open)
            {
                Metrics.RecordCircuitOpen();
            }
        }
    }
}