using Fortis.Core.Utilities;
using UnityEditor;
using UnityEngine;

namespace Fortis
{
    public class ResilientAnalyticsConfig : ScriptableObject
    {
        [Header("Circuit Breaker")]
        public int FailureThreshold = 5;
        public int CircuitOpenDurationMs = 10000;

        [Header("Retry Buffer")]
        public int MaxRetryBufferSize = 100;
        
#if UNITY_EDITOR
        [MenuItem("Assets/Create/Config/ResilientAnalyticsConfig")]
        public static void CreateConfig()
        {
            ScriptableObjectUtility.CreateAsset<ResilientAnalyticsConfig>("Assets/Config", "ResilientAnalyticsConfig");
        }
#endif
    }
}