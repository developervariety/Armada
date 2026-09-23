namespace Armada.Core.Models
{
    using System;
    using System.Collections.Generic;

    /// <summary>One model-list entry no eligible captain can satisfy.</summary>
    public sealed class DeadPersonaModelEntry
    {
        public string Persona { get; set; } = String.Empty;
        public string List { get; set; } = String.Empty;
        public string Model { get; set; } = String.Empty;
    }

    /// <summary>Effective routing facts for one persona's model lists.</summary>
    public sealed class PersonaModelRoutingView
    {
        public string Persona { get; set; } = String.Empty;
        public string Floor { get; set; } = String.Empty;
        public string? MinimumTier { get; set; }
        public List<string> EligibleCaptainIds { get; set; } = new List<string>();
        public List<PersonaModelListMark> Default { get; set; } = new List<PersonaModelListMark>();
        public List<PersonaModelListMark> Lighter { get; set; } = new List<PersonaModelListMark>();
        public List<PersonaModelListMark> Stronger { get; set; } = new List<PersonaModelListMark>();
    }

    /// <summary>One list entry marked live or dead.</summary>
    public sealed class PersonaModelListMark
    {
        public string Model { get; set; } = String.Empty;
        public bool Live { get; set; }
    }
}
