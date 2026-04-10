using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Interview.Mocks;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Claude.Analytics
{
    /// <summary>
    /// Worker-thread analytics service that offloads all processing to a background thread.
    ///
    /// THREADING CAVEAT: UnstableLegacyService.SendEvent() calls UnityEngine.Random.value,
    /// which is a main-thread-only API. In development builds, this will throw
    /// "UnityException: Random can only be called from the main thread."
    /// In release builds, the thread check is skipped for performance, so it runs
    /// but with technically undefined behavior.
    ///
    /// In a real production scenario, a legacy SDK would not use UnityEngine.Random
    /// internally — it would use System.Random or its own RNG. This caveat is specific
    /// to the mock's simplified implementation.
    /// </summary>
    public class ResilientAnalytics : MonoBehaviour
    {
        private static ResilientAnalytics _instance;
        public static ResilientAnalytics Instance
        {
            get
            {
                if (_instance == null)
                {
                    var go = new GameObject("[ResilientAnalytics]");
                    _instance = go.AddComponent<ResilientAnalytics>();
                    DontDestroyOnLoad(go);
                }
                return _instance;
            }
        }

        [Header("Circuit Breaker")]
        [SerializeField] private int _failureThreshold = 5;
        [SerializeField] private int _circuitOpenDurationMs = 10000;

        [Header("Retry Buffer")]
        [SerializeField] private int _maxRetryBufferSize = 100;

        public CircuitBreaker CircuitBreaker { get; private set; }
        public AnalyticsMetrics Metrics { get; private set; }

        public bool IsReady => CircuitBreaker?.State != CircuitState.Open;
        public int MaxRetryBufferSize => _maxRetryBufferSize;

        private UnstableLegacyService _service;
        private ConcurrentQueue<AnalyticsEvent> _eventQueue;
        private ConcurrentQueue<Action> _mainThreadActions;
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

        public void SendEvent(string eventName, Action<bool> onComplete = null)
        {
            Metrics.RecordEnqueue();
            _eventQueue.Enqueue(new AnalyticsEvent(eventName, onComplete));
            _workAvailable.Set();
        }

        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);
                return;
            }
            _instance = this;
            DontDestroyOnLoad(gameObject);

            _eventQueue = new ConcurrentQueue<AnalyticsEvent>();
            _mainThreadActions = new ConcurrentQueue<Action>();
            _retryBuffer = new Queue<AnalyticsEvent>();
            _workAvailable = new AutoResetEvent(false);

            _service = new UnstableLegacyService();
            Metrics = new AnalyticsMetrics();
            CircuitBreaker = new CircuitBreaker(_failureThreshold, _circuitOpenDurationMs);

            CircuitBreaker.OnStateChanged += OnCircuitStateChanged;

            _workerThread = new Thread(WorkerLoop)
            {
                IsBackground = true,
                Name = "ResilientAnalytics-Worker"
            };
            _workerThread.Start();

            Debug.Log("[ResilientAnalytics] Initialized.");
        }

        private void Update()
        {
            while (_mainThreadActions.TryDequeue(out var action))
            {
                try { action?.Invoke(); }
                catch (Exception ex) { Debug.LogException(ex); }
            }
        }

        private void OnDestroy()
        {
            Shutdown();
        }

        private void OnApplicationQuit()
        {
            Shutdown();
        }

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

                ExecuteEvent(evt);
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
                Debug.LogWarning($"[ResilientAnalytics] Event '{evt.EventName}' failed: {ex.Message}");
            }

            Metrics.AddSavedHitchTime(sw.ElapsedMilliseconds);
            DispatchCallback(evt.Callback, success);
        }

        private void BufferForRetry(AnalyticsEvent evt)
        {
            DispatchCallback(evt.Callback, false);

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
            _workAvailable.Set();
        }

        private void OnCircuitStateChanged(CircuitState newState)
        {
            if (newState == CircuitState.Open)
                Metrics.RecordCircuitOpen();

            if (newState == CircuitState.Closed)
                _flushRequested = true;

            Debug.Log($"[ResilientAnalytics] Circuit state -> {newState}");
        }

        private void DispatchCallback(Action<bool> callback, bool result)
        {
            if (callback != null)
                _mainThreadActions.Enqueue(() => callback(result));
        }

        private void Shutdown()
        {
            if (_shutdownRequested) return;
            _shutdownRequested = true;
            _workAvailable.Set();

            bool threadExited = false;
            if (_workerThread != null && _workerThread.IsAlive)
                threadExited = _workerThread.Join(2000);

            if (threadExited || _workerThread == null || !_workerThread.IsAlive)
                _workAvailable?.Dispose();

            if (CircuitBreaker != null)
                CircuitBreaker.OnStateChanged -= OnCircuitStateChanged;

            Debug.Log("[ResilientAnalytics] Shut down.");
        }
    }
}
