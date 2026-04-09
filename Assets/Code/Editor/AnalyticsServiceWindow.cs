using Fortis;
using Fortis.Analytics;
using Fortis.Core.DependencyInjection;
using UnityEditor;
using UnityEngine;

namespace Code.Editor
{
    public class AnalyticsServiceWindow : EditorWindow
    {
        [MenuItem("Tools/Analytics Monitor")]       
        public static void ShowWindow()
        {
            GetWindow<AnalyticsServiceWindow>("Analytics Monitor");
        }

        private void OnGUI()
        {
            if (!Application.isPlaying)
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
                return;
            }

            var analyticsService = DiContainer.Resolve<IAnalyticsService>();
            if (analyticsService == null)
            {
                return;
            }

            var breaker = analyticsService.CircuitBreaker;
            var metrics = analyticsService.Metrics;

            // Circuit Breaker State
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

            // Event Metrics
            EditorGUILayout.LabelField("Event Metrics", EditorStyles.boldLabel);
            EditorGUI.BeginDisabledGroup(true);
            EditorGUILayout.LongField("Total Enqueued", metrics.TotalEnqueued);
            EditorGUILayout.LongField("Succeeded", metrics.TotalSucceeded);
            EditorGUILayout.LongField("Failed", metrics.TotalFailed);
            EditorGUILayout.LongField("Dropped (buffer full)", metrics.TotalDropped);
            EditorGUILayout.LongField("Retried (replayed)", metrics.TotalRetried);
            EditorGUI.EndDisabledGroup();

            EditorGUILayout.Space(10);

            // Performance
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

            // Retry Buffer
            EditorGUILayout.LabelField("Retry Buffer", EditorStyles.boldLabel);
            var bufferSize = metrics.RetryBufferSize;
            var maxBuffer = analyticsService.MaxRetryBufferSize;
            var barRect = EditorGUILayout.GetControlRect(false, 20);
            EditorGUI.ProgressBar(barRect,
                maxBuffer > 0 ? bufferSize / (float)maxBuffer : 0f,
                $"{bufferSize} / {maxBuffer}");

            EditorGUILayout.Space(10);

            // Test Controls
            EditorGUILayout.LabelField("Test Controls", EditorStyles.boldLabel);

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Send Test Event"))
            {
                analyticsService.SendEvent("editor_test_event", success =>
                    Debug.Log($"[AnalyticsMonitor] Test event result: {success}"));
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

            Repaint();
        }
    }
}