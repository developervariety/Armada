import { lazy, Suspense, type ReactNode } from 'react';
import PageHeader from '../components/shared/PageHeader';
import Tabs, { type TabDef } from '../components/shared/Tabs';
import { useLocale } from '../context/LocaleContext';

const History = lazy(() => import('./History'));
const RequestHistory = lazy(() => import('./RequestHistory'));
const Events = lazy(() => import('./Events'));
const Signals = lazy(() => import('./Signals'));

function panel(node: ReactNode): ReactNode {
  return <Suspense fallback={<p className="text-dim" style={{ padding: '1rem' }}>Loading...</p>}>{node}</Suspense>;
}

/**
 * Activity log. Unifies the four "what happened" surfaces behind one source
 * selector so an operator stops guessing which of History, Requests, Events, or
 * Signals to open. "All activity" is the cross-entity History superset; the
 * Requests source keeps its KPIs, activity chart, and request inspector modal;
 * Events and Signals keep their audit/message detail routes. The active source
 * lives in the URL (?source=), so Needs You and Dashboard links land here
 * pre-filtered.
 */
export default function Activity() {
  const { t } = useLocale();

  const tabs: TabDef[] = [
    { key: 'history', label: 'All Activity', render: () => panel(<History />) },
    { key: 'requests', label: 'Requests', render: () => panel(<RequestHistory />) },
    { key: 'events', label: 'Events', render: () => panel(<Events />) },
    { key: 'signals', label: 'Signals', render: () => panel(<Signals />) },
  ];

  return (
    <div>
      <PageHeader
        title={t('Activity')}
        subtitle={t('One log across requests, events, signals, and cross-entity history. Filter by source.')}
      />
      <Tabs tabs={tabs} param="source" defaultTabKey="history" ariaLabel="Activity sources" />
    </div>
  );
}
