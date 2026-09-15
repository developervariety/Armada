namespace Armada.Test.Unit.TestHelpers
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using PolyPrompt.Clients;
    using PolyPrompt.Models;
    using SyslogLogging;

    /// <summary>
    /// Inference client that returns scripted tool-calling responses in order and records every request it
    /// receives, so a test can read the tools offered to the model and the tool results sent back.
    /// </summary>
    public sealed class ScriptedToolChatClient : CompletionClientBase
    {
        #region Public-Members

        /// <summary>
        /// Requests received, in order. Each entry is a snapshot of the message list at the time of the call.
        /// </summary>
        public List<ToolChatRequest> Requests { get; } = new List<ToolChatRequest>();

        #endregion

        #region Private-Members

        private readonly Queue<ToolChatResponse> _Script;
        private readonly object _Lock = new object();

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="script">Responses returned in order; an empty script ends the conversation.</param>
        /// <param name="logging">Logging module.</param>
        public ScriptedToolChatClient(IEnumerable<ToolChatResponse> script, LoggingModule logging)
            : base("http://127.0.0.1:1", null, logging)
        {
            if (script == null) throw new ArgumentNullException(nameof(script));
            _Script = new Queue<ToolChatResponse>(script);
        }

        #endregion

        #region Public-Methods

        /// <inheritdoc />
        public override Task<ToolChatResponse> ToolChatAsync(ToolChatRequest request, CancellationToken token = default)
        {
            return Task.FromResult(Next(request));
        }

        /// <inheritdoc />
        public override Task<ToolChatStreamingResponse> ToolChatStreamingAsync(ToolChatRequest request, CancellationToken token = default)
        {
            ToolChatResponse response = Next(request);
            ToolChatStreamingResponse streamed = new ToolChatStreamingResponse
            {
                Success = response.Success,
                Text = response.Text,
                ToolCalls = response.ToolCalls,
                Error = response.Error
            };
            return Task.FromResult(streamed);
        }

        /// <inheritdoc />
        public override Task<ChatResponse> ChatAsync(string prompt, ChatCompletionOptions? options = null, CancellationToken token = default) => throw new NotSupportedException();

        /// <inheritdoc />
        public override Task<ChatStreamingResponse> ChatStreamingAsync(string prompt, ChatCompletionOptions? options = null, CancellationToken token = default) => throw new NotSupportedException();

        /// <inheritdoc />
        public override Task<EmbeddingResponse> EmbedAsync(string input, EmbeddingOptions? options = null, CancellationToken token = default) => throw new NotSupportedException();

        /// <inheritdoc />
        public override Task<EmbeddingResponse> EmbedAsync(List<string> inputs, EmbeddingOptions? options = null, CancellationToken token = default) => throw new NotSupportedException();

        /// <inheritdoc />
        public override Task<GenerationResponse> GenerateAsync(string prompt, GenerationOptions? options = null, CancellationToken token = default) => throw new NotSupportedException();

        /// <inheritdoc />
        public override Task<GenerationStreamingResponse> GenerateStreamingAsync(string prompt, GenerationOptions? options = null, CancellationToken token = default) => throw new NotSupportedException();

        /// <inheritdoc />
        public override IAsyncEnumerable<ModelInformation> ListModelsAsync(CancellationToken token = default) => throw new NotSupportedException();

        /// <inheritdoc />
        public override Task<ModelInformation?> GetModelInformationAsync(string model, CancellationToken token = default) => throw new NotSupportedException();

        #endregion

        #region Private-Methods

        private ToolChatResponse Next(ToolChatRequest request)
        {
            lock (_Lock)
            {
                ToolChatRequest snapshot = new ToolChatRequest();
                snapshot.Messages = request.Messages == null ? new List<ChatMessage>() : new List<ChatMessage>(request.Messages);
                snapshot.Tools = request.Tools;
                Requests.Add(snapshot);
                if (_Script.Count == 0) return new ToolChatResponse { Success = true, Text = String.Empty, ToolCalls = new List<ToolCall>() };
                return _Script.Dequeue();
            }
        }

        #endregion
    }
}
