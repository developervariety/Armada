namespace Armada.Core.Models
{
    using Armada.Core.Enums;

    /// <summary>
    /// Runtime-independent prompt options read from <see cref="Captain.RuntimeOptionsJson"/> (camelCase JSON, for
    /// example <c>{"promptVariant": "Lean"}</c>). They share the JSON object with the other captain options such as
    /// <see cref="CaptainApprovalOptions"/> and <see cref="MuxCaptainOptions"/>.
    /// </summary>
    public class CaptainPromptOptions
    {
        #region Public-Members

        /// <summary>
        /// Persona prompt variant this captain's missions render with, or null (the default) for
        /// <see cref="PromptVariantEnum.Current"/>. A voyage's captain override can choose another variant for one
        /// voyage (fixture battery runs do).
        /// </summary>
        public PromptVariantEnum? PromptVariant { get; set; } = null;

        #endregion
    }
}
