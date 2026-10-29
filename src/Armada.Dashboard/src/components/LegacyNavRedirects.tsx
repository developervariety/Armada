import { Navigate, useLocation, useParams } from 'react-router-dom';

/**
 * Redirects for pages that became hub tabs (v1.2.0 nav consolidation). Plain `<Navigate>` drops the query string and
 * router state, so these keep both: deep links such as `/cli-permissions?request=cpr_1` and Planning handoffs that
 * carry their draft in `location.state` arrive at the new tab intact. Links should use `lib/navTargets` directly.
 */

/** `/cli-permissions[?tab=rules&request=...]` -> Settings > CLI Tool Permissions; the old `tab` becomes `section`. */
export function CliPermissionsRedirect() {
  const location = useLocation();
  const params = new URLSearchParams(location.search);
  const section = params.get('tab');
  params.delete('tab');
  const next = new URLSearchParams({ tab: 'cli-permissions' });
  if (section) next.set('section', section);
  params.forEach((value, key) => next.set(key, value));
  return <Navigate to={`/server?${next.toString()}`} state={location.state} replace />;
}

/** `/planning` and `/planning/:id` -> Dispatch > Planning, keeping the session id, the query, and the router state. */
export function PlanningRedirect() {
  const location = useLocation();
  const { id } = useParams();
  const params = new URLSearchParams(location.search);
  params.delete('tab');
  const next = new URLSearchParams({ tab: 'planning' });
  if (id) next.set('session', id);
  params.forEach((value, key) => next.set(key, value));
  return <Navigate to={`/dispatch?${next.toString()}`} state={location.state} replace />;
}
