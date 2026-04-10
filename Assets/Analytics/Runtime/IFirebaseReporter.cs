using System;

namespace Fortis.Analytics.PlayerLoop
{
    public interface IFirebaseReporter
    {
        void ReportException(Exception ex, string context);
    }
}
