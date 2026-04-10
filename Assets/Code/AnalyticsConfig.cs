using Fortis.Core.DependencyInjection;
using Fortis.Core.Utilities;
using UnityEditor;
using UnityEngine;

namespace Fortis
{
    public enum AnalyticsImplementation
    {
        Baseline,
        WorkerThread,
        Throttled,
        HybridDispatch,
        Awaitable,
        PlayerLoop
    }

    public class AnalyticsConfig : ScriptableObject
    {
        private const string k_Path = "Assets/Config";
        private const string k_FileName = "AnalyticsConfig";

        [Header("Implementation")]
        public AnalyticsImplementation Implementation = AnalyticsImplementation.Baseline;
        
        [Header("Circuit Breaker")]
        public int FailureThreshold = 5;
        public int CircuitOpenDurationMs = 10000;

        [Header("Retry Buffer")]
        public int MaxRetryBufferSize = 100;

        [Header("Frame Budget (PlayerLoop)")]
        [Tooltip("Max milliseconds per frame to spend draining the analytics queue.")]
        public float FrameBudgetMs = 8f;

        [Header("Retry Policy (PlayerLoop)")]
        [Tooltip("Maximum retry attempts before an event is dropped.")]
        public int MaxRetryAttempts = 4;
        
        public static AnalyticsConfig Instance
        {
            get
            {
                if (Application.isPlaying)
                {
                    return DiContainer.Resolve<AnalyticsConfig>();
                }
#if UNITY_EDITOR
                return (AnalyticsConfig)AssetDatabase.LoadAssetAtPath($"{k_Path}/{k_FileName}.asset", typeof(AnalyticsConfig));
#else
            return null;
#endif
            }
        }
        
#if UNITY_EDITOR
        [MenuItem("Assets/Create/Config/AnalyticsConfig")]
        public static void CreateConfig()
        {
            ScriptableObjectUtility.CreateAsset<AnalyticsConfig>(k_Path, k_FileName);
        }
#endif
    }
}