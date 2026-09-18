namespace Armada.Core.Models
{
    using System;
    using System.Collections.Generic;
    using Armada.Core.Enums;
    using Armada.Core.Settings;

    /// <summary>The operator view of one custom typed decision, returned by the status API.</summary>
    public sealed class CustomTypedDecisionView
    {
        /// <summary>The decision name.</summary>
        public string Name { get; set; } = String.Empty;

        /// <summary>Its stored mode; the global effective mode caps it.</summary>
        public TypedDecisionModeEnum Mode { get; set; } = TypedDecisionModeEnum.Off;

        /// <summary>The gate threshold.</summary>
        public double Threshold { get; set; }

        /// <summary>Whether its redacted state is retained as training data.</summary>
        public bool RetainState { get; set; }

        /// <summary>The one-line description.</summary>
        public string Description { get; set; } = String.Empty;

        /// <summary>Where it runs.</summary>
        public CustomDecisionSurfaceEnum Surface { get; set; }

        /// <summary>Its bound conservative action.</summary>
        public CustomDecisionSeamEnum Binding { get; set; }

        /// <summary>The mission fields it assembles (MissionDiff surface).</summary>
        public List<string> StateFields { get; set; } = new List<string>();

        /// <summary>The vessels it reads, by name or id (MissionDiff surface); empty means every vessel.</summary>
        public List<string> Vessels { get; set; } = new List<string>();

        /// <summary>Its questions.</summary>
        public List<CustomTypedQuestionSettings> Questions { get; set; } = new List<CustomTypedQuestionSettings>();

        /// <summary>Build a view from a definition.</summary>
        /// <param name="name">The decision name.</param>
        /// <param name="definition">The stored definition.</param>
        /// <returns>The view.</returns>
        public static CustomTypedDecisionView From(string name, CustomTypedDecisionSettings definition)
        {
            List<CustomTypedQuestionSettings> questions = new List<CustomTypedQuestionSettings>();
            foreach (CustomTypedQuestionSettings question in definition.Questions) questions.Add(question.Clone());
            return new CustomTypedDecisionView
            {
                Name = name,
                Mode = definition.Mode,
                Threshold = definition.GateThreshold,
                RetainState = definition.RetainState,
                Description = definition.Description,
                Surface = definition.Surface,
                Binding = definition.Binding,
                StateFields = new List<string>(definition.StateFields),
                Vessels = new List<string>(definition.Vessels),
                Questions = questions
            };
        }
    }
}
