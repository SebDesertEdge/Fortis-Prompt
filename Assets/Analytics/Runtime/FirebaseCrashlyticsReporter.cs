using System;
using UnityEngine;

namespace Fortis.Analytics.PlayerLoop
{
#if FIREBASE_CRASHLYTICS
    public class FirebaseCrashlyticsReporter : IFirebaseReporter
    {
        public void ReportException(Exception ex, string context)
        {
            Firebase.Crashlytics.Crashlytics.SetCustomKey("analytics_context", context);
            Firebase.Crashlytics.Crashlytics.ReportUncaughtException(ex);
        }
    }
#endif

    public class NullFirebaseReporter : IFirebaseReporter
    {
        public void ReportException(Exception ex, string context)
        {
            // No-op: Firebase Crashlytics not available
        }
    }
}
