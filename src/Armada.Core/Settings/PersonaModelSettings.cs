namespace Armada.Core.Settings
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// Smart Routing model preference for one persona: the models that normally serve it, the models for
    /// routine work, and the models for work harder than the default is expected to handle. A list only
    /// groups the captains Legacy Routing already ordered; it never makes a captain eligible.
    /// </summary>
    public sealed class PersonaModelSettings
    {
        #region Public-Members

        /// <summary>Models that normally serve the persona, in no implied order (Legacy Routing orders the captains).</summary>
        public List<string> Default
        {
            get => _Default;
            set => _Default = value ?? new List<string>();
        }

        /// <summary>Models for routine, well-specified, mechanical work. Empty means no lighter group.</summary>
        public List<string> Lighter
        {
            get => _Lighter;
            set => _Lighter = value ?? new List<string>();
        }

        /// <summary>Models for work harder than the default models are expected to handle. Empty means no stronger group.</summary>
        public List<string> Stronger
        {
            get => _Stronger;
            set => _Stronger = value ?? new List<string>();
        }

        #endregion

        #region Private-Members

        private List<string> _Default = new List<string>();
        private List<string> _Lighter = new List<string>();
        private List<string> _Stronger = new List<string>();

        #endregion
    }
}
