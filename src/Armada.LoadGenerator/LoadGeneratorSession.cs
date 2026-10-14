namespace Armada.LoadGenerator
{
    /// <summary>External session file shape. Never commit session files.</summary>
    public class LoadGeneratorSession
    {
        /// <summary>Authenticated encrypted session token.</summary>
        public string Token { get; set; } = "";
    }
}
