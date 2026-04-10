using Fortis;
using Fortis.Analytics;
using Fortis.Core.DependencyInjection;
using UnityEditor;
using UnityEngine;

namespace Fortis.Analytics.PlayerLoop.Editor
{
    public class ResilientAnalyticsEditorWindow : EditorWindow
    {
        private double _lastRepaintTime;
        private const double RepaintInterval = 0.5;

        private GUIStyle _headerStyle;
        private GUIStyle _bodyStyle;
        private GUIStyle _stateStyle;
        private bool _stylesInitialized;

        [MenuItem("Fortis/Analytics Monitor")]
        public static void ShowWindow()
        {
            var window = GetWindow<ResilientAnalyticsEditorWindow>("Analytics Monitor");
            window.minSize = new Vector2(340, 400);
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

            _stateStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 14,
                alignment = TextAnchor.MiddleLeft
            };

            _stylesInitialized = true;
        }

        private void OnGUI()
        {
            InitStyles();

            // Header
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("ANALYTICS MONITOR", _headerStyle);
            GUILayout.FlexibleSpace();
            EditorGUILayout.LabelField("Fortis", EditorStyles.miniLabel, GUILayout.Width(40));
            EditorGUILayout.EndHorizontal();

            DrawSeparator();

            if (!Application.isPlaying)
            {
                EditorGUILayout.HelpBox(
                    "Not running \u2014 enter Play Mode to see live analytics data.\n\n" +
                    "Set Implementation to \"PlayerLoop\" in the AnalyticsConfig asset.",
                    MessageType.Info);
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

            var breaker = service.CircuitBreaker;
            var metrics = service.Metrics;
            if (breaker == null || metrics == null) return;

            // Circuit State
            var state = breaker.State;
            var stateColor = state switch
            {
                CircuitState.Closed  => new Color(0.2f, 0.8f, 0.3f),
                CircuitState.Open    => new Color(0.9f, 0.2f, 0.2f),
                CircuitState.HalfOpen => new Color(0.95f, 0.6f, 0.1f),
                _                    => Color.white
            };

            EditorGUILayout.LabelField("CIRCUIT STATE", _bodyStyle);
            var prevColor = GUI.color;
            GUI.color = stateColor;
            string stateIcon = state == CircuitState.Closed ? "\u25C9" :
                               state == CircuitState.Open ? "\u25C9" : "\u25C9";
            EditorGUILayout.LabelField($"  {state.ToString().ToUpper()} {stateIcon}", _stateStyle);
            GUI.color = prevColor;

            EditorGUILayout.Space(2);

            // Failure rate
            var total = metrics.TotalSucceeded + metrics.TotalFailed;
            float failureRate = total > 0 ? (float)metrics.TotalFailed / total : 0f;
            EditorGUILayout.LabelField("Failure Rate", $"{failureRate * 100f:F1}%", _bodyStyle);

            // Frame budget (editable)
            var config = AnalyticsConfig.Instance;
            if (config != null)
            {
                float newBudget = EditorGUILayout.FloatField("Frame Budget (ms)", config.FrameBudgetMs);
                if (!Mathf.Approximately(newBudget, config.FrameBudgetMs))
                {
                    config.FrameBudgetMs = Mathf.Max(0.5f, newBudget);
                }
            }

            DrawSeparator();

            // Stats
            var savedMs = metrics.SavedHitchTimeMs;
            var savedDisplay = savedMs >= 1000
                ? $"{savedMs / 1000f:F1}s ({savedMs:N0} ms)"
                : $"{savedMs:N0} ms";
            EditorGUILayout.LabelField("SAVED HITCH TIME", savedDisplay, _bodyStyle);
            EditorGUILayout.LabelField("Events Sent", metrics.TotalSucceeded.ToString("N0"), _bodyStyle);
            EditorGUILayout.LabelField("Events Dropped", metrics.TotalDropped.ToString("N0"), _bodyStyle);

            // Pending queue bar
            var bufferSize = metrics.RetryBufferSize;
            var maxBuffer = service.MaxRetryBufferSize;
            EditorGUILayout.LabelField("Pending Queue", _bodyStyle);
            var barRect = EditorGUILayout.GetControlRect(false, 18);
            EditorGUI.ProgressBar(barRect,
                maxBuffer > 0 ? bufferSize / (float)maxBuffer : 0f,
                $"{bufferSize} / {maxBuffer}");

            EditorGUILayout.Space(4);

            // Extra metrics
            EditorGUI.BeginDisabledGroup(true);
            EditorGUILayout.LongField("Total Enqueued", metrics.TotalEnqueued);
            EditorGUILayout.LongField("Total Retried", metrics.TotalRetried);
            EditorGUILayout.LongField("Circuit Opens", metrics.CircuitOpenCount);
            EditorGUILayout.LabelField("Success Rate", $"{metrics.SuccessRate * 100f:F1}%");
            EditorGUI.EndDisabledGroup();

            DrawSeparator();

            // Test Controls
            EditorGUILayout.LabelField("Test Controls", EditorStyles.boldLabel);
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Send Test Event"))
            {
                service.SendEvent("editor_test_event", success =>
                    Debug.Log($"[AnalyticsMonitor] Test event result: {success}"));
            }
            if (GUILayout.Button("Send 20 Events (Burst)"))
            {
                for (int i = 0; i < 20; i++)
                    service.SendEvent($"burst_event_{i}");
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(4);

            // Force Reset button
            var prevBg = GUI.backgroundColor;
            GUI.backgroundColor = new Color(0.9f, 0.25f, 0.25f);
            if (GUILayout.Button("FORCE RESET", GUILayout.Height(28)))
            {
                metrics.Reset();
                Debug.Log("[AnalyticsMonitor] Metrics reset.");
            }
            GUI.backgroundColor = prevBg;
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
