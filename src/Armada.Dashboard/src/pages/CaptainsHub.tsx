import { lazy, Suspense, type ReactNode } from 'react';
import Tabs, { type TabDef } from '../components/shared/Tabs';

const Captains = lazy(() => import('./Captains'));
const Docks = lazy(() => import('./Docks'));

function panel(node: ReactNode): ReactNode {
  return <Suspense fallback={<p className="text-dim" style={{ padding: '1rem' }}>Loading...</p>}>{node}</Suspense>;
}

/**
 * Captains hub. Docks are git worktrees, an implementation detail of how a
 * captain runs a mission, so they move off the top level into a tab here for the
 * rare cleanup case. Keeps the nautical vocabulary: the nav item and route stay
 * `/captains`.
 */
export default function CaptainsHub() {
  const tabs: TabDef[] = [
    { key: 'captains', label: 'Captains', render: () => panel(<Captains />) },
    { key: 'docks', label: 'Docks', render: () => panel(<Docks />) },
  ];

  return <Tabs tabs={tabs} defaultTabKey="captains" ariaLabel="Captain sections" />;
}
