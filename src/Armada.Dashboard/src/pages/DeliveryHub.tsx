import { lazy, Suspense, type ReactNode } from 'react';
import Tabs, { type TabDef } from '../components/shared/Tabs';

const Deployments = lazy(() => import('./Deployments'));
const Environments = lazy(() => import('./Environments'));
const Releases = lazy(() => import('./Releases'));
const Incidents = lazy(() => import('./Incidents'));
const CheckRuns = lazy(() => import('./CheckRuns'));
const Runbooks = lazy(() => import('./Runbooks'));

function panel(node: ReactNode): ReactNode {
  return <Suspense fallback={<p className="text-dim" style={{ padding: '1rem' }}>Loading...</p>}>{node}</Suspense>;
}

/**
 * Delivery hub. Environments, Deployments, Releases, and Incidents are distinct
 * lifecycle concepts, so they stay separate tabs rather than merging into one
 * blob; Checks and Runbooks are the supporting material and live here too. All
 * six tabs are shown to everyone this pass (per-tenant gating can layer on later).
 */
export default function DeliveryHub() {
  const tabs: TabDef[] = [
    { key: 'deployments', label: 'Deployments', render: () => panel(<Deployments />) },
    { key: 'environments', label: 'Environments', render: () => panel(<Environments />) },
    { key: 'releases', label: 'Releases', render: () => panel(<Releases />) },
    { key: 'incidents', label: 'Incidents', render: () => panel(<Incidents />) },
    { key: 'checks', label: 'Checks', render: () => panel(<CheckRuns />) },
    { key: 'runbooks', label: 'Runbooks', render: () => panel(<Runbooks />) },
  ];

  return <Tabs tabs={tabs} defaultTabKey="deployments" ariaLabel="Delivery sections" />;
}
