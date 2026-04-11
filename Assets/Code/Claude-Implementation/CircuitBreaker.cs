using System;
using System.Threading;

namespace Claude.Analytics
{
    public enum CircuitState
    {
        Closed,
        Open,
        HalfOpen
    }

    public class CircuitBreaker
    {
        public int FailureThreshold { get; }
        public int OpenDurationMs { get; }
        public int ConsecutiveFailures => _consecutiveFailures;
        public long ClosenessToOpen => State == CircuitState.Open ? _getTimestamp() - Interlocked.Read(ref _openedAtTicks) : 0;

        public float PercentageOpenStateCompletion =>
            State == CircuitState.Open ? (ClosenessToOpen / (float) OpenDurationMs) * 100 : 100; 
        
        private int _state;
        private int _consecutiveFailures;
        private long _openedAtTicks;
        private readonly long _openDurationTicks;
        private readonly Func<long> _getTimestamp;

        public event Action<CircuitState> OnStateChanged;

        public CircuitState State =>
            (CircuitState)Interlocked.CompareExchange(ref _state, 0, 0);

        public CircuitBreaker(int failureThreshold = 5, int openDurationMs = 10000,
                              Func<long> getTimestamp = null)
        {
            if (failureThreshold < 1)
                throw new ArgumentOutOfRangeException(nameof(failureThreshold), "Must be >= 1");
            if (openDurationMs < 0)
                throw new ArgumentOutOfRangeException(nameof(openDurationMs), "Must be >= 0");

            FailureThreshold = failureThreshold;
            OpenDurationMs = openDurationMs;
            _openDurationTicks = TimeSpan.FromMilliseconds(openDurationMs).Ticks;
            _getTimestamp = getTimestamp ?? (() => DateTime.UtcNow.Ticks);
            _state = (int)CircuitState.Closed;
        }

        public bool AllowRequest()
        {
            var state = State;

            if (state == CircuitState.Closed)
                return true;

            if (state == CircuitState.Open)
            {
                var elapsed = _getTimestamp() - Interlocked.Read(ref _openedAtTicks);
                if (elapsed >= _openDurationTicks)
                {
                    if (TryTransition(CircuitState.Open, CircuitState.HalfOpen))
                    {
                        RaiseStateChanged(CircuitState.HalfOpen);
                        return true;
                    }
                }
                return false;
            }

            // HalfOpen: allow one probe request
            return true;
        }

        public void RecordSuccess()
        {
            Interlocked.Exchange(ref _consecutiveFailures, 0);
            if (TryTransition(CircuitState.HalfOpen, CircuitState.Closed))
            {
                RaiseStateChanged(CircuitState.Closed);
            }
        }

        public void RecordFailure()
        {
            var failures = Interlocked.Increment(ref _consecutiveFailures);

            if (State == CircuitState.HalfOpen)
            {
                Trip();
            }
            else if (failures >= FailureThreshold)
            {
                Trip();
            }
        }

        private void Trip()
        {
            var prev = (CircuitState)Interlocked.Exchange(ref _state, (int)CircuitState.Open);
            Interlocked.Exchange(ref _openedAtTicks, _getTimestamp());

            if (prev != CircuitState.Open)
                RaiseStateChanged(CircuitState.Open);
        }

        private void RaiseStateChanged(CircuitState newState)
        {
            try { OnStateChanged?.Invoke(newState); }
            catch (Exception ex) { UnityEngine.Debug.LogException(ex); }
        }

        private bool TryTransition(CircuitState from, CircuitState to)
        {
            return Interlocked.CompareExchange(ref _state, (int)to, (int)from) == (int)from;
        }
    }
}
