import { lazy, Suspense, type ReactNode } from 'react';
import Tabs, { type TabDef } from '../components/shared/Tabs';

const Vessels = lazy(() => import('./Vessels'));
const Fleets = lazy(() => import('./Fleets'));
const Workspace = lazy(() => import('./Workspace'));

function panel(node: ReactNode): ReactNode {
  return <Suspense fallback={<p className="text-dim" style={{ padding: '1rem' }}>Loading...</p>}>{node}</Suspense>;
}

/**
 * Vessels hub. A fleet is a folder of vessels, so managing the container and its
 * contents belongs on one surface. Vessels is the primary view; Fleets folds in
 * as a tab (create/rename/delete), and Workspace is the per-vessel drill-in.
 * Keeps Armada's nautical vocabulary: the nav item and route stay `/vessels`.
 */
export default function VesselsHub() {
  const tabs: TabDef[] = [
    { key: 'vessels', label: 'Vessels', render: () => panel(<Vessels />) },
    { key: 'fleets', label: 'Fleets', render: () => panel(<Fleets />) },
    { key: 'workspace', label: 'Workspace', render: () => panel(<Workspace />) },
  ];

  return <Tabs tabs={tabs} defaultTabKey="vessels" ariaLabel="Vessel sections" />;
}
