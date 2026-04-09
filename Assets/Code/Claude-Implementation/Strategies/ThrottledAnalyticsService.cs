using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using Fortis.Analytics;
using Fortis.Core.DependencyInjection;
using Interview.Mocks;
using Debug = UnityEngine.Debug;

namespace Fortis.Analytics.Strategies
{
    /// <summary>
    /// Main-thread-only analytics service that processes at most 1 event per frame.
    /// Safe for all Unity APIs since everything runs on the main thread.
    /// Trade-off: hitches still occur but are bounded to 1 per frame, and the
    /// circuit breaker will stop calls when failures cascade.
    /// </summary>
    public class ThrottledAnalyticsService : IAnalyticsService, IInitializable, ITickable
    {
        [Inject] protected AnalyticsConfig Config;

        public CircuitBreaker CircuitBreaker { get; private set; }
        public AnalyticsMetrics Metrics { get; private set; }

        public bool IsReady => CircuitBreaker?.State != CircuitState.Open;
        public int MaxRetryBufferSize => Config.MaxRetryBufferSize;

        private UnstableLegacyService _service;
        private ConcurrentQueue<AnalyticsEvent> _eventQueue;
        private Queue<AnalyticsEvent> _retryBuffer;
        private bool _flushRequested;

        private readonly struct AnalyticsEvent
        {
            public readonly string EventName;
            public readonly Action<bool> Callback;

            public AnalyticsEvent(string eventName, Action<bool> callback)
            {
                EventName = eventName;
                Callback = callback;
            }
        }

        public void SendEvent(string eventName, Action<bool> onComplete = null)
        {
            Metrics.RecordEnqueue();
            _eventQueue.Enqueue(new AnalyticsEvent(eventName, onComplete));
        }

        public void Initialize()
        {
            _eventQueue = new ConcurrentQueue<AnalyticsEvent>();
            _retryBuffer = new Queue<AnalyticsEvent>();

            _service = new UnstableLegacyService();
            Metrics = new AnalyticsMetrics();
            CircuitBreaker = new CircuitBreaker(Config.FailureThreshold, Config.CircuitOpenDurationMs);

            CircuitBreaker.OnStateChanged += OnCircuitStateChanged;

            Debug.Log("[ThrottledAnalytics] Initialized.");
        }

        public void Tick()
        {
            // Process at most 1 event per frame to bound hitch impact
            if (_eventQueue.TryDequeue(out var evt))
            {
                if (!CircuitBreaker.AllowRequest())
                {
                    BufferForRetry(evt);
                }
                else
                {
                    ExecuteEvent(evt);
                }
            }

            if (_flushRequested)
            {
                _flushRequested = false;
                FlushRetryBuffer();
            }
        }

        private void ExecuteEvent(AnalyticsEvent evt)
        {
            var sw = Stopwatch.StartNew();
            bool success = false;

            try
            {
                success = _service.SendEvent(evt.EventName);
                sw.Stop();
                CircuitBreaker.RecordSuccess();
                Metrics.RecordSuccess();
            }
            catch (Exception ex)
            {
                sw.Stop();
                CircuitBreaker.RecordFailure();
                Metrics.RecordFailure();
                Debug.LogWarning($"[ThrottledAnalytics] Event '{evt.EventName}' failed: {ex.Message}");
            }

            Metrics.AddSavedHitchTime(sw.ElapsedMilliseconds);
            evt.Callback?.Invoke(success);
        }

        private void BufferForRetry(AnalyticsEvent evt)
        {
            evt.Callback?.Invoke(false);

            if (_retryBuffer.Count >= MaxRetryBufferSize)
            {
                _retryBuffer.Dequeue();
                Metrics.RecordDrop();
            }
            _retryBuffer.Enqueue(new AnalyticsEvent(evt.EventName, null));
            Metrics.SetRetryBufferSize(_retryBuffer.Count);
        }

        private void FlushRetryBuffer()
        {
            while (_retryBuffer.Count > 0)
            {
                var evt = _retryBuffer.Dequeue();
                _eventQueue.Enqueue(evt);
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

            Debug.Log($"[ThrottledAnalytics] Circuit state -> {newState}");
        }
    }
}
