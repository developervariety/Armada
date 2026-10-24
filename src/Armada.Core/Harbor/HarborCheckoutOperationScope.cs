namespace Armada.Core.Harbor
{
    using System;
    using System.Threading;

    /// <summary>Supplies a durable workflow identity and pinned commit to checkout mutations.</summary>
    public sealed class HarborCheckoutOperationScope : IDisposable
    {
        private static readonly AsyncLocal<HarborCheckoutOperationScope?> _Current = new AsyncLocal<HarborCheckoutOperationScope?>();
        private readonly HarborCheckoutOperationScope? _Previous;
        private bool _Disposed;

        /// <summary>Current workflow mutation scope.</summary>
        public static HarborCheckoutOperationScope? Current => _Current.Value;
        /// <summary>Durable workflow attempt identity, retained across retries.</summary>
        public string Identity { get; }
        /// <summary>Full commit observed and stored by the workflow before mutation.</summary>
        public string ExpectedHead { get; }

        /// <summary>Enter an operation scope with a stored workflow attempt and full commit.</summary>
        /// <param name="identity">Stable durable workflow attempt.</param>
        /// <param name="expectedHead">Full pinned checkout commit.</param>
        public HarborCheckoutOperationScope(string identity, string expectedHead)
        {
            if (String.IsNullOrWhiteSpace(identity) || identity.Length > 500)
                throw new ArgumentException("harbor_operation_scope_invalid", nameof(identity));
            if (expectedHead == null || !System.Text.RegularExpressions.Regex.IsMatch(expectedHead,
                @"\A(?:[0-9a-fA-F]{40}|[0-9a-fA-F]{64})\z"))
                throw new ArgumentException("harbor_operation_head_required", nameof(expectedHead));
            Identity = identity;
            ExpectedHead = expectedHead;
            _Previous = _Current.Value;
            _Current.Value = this;
        }

        /// <summary>Restore the caller's operation scope.</summary>
        public void Dispose()
        {
            if (_Disposed) return;
            if (!Object.ReferenceEquals(_Current.Value, this))
                throw new InvalidOperationException("harbor_operation_scope_order_invalid");
            _Current.Value = _Previous;
            _Disposed = true;
        }
    }
}
