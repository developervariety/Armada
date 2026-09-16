namespace Armada.Core.Services.Interfaces
{
    using Armada.Core.Models;

    /// <summary>Starts runtime login commands. Replaceable so tests run a fake process instead of a real CLI.</summary>
    public interface IAccountLoginProcessRunner
    {
        /// <summary>Start the command. Throws when the CLI cannot be started.</summary>
        IAccountLoginProcess Start(AccountLoginProcessRequest request);
    }
}
