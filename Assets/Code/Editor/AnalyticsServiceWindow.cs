using System;
using System.Collections.Generic;
using System.Diagnostics;
using Fortis;
using Fortis.Analytics;
using Fortis.Core.DependencyInjection;
using UnityEditor;
using UnityEngine;

namespace Code.Editor
{
    public class AnalyticsServiceWindow : EditorWindow
    {
        private struct BenchmarkResult
        {
            public string ImplementationName;
            public int EventCount;
            public long WallClockMs;
            public double ThroughputPerSec;
            public long Succeeded;
            public long Failed;
            public long Dropped;
            public long Retried;
            public float SuccessRate;
            public long SavedHitchTimeMs;
            public long PeakRetryBuffer;
            public long CircuitOpenCount;
        }

        // Benchmark state
        private int _eventCount = 50;
        private bool _benchmarkRunning;
        private bool _restarting;
        private int _completedCallbacks;
        private int _expectedCallbacks;
        private Stopwatch _stopwatch;
        private long _peakRetryBuffer;

        // Implementation switching
        private AnalyticsImplementation _selectedImpl;
        private bool _implInitialized;

        // History
        private readonly List<BenchmarkResult> _history = new();
        private Vector2 _historyScroll;

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
            if (!_benchmarkRunning && !_restarting) return;

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
            if (!Application.isPlaying)
            {
                DrawEditMode();
                return;
            }

            DrawPlayMode();
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

            var serializedState = new SerializedObject(AnalyticsConfig.Instance);
            EditorGUILayout.PropertyField(serializedState.FindProperty(nameof(AnalyticsConfig.Implementation)));
            EditorGUILayout.PropertyField(serializedState.FindProperty(nameof(AnalyticsConfig.FailureThreshold)));
            EditorGUILayout.PropertyField(serializedState.FindProperty(nameof(AnalyticsConfig.CircuitOpenDurationMs)));
            EditorGUILayout.PropertyField(serializedState.FindProperty(nameof(AnalyticsConfig.MaxRetryBufferSize)));

            serializedState.ApplyModifiedProperties();

            EditorGUILayout.Space(10);
            DrawHistory();
        }

        private void DrawPlayMode()
        {
            var config = AnalyticsConfig.Instance;
            if (config == null) return;

            // --- Implementation Switching ---
            if (!_implInitialized)
            {
                _selectedImpl = config.Implementation;
                _implInitialized = true;
            }

            EditorGUILayout.LabelField("Implementation", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Active", config.Implementation.ToString());

            EditorGUILayout.BeginHorizontal();
            _selectedImpl = (AnalyticsImplementation)EditorGUILayout.EnumPopup("Switch To", _selectedImpl);

            bool needsRestart = _selectedImpl != config.Implementation;
            EditorGUI.BeginDisabledGroup(!needsRestart || _benchmarkRunning || _restarting);
            if (GUILayout.Button("Restart", GUILayout.Width(70)))
            {
                SwitchImplementation(config);
            }
            EditorGUI.EndDisabledGroup();
            EditorGUILayout.EndHorizontal();

            if (_restarting)
            {
                EditorGUILayout.HelpBox("Restarting container...", MessageType.Info);
                return;
            }

            EditorGUILayout.Space(10);

            var analyticsService = DiContainer.Resolve<IAnalyticsService>();
            if (analyticsService == null) return;

            var breaker = analyticsService.CircuitBreaker;
            var metrics = analyticsService.Metrics;

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
            GUI.color = prevColor;

            EditorGUILayout.Space(10);

            // --- Event Metrics ---
            EditorGUILayout.LabelField("Event Metrics", EditorStyles.boldLabel);
            EditorGUI.BeginDisabledGroup(true);
            EditorGUILayout.LongField("Total Enqueued", metrics.TotalEnqueued);
            EditorGUILayout.LongField("Succeeded", metrics.TotalSucceeded);
            EditorGUILayout.LongField("Failed", metrics.TotalFailed);
            EditorGUILayout.LongField("Dropped (buffer full)", metrics.TotalDropped);
            EditorGUILayout.LongField("Retried (replayed)", metrics.TotalRetried);
            EditorGUI.EndDisabledGroup();

            EditorGUILayout.Space(10);

            // --- Performance ---
            EditorGUILayout.LabelField("Performance", EditorStyles.boldLabel);
            var savedMs = metrics.SavedHitchTimeMs;
            var savedDisplay = savedMs >= 1000
                ? $"{savedMs / 1000f:F1}s"
                : $"{savedMs}ms";
            EditorGUILayout.LabelField("Saved Hitch Time", savedDisplay);
            EditorGUILayout.LabelField("Success Rate",
                $"{metrics.SuccessRate * 100f:F1}%");
            EditorGUILayout.LabelField("Circuit Opens", metrics.CircuitOpenCount.ToString());
            EditorGUILayout.LabelField("SDK Ready",
                analyticsService.IsReady ? "Yes" : "No (circuit open)");

            EditorGUILayout.Space(10);

            // --- Retry Buffer ---
            EditorGUILayout.LabelField("Retry Buffer", EditorStyles.boldLabel);
            var bufferSize = metrics.RetryBufferSize;
            var maxBuffer = analyticsService.MaxRetryBufferSize;
            var barRect = EditorGUILayout.GetControlRect(false, 20);
            EditorGUI.ProgressBar(barRect,
                maxBuffer > 0 ? bufferSize / (float)maxBuffer : 0f,
                $"{bufferSize} / {maxBuffer}");

            EditorGUILayout.Space(10);

            // --- Test Controls ---
            EditorGUILayout.LabelField("Test Controls", EditorStyles.boldLabel);

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Send Test Event"))
            {
                analyticsService.SendEvent("editor_test_event", success =>
                    UnityEngine.Debug.Log($"[AnalyticsMonitor] Test event result: {success}"));
            }
            if (GUILayout.Button("Send 20 Events (Burst)"))
            {
                for (int i = 0; i < 20; i++)
                    analyticsService.SendEvent($"burst_event_{i}");
            }
            EditorGUILayout.EndHorizontal();

            if (GUILayout.Button("Reset Metrics"))
            {
                metrics.Reset();
            }

            EditorGUILayout.Space(10);

            // --- Benchmark ---
            EditorGUILayout.LabelField("Benchmark", EditorStyles.boldLabel);
            EditorGUI.BeginDisabledGroup(_benchmarkRunning);
            _eventCount = EditorGUILayout.IntSlider("Event Count", _eventCount, 10, 500);

            if (GUILayout.Button("Run Benchmark", GUILayout.Height(28)))
            {
                StartBenchmark(analyticsService, config.Implementation.ToString());
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

        private void StartBenchmark(IAnalyticsService service, string implName)
        {
            service.Metrics.Reset();

            _benchmarkRunning = true;
            _completedCallbacks = 0;
            _expectedCallbacks = _eventCount;
            _peakRetryBuffer = 0;
            _stopwatch = Stopwatch.StartNew();

            for (int i = 0; i < _eventCount; i++)
            {
                service.SendEvent($"benchmark_event_{i}", success =>
                {
                    _completedCallbacks++;

                    if (_completedCallbacks >= _expectedCallbacks)
                    {
                        FinishBenchmark(service, implName);
                    }
                });
            }
        }

        private void FinishBenchmark(IAnalyticsService service, string implName)
        {
            _stopwatch.Stop();
            _benchmarkRunning = false;

            var metrics = service.Metrics;
            long wallMs = _stopwatch.ElapsedMilliseconds;
            double throughput = wallMs > 0
                ? (_expectedCallbacks / (wallMs / 1000.0))
                : 0;

            _history.Add(new BenchmarkResult
            {
                ImplementationName = implName,
                EventCount = _expectedCallbacks,
                WallClockMs = wallMs,
                ThroughputPerSec = throughput,
                Succeeded = metrics.TotalSucceeded,
                Failed = metrics.TotalFailed,
                Dropped = metrics.TotalDropped,
                Retried = metrics.TotalRetried,
                SuccessRate = metrics.SuccessRate,
                SavedHitchTimeMs = metrics.SavedHitchTimeMs,
                PeakRetryBuffer = _peakRetryBuffer,
                CircuitOpenCount = metrics.CircuitOpenCount
            });

            Repaint();
        }

        private async void SwitchImplementation(AnalyticsConfig config)
        {
            _restarting = true;
            Repaint();

            config.Implementation = _selectedImpl;

            var installer = UnityEngine.Object.FindAnyObjectByType<MainGameInstaller>();
            if (installer != null)
            {
                await installer.FullRestart();
                UnityEngine.Debug.Log($"[AnalyticsMonitor] Switched to {_selectedImpl}");
            }
            else
            {
                UnityEngine.Debug.LogError("[AnalyticsMonitor] MainGameInstaller not found in scene.");
            }

            _restarting = false;
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

            _historyScroll = EditorGUILayout.BeginScrollView(_historyScroll);

            for (int i = _history.Count - 1; i >= 0; i--)
            {
                DrawRunResult(i + 1, _history[i]);

                if (i > 0)
                    EditorGUILayout.Space(2);
            }

            EditorGUILayout.EndScrollView();
        }

        private void DrawRunResult(int runNumber, BenchmarkResult r)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            EditorGUILayout.LabelField(
                $"Run #{runNumber}  |  {r.ImplementationName}  |  {r.EventCount} events",
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

            EditorGUILayout.LabelField("Saved Hitch Time", FormatTime(r.SavedHitchTimeMs));
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
