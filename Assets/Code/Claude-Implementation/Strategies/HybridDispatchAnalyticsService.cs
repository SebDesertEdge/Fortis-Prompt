using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Fortis.Analytics;
using Fortis.Core.DependencyInjection;
using Interview.Mocks;
using Debug = UnityEngine.Debug;

namespace Fortis.Analytics.Strategies
{
    /// <summary>
    /// Hybrid analytics service: a background thread handles queue management,
    /// circuit breaker checks, and retry logic. Only the actual SendEvent() call
    /// is marshalled back to the main thread (required by UnityEngine.Random.value).
    ///
    /// Trade-off: the SDK call still hitches the main thread, but all surrounding
    /// logic (retry buffering, circuit breaker state, metrics) runs concurrently.
    /// Main thread processes at most 1 dispatched SDK call per frame.
    /// </summary>
    public class HybridDispatchAnalyticsService : IAnalyticsService, IInitializable, ITickable, Fortis.Core.DependencyInjection.IDisposable
    {
        [Inject] protected AnalyticsConfig Config;

        public CircuitBreaker CircuitBreaker { get; private set; }
        public AnalyticsMetrics Metrics { get; private set; }

        public bool IsReady => CircuitBreaker?.State != CircuitState.Open;
        public int MaxRetryBufferSize => Config.MaxRetryBufferSize;

        private UnstableLegacyService _service;
        private ConcurrentQueue<AnalyticsEvent> _eventQueue;
        private ConcurrentQueue<PendingExecution> _mainThreadDispatch;
        private ConcurrentQueue<Action> _mainThreadCallbacks;
        private Queue<AnalyticsEvent> _retryBuffer;
        private Thread _workerThread;
        private volatile bool _shutdownRequested;
        private volatile bool _flushRequested;
        private AutoResetEvent _workAvailable;

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

        private readonly struct PendingExecution
        {
            public readonly string EventName;
            public readonly Action<bool> Callback;

            public PendingExecution(string eventName, Action<bool> callback)
            {
                EventName = eventName;
                Callback = callback;
            }
        }

        public void SendEvent(string eventName, Action<bool> onComplete = null)
        {
            Metrics.RecordEnqueue();
            _eventQueue.Enqueue(new AnalyticsEvent(eventName, onComplete));
            _workAvailable.Set();
        }

        public void Initialize()
        {
            _eventQueue = new ConcurrentQueue<AnalyticsEvent>();
            _mainThreadDispatch = new ConcurrentQueue<PendingExecution>();
            _mainThreadCallbacks = new ConcurrentQueue<Action>();
            _retryBuffer = new Queue<AnalyticsEvent>();
            _workAvailable = new AutoResetEvent(false);

            _service = new UnstableLegacyService();
            Metrics = new AnalyticsMetrics();
            CircuitBreaker = new CircuitBreaker(Config.FailureThreshold, Config.CircuitOpenDurationMs);

            CircuitBreaker.OnStateChanged += OnCircuitStateChanged;

            _workerThread = new Thread(WorkerLoop)
            {
                IsBackground = true,
                Name = "HybridDispatchAnalytics-Worker"
            };
            _workerThread.Start();

            Debug.Log("[HybridDispatchAnalytics] Initialized.");
        }

        /// <summary>
        /// Main thread: execute at most 1 dispatched SDK call per frame,
        /// then drain any queued callbacks.
        /// </summary>
        public void Tick()
        {
            if (_mainThreadDispatch.TryDequeue(out var pending))
            {
                ExecuteOnMainThread(pending);
            }

            while (_mainThreadCallbacks.TryDequeue(out var action))
            {
                try { action?.Invoke(); }
                catch (Exception ex) { Debug.LogException(ex); }
            }
        }

        private void ExecuteOnMainThread(PendingExecution pending)
        {
            var sw = Stopwatch.StartNew();
            bool success = false;

            try
            {
                success = _service.SendEvent(pending.EventName);
                sw.Stop();
                CircuitBreaker.RecordSuccess();
                Metrics.RecordSuccess();
            }
            catch (Exception ex)
            {
                sw.Stop();
                CircuitBreaker.RecordFailure();
                Metrics.RecordFailure();
                Debug.LogWarning($"[HybridDispatchAnalytics] Event '{pending.EventName}' failed: {ex.Message}");
            }

            Metrics.AddSavedHitchTime(sw.ElapsedMilliseconds);
            pending.Callback?.Invoke(success);
        }

        /// <summary>
        /// Worker thread: dequeue events, check circuit breaker, and dispatch
        /// allowed requests to the main-thread execution queue.
        /// </summary>
        private void WorkerLoop()
        {
            while (!_shutdownRequested)
            {
                _workAvailable.WaitOne(200);
                ProcessEventQueue();
            }
        }

        private void ProcessEventQueue()
        {
            while (_eventQueue.TryDequeue(out var evt))
            {
                if (_shutdownRequested) break;

                if (!CircuitBreaker.AllowRequest())
                {
                    BufferForRetry(evt);
                    continue;
                }

                // Dispatch to main thread for actual SDK call
                _mainThreadDispatch.Enqueue(new PendingExecution(evt.EventName, evt.Callback));
            }

            if (_flushRequested)
            {
                _flushRequested = false;
                FlushRetryBuffer();
            }
        }

        private void BufferForRetry(AnalyticsEvent evt)
        {
            if (evt.Callback != null)
                _mainThreadCallbacks.Enqueue(() => evt.Callback(false));

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
            _workAvailable.Set();
        }

        private void OnCircuitStateChanged(CircuitState newState)
        {
            if (newState == CircuitState.Open)
                Metrics.RecordCircuitOpen();

            if (newState == CircuitState.Closed)
                _flushRequested = true;

            Debug.Log($"[HybridDispatchAnalytics] Circuit state -> {newState}");
        }

        public void Dispose()
        {
            if (_shutdownRequested) return;
            _shutdownRequested = true;
            _workAvailable?.Set();

            if (_workerThread != null && _workerThread.IsAlive)
                _workerThread.Join(2000);

            _workAvailable?.Dispose();

            if (CircuitBreaker != null)
                CircuitBreaker.OnStateChanged -= OnCircuitStateChanged;

            Debug.Log("[HybridDispatchAnalytics] Shut down.");
        }
    }
}
