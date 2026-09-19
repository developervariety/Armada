namespace Armada.Runtimes
{
    using SyslogLogging;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Settings;
    using Armada.Runtimes.Interfaces;

    /// <summary>
    /// Factory for creating agent runtime instances.
    /// </summary>
    public class AgentRuntimeFactory
    {
        #region Public-Members

        /// <summary>
        /// The context-compaction decision handed to every API-endpoint runtime this factory creates, or
        /// null to compact deterministically. Set after construction so existing construction sites and
        /// tests are unchanged, exactly as the other typed-decision adapters are wired.
        /// </summary>
        public Armada.Core.Services.TypedContextCompactionAdapter? ContextCompactionAdapter { get; set; }

        #endregion

        #region Private-Members

        private string _Header = "[AgentRuntimeFactory] ";
        private LoggingModule _Logging;
        private OpenCodeServerSettings? _OpenCodeConnection;
        private ModelProvidersSettings? _ModelProviders;
        private Dictionary<string, Func<IAgentRuntime>> _CustomRuntimes = new Dictionary<string, Func<IAgentRuntime>>();

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="logging">Logging module.</param>
        public AgentRuntimeFactory(LoggingModule logging) : this(logging, null, null)
        {
        }

        /// <summary>
        /// Instantiate with OpenCode connection settings.
        /// </summary>
        /// <param name="logging">Logging module.</param>
        /// <param name="openCodeConnection">
        /// Optional OpenCode server settings threaded to <see cref="OpenCodeRuntime"/> so the
        /// runtime reads the same shared config as the inference client.
        /// </param>
        public AgentRuntimeFactory(LoggingModule logging, OpenCodeServerSettings? openCodeConnection)
            : this(logging, openCodeConnection, null)
        {
        }

        /// <summary>
        /// Instantiate with OpenCode connection settings and the external provider registry.
        /// </summary>
        /// <param name="logging">Logging module.</param>
        /// <param name="openCodeConnection">
        /// Optional OpenCode server settings threaded to <see cref="OpenCodeRuntime"/> so the
        /// runtime reads the same shared config as the inference client.
        /// </param>
        /// <param name="modelProviders">
        /// External model provider registry threaded to the runtimes so provider routing is
        /// configuration, not code.
        /// </param>
        public AgentRuntimeFactory(LoggingModule logging, OpenCodeServerSettings? openCodeConnection, ModelProvidersSettings? modelProviders)
        {
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
            _OpenCodeConnection = openCodeConnection;
            _ModelProviders = modelProviders;
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Create an agent runtime by type.
        /// </summary>
        /// <param name="runtimeType">Runtime type.</param>
        /// <returns>Agent runtime instance.</returns>
        public virtual IAgentRuntime Create(AgentRuntimeEnum runtimeType)
        {
            return CreateAdapter(runtimeType);
        }

        /// <summary>
        /// Create the built-in adapter for a CLI runtime to build a launch plan that runs elsewhere, such as on a
        /// Harbor runner. The plan uses the adapter's command, arguments and output parsing; the adapter never
        /// starts a local process on that path, so a factory that replaces local launches (for example in a test
        /// host) still yields the real plan.
        /// </summary>
        /// <param name="runtimeType">Runtime type.</param>
        /// <returns>The built-in adapter.</returns>
        /// <exception cref="InvalidOperationException">The runtime has no built-in CLI adapter.</exception>
        public BaseAgentRuntime CreateLaunchPlanAdapter(AgentRuntimeEnum runtimeType)
        {
            if (CreateAdapter(runtimeType) is BaseAgentRuntime adapter) return adapter;
            throw new InvalidOperationException("Runtime " + runtimeType + " has no CLI launch plan.");
        }

        private IAgentRuntime CreateAdapter(AgentRuntimeEnum runtimeType)
        {
            switch (runtimeType)
            {
                case AgentRuntimeEnum.ClaudeCode:
                    return new ClaudeCodeRuntime(_Logging, _ModelProviders);
                case AgentRuntimeEnum.Codex:
                    return new CodexRuntime(_Logging, _ModelProviders);
                case AgentRuntimeEnum.Gemini:
                    return new GeminiRuntime(_Logging);
                case AgentRuntimeEnum.Cursor:
                    return new CursorRuntime(_Logging);
                case AgentRuntimeEnum.OpenCode:
                    return new OpenCodeRuntime(_Logging, _OpenCodeConnection, _ModelProviders);
                case AgentRuntimeEnum.Mux:
                    return new MuxRuntime(_Logging);
                case AgentRuntimeEnum.ApiEndpoint:
                    throw new InvalidOperationException("An API-endpoint runtime requires its tenant-owned model endpoint.");
                case AgentRuntimeEnum.Custom:
                    throw new InvalidOperationException("Use Create(string name) for custom runtimes");
                default:
                    throw new ArgumentOutOfRangeException(nameof(runtimeType), "Unknown runtime type: " + runtimeType);
            }
        }

        /// <summary>
        /// Create an API-endpoint runtime for an already authorized model endpoint.
        /// The endpoint object is the only source of provider credentials and routing data.
        /// </summary>
        /// <param name="endpoint">Enabled inference endpoint authorized for the captain tenant.</param>
        /// <returns>API-endpoint runtime instance.</returns>
        public virtual IAgentRuntime Create(ModelEndpoint endpoint)
        {
            if (endpoint == null) throw new ArgumentNullException(nameof(endpoint));
            ApiAgentRuntime runtime = new ApiAgentRuntime(endpoint, _Logging);
            Armada.Core.Services.TypedContextCompactionAdapter? adapter = ContextCompactionAdapter;
            if (adapter != null)
            {
                // The rule verdict is supplied here, at the one place that knows the decision's direction:
                // the deterministic pass spares nothing, so the decision can only ever retain more.
                runtime.ContextCompactionDecider = (input, token) => adapter.DecideAllowedAsync(input, token);
            }
            return runtime;
        }

        /// <summary>
        /// Create a runtime with an endpoint when the runtime type requires one.
        /// </summary>
        /// <param name="runtimeType">Runtime type.</param>
        /// <param name="endpoint">Authorized endpoint for API-endpoint runtimes.</param>
        /// <returns>Agent runtime instance.</returns>
        public virtual IAgentRuntime Create(AgentRuntimeEnum runtimeType, ModelEndpoint? endpoint)
        {
            if (runtimeType == AgentRuntimeEnum.ApiEndpoint)
                return Create(endpoint ?? throw new ArgumentNullException(nameof(endpoint)));

            if (endpoint != null)
                throw new ArgumentException("Only API-endpoint runtimes accept a model endpoint.", nameof(endpoint));

            return Create(runtimeType);
        }

        /// <summary>
        /// Create a custom agent runtime by name.
        /// </summary>
        /// <param name="name">Custom runtime name.</param>
        /// <returns>Agent runtime instance.</returns>
        public IAgentRuntime Create(string name)
        {
            if (String.IsNullOrEmpty(name)) throw new ArgumentNullException(nameof(name));

            if (_CustomRuntimes.TryGetValue(name, out Func<IAgentRuntime>? factory))
            {
                return factory();
            }

            throw new InvalidOperationException("No custom runtime registered with name: " + name);
        }

        /// <summary>
        /// Register a custom runtime factory.
        /// </summary>
        /// <param name="name">Custom runtime name.</param>
        /// <param name="factory">Factory function to create the runtime.</param>
        public void Register(string name, Func<IAgentRuntime> factory)
        {
            if (String.IsNullOrEmpty(name)) throw new ArgumentNullException(nameof(name));
            if (factory == null) throw new ArgumentNullException(nameof(factory));

            _CustomRuntimes[name] = factory;
            _Logging.Info(_Header + "registered custom runtime: " + name);
        }

        #endregion
    }
}
