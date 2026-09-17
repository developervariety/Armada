namespace Armada.Core.Models
{
    using System;
    using System.Collections.Generic;
    using Armada.Core.Enums;

    /// <summary>The operator view of the typed-decision system. Never carries the key.</summary>
    public sealed class TypedDecisionStatus
    {
        #region Public-Members

        /// <summary>The global mode in effect: the stored mode with a key, Off without one.</summary>
        public TypedDecisionModeEnum EffectiveMode { get; set; } = TypedDecisionModeEnum.Off;

        /// <summary>Why the effective mode differs from the stored mode (<c>typed_decisions_no_key</c>), or null.</summary>
        public string? EffectiveReason { get; set; }

        /// <summary>The stored global mode.</summary>
        public TypedDecisionModeEnum StoredMode { get; set; } = TypedDecisionModeEnum.Gate;

        /// <summary>Whether a key resolves.</summary>
        public bool KeyPresent { get; set; } = false;

        /// <summary><c>env</c> or <c>file</c> when a key resolves; otherwise null.</summary>
        public string? KeySource { get; set; }

        /// <summary>Every decision in the settings map.</summary>
        public List<TypedDecisionStatusEntry> Decisions { get; set; } = new List<TypedDecisionStatusEntry>();

        /// <summary>Every user-defined custom decision.</summary>
        public List<CustomTypedDecisionView> Custom { get; set; } = new List<CustomTypedDecisionView>();

        #endregion
    }
}
