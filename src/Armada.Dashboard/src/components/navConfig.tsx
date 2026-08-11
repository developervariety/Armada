import { type ReactNode } from 'react';

/**
 * Single source of truth for the sidebar navigation. Both `Layout` (the sidebar)
 * and `CommandPalette` (the Cmd-K launcher) consume this, and the nav-inventory
 * test asserts against it, so the navigation model lives in exactly one place.
 *
 * Ordering rule: the nav reads Dashboard, then Ask Armada (the primary workflow
 * interface, a standalone top-level item), then the grouped sections. The
 * consolidation phases edit `navSections` in place as each hub is built; this
 * file always reflects the currently shipped nav.
 */

export interface NavItem {
  key?: string;
  to: string;
  label: string;
  icon: ReactNode;
  hidden?: boolean;
  tooltip?: string;
}

export interface NavSection {
  key: string;
  label: string;
  matchers: string[];
  items: NavItem[];
}

export const dashboardItem: NavItem = {
  to: '/',
  label: 'Dashboard',
  tooltip: 'Overview of captains, missions, and voyages',
  icon: (
    <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
      <rect x="3" y="3" width="7" height="7" />
      <rect x="14" y="3" width="7" height="7" />
      <rect x="14" y="14" width="7" height="7" />
      <rect x="3" y="14" width="7" height="7" />
    </svg>
  ),
};

/**
 * Ask Armada is a standalone top-level item, rendered directly under Dashboard
 * and above every grouped section. It is expected to be the primary way people
 * drive Armada, so it is never nested inside a section, a tab, or a menu.
 */
export const askArmadaItem: NavItem = {
  to: '/ask',
  label: 'Ask Armada',
  tooltip: 'Ask about fleet state in plain language and drive work from the conversation',
  icon: (
    <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
      <path d="M21 15a2 2 0 0 1-2 2H7l-4 4V5a2 2 0 0 1 2-2h14a2 2 0 0 1 2 2z" />
      <path d="M12 7v4" />
      <path d="M12 15h.01" />
    </svg>
  ),
};

export const navSections: NavSection[] = [
  {
    key: 'operations',
    label: 'OPERATIONS',
    matchers: ['/inbox', '/dispatch', '/planning', '/backlog', '/objectives', '/voyages', '/missions', '/merge-queue'],
    items: [
      {
        to: '/inbox',
        label: 'Needs You',
        tooltip: 'Reviews, failures, and stalls awaiting your attention',
        icon: (
          <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
            <path d="M22 12h-6l-2 3h-4l-2-3H2" />
            <path d="M5.45 5.11 2 12v6a2 2 0 0 0 2 2h16a2 2 0 0 0 2-2v-6l-3.45-6.89A2 2 0 0 0 16.76 4H7.24a2 2 0 0 0-1.79 1.11z" />
          </svg>
        ),
      },
      {
        to: '/planning',
        label: 'Planning',
        tooltip: 'Plan with a captain, preserve the transcript, and dispatch directly from the session',
        icon: (
          <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
            <path d="M21 15a2 2 0 0 1-2 2H7l-4 4V5a2 2 0 0 1 2-2h14a2 2 0 0 1 2 2z" />
            <path d="M8 9h8" />
            <path d="M8 13h5" />
          </svg>
        ),
      },
      {
        to: '/dispatch',
        label: 'Dispatch',
        tooltip: 'Send work to vessels via missions and voyages',
        icon: (
          <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
            <path d="M22 2 11 13" />
            <polygon points="22 2 15 22 11 13 2 9 22 2" />
          </svg>
        ),
      },
      {
        to: '/backlog',
        label: 'Backlog',
        tooltip: 'Capture future work and refine it with a selected captain before dispatch',
        icon: (
          <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
            <path d="M4 4h16v16H4z" />
            <path d="M8 8h8" />
            <path d="M8 12h8" />
            <path d="M8 16h5" />
          </svg>
        ),
      },
      {
        to: '/voyages',
        label: 'Voyages',
        tooltip: 'Batches of related missions dispatched together',
        icon: (
          <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
            <circle cx="12" cy="12" r="10" />
            <polyline points="12 6 12 12 16 14" />
          </svg>
        ),
      },
      {
        to: '/missions',
        label: 'Missions',
        tooltip: 'Individual work units assigned to captains',
        icon: (
          <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
            <path d="M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8z" />
            <polyline points="14 2 14 8 20 8" />
            <line x1="16" y1="13" x2="8" y2="13" />
            <line x1="16" y1="17" x2="8" y2="17" />
          </svg>
        ),
      },
      {
        to: '/merge-queue',
        label: 'Merge Queue',
        tooltip: 'Bors-style queue for landing and testing branches',
        icon: (
          <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
            <circle cx="18" cy="18" r="3" />
            <circle cx="6" cy="6" r="3" />
            <path d="M6 21V9a9 9 0 0 0 9 9" />
          </svg>
        ),
      },
    ],
  },
  {
    key: 'delivery',
    label: 'DELIVERY',
    matchers: ['/workflow-profiles', '/project-profiles', '/skills', '/checks', '/environments', '/deployments', '/releases', '/incidents', '/runbooks'],
    items: [
      {
        to: '/workflow-profiles',
        label: 'Workflow Profiles',
        tooltip: 'Project-specific commands for build, test, release, deploy, and verification',
        icon: (
          <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
            <path d="M4 7h16" />
            <path d="M4 12h10" />
            <path d="M4 17h7" />
            <rect x="3" y="3" width="18" height="18" rx="2" />
          </svg>
        ),
      },
      {
        to: '/project-profiles',
        label: 'Project Profiles',
        tooltip: 'Per-project persona overrides, pipeline, workflow profile, and skills',
        icon: (
          <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
            <path d="M12 2 2 7l10 5 10-5-10-5Z" />
            <path d="m2 17 10 5 10-5" />
            <path d="m2 12 10 5 10-5" />
          </svg>
        ),
      },
      {
        to: '/skills',
        label: 'Skills',
        tooltip: 'Reusable capability snippets attached to projects and injected into prompts',
        icon: (
          <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
            <path d="m12 2 3 7 7 .5-5.5 4.5 2 7-6.5-4-6.5 4 2-7L2 9.5 9 9Z" />
          </svg>
        ),
      },
      {
        to: '/checks',
        label: 'Checks',
        tooltip: 'Structured build, test, deploy, and verification runs',
        icon: (
          <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
            <path d="M20 6 9 17l-5-5" />
          </svg>
        ),
      },
      {
        to: '/environments',
        label: 'Environments',
        tooltip: 'Named deployment targets with metadata, URLs, approval rules, and access notes',
        icon: (
          <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
            <path d="M3 11h18" />
            <path d="M6 7h12" />
            <path d="M8 15h8" />
            <path d="M10 19h4" />
            <path d="M12 3v4" />
          </svg>
        ),
      },
      {
        to: '/deployments',
        label: 'Deployments',
        tooltip: 'Approve, execute, verify, and roll back deployments into named environments',
        icon: (
          <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
            <path d="M12 3v12" />
            <path d="m7 10 5 5 5-5" />
            <path d="M5 21h14" />
          </svg>
        ),
      },
      {
        to: '/releases',
        label: 'Releases',
        tooltip: 'Draft, candidate, shipped, failed, and rolled-back release records',
        icon: (
          <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
            <path d="M7 3h10l4 4v14H3V3h4" />
            <path d="M7 3v6h10" />
            <path d="M9 13h6" />
            <path d="M9 17h6" />
          </svg>
        ),
      },
      {
        to: '/incidents',
        label: 'Incidents',
        tooltip: 'Operational incidents tied to deployments, rollback, hotfix planning, and postmortems',
        icon: (
          <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
            <path d="M12 9v4" />
            <path d="M12 17h.01" />
            <path d="M10.29 3.86 1.82 18a2 2 0 0 0 1.71 3h16.94a2 2 0 0 0 1.71-3L13.71 3.86a2 2 0 0 0-3.42 0z" />
          </svg>
        ),
      },
      {
        to: '/runbooks',
        label: 'Runbooks',
        tooltip: 'Playbook-backed operational runbooks with parameters, step tracking, and history',
        icon: (
          <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
            <path d="M4 19.5A2.5 2.5 0 0 1 6.5 17H20" />
            <path d="M6.5 2H20v20H6.5A2.5 2.5 0 0 1 4 19.5v-15A2.5 2.5 0 0 1 6.5 2z" />
            <path d="M8 7h8" />
            <path d="M8 11h8" />
          </svg>
        ),
      },
    ],
  },
  {
    key: 'fleet',
    label: 'FLEET',
    matchers: ['/fleets', '/vessels', '/workspace', '/captains', '/docks'],
    items: [
      {
        to: '/fleets',
        label: 'Fleets',
        tooltip: 'Collections of vessels grouped together',
        icon: (
          <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
            <rect x="2" y="2" width="20" height="8" rx="2" ry="2" />
            <rect x="2" y="14" width="20" height="8" rx="2" ry="2" />
          </svg>
        ),
      },
      {
        to: '/workspace',
        label: 'Workspace',
        tooltip: 'Open a vessel as a browsable, editable repository workspace',
        icon: (
          <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
            <path d="M3 6h18" />
            <path d="M7 12h10" />
            <path d="M10 18h4" />
            <rect x="3" y="3" width="18" height="18" rx="2" />
          </svg>
        ),
      },
      {
        to: '/vessels',
        label: 'Vessels',
        tooltip: 'Registered git repositories and vessel configuration',
        icon: (
          <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
            <path d="M8 6h13" />
            <path d="M8 12h13" />
            <path d="M8 18h13" />
            <path d="M3 6h.01" />
            <path d="M3 12h.01" />
            <path d="M3 18h.01" />
          </svg>
        ),
      },
      {
        to: '/captains',
        label: 'Captains',
        tooltip: 'AI coding agents that execute missions',
        icon: (
          <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
            <path d="M20 21v-2a4 4 0 0 0-4-4H8a4 4 0 0 0-4 4v2" />
            <circle cx="12" cy="7" r="4" />
          </svg>
        ),
      },
      {
        to: '/docks',
        label: 'Docks',
        tooltip: 'Isolated git worktrees assigned to captains',
        icon: (
          <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
            <rect x="2" y="7" width="20" height="14" rx="2" ry="2" />
            <path d="M16 21V5a2 2 0 0 0-2-2h-4a2 2 0 0 0-2 2v16" />
          </svg>
        ),
      },
    ],
  },
  {
    key: 'activity',
    label: 'ACTIVITY',
    matchers: ['/history', '/signals', '/events', '/notifications'],
    items: [
      {
        to: '/history',
        label: 'History',
        tooltip: 'Cross-entity timeline spanning missions, checks, requests, planning, merge queue, and events',
        icon: (
          <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
            <path d="M3 12h18" />
            <path d="M12 3v18" />
            <circle cx="12" cy="12" r="9" />
          </svg>
        ),
      },
      {
        to: '/signals',
        label: 'Signals',
        tooltip: 'Messages exchanged between the Admiral and captains',
        icon: (
          <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
            <polyline points="22 12 18 12 15 21 9 3 6 12 2 12" />
          </svg>
        ),
      },
      {
        to: '/events',
        label: 'Events',
        tooltip: 'Audit log of system-wide actions and state changes',
        icon: (
          <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
            <path d="M12 20h9" />
            <path d="M16.5 3.5a2.121 2.121 0 0 1 3 3L7 19l-4 1 1-4L16.5 3.5z" />
          </svg>
        ),
      },
      {
        to: '/notifications',
        label: 'Notifications',
        tooltip: 'Real-time alerts for mission completions and failures',
        icon: (
          <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
            <path d="M18 8A6 6 0 0 0 6 8c0 7-3 9-3 9h18s-3-2-3-9" />
            <path d="M13.73 21a2 2 0 0 1-3.46 0" />
          </svg>
        ),
      },
    ],
  },
  {
    key: 'system',
    label: 'SYSTEM',
    matchers: ['/server', '/doctor', '/settings', '/personas', '/pipelines', '/prompt-templates', '/playbooks', '/requests', '/api-explorer'],
    items: [
      {
        to: '/personas',
        label: 'Personas',
        tooltip: 'Agent personality profiles (Worker, Architect, Judge, etc.)',
        icon: (
          <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
            <path d="M20 21v-2a4 4 0 0 0-4-4H8a4 4 0 0 0-4 4v2" />
            <circle cx="12" cy="7" r="4" />
          </svg>
        ),
      },
      {
        to: '/pipelines',
        label: 'Pipelines',
        tooltip: 'Multi-stage workflows combining different personas',
        icon: (
          <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
            <polyline points="16 3 21 3 21 8" />
            <line x1="4" y1="20" x2="21" y2="3" />
            <polyline points="21 16 21 21 16 21" />
            <line x1="15" y1="15" x2="21" y2="21" />
            <line x1="4" y1="4" x2="9" y2="9" />
          </svg>
        ),
      },
      {
        to: '/prompt-templates',
        label: 'Prompts',
        tooltip: 'Customizable prompt templates injected into agent instructions',
        icon: (
          <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
            <path d="M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8z" />
            <polyline points="14 2 14 8 20 8" />
            <line x1="16" y1="13" x2="8" y2="13" />
            <line x1="16" y1="17" x2="8" y2="17" />
            <line x1="10" y1="9" x2="8" y2="9" />
          </svg>
        ),
      },
      {
        to: '/playbooks',
        label: 'Playbooks',
        tooltip: 'Reusable markdown guidance applied during dispatch and voyages',
        icon: (
          <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
            <path d="M4 19.5A2.5 2.5 0 0 1 6.5 17H20" />
            <path d="M6.5 2H20v20H6.5A2.5 2.5 0 0 1 4 19.5v-15A2.5 2.5 0 0 1 6.5 2z" />
          </svg>
        ),
      },
      {
        to: '/requests',
        label: 'Requests',
        tooltip: 'Captured request history with summaries, replay, and request inspection',
        icon: (
          <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
            <path d="M3 12h18" />
            <path d="M8 7l-5 5 5 5" />
            <path d="M16 17l5-5-5-5" />
          </svg>
        ),
      },
      {
        to: '/api-explorer',
        label: 'API Explorer',
        tooltip: 'Browse the live OpenAPI document, execute requests, and inspect responses',
        icon: (
          <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
            <circle cx="11" cy="11" r="8" />
            <path d="m21 21-4.35-4.35" />
            <path d="M11 8v6" />
            <path d="M8 11h6" />
          </svg>
        ),
      },
      {
        to: '/server',
        label: 'Server',
        tooltip: 'Admiral server settings, ports, and configuration',
        icon: (
          <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
            <rect x="2" y="2" width="20" height="8" rx="2" ry="2" />
            <rect x="2" y="14" width="20" height="8" rx="2" ry="2" />
            <line x1="6" y1="6" x2="6.01" y2="6" />
            <line x1="6" y1="18" x2="6.01" y2="18" />
          </svg>
        ),
      },
      {
        to: '/doctor',
        label: 'Doctor',
        tooltip: 'System health diagnostics and environment checks',
        icon: (
          <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
            <path d="M22 12h-4l-3 9L9 3l-3 9H2" />
          </svg>
        ),
      },
    ],
  },
  {
    key: 'security',
    label: 'SECURITY',
    matchers: ['/admin/tenants', '/admin/users', '/admin/credentials'],
    items: [
      {
        to: '/admin/tenants',
        label: 'Tenants',
        tooltip: 'Multi-tenant organizations within Armada',
        icon: (
          <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
            <path d="M3 21h18" />
            <path d="M5 21V7l7-4 7 4v14" />
            <path d="M9 9h.01" />
            <path d="M9 13h.01" />
            <path d="M15 9h.01" />
            <path d="M15 13h.01" />
          </svg>
        ),
      },
      {
        to: '/admin/users',
        label: 'Users',
        tooltip: 'User accounts and role assignments',
        icon: (
          <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
            <path d="M17 21v-2a4 4 0 0 0-4-4H5a4 4 0 0 0-4 4v2" />
            <circle cx="9" cy="7" r="4" />
            <path d="M23 21v-2a4 4 0 0 0-3-3.87" />
            <path d="M16 3.13a4 4 0 0 1 0 7.75" />
          </svg>
        ),
      },
      {
        to: '/admin/credentials',
        label: 'Credentials',
        tooltip: 'API tokens and bearer credentials for authentication',
        icon: (
          <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
            <path d="M21 2l-2 2m-7.61 7.61a5.5 5.5 0 1 1 7.78 7.78l-3.19-3.19" />
            <path d="M5.5 12.5 2 16l6 6 3.5-3.5" />
            <path d="m14.5 8.5 1 1" />
            <path d="m10.5 12.5 1 1" />
          </svg>
        ),
      },
    ],
  },
];

/** Section keys whose collapse-state defaults to open on first paint (daily drivers). */
export const DEFAULT_EXPANDED_SECTIONS: Record<string, boolean> = {
  operations: true,
  delivery: false,
  fleet: true,
  activity: true,
  system: false,
  security: true,
};

/**
 * Flatten every reachable destination (Dashboard, Ask Armada, and each section
 * item) into a simple list the command palette can search. Icons are dropped.
 */
export function flattenNavCommands(): Array<{ to: string; label: string; section: string }> {
  const commands: Array<{ to: string; label: string; section: string }> = [
    { to: dashboardItem.to, label: dashboardItem.label, section: '' },
    { to: askArmadaItem.to, label: askArmadaItem.label, section: '' },
  ];
  for (const section of navSections) {
    for (const item of section.items) {
      if (item.hidden) continue;
      commands.push({ to: item.to, label: item.label, section: section.label });
    }
  }
  return commands;
}
