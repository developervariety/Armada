namespace Armada.Test.Common
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// Bounds a network or relay call a test makes. The call receives a token that is cancelled at the
    /// limit, and the wait itself ends at the limit even when the call ignores its token, so a stuck
    /// connect, send or receive fails the test with a <see cref="TimeoutException"/> that names the
    /// operation instead of hanging the run.
    /// </summary>
    public static class TestTimeout
    {
        #region Public-Members

        /// <summary>
        /// Default limit for one network call: far above a normal run time, far below a gate time limit.
        /// </summary>
        public static readonly TimeSpan DefaultLimit = TimeSpan.FromSeconds(60);

        #endregion

        #region Public-Methods

        /// <summary>
        /// Run a call that returns a value within the limit.
        /// </summary>
        /// <typeparam name="T">Result type.</typeparam>
        /// <param name="operation">Name of the call, used in the timeout message.</param>
        /// <param name="call">Call that receives the bounded token.</param>
        /// <param name="limit">Limit; <see cref="DefaultLimit"/> when null.</param>
        /// <returns>The call's result.</returns>
        public static async Task<T> WithinAsync<T>(string operation, Func<CancellationToken, Task<T>> call, TimeSpan? limit = null)
        {
            TimeSpan bound = limit ?? DefaultLimit;
            using CancellationTokenSource source = new CancellationTokenSource(bound);
            try
            {
                return await call(source.Token).WaitAsync(source.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (source.IsCancellationRequested)
            {
                throw Expired(operation, bound);
            }
        }

        /// <summary>
        /// Run a call within the limit.
        /// </summary>
        /// <param name="operation">Name of the call, used in the timeout message.</param>
        /// <param name="call">Call that receives the bounded token.</param>
        /// <param name="limit">Limit; <see cref="DefaultLimit"/> when null.</param>
        public static async Task WithinAsync(string operation, Func<CancellationToken, Task> call, TimeSpan? limit = null)
        {
            TimeSpan bound = limit ?? DefaultLimit;
            using CancellationTokenSource source = new CancellationTokenSource(bound);
            try
            {
                await call(source.Token).WaitAsync(source.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (source.IsCancellationRequested)
            {
                throw Expired(operation, bound);
            }
        }

        /// <summary>
        /// Run a call that returns a value task within the limit.
        /// </summary>
        /// <typeparam name="T">Result type.</typeparam>
        /// <param name="operation">Name of the call, used in the timeout message.</param>
        /// <param name="call">Call that receives the bounded token.</param>
        /// <param name="limit">Limit; <see cref="DefaultLimit"/> when null.</param>
        /// <returns>The call's result.</returns>
        public static Task<T> WithinAsync<T>(string operation, Func<CancellationToken, ValueTask<T>> call, TimeSpan? limit = null)
        {
            return WithinAsync(operation, token => call(token).AsTask(), limit);
        }

        #endregion

        #region Private-Methods

        private static TimeoutException Expired(string operation, TimeSpan bound)
        {
            return new TimeoutException("Timed out after " + bound.TotalSeconds + " s: " + operation);
        }

        #endregion
    }
}
