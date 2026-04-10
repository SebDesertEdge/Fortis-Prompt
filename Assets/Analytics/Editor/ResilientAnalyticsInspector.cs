using Fortis;
using Fortis.Analytics;
using Fortis.Core.DependencyInjection;
using UnityEditor;
using UnityEngine;

namespace Fortis.Analytics.PlayerLoop.Editor
{
    [CustomEditor(typeof(AnalyticsMonitor))]
    public class ResilientAnalyticsInspector : UnityEditor.Editor
    {
        private double _lastRepaintTime;
        private const double RepaintInterval = 0.5;

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

        public override void OnInspectorGUI()
        {
            EditorGUILayout.LabelField("Analytics Monitor", EditorStyles.boldLabel);
            EditorGUILayout.Space(4);

            if (!Application.isPlaying)
            {
                EditorGUILayout.HelpBox(
                    "Enter Play Mode to see live analytics data.", MessageType.Info);
                return;
            }

            var service = DiContainer.Resolve<IAnalyticsService>();
            if (service == null)
            {
                EditorGUILayout.HelpBox(
                    "IAnalyticsService not resolved.", MessageType.Warning);
                return;
            }

            var breaker = service.CircuitBreaker;
            var metrics = service.Metrics;
            if (breaker == null || metrics == null) return;

            // Circuit State with coloured dot
            var state = breaker.State;
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.PrefixLabel("Circuit State");
            var prevColor = GUI.color;
            GUI.color = state switch
            {
                CircuitState.Closed  => new Color(0.2f, 0.8f, 0.3f),
                CircuitState.Open    => new Color(0.9f, 0.2f, 0.2f),
                CircuitState.HalfOpen => new Color(0.95f, 0.6f, 0.1f),
                _                    => Color.white
            };
            EditorGUILayout.LabelField($"{state}  \u25CF");
            GUI.color = prevColor;
            EditorGUILayout.EndHorizontal();

            // Failure rate
            var total = metrics.TotalSucceeded + metrics.TotalFailed;
            float failureRate = total > 0 ? (float)metrics.TotalFailed / total : 0f;
            EditorGUILayout.LabelField("Failure Rate", $"{failureRate * 100f:F1}%");

            // Saved hitch time
            var savedMs = metrics.SavedHitchTimeMs;
            var savedDisplay = savedMs >= 1000
                ? $"{savedMs / 1000f:F1}s"
                : $"{savedMs} ms";
            EditorGUILayout.LabelField("Saved Hitch", savedDisplay);

            // Sent / Dropped
            EditorGUILayout.LabelField("Sent / Dropped",
                $"{metrics.TotalSucceeded} / {metrics.TotalDropped}");

            // Pending
            EditorGUILayout.LabelField("Pending", metrics.RetryBufferSize.ToString());

            // Frame budget (read-only with hint)
            var config = AnalyticsConfig.Instance;
            if (config != null)
            {
                EditorGUILayout.LabelField("Frame Budget",
                    $"{config.FrameBudgetMs:F1} ms     [Edit in Fortis/Analytics Monitor]");
            }
        }
    }
}
