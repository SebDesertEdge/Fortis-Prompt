using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Claude.Analytics;
using Code.Core.DependencyInjection;
using UnityEditor;
using UnityEngine;

namespace Code.Editor
{
    public class AnalyticsServiceWindow : EditorWindow
    {
        private struct BenchmarkResult
        {
            public int EventCount;
            public long WallClockMs;
            public double ThroughputPerSec;
            public long Succeeded;
            public long Failed;
            public long Dropped;
            public long Retried;
            public float SuccessRate;
            public long PeakRetryBuffer;
            public long CircuitOpenCount;
            public float FrameBudgetMs;
        }

        private Vector2 _scrollPosition;
        private bool _sendEventsInMainThread;
        
        // Benchmark state
        private int _eventCount = 50;
        private bool _benchmarkRunning;
        private int _completedCallbacks;
        private int _expectedCallbacks;
        private Stopwatch _stopwatch;
        private long _peakRetryBuffer;

        // History
        private readonly List<BenchmarkResult> _history = new();

        private double _lastRepaintTime;
        private const double RepaintInterval = 0.25;

        [MenuItem("Tools/Analytics Monitor")]
        public static void ShowWindow()
        {
            GetWindow<AnalyticsServiceWindow>("Analytics Monitor");
        }

        private void OnEnable()
        {
            EditorApplication.update += OnEditorUpdate;
        }

        private void OnDisable()
        {
            EditorApplication.update -= OnEditorUpdate;
        }

        private void OnEditorUpdate()
        {
            if (!_benchmarkRunning)
            {
                return;
            }

            if (_benchmarkRunning)
            {
                var service = DiContainer.Resolve<IAnalyticsService>();
                if (service?.Metrics != null)
                {
                    var current = service.Metrics.RetryBufferSize;
                    if (current > _peakRetryBuffer)
                        _peakRetryBuffer = current;
                }
            }

            if (EditorApplication.timeSinceStartup - _lastRepaintTime > RepaintInterval)
            {
                _lastRepaintTime = EditorApplication.timeSinceStartup;
                Repaint();
            }
        }

        private void OnGUI()
        {
            _scrollPosition = EditorGUILayout.BeginScrollView(_scrollPosition);
            if (!Application.isPlaying)
            {
                DrawEditMode();
            }
            else
            {
                DrawPlayMode();    
            }
            EditorGUILayout.Space(10);
            EditorGUILayout.EndScrollView();
        }

        private void DrawEditMode()
        {
            var config = AnalyticsConfig.Instance;
            if (config == null)
            {
                EditorGUILayout.HelpBox(
                    "Analytics configuration not found. " +
                    "Please create a new AnalyticsConfig asset in the Resources folder.",
                    MessageType.Error);
                if (GUILayout.Button("Create Config"))
                {
                    AnalyticsConfig.CreateConfig();
                }
                return;
            }

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("SETUP CONFIG FILE", new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 18,
                alignment = TextAnchor.MiddleLeft
            });
            GUILayout.FlexibleSpace();
            EditorGUILayout.LabelField("Fortis", EditorStyles.miniLabel, GUILayout.Width(40));
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.Space(4);
            
            var serializedState = new SerializedObject(config);
            EditorGUILayout.PropertyField(serializedState.FindProperty(nameof(AnalyticsConfig.FailureThreshold)));
            EditorGUILayout.PropertyField(serializedState.FindProperty(nameof(AnalyticsConfig.CircuitOpenDurationMs)));
            EditorGUILayout.PropertyField(serializedState.FindProperty(nameof(AnalyticsConfig.FrameBudgetMs)));
            EditorGUILayout.PropertyField(serializedState.FindProperty(nameof(AnalyticsConfig.RetryConfig)));

            serializedState.ApplyModifiedProperties();
        }

        private void DrawPlayMode()
        {
            var config = AnalyticsConfig.Instance;
            EditorGUILayout.Space(10);

            var service = DiContainer.Resolve<IAnalyticsService>();
            if (service == null)
            {
                return;
            }
            
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("LIVE MODE", new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 18,
                alignment = TextAnchor.MiddleLeft
            });
            GUILayout.FlexibleSpace();
            EditorGUILayout.LabelField("Fortis", EditorStyles.miniLabel, GUILayout.Width(40));
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.Space(4);
            
            EditorGUI.BeginDisabledGroup(_benchmarkRunning);
            _sendEventsInMainThread = EditorGUILayout.ToggleLeft("Send Events in Main Thread", _sendEventsInMainThread);
            EditorGUILayout.Space(4);
            
            EditorGUILayout.LabelField("Current Config", EditorStyles.boldLabel);
            config.FrameBudgetMs = EditorGUILayout.FloatField("Frame Budget Ms", config.FrameBudgetMs);
            EditorGUILayout.Space(4);
            
            var breaker = service.CircuitBreaker;
            var metrics = service.Metrics;

            // --- Circuit Breaker State ---
            EditorGUILayout.LabelField("Circuit Breaker", EditorStyles.boldLabel);
            var state = breaker.State;
            var prevColor = GUI.color;
            GUI.color = state switch
            {
                CircuitState.Closed  => Color.green,
                CircuitState.Open    => Color.red,
                CircuitState.HalfOpen => Color.yellow,
                _                    => Color.white
            };
            EditorGUILayout.LabelField("State", state.ToString(), EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Failure Threshold", config.FailureThreshold.ToString());
            
            if (state == CircuitState.Open)
            {
                EditorGUILayout.LabelField("Circuit Open DurationMs", $"{breaker.ClosenessToOpen} of {breaker.OpenDurationMs} ({breaker.PercentageOpenStateCompletion}%)");
            }
            else
            {
                EditorGUILayout.LabelField("Consecutive Failures", breaker.ConsecutiveFailures.ToString());    
            }
            GUI.color = prevColor;

            EditorGUILayout.Space(10);

            GUI.color = prevColor;

            EditorGUILayout.Space(10);

            // --- Event Metrics ---
            EditorGUILayout.LabelField("Benchmark Metrics", EditorStyles.boldLabel);
            EditorGUI.BeginDisabledGroup(true);
            EditorGUILayout.LongField("Total Enqueued", metrics.TotalEnqueued);
            EditorGUILayout.LongField("Succeeded", metrics.TotalSucceeded);
            EditorGUILayout.LongField("Failed", metrics.TotalFailed);
            EditorGUILayout.LongField("Dropped (buffer full)", metrics.TotalDropped);
            EditorGUILayout.LongField("Retried (replayed)", metrics.TotalRetried);
            EditorGUI.EndDisabledGroup();

            EditorGUILayout.Space(10);
            
            // --- Benchmark ---
            EditorGUILayout.LabelField("Benchmark", EditorStyles.boldLabel);
            _eventCount = EditorGUILayout.IntSlider("Event Count", _eventCount, 10, 500);

            if (GUILayout.Button("Run Benchmark", GUILayout.Height(28)))
            {
                StartBenchmark(service);
            }
            EditorGUI.EndDisabledGroup();

            if (_benchmarkRunning)
            {
                EditorGUILayout.Space(4);
                float progress = _expectedCallbacks > 0
                    ? (float)_completedCallbacks / _expectedCallbacks
                    : 0f;
                var progressRect = EditorGUILayout.GetControlRect(false, 20);
                EditorGUI.ProgressBar(progressRect, progress,
                    $"Running... {_completedCallbacks} / {_expectedCallbacks} callbacks");
            }

            EditorGUILayout.Space(10);
            DrawHistory();

            Repaint();
        }

        private void SendEvent(IAnalyticsService service, string eventName, Action<bool> onComplete = null)
        {
            if (_sendEventsInMainThread)
            {
                service.SendEvent(eventName, onComplete);
            }
            else
            {
                Task.Run(() => { service.SendEvent(eventName, onComplete); });
            }
        }

        private void StartBenchmark(IAnalyticsService service)
        {
            service.Metrics.Reset();

            _benchmarkRunning = true;
            _completedCallbacks = 0;
            _expectedCallbacks = _eventCount;
            _peakRetryBuffer = 0;
            _stopwatch = Stopwatch.StartNew();

            for (int i = 0; i < _eventCount; i++)
            {
                SendEvent(service, $"benchmark_event_{i}", success =>
                {
                    _completedCallbacks++;

                    if (_completedCallbacks >= _expectedCallbacks)
                    {
                        FinishBenchmark(service);
                    }
                });
            }
        }

        private void FinishBenchmark(IAnalyticsService service)
        {
            _stopwatch.Stop();
            _benchmarkRunning = false;

            var metrics = service.Metrics;
            long wallMs = _stopwatch.ElapsedMilliseconds;
            double throughput = wallMs > 0
                ? (_expectedCallbacks / (wallMs / 1000.0))
                : 0;

            var config = AnalyticsConfig.Instance;
            _history.Add(new BenchmarkResult
            {
                EventCount = _expectedCallbacks,
                WallClockMs = wallMs,
                ThroughputPerSec = throughput,
                Succeeded = metrics.TotalSucceeded,
                Failed = metrics.TotalFailed,
                Dropped = metrics.TotalDropped,
                Retried = metrics.TotalRetried,
                SuccessRate = metrics.SuccessRate,
                PeakRetryBuffer = _peakRetryBuffer,
                CircuitOpenCount = metrics.CircuitOpenCount,
                FrameBudgetMs = config.FrameBudgetMs,
            });

            Repaint();
        }

        private void DrawHistory()
        {
            EditorGUILayout.LabelField("Benchmark History", EditorStyles.boldLabel);

            if (_history.Count == 0)
            {
                EditorGUILayout.LabelField("No runs yet.", EditorStyles.miniLabel);
                return;
            }

            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Clear History", EditorStyles.miniButton, GUILayout.Width(90)))
            {
                _history.Clear();
                Repaint();
                return;
            }
            EditorGUILayout.EndHorizontal();

            for (int i = _history.Count - 1; i >= 0; i--)
            {
                DrawRunResult(i + 1, _history[i]);

                if (i > 0)
                    EditorGUILayout.Space(2);
            }
        }

        private void DrawRunResult(int runNumber, BenchmarkResult r)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            EditorGUILayout.LabelField(
                $"Run #{runNumber}  |  {r.EventCount} events | {r.FrameBudgetMs} frame budget",
                EditorStyles.boldLabel);

            EditorGUILayout.Space(2);

            EditorGUILayout.LabelField("Wall Clock Time", FormatTime(r.WallClockMs));
            EditorGUILayout.LabelField("Throughput", $"{r.ThroughputPerSec:F1} events/sec");

            EditorGUILayout.Space(2);

            EditorGUILayout.LabelField("Succeeded / Failed", $"{r.Succeeded} / {r.Failed}");
            EditorGUILayout.LabelField("Success Rate", $"{r.SuccessRate * 100f:F1}%");
            EditorGUILayout.LabelField("Dropped", r.Dropped.ToString());
            EditorGUILayout.LabelField("Retried", r.Retried.ToString());

            EditorGUILayout.Space(2);
            
            EditorGUILayout.LabelField("Peak Retry Buffer", r.PeakRetryBuffer.ToString());
            EditorGUILayout.LabelField("Circuit Opens", r.CircuitOpenCount.ToString());

            EditorGUILayout.EndVertical();
        }

        private static string FormatTime(long ms)
        {
            if (ms >= 1000)
                return $"{ms / 1000f:F2}s ({ms:N0} ms)";
            return $"{ms:N0} ms";
        }
    }
}
