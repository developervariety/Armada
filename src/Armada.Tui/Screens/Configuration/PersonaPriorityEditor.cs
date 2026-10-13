namespace Armada.Tui.Screens.Configuration
{
    using System;
    using System.Linq;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Tui.Screens.Entities;
    using Armada.Tui.Services;
    using Armada.Tui.Widgets;

    /// <summary>Ordered persona matching-class editor, including availability and exhaustion controls.</summary>
    public sealed class PersonaPriorityEditor
    {
        private readonly RecordListField<PersonaCaptainPriority> _Priorities;
        private readonly SelectField<string> _Availability;
        private readonly SelectField<string> _Exhaustion;

        /// <summary>Attach localized fields to a persona form. The existing persona is never mutated.</summary>
        /// <exception cref="ArgumentNullException">The context or form is null.</exception>
        public PersonaPriorityEditor(TuiContext context, EntityForm form, Persona? persona)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
            if (form == null) throw new ArgumentNullException(nameof(form));
            _Priorities = form.List<PersonaCaptainPriority>("Captain priorities", p => Describe(p),
                persona?.CaptainPriorities.Select(Copy).ToList(), 5,
                "a adds a priority; Enter edits; Del removes; Alt+Up/Down reorders.");
            _Priorities.EmptyText = "None (default routing)";
            _Priorities.Fingerprint = Describe;
            _Priorities.Editor = (priority, done) => Edit(context, priority, done);
            _Availability = form.Select("When a priority is unavailable", Enum.GetNames<PersonaPriorityAvailabilityEnum>()
                .Select(v => new SelectOption<string>(v, context.Loc.T(v == "FallThrough" ? "Try lower priorities" : "Wait for the first priority"))).ToList(),
                (persona?.PriorityAvailability ?? PersonaPriorityAvailabilityEnum.FallThrough).ToString(), null, true);
            _Exhaustion = form.Select("When all priorities are unavailable", Enum.GetNames<PersonaPriorityExhaustionEnum>()
                .Select(v => new SelectOption<string>(v, context.Loc.T(v == "Wait" ? "Wait" : "Use any eligible captain"))).ToList(),
                (persona?.PriorityExhaustion ?? PersonaPriorityExhaustionEnum.Wait).ToString(), null, true,
                "Wait for the first priority always waits, regardless of the exhaustion setting.");
        }

        /// <summary>Copy form values into a save payload.</summary>
        /// <exception cref="ArgumentNullException">The target persona is null.</exception>
        public void Apply(Persona persona)
        {
            if (persona == null) throw new ArgumentNullException(nameof(persona));
            persona.CaptainPriorities = _Priorities.Items.Select(Copy).ToList();
            persona.PriorityAvailability = EntityForm.EnumValue(_Availability, PersonaPriorityAvailabilityEnum.FallThrough);
            persona.PriorityExhaustion = EntityForm.EnumValue(_Exhaustion, PersonaPriorityExhaustionEnum.Wait);
        }

        private static string Describe(PersonaCaptainPriority priority)
        {
            return String.Join(" / ", new[] { priority.Runtime?.ToString(), priority.Model, priority.Endpoint, priority.MinimumTier?.ToString() }.Where(v => !String.IsNullOrEmpty(v)));
        }

        private static PersonaCaptainPriority Copy(PersonaCaptainPriority priority)
        {
            return new PersonaCaptainPriority { Runtime = priority.Runtime, Model = priority.Model, Endpoint = priority.Endpoint, MinimumTier = priority.MinimumTier };
        }

        private static void Edit(TuiContext context, PersonaCaptainPriority? priority, Action<PersonaCaptainPriority> done)
        {
            EntityForm form = new EntityForm(context);
            SelectField<string> runtime = form.Select("Runtime", Enum.GetNames<AgentRuntimeEnum>().Select(v => new SelectOption<string>(v, v)).ToList(), priority?.Runtime?.ToString(), "Any");
            InputField model = form.Text("Model", priority?.Model);
            InputField endpoint = form.Text("Endpoint", priority?.Endpoint, "", false, "An endpoint requires the Mux runtime.");
            SelectField<string> tier = form.Select("Minimum tier", Enum.GetNames<CaptainTierEnum>().Select(v => new SelectOption<string>(v, context.Loc.T(v))).ToList(), priority?.MinimumTier?.ToString(), "Any");
            form.MarkClean();
            PersonaCaptainPriority? result = null;
            EntityUi.ShowForm(context, "Captain priorities", form, "Save", token =>
            {
                token.ThrowIfCancellationRequested();
                PersonaCaptainPriority candidate = new PersonaCaptainPriority
                {
                    Runtime = Enum.TryParse(runtime.Value, out AgentRuntimeEnum parsedRuntime) ? parsedRuntime : null,
                    Model = EntityUi.Blank(model.Value),
                    Endpoint = EntityUi.Blank(endpoint.Value),
                    MinimumTier = Enum.TryParse(tier.Value, out CaptainTierEnum parsedTier) ? parsedTier : null
                };
                Persona validation = new Persona();
                validation.CaptainPriorities.Add(candidate);
                string? error = PersonaCaptainPrioritySelector.Validate(validation);
                if (error == null) result = candidate;
                return Task.FromResult(error);
            }, () => { if (result != null) done(result); });
        }
    }
}
