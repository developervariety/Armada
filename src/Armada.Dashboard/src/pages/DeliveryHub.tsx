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
    { key: 'deployments', label: 'Deployments', tooltip: 'Each time a release was deployed to an environment, with approval, verification, and rollback state.', render: () => panel(<Deployments />) },
    { key: 'environments', label: 'Environments', tooltip: 'Named deployment targets for vessels (for example staging or production), with URLs, configuration, approval requirements, and notes.', render: () => panel(<Environments />) },
    { key: 'releases', label: 'Releases', tooltip: 'Release records that bundle a version, notes, the voyages and missions in it, its checks, and its artifacts.', render: () => panel(<Releases />) },
    { key: 'incidents', label: 'Incidents', tooltip: 'Problems after a deployment or release, with hotfix context, rollback history, recovery notes, and postmortems.', render: () => panel(<Incidents />) },
    { key: 'checks', label: 'Checks', tooltip: 'Runs of a workflow profile\'s commands (lint, build, test, and so on) and their results.', render: () => panel(<CheckRuns />) },
    { key: 'runbooks', label: 'Runbooks', tooltip: 'Operational procedures built on playbooks, with parameters, step tracking, and a history of each run.', render: () => panel(<Runbooks />) },
  ];

  return <Tabs tabs={tabs} defaultTabKey="deployments" ariaLabel="Delivery sections" />;
}
