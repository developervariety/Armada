namespace Armada.Core.Services
{
    using Armada.Core.Models;

    /// <summary>
    /// Resolves the existing build-participating sibling relation into tenant-local,
    /// transitive repository lanes.
    /// </summary>
    public sealed class VesselLaneMap
    {
        private readonly Dictionary<string, HashSet<string>> _MembersByVessel;

        private VesselLaneMap(Dictionary<string, HashSet<string>> membersByVessel)
        {
            _MembersByVessel = membersByVessel;
        }

        /// <summary>
        /// Build lanes from vessel sibling declarations. Read-only siblings do not join a lane.
        /// </summary>
        public static VesselLaneMap Build(IEnumerable<Vessel> vessels, Action<string>? warn = null)
        {
            if (vessels == null) throw new ArgumentNullException(nameof(vessels));

            Dictionary<string, HashSet<string>> byVessel =
                new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

            foreach (IGrouping<string, Vessel> tenantGroup in vessels
                .Where(v => v != null && !String.IsNullOrWhiteSpace(v.Id))
                .GroupBy(v => v.TenantId ?? String.Empty, StringComparer.Ordinal))
            {
                List<Vessel> tenantVessels = tenantGroup.ToList();
                Dictionary<string, string> parent = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                Dictionary<string, string> idByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (Vessel vessel in tenantVessels)
                {
                    parent[vessel.Id] = vessel.Id;
                    if (!String.IsNullOrWhiteSpace(vessel.Name)) idByName[vessel.Name] = vessel.Id;
                }

                foreach (Vessel vessel in tenantVessels)
                {
                    foreach (SiblingRepo sibling in vessel.GetSiblingRepos())
                    {
                        if (sibling == null || !sibling.BuildParticipant || String.IsNullOrWhiteSpace(sibling.VesselRef)) continue;
                        string? otherId = parent.ContainsKey(sibling.VesselRef) ? sibling.VesselRef
                            : (idByName.TryGetValue(sibling.VesselRef, out string? byName) ? byName : null);
                        if (otherId == null)
                        {
                            warn?.Invoke("vessel " + vessel.Id + " declares build-participating sibling "
                                + sibling.VesselRef + " which is not a known vessel; ignored for lanes");
                            continue;
                        }

                        parent[FindRoot(parent, vessel.Id)] = FindRoot(parent, otherId);
                    }
                }

                Dictionary<string, HashSet<string>> membersByRoot =
                    new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
                foreach (string vesselId in parent.Keys)
                {
                    string root = FindRoot(parent, vesselId);
                    if (!membersByRoot.TryGetValue(root, out HashSet<string>? members))
                    {
                        members = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        membersByRoot[root] = members;
                    }
                    members.Add(vesselId);
                }

                foreach (HashSet<string> members in membersByRoot.Values)
                {
                    foreach (string member in members) byVessel[member] = members;
                }
            }

            return new VesselLaneMap(byVessel);
        }

        /// <summary>
        /// Get all vessels in the lane that contains the specified vessel.
        /// </summary>
        public IReadOnlySet<string> MembersFor(string vesselId)
        {
            if (String.IsNullOrWhiteSpace(vesselId)) throw new ArgumentNullException(nameof(vesselId));
            if (_MembersByVessel.TryGetValue(vesselId, out HashSet<string>? members)) return members;
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase) { vesselId };
        }

        private static string FindRoot(Dictionary<string, string> parent, string vesselId)
        {
            string current = vesselId;
            while (parent.TryGetValue(current, out string? next)
                && !String.Equals(next, current, StringComparison.OrdinalIgnoreCase))
            {
                current = next;
            }
            return current;
        }
    }
}
