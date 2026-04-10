using Code;
using Code.Core.DependencyInjection;
using UnityEditor;
using UnityEngine;

namespace Claude.Analytics.Editor
{
    public class ResilientAnalyticsWindow : EditorWindow
    {
        [MenuItem("Tools/Resilient Analytics Monitor")]
        public static void ShowWindow()
        {
            GetWindow<ResilientAnalyticsWindow>("Analytics Monitor");
        }

        private void OnGUI()
        {
            if (!Application.isPlaying)
            {
                EditorGUILayout.HelpBox(
                    "Enter Play Mode to view analytics metrics.",
                    MessageType.Info);
                return;
            }

            var instance = DiContainer.Resolve<IAnalyticsService>();
            if (instance == null) return;

            var breaker = instance.CircuitBreaker;
            var metrics = instance.Metrics;

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
            EditorGUILayout.LabelField("Circuit Opens",
                metrics.CircuitOpenCount.ToString());
            EditorGUILayout.LabelField("SDK Ready",
                instance.IsReady ? "Yes" : "No (circuit open)");

            EditorGUILayout.Space(10);

            // Test Controls
            EditorGUILayout.LabelField("Test Controls", EditorStyles.boldLabel);

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Send Test Event"))
            {
                instance.SendEvent("editor_test_event", success =>
                    UnityEngine.Debug.Log($"[AnalyticsMonitor] Test event result: {success}"));
            }
            if (GUILayout.Button("Send 20 Events (Burst)"))
            {
                for (int i = 0; i < 20; i++)
                    instance.SendEvent($"burst_event_{i}");
            }
            EditorGUILayout.EndHorizontal();

            if (GUILayout.Button("Reset Metrics"))
            {
                metrics.Reset();
            }

            Repaint();
        }

        private void OnInspectorUpdate()
        {
            if (Application.isPlaying)
                Repaint();
        }
    }
}
