namespace Armada.Runtimes
{
    using SyslogLogging;
    using Armada.Core.Enums;
    using Armada.Core.Settings;
    using Armada.Runtimes.Interfaces;

    /// <summary>
    /// Factory for creating agent runtime instances.
    /// </summary>
    public class AgentRuntimeFactory
    {
        #region Public-Members

        #endregion

        #region Private-Members

        private string _Header = "[AgentRuntimeFactory] ";
        private LoggingModule _Logging;
        private OpenCodeServerSettings? _OpenCodeConnection;
        private Dictionary<string, Func<IAgentRuntime>> _CustomRuntimes = new Dictionary<string, Func<IAgentRuntime>>();

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="logging">Logging module.</param>
        public AgentRuntimeFactory(LoggingModule logging)
        {
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
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
        {
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
            _OpenCodeConnection = openCodeConnection;
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Create an agent runtime by type.
        /// </summary>
        /// <param name="runtimeType">Runtime type.</param>
        /// <returns>Agent runtime instance.</returns>
        public IAgentRuntime Create(AgentRuntimeEnum runtimeType)
        {
            switch (runtimeType)
            {
                case AgentRuntimeEnum.ClaudeCode:
                    return new ClaudeCodeRuntime(_Logging);
                case AgentRuntimeEnum.Codex:
                    return new CodexRuntime(_Logging);
                case AgentRuntimeEnum.Gemini:
                    return new GeminiRuntime(_Logging);
                case AgentRuntimeEnum.Cursor:
                    return new CursorRuntime(_Logging);
                case AgentRuntimeEnum.OpenCode:
                    return new OpenCodeRuntime(_Logging, _OpenCodeConnection);
                case AgentRuntimeEnum.Mux:
                    return new MuxRuntime(_Logging);
                case AgentRuntimeEnum.Custom:
                    throw new InvalidOperationException("Use Create(string name) for custom runtimes");
                default:
                    throw new ArgumentOutOfRangeException(nameof(runtimeType), "Unknown runtime type: " + runtimeType);
            }
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
