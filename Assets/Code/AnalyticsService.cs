using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using Fortis.Analytics;
using Fortis.Core.DependencyInjection;
using Interview.Mocks;
using UnityEditor.Searcher;
using IDisposable = System.IDisposable;

namespace Fortis
{
    public class AnalyticsService : IInitializable, IDisposable
    {
        [Inject] protected ResilientAnalyticsConfig Config;
        
        public CircuitBreaker CircuitBreaker { get; private set; }
        public AnalyticsMetrics Metrics { get; private set; }
        
        public bool IsReady => CircuitBreaker?.State != CircuitState.Open;
        public int MaxRetryBufferSize => Config.MaxRetryBufferSize;
        
        private UnstableLegacyService _service;
        private ConcurrentQueue<Searcher.AnalyticsEvent> _eventQueue;
        private ConcurrentQueue<Action> _mainThreadActions;
        private Queue<Searcher.AnalyticsEvent> _retryBuffer;
        private Thread _workerThread;
        private volatile bool _shutdownRequested;
        private volatile bool _flushRequested;
        private AutoResetEvent _workAvailable;
        
        public void Initialize()
        {
            _eventQueue = new ConcurrentQueue<Searcher.AnalyticsEvent>();
            _mainThreadActions = new ConcurrentQueue<Action>();
            _retryBuffer = new Queue<Searcher.AnalyticsEvent>();
            _workAvailable = new AutoResetEvent(false);

            _service = new UnstableLegacyService();
            Metrics = new AnalyticsMetrics();
            CircuitBreaker = new CircuitBreaker(Config.FailureThreshold, Config.CircuitOpenDurationMs);

            CircuitBreaker.OnStateChanged += OnCircuitStateChanged;

            _workerThread = new Thread(WorkerLoop)
            {
                IsBackground = true,
                Name = "ResilientAnalytics-Worker"
            };
            _workerThread.Start();
        }
        
        public void Dispose()
        {
            _shutdownRequested = true;
            _workAvailable.Set();
            _workerThread.Join();
        }
        
    }
}