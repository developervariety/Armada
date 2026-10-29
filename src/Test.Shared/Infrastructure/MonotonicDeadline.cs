namespace Test.Shared.Infrastructure
{
    using System.Diagnostics;

    /// <summary>
    /// A test wait deadline measured on the monotonic clock. A wall-clock deadline expires early when the
    /// clock jumps forward while the process is suspended. The monotonic clock does not. Reads are safe
    /// from any thread.
    /// </summary>
    public sealed class MonotonicDeadline
    {
        /// <summary>True once the timeout has elapsed.</summary>
        public bool Passed
        {
            get { return _Stopwatch.Elapsed >= _Timeout; }
        }

        /// <summary>Time left before the deadline; zero once it has passed.</summary>
        public TimeSpan Remaining
        {
            get
            {
                TimeSpan left = _Timeout - _Stopwatch.Elapsed;
                return left > TimeSpan.Zero ? left : TimeSpan.Zero;
            }
        }

        private readonly Stopwatch _Stopwatch;
        private readonly TimeSpan _Timeout;

        private MonotonicDeadline(TimeSpan timeout)
        {
            _Timeout = timeout;
            _Stopwatch = Stopwatch.StartNew();
        }

        /// <summary>
        /// Start a deadline that passes after <paramref name="timeout"/> of monotonic time.
        /// A negative timeout is treated as zero.
        /// </summary>
        /// <param name="timeout">Timeout.</param>
        /// <returns>The deadline.</returns>
        public static MonotonicDeadline After(TimeSpan timeout)
        {
            return new MonotonicDeadline(timeout < TimeSpan.Zero ? TimeSpan.Zero : timeout);
        }
    }
}
