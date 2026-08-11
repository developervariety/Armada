import { lazy, Suspense, type ReactNode } from 'react';
import Tabs, { type TabDef } from '../components/shared/Tabs';
import { useAuth } from '../context/AuthContext';

const Server = lazy(() => import('./Server'));
const Doctor = lazy(() => import('./Doctor'));
const Tenants = lazy(() => import('./admin/Tenants'));
const Users = lazy(() => import('./admin/Users'));
const Credentials = lazy(() => import('./admin/Credentials'));

function panel(node: ReactNode): ReactNode {
  return <Suspense fallback={<p className="text-dim" style={{ padding: '1rem' }}>Loading...</p>}>{node}</Suspense>;
}

/**
 * Settings hub. System-level administration belongs together: server settings,
 * one-screen Diagnostics (formerly Doctor), and the tenant/user/credential admin
 * surfaces as tabs. The admin tabs are role-gated (client-side UI only; the
 * backend still enforces authorization).
 */
export default function ServerHub() {
  const { isAdmin, isTenantAdmin } = useAuth();
  const admin = isAdmin || isTenantAdmin;

  const tabs: TabDef[] = [
    { key: 'server', label: 'Server', render: () => panel(<Server />) },
    { key: 'diagnostics', label: 'Diagnostics', render: () => panel(<Doctor />) },
    { key: 'tenants', label: 'Tenants', hidden: !isAdmin, render: () => panel(<Tenants />) },
    { key: 'users', label: 'Users', hidden: !admin, render: () => panel(<Users />) },
    { key: 'credentials', label: 'Credentials', hidden: !admin, render: () => panel(<Credentials />) },
  ];

  return <Tabs tabs={tabs} defaultTabKey="server" ariaLabel="Settings sections" />;
}
