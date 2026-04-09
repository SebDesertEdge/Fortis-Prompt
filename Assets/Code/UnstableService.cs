using System;
using System.Threading;
using UnityEngine;

namespace Interview.Mocks
{
    /// <summary>
    /// DO NOT MODIFY THIS SCRIPT.
    /// This represents a legacy, poorly written 3rd party SDK.
    /// </summary>
    public class UnstableLegacyService
    {
        private readonly float _failureRate = 0.3f;
        private readonly int _hitchDurationMs = 500;

        /// <summary>
        /// Sends an event. 
        /// WARNING: This method is synchronous and often blocks the calling thread.
        /// </summary>
        public bool SendEvent(string eventName)
        {
            // Simulate a heavy main-thread hitch
            if (UnityEngine.Random.value < 0.2f)
            {
                Thread.Sleep(_hitchDurationMs);
            }

            // Simulate a random crash/exception
            if (UnityEngine.Random.value < _failureRate)
            {
                throw new Exception("LegacyService internal failure: NullReferenceException at 0x004F");
            }

            Debug.Log($"[LegacySDK] Event '{eventName}' sent successfully.");
            return true;
        }
    }
}