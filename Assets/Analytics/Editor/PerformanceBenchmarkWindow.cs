using System;
using System.Collections.Generic;
using System.Diagnostics;
using Fortis;
using Fortis.Analytics;
using Fortis.Core.DependencyInjection;
using UnityEditor;
using UnityEngine;

namespace Fortis.Analytics.Editor
{
    public class PerformanceBenchmarkWindow : EditorWindow
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

        private int _eventCount = 50;
        private bool _benchmarkRunning;
        private int _completedCallbacks;
        private int _expectedCallbacks;
        private Stopwatch _stopwatch;
        private long _peakRetryBuffer;

        private readonly List<BenchmarkResult> _history = new();
        private Vector2 _historyScroll;

        private double _lastRepaintTime;
        private const double RepaintInterval = 0.25;

        private GUIStyle _headerStyle;
        private GUIStyle _bodyStyle;
        private GUIStyle _sectionStyle;
        private GUIStyle _historyHeaderStyle;
        private bool _stylesInitialized;

        [MenuItem("Fortis/Performance Benchmark")]
        public static void ShowWindow()
        {
            var window = GetWindow<PerformanceBenchmarkWindow>("Performance Benchmark");
            window.minSize = new Vector2(420, 500);
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
            if (!_benchmarkRunning) return;

            // Track peak retry buffer during the run
            var service = DiContainer.Resolve<IAnalyticsService>();
            if (service?.Metrics != null)
            {
                var current = service.Metrics.RetryBufferSize;
                if (current > _peakRetryBuffer)
                    _peakRetryBuffer = current;
            }

            // Throttle repaints
            if (EditorApplication.timeSinceStartup - _lastRepaintTime > RepaintInterval)
            {
                _lastRepaintTime = EditorApplication.timeSinceStartup;
                Repaint();
            }
        }

        private void InitStyles()
        {
            if (_stylesInitialized) return;

            _headerStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 18,
                alignment = TextAnchor.MiddleLeft
            };

            _bodyStyle = new GUIStyle(EditorStyles.label)
            {
                fontSize = 12
            };

            _sectionStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 13
            };

            _historyHeaderStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleLeft
            };

            _stylesInitialized = true;
        }

        private void OnGUI()
        {
            InitStyles();

            // Header
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("PERFORMANCE BENCHMARK", _headerStyle);
            GUILayout.FlexibleSpace();
            EditorGUILayout.LabelField("Fortis", EditorStyles.miniLabel, GUILayout.Width(40));
            EditorGUILayout.EndHorizontal();

            DrawSeparator();

            if (!Application.isPlaying)
            {
                EditorGUILayout.HelpBox(
                    "Enter Play Mode to run benchmarks.\n\n" +
                    "Select the implementation to test in the AnalyticsConfig asset,\n" +
                    "then enter Play Mode and click \"Run Benchmark\".",
                    MessageType.Info);
                DrawHistory();
                return;
            }

            var service = DiContainer.Resolve<IAnalyticsService>();
            if (service == null)
            {
                EditorGUILayout.HelpBox(
                    "IAnalyticsService not resolved. Check MainGameInstaller bindings.",
                    MessageType.Warning);
                return;
            }

            var config = AnalyticsConfig.Instance;
            string implName = config != null ? config.Implementation.ToString() : "Unknown";

            EditorGUILayout.LabelField("Active Implementation", implName, _sectionStyle);
            EditorGUILayout.Space(4);

            // Controls
            EditorGUI.BeginDisabledGroup(_benchmarkRunning);
            _eventCount = EditorGUILayout.IntSlider("Event Count", _eventCount, 10, 500);

            if (GUILayout.Button("Run Benchmark", GUILayout.Height(30)))
            {
                StartBenchmark(service, implName);
            }
            EditorGUI.EndDisabledGroup();

            // Progress during run
            if (_benchmarkRunning)
            {
                EditorGUILayout.Space(4);
                float progress = _expectedCallbacks > 0
                    ? (float)_completedCallbacks / _expectedCallbacks
                    : 0f;
                var rect = EditorGUILayout.GetControlRect(false, 20);
                EditorGUI.ProgressBar(rect, progress,
                    $"Running... {_completedCallbacks} / {_expectedCallbacks} callbacks");
            }

            DrawSeparator();
            DrawHistory();
        }

        private void StartBenchmark(IAnalyticsService service, string implName)
        {
            // Reset metrics before the run
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

        private void DrawHistory()
        {
            EditorGUILayout.LabelField("BENCHMARK HISTORY", _sectionStyle);
            EditorGUILayout.Space(2);

            if (_history.Count == 0)
            {
                EditorGUILayout.LabelField("No runs yet.", EditorStyles.miniLabel);
                return;
            }

            // Clear button
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
                var r = _history[i];
                DrawRunResult(i + 1, r);

                if (i > 0)
                    EditorGUILayout.Space(2);
            }

            EditorGUILayout.EndScrollView();
        }

        private void DrawRunResult(int runNumber, BenchmarkResult r)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            // Run header
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(
                $"Run #{runNumber}  |  {r.ImplementationName}  |  {r.EventCount} events",
                EditorStyles.boldLabel);
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(2);

            // Timing
            EditorGUILayout.LabelField("Wall Clock Time",
                FormatTime(r.WallClockMs), _bodyStyle);
            EditorGUILayout.LabelField("Throughput",
                $"{r.ThroughputPerSec:F1} events/sec", _bodyStyle);

            EditorGUILayout.Space(2);

            // Outcomes
            EditorGUILayout.LabelField("Succeeded / Failed",
                $"{r.Succeeded} / {r.Failed}", _bodyStyle);
            EditorGUILayout.LabelField("Success Rate",
                $"{r.SuccessRate * 100f:F1}%", _bodyStyle);
            EditorGUILayout.LabelField("Dropped",
                r.Dropped.ToString(), _bodyStyle);
            EditorGUILayout.LabelField("Retried",
                r.Retried.ToString(), _bodyStyle);

            EditorGUILayout.Space(2);

            // Resilience
            EditorGUILayout.LabelField("Saved Hitch Time",
                FormatTime(r.SavedHitchTimeMs), _bodyStyle);
            EditorGUILayout.LabelField("Peak Retry Buffer",
                r.PeakRetryBuffer.ToString(), _bodyStyle);
            EditorGUILayout.LabelField("Circuit Opens",
                r.CircuitOpenCount.ToString(), _bodyStyle);

            EditorGUILayout.EndVertical();
        }

        private static string FormatTime(long ms)
        {
            if (ms >= 1000)
                return $"{ms / 1000f:F2}s ({ms:N0} ms)";
            return $"{ms:N0} ms";
        }

        private static void DrawSeparator()
        {
            EditorGUILayout.Space(4);
            var rect = EditorGUILayout.GetControlRect(false, 1);
            EditorGUI.DrawRect(rect, new Color(0.3f, 0.3f, 0.3f));
            EditorGUILayout.Space(4);
        }
    }
}
