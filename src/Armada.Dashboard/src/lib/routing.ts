/**
 * Maps an entity ID prefix to its dashboard detail route. Centralizes the
 * prefix-to-route logic that was previously reimplemented per page (e.g. Events).
 */

const ID_PREFIX_ROUTES: Array<{ prefix: string; route: (id: string) => string }> = [
  { prefix: 'flt_', route: (id) => `/fleets/${id}` },
  { prefix: 'vsl_', route: (id) => `/vessels/${id}` },
  { prefix: 'cpt_', route: (id) => `/captains/${id}` },
  { prefix: 'msn_', route: (id) => `/missions/${id}` },
  { prefix: 'vyg_', route: (id) => `/voyages/${id}` },
  { prefix: 'sig_', route: (id) => `/signals/${id}` },
  { prefix: 'evt_', route: (id) => `/events/${id}` },
  { prefix: 'dck_', route: (id) => `/docks/${id}` },
  { prefix: 'mrg_', route: () => '/missions?tab=merge-queue' },
];

/** Return the dashboard detail route for an entity id, or null when the prefix is unknown. */
export function entityRoute(entityId: string | null | undefined): string | null {
  if (!entityId) return null;
  const match = ID_PREFIX_ROUTES.find((entry) => entityId.startsWith(entry.prefix));
  return match ? match.route(entityId) : null;
}
