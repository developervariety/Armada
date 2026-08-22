import { lazy, Suspense, type ReactNode } from 'react';
import Tabs, { type TabDef } from '../components/shared/Tabs';

const Dispatch = lazy(() => import('./Dispatch'));
const Objectives = lazy(() => import('./Objectives'));

function panel(node: ReactNode): ReactNode {
  return <Suspense fallback={<p className="text-dim" style={{ padding: '1rem' }}>Loading...</p>}>{node}</Suspense>;
}

/**
 * Dispatch surface. Backlog is the intake step that feeds Dispatch, so it reads
 * as one workflow: Dispatch is the default view and Backlog is an intake tab.
 */
export default function DispatchHub() {
  const tabs: TabDef[] = [
    { key: 'dispatch', label: 'Dispatch', render: () => panel(<Dispatch />) },
    { key: 'backlog', label: 'Backlog', render: () => panel(<Objectives />) },
  ];

  return <Tabs tabs={tabs} defaultTabKey="dispatch" ariaLabel="Dispatch sections" />;
}
