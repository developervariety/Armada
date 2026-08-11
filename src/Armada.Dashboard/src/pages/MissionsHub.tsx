import { lazy, Suspense, type ReactNode } from 'react';
import PageHeader from '../components/shared/PageHeader';
import Tabs, { type TabDef } from '../components/shared/Tabs';
import { useLocale } from '../context/LocaleContext';

const Missions = lazy(() => import('./Missions'));
const Voyages = lazy(() => import('./Voyages'));
const MergeQueue = lazy(() => import('./MergeQueue'));

function panel(node: ReactNode): ReactNode {
  return <Suspense fallback={<p className="text-dim" style={{ padding: '1rem' }}>Loading...</p>}>{node}</Suspense>;
}

/**
 * Missions surface. Missions, Voyages, and the merge queue are the same
 * operational flow at three grains, so they share one surface: Missions is the
 * default view, with Voyages and the full Merge Queue as tabs. Attention-worthy
 * merge entries also surface in Needs You (backend inbox).
 */
export default function MissionsHub() {
  const { t } = useLocale();

  const tabs: TabDef[] = [
    { key: 'missions', label: 'Missions', render: () => panel(<Missions />) },
    { key: 'voyages', label: 'Voyages', render: () => panel(<Voyages />) },
    { key: 'merge-queue', label: 'Merge Queue', render: () => panel(<MergeQueue />) },
  ];

  return (
    <div>
      <PageHeader
        title={t('Missions')}
        subtitle={t('Work units, the voyages that batch them, and the merge queue that lands them.')}
      />
      <Tabs tabs={tabs} defaultTabKey="missions" ariaLabel="Mission sections" />
    </div>
  );
}
