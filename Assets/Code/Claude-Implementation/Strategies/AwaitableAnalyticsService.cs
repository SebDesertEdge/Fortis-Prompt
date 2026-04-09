using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Fortis.Analytics;
using Fortis.Core.DependencyInjection;
using Interview.Mocks;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Fortis.Analytics.Strategies
{
    /// <summary>
    /// Unity 6 Awaitable-based analytics service. Uses async Awaitable to process
    /// events one per frame on the main thread, with no raw threads.
    ///
    /// The processing loop awaits NextFrameAsync() between events, naturally
    /// spreading work across frames. SendEvent() executes on the main thread
    /// (required by UnityEngine.Random.value in the mock).
    ///
    /// Trade-off: hitches still occur for the SDK call, but the pattern is clean,
    /// allocation-free (Awaitable is pooled), and uses Unity 6 native async.
    /// </summary>
    public class AwaitableAnalyticsService : MonoBehaviour, IAnalyticsService
    {
        public CircuitBreaker CircuitBreaker { get; private set; }
        public AnalyticsMetrics Metrics { get; private set; }

        public bool IsReady => CircuitBreaker?.State != CircuitState.Open;
        public int MaxRetryBufferSize => _maxRetryBufferSize;

        private int _failureThreshold = 5;
        private int _circuitOpenDurationMs = 10000;
        private int _maxRetryBufferSize = 100;

        private UnstableLegacyService _service;
        private ConcurrentQueue<AnalyticsEvent> _eventQueue;
        private Queue<AnalyticsEvent> _retryBuffer;
        private volatile bool _flushRequested;

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

        private void Awake()
        {
            var config = AnalyticsConfig.Instance;
            if (config != null)
            {
                _failureThreshold = config.FailureThreshold;
                _circuitOpenDurationMs = config.CircuitOpenDurationMs;
                _maxRetryBufferSize = config.MaxRetryBufferSize;
            }

            _eventQueue = new ConcurrentQueue<AnalyticsEvent>();
            _retryBuffer = new Queue<AnalyticsEvent>();

            _service = new UnstableLegacyService();
            Metrics = new AnalyticsMetrics();
            CircuitBreaker = new CircuitBreaker(_failureThreshold, _circuitOpenDurationMs);

            CircuitBreaker.OnStateChanged += OnCircuitStateChanged;

            StartProcessingLoop();

            Debug.Log("[AwaitableAnalytics] Initialized.");
        }

        private async void StartProcessingLoop()
        {
            try
            {
                await ProcessingLoop();
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Debug.LogException(ex); }
        }

        private async Awaitable ProcessingLoop()
        {
            var token = destroyCancellationToken;

            while (!token.IsCancellationRequested)
            {
                // Process at most 1 event per frame
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

                // Yield to next frame
                await Awaitable.NextFrameAsync(token);
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
                Debug.LogWarning($"[AwaitableAnalytics] Event '{evt.EventName}' failed: {ex.Message}");
            }

            Metrics.AddSavedHitchTime(sw.ElapsedMilliseconds);
            evt.Callback?.Invoke(success);
        }

        private void BufferForRetry(AnalyticsEvent evt)
        {
            evt.Callback?.Invoke(false);

            if (_retryBuffer.Count >= _maxRetryBufferSize)
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

            Debug.Log($"[AwaitableAnalytics] Circuit state -> {newState}");
        }

        private void OnDestroy()
        {
            if (CircuitBreaker != null)
                CircuitBreaker.OnStateChanged -= OnCircuitStateChanged;

            Debug.Log("[AwaitableAnalytics] Shut down.");
        }
    }
}
