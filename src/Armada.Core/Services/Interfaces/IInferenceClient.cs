namespace Armada.Core.Services.Interfaces
{
    /// <summary>
    /// Inference client abstraction for prompt completion.
    /// </summary>
    public interface IInferenceClient
    {
        /// <summary>
        /// Run completion with the supplied system prompt and user message.
        /// </summary>
        Task<string> CompleteAsync(string systemPrompt, string userMessage, CancellationToken token = default);
    }
}
