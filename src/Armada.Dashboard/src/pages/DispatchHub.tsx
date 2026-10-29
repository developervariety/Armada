import { lazy, Suspense, type ReactNode } from 'react';
import Tabs, { type TabDef } from '../components/shared/Tabs';

const Dispatch = lazy(() => import('./Dispatch'));
const Planning = lazy(() => import('./Planning'));
const Objectives = lazy(() => import('./Objectives'));

function panel(node: ReactNode): ReactNode {
  return <Suspense fallback={<p className="text-dim" style={{ padding: '1rem' }}>Loading...</p>}>{node}</Suspense>;
}

/**
 * Dispatch surface. Backlog is the intake step that feeds Dispatch and Planning
 * is the conversation that shapes it, so they read as one workflow: Dispatch is
 * the default view, Planning and Backlog are tabs. A planning session opens with
 * ?tab=planning&session={id} (/planning and /planning/:id redirect there).
 */
export default function DispatchHub() {
  const tabs: TabDef[] = [
    { key: 'dispatch', label: 'Dispatch', tooltip: 'Describe work and send it to captains as one or more missions on a vessel.', render: () => panel(<Dispatch />) },
    { key: 'planning', label: 'Planning', tooltip: 'Plan with a captain against a vessel, keep the transcript, and dispatch straight from the session.', render: () => panel(<Planning />) },
    { key: 'backlog', label: 'Backlog', tooltip: 'Work you have captured but not dispatched yet, which you can refine, plan, and order.', render: () => panel(<Objectives />) },
  ];

  return <Tabs tabs={tabs} defaultTabKey="dispatch" ariaLabel="Dispatch sections" />;
}
