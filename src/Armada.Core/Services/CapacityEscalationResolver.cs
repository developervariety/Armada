namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Settings;

    /// <summary>
    /// Resolves the <c>capacity_escalation</c> reading for a mission and keeps it per mission id, so repeated
    /// assignment attempts do not ask again. The cache is in memory, bounded, and entries expire.
    /// </summary>
    public sealed class CapacityEscalationResolver
    {
        #region Public-Members

        /// <summary>The persona has no Lighter or Stronger list, so there is nothing to ask.</summary>
        public const string SourceNoAlternative = "no_alternative_list";

        /// <summary>No capacity adapter is wired (no typed-decision client), so the default list applies.</summary>
        public const string SourceNoAdapter = "no_adapter";

        /// <summary>No work text was supplied, so the client was not called and the default list applies.</summary>
        public const string SourceNoWorkText = "no_work_text";

        /// <summary>The reading came from the decision (a gated answer, or the Default rule verdict).</summary>
        public const string SourceDecision = "decision";

        /// <summary>The reading came from this mission's earlier decision.</summary>
        public const string SourceCached = "cached";

        /// <summary>Reading the work text failed, so the default list applies.</summary>
        public const string SourceWorkTextFailed = "work_text_unavailable";

        /// <summary>The capacity adapter, or null when none is wired.</summary>
        public TypedCapacityEscalationAdapter? Adapter { get; set; }

        #endregion

        #region Private-Members

        private readonly object _Lock = new object();
        private readonly Dictionary<string, CacheEntry> _Cache = new Dictionary<string, CacheEntry>(StringComparer.Ordinal);
        private readonly LinkedList<string> _Order = new LinkedList<string>();
        private readonly int _Capacity;
        private readonly TimeSpan _Lifetime;

        #endregion

        #region Constructors-and-Factories

        /// <summary>Create a resolver.</summary>
        /// <param name="adapter">The capacity adapter, or null.</param>
        /// <param name="capacity">Maximum cached missions, from 1 to 100000.</param>
        /// <param name="lifetime">How long a reading is reused; null uses 30 minutes.</param>
        public CapacityEscalationResolver(TypedCapacityEscalationAdapter? adapter = null, int capacity = 1024, TimeSpan? lifetime = null)
        {
            Adapter = adapter;
            _Capacity = Math.Max(1, Math.Min(100000, capacity));
            _Lifetime = lifetime ?? TimeSpan.FromMinutes(30);
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Resolve the model list to try first. Never throws for a decision failure: every failure is the Default
        /// list. The client is called at most once per mission id while the reading is cached.
        /// </summary>
        /// <param name="mission">The mission being routed.</param>
        /// <param name="models">The persona's model preference entry.</param>
        /// <param name="workText">Supplies the work text; null means none is available and the client is not called.</param>
        /// <param name="useCache">False for a preview, which must not share readings with dispatch.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The reading.</returns>
        public async Task<CapacityReading> ResolveAsync(
            Mission mission,
            PersonaModelSettings models,
            Func<CancellationToken, Task<CapacityWorkText>>? workText,
            bool useCache,
            CancellationToken token = default)
        {
            if (mission == null) throw new ArgumentNullException(nameof(mission));
            if (models == null) throw new ArgumentNullException(nameof(models));
            if (models.Lighter.Count == 0 && models.Stronger.Count == 0)
                return new CapacityReading { Choice = CapacityChoiceEnum.Default, Source = SourceNoAlternative };
            if (useCache && TryGetCached(mission.Id, out CapacityChoiceEnum cached))
                return new CapacityReading { Choice = cached, Source = SourceCached };
            if (workText == null)
                return new CapacityReading { Choice = CapacityChoiceEnum.Default, Source = SourceNoWorkText };
            TypedCapacityEscalationAdapter? adapter = Adapter;
            if (adapter == null)
                return new CapacityReading { Choice = CapacityChoiceEnum.Default, Source = SourceNoAdapter };

            CapacityWorkText text;
            try
            {
                text = await workText(token).ConfigureAwait(false);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException && token.IsCancellationRequested))
            {
                return new CapacityReading { Choice = CapacityChoiceEnum.Default, Source = SourceWorkTextFailed };
            }

            CapacityEscalationDecisionInput input = new CapacityEscalationDecisionInput
            {
                Mission = mission,
                Persona = mission.Persona ?? String.Empty,
                Title = text?.Title ?? String.Empty,
                Description = text?.Description ?? String.Empty,
                DefaultModels = models.Default,
                LighterModels = models.Lighter,
                StrongerModels = models.Stronger
            };
            CapacityChoiceEnum choice = await adapter.DecideAsync(input, CapacityChoiceEnum.Default, token).ConfigureAwait(false);
            if (useCache) Store(mission.Id, choice);
            return new CapacityReading { Choice = choice, Source = SourceDecision };
        }

        #endregion

        #region Private-Methods

        private bool TryGetCached(string missionId, out CapacityChoiceEnum choice)
        {
            choice = CapacityChoiceEnum.Default;
            if (String.IsNullOrEmpty(missionId)) return false;
            lock (_Lock)
            {
                if (!_Cache.TryGetValue(missionId, out CacheEntry? entry)) return false;
                if (entry.ExpiresUtc <= DateTime.UtcNow)
                {
                    _Cache.Remove(missionId);
                    _Order.Remove(entry.Node);
                    return false;
                }
                choice = entry.Choice;
                return true;
            }
        }

        private void Store(string missionId, CapacityChoiceEnum choice)
        {
            if (String.IsNullOrEmpty(missionId)) return;
            lock (_Lock)
            {
                if (_Cache.TryGetValue(missionId, out CacheEntry? existing))
                {
                    _Order.Remove(existing.Node);
                    _Cache.Remove(missionId);
                }
                while (_Cache.Count >= _Capacity && _Order.First != null)
                {
                    _Cache.Remove(_Order.First.Value);
                    _Order.RemoveFirst();
                }
                LinkedListNode<string> node = _Order.AddLast(missionId);
                _Cache[missionId] = new CacheEntry { Choice = choice, ExpiresUtc = DateTime.UtcNow.Add(_Lifetime), Node = node };
            }
        }

        #endregion

        #region Private-Classes

        private sealed class CacheEntry
        {
            public CapacityChoiceEnum Choice { get; set; }
            public DateTime ExpiresUtc { get; set; }
            public LinkedListNode<string> Node { get; set; } = null!;
        }

        #endregion
    }
}
