namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using Armada.Core.Models;

    /// <summary>
    /// Selects a Harbor for a unit of host work. The decision is made once, when a dock is provisioned, and
    /// pinned afterward by dock affinity. Precedence: (1) the Harbor that already owns the mission's dock;
    /// (2) an eligible preferred Harbor; (3) among eligible Harbors, the least loaded under its capacity.
    /// Eligibility requires the Harbor to be enabled, connected, within capacity, and to advertise the
    /// requested runtime and all required capabilities as available.
    /// </summary>
    public class HarborRouter
    {
        #region Public-Methods

        /// <summary>
        /// Select a Harbor for the request from the candidate set.
        /// </summary>
        /// <param name="candidates">Registered Harbors to choose from.</param>
        /// <param name="isConnected">Returns whether a Harbor currently has a live link.</param>
        /// <param name="inFlight">Returns the number of jobs a Harbor currently reports running.</param>
        /// <param name="request">Routing inputs.</param>
        /// <returns>The routing decision.</returns>
        /// <exception cref="ArgumentNullException">Thrown when a required argument is null.</exception>
        public HarborRoutingDecision Select(
            IReadOnlyList<Harbor> candidates,
            Func<string, bool> isConnected,
            Func<string, int> inFlight,
            HarborRoutingRequest request)
        {
            if (candidates == null) throw new ArgumentNullException(nameof(candidates));
            if (isConnected == null) throw new ArgumentNullException(nameof(isConnected));
            if (inFlight == null) throw new ArgumentNullException(nameof(inFlight));
            if (request == null) throw new ArgumentNullException(nameof(request));

            // 1) Dock affinity: a mission that already owns a dock must run on that Harbor. It cannot move
            // hosts, so if the owning Harbor is offline we stall rather than re-route.
            if (!String.IsNullOrWhiteSpace(request.ExistingHarborId))
            {
                Harbor? owner = FindById(candidates, request.ExistingHarborId!);
                if (owner != null && IsExcluded(request, owner.Id))
                    return HarborRoutingDecision.None("The Harbor that owns this mission's dock (" + owner.Id + ") cannot take it.");
                if (owner == null)
                    return HarborRoutingDecision.None("The Harbor that owns this dock (" + request.ExistingHarborId + ") is no longer registered.");
                if (!isConnected(owner.Id))
                    return HarborRoutingDecision.None("Waiting for Harbor " + owner.Id + " (owns this mission's dock) to reconnect.");

                // Affinity does not excuse capability: a Harbor that holds the dock but cannot run the captain's runtime
                // would accept the launch and fail it on every attempt. Say so instead.
                if (!owner.Enabled)
                    return HarborRoutingDecision.None("The Harbor that owns this mission's dock (" + owner.Id + ") is disabled; enable it to continue.");
                if (!HasCapabilities(owner, request))
                    return HarborRoutingDecision.None("The Harbor that owns this mission's dock (" + owner.Id + ") does not advertise the required capabilities"
                        + (String.IsNullOrWhiteSpace(request.RequestedRuntime) ? "" : " (runtime " + request.RequestedRuntime + ")")
                        + "; install the runtime on that Harbor or route this stage to a captain whose runtime it has.");
                return HarborRoutingDecision.Chosen(owner.Id, true, "Pinned to the Harbor that owns this mission's dock.");
            }

            List<Harbor> eligible = new List<Harbor>();
            foreach (Harbor harbor in candidates)
            {
                if (!harbor.Enabled) continue;
                if (IsExcluded(request, harbor.Id)) continue;
                if (!isConnected(harbor.Id)) continue;
                if (!HasCapabilities(harbor, request)) continue;
                if (inFlight(harbor.Id) >= harbor.MaxConcurrentJobs) continue;
                eligible.Add(harbor);
            }

            if (eligible.Count == 0)
            {
                HarborRoutingDecision none = HarborRoutingDecision.None(BuildNoneReason(candidates, isConnected, inFlight, request));
                none.CapableHarborsAtCapacity = candidates.Any(h => h.Enabled && isConnected(h.Id) && !IsExcluded(request, h.Id) && HasCapabilities(h, request));
                return none;
            }

            // 2) Honor an eligible preferred Harbor.
            if (!String.IsNullOrWhiteSpace(request.PreferredHarborId))
            {
                Harbor? preferred = FindById(eligible, request.PreferredHarborId!);
                if (preferred != null)
                    return HarborRoutingDecision.Chosen(preferred.Id, false, "Selected the preferred Harbor.");
            }

            // 3) Least loaded under capacity; tie-break by larger remaining headroom, then by name for
            // determinism.
            Harbor best = eligible[0];
            int bestLoad = inFlight(best.Id);
            int bestHeadroom = best.MaxConcurrentJobs - bestLoad;
            for (int i = 1; i < eligible.Count; i++)
            {
                Harbor candidate = eligible[i];
                int load = inFlight(candidate.Id);
                int headroom = candidate.MaxConcurrentJobs - load;
                bool better = load < bestLoad
                    || (load == bestLoad && headroom > bestHeadroom)
                    || (load == bestLoad && headroom == bestHeadroom && String.CompareOrdinal(candidate.Name, best.Name) < 0);
                if (better)
                {
                    best = candidate;
                    bestLoad = load;
                    bestHeadroom = headroom;
                }
            }

            return HarborRoutingDecision.Chosen(best.Id, false, "Selected the least-loaded eligible Harbor.");
        }

        #endregion

        #region Private-Methods

        private static bool IsExcluded(HarborRoutingRequest request, string harborId)
        {
            if (request.ExcludedHarborIds == null) return false;
            foreach (string excluded in request.ExcludedHarborIds)
                if (String.Equals(excluded, harborId, StringComparison.Ordinal)) return true;
            return false;
        }

        private static Harbor? FindById(IReadOnlyList<Harbor> harbors, string id)
        {
            foreach (Harbor harbor in harbors)
                if (String.Equals(harbor.Id, id, StringComparison.Ordinal)) return harbor;
            return null;
        }

        private static bool HasCapabilities(Harbor harbor, HarborRoutingRequest request)
        {
            if (!String.IsNullOrWhiteSpace(request.RequestedRuntime) && !HasCapability(harbor, request.RequestedRuntime!))
                return false;
            if (request.RequiredCapabilities != null)
            {
                foreach (string required in request.RequiredCapabilities)
                    if (!String.IsNullOrWhiteSpace(required) && !HasCapability(harbor, required)) return false;
            }

            return true;
        }

        private static bool HasCapability(Harbor harbor, string name)
        {
            foreach (HarborCapability capability in harbor.Capabilities)
                if (capability.Available && String.Equals(capability.Name, name, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        private static string BuildNoneReason(
            IReadOnlyList<Harbor> candidates,
            Func<string, bool> isConnected,
            Func<string, int> inFlight,
            HarborRoutingRequest request)
        {
            int connected = 0;
            int connectedCapable = 0;
            foreach (Harbor harbor in candidates)
            {
                if (!harbor.Enabled || !isConnected(harbor.Id) || IsExcluded(request, harbor.Id)) continue;
                connected++;
                if (HasCapabilities(harbor, request)) connectedCapable++;
            }

            if (connected == 0)
                return "No enabled Harbor is currently connected.";
            if (connectedCapable == 0)
            {
                // Name what is actually missing: a Harbor may have the runtime and lack only a newer protocol capability.
                List<string> required = new List<string>();
                if (!String.IsNullOrWhiteSpace(request.RequestedRuntime)) required.Add(request.RequestedRuntime!);
                if (request.RequiredCapabilities != null)
                    required.AddRange(request.RequiredCapabilities.Where(c => !String.IsNullOrWhiteSpace(c)));
                List<string> missing = required.Where(name => !candidates.Any(h => h.Enabled && isConnected(h.Id) && !IsExcluded(request, h.Id) && HasCapability(h, name)))
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (missing.Count > 0)
                    return "No connected Harbor advertises " + String.Join(", ", missing)
                        + (missing.Any(m => m.StartsWith("api-inference-", StringComparison.OrdinalIgnoreCase)) ? "; update the Harbor to one that supports this endpoint's settings." : ".");
                return "No single connected Harbor advertises all of " + String.Join(", ", required) + ".";
            }
            string usage = String.Join(", ", candidates
                .Where(h => h.Enabled && isConnected(h.Id) && !IsExcluded(request, h.Id) && HasCapabilities(h, request))
                .Select(h => h.Name + " (" + h.Id + "): " + inFlight(h.Id) + "/" + h.MaxConcurrentJobs + " slots in use"));
            return "All capable Harbors are at capacity: " + usage + ". "
                + "Ask turns and other interactive work use slots too, even when captains appear idle. "
                + "Wait for running jobs to finish, review Running now in the Harbor app, or increase MaxConcurrentJobs "
                + "(Jobs at once) in Harbor Settings > General and save. Queued missions wait for a free slot; retry interactive requests after a slot is available.";
        }

        #endregion
    }
}
