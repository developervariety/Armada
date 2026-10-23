namespace Armada.Server
{
    using System;
    using Armada.Core.Models;
    using Armada.Runtimes.Interfaces;

    /// <summary>Stops provider callbacks when their process launch releases its source.</summary>
    internal sealed class RuntimeProviderErrorSubscription
    {
        private readonly IRuntimeProviderErrorSource _Source;
        private readonly Action<int, RuntimeProviderError> _Callback;
        private readonly object _Sync = new object();
        private bool _Closed;

        internal RuntimeProviderErrorSubscription(IRuntimeProviderErrorSource source, Action<int, RuntimeProviderError> callback)
        {
            _Source = source ?? throw new ArgumentNullException(nameof(source));
            _Callback = callback ?? throw new ArgumentNullException(nameof(callback));
        }

        internal void Subscribe()
        {
            _Source.OnProviderErrorReceived += HandleProviderError;
        }

        internal void Close()
        {
            lock (_Sync)
            {
                if (_Closed) return;
                _Closed = true;
                _Source.OnProviderErrorReceived -= HandleProviderError;
            }
        }

        private void HandleProviderError(int processId, RuntimeProviderError error)
        {
            if (error == null) return;
            lock (_Sync)
            {
                if (_Closed) return;
                _Callback(processId, error);
            }
        }
    }
}
