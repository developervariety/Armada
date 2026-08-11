import { lazy, Suspense, type ReactNode } from 'react';
import Tabs, { type TabDef } from '../components/shared/Tabs';

const Server = lazy(() => import('./Server'));
const Doctor = lazy(() => import('./Doctor'));

function panel(node: ReactNode): ReactNode {
  return <Suspense fallback={<p className="text-dim" style={{ padding: '1rem' }}>Loading...</p>}>{node}</Suspense>;
}

/**
 * Server hub. System-level admin belongs together, so the one-screen Doctor
 * diagnostics become a tab on Server rather than a standalone route. The top-bar
 * health dot links here (/server?tab=diagnostics).
 */
export default function ServerHub() {
  const tabs: TabDef[] = [
    { key: 'server', label: 'Server', render: () => panel(<Server />) },
    { key: 'diagnostics', label: 'Diagnostics', render: () => panel(<Doctor />) },
  ];

  return <Tabs tabs={tabs} defaultTabKey="server" ariaLabel="Server sections" />;
}
