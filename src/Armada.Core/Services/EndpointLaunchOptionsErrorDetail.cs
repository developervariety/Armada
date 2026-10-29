namespace Armada.Core.Services
{
    /// <summary>
    /// Machine-readable body of a launch-option validation refusal.
    /// </summary>
    public sealed class EndpointLaunchOptionsErrorDetail
    {
        #region Public-Members

        /// <summary>
        /// Always <see cref="EndpointLaunchOptionsException.ErrorCode"/>.
        /// </summary>
        public string Code { get; set; } = EndpointLaunchOptionsException.ErrorCode;

        /// <summary>
        /// Name of the field that failed validation.
        /// </summary>
        public string Field { get; set; } = String.Empty;

        #endregion
    }
}
