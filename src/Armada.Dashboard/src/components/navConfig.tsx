import { type ReactNode } from 'react';
import { icons } from './navIcons';

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
  icon: icons.dashboard,
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
  icon: icons.ask,
};

export const navSections: NavSection[] = [
  {
    key: 'operations',
    label: 'OPERATIONS',
    matchers: ['/inbox', '/notifications', '/dispatch', '/planning', '/backlog', '/objectives', '/voyages', '/missions', '/merge-queue', '/mission-actions'],
    items: [
      { to: '/inbox', label: 'Needs You', tooltip: 'Reviews, failures, and stalls awaiting your attention', icon: icons.needsYou },
      { to: '/notifications', label: 'Notifications', tooltip: 'Delivered state-change history', icon: icons.notifications },
      { to: '/planning', label: 'Planning', tooltip: 'Plan with a captain, preserve the transcript, and dispatch directly from the session', icon: icons.planning },
      { to: '/dispatch', label: 'Dispatch', tooltip: 'Send work to vessels; capture and refine backlog on the Backlog tab', icon: icons.dispatch },
      { to: '/missions', label: 'Missions', tooltip: 'Work units, plus Voyages and the full Merge Queue as tabs', icon: icons.missions },
      { to: '/mission-actions', label: 'Mission Actions', tooltip: 'Save reusable mission prompts and review their runs', icon: icons.missions },
    ],
  },
  {
    key: 'delivery',
    label: 'DELIVERY',
    matchers: ['/delivery', '/checks', '/environments', '/deployments', '/releases', '/incidents', '/runbooks'],
    items: [
      { to: '/delivery', label: 'Delivery', tooltip: 'Deployments, Environments, Releases, Incidents, Checks, and Runbooks as tabs', icon: icons.delivery },
    ],
  },
  {
    key: 'fleet',
    label: 'BUILD',
    matchers: ['/fleets', '/vessels', '/workspace', '/captains', '/docks', '/code-index'],
    items: [
      { to: '/vessels', label: 'Vessels', tooltip: 'Repositories grouped by fleet, plus the vessel workspace, on one surface', icon: icons.vessels },
      { to: '/captains', label: 'Captains', tooltip: 'AI coding agents that execute missions', icon: icons.captains },
      { to: '/code-index', label: 'Code Index', tooltip: 'Search indexed source and graph structure across vessels', icon: icons.codeIndex },
    ],
  },
  {
    key: 'configuration',
    label: 'CONFIGURATION',
    matchers: ['/configuration', '/workflow-profiles', '/project-profiles', '/skills', '/personas', '/pipelines', '/prompt-templates', '/playbooks'],
    items: [
      { to: '/configuration', label: 'Configuration', tooltip: 'Workflow Profiles, Project Profiles, Skills, Personas, Pipelines, Prompts, Playbooks, Endpoints, and Memory as tabs', icon: icons.configuration },
    ],
  },
  {
    key: 'activity',
    label: 'ACTIVITY',
    matchers: ['/activity', '/history', '/requests', '/events', '/signals', '/jobs'],
    items: [
      { to: '/activity', label: 'Activity', tooltip: 'One log across requests, events, signals, and history; filter by source type', icon: icons.activity },
      { to: '/jobs', label: 'Jobs', tooltip: 'Background jobs and their status', icon: icons.activity },
    ],
  },
  {
    key: 'system',
    label: 'SYSTEM',
    matchers: ['/server', '/doctor', '/settings', '/admin', '/api-explorer', '/storage', '/battery'],
    items: [
      { to: '/api-explorer', label: 'API Explorer', tooltip: 'Browse the live OpenAPI document, execute requests, and inspect responses', icon: icons.apiExplorer },
      { to: '/storage', label: 'Storage', tooltip: 'Where the data directory and database use disk space (global administrators)', icon: icons.storage },
      { to: '/battery', label: 'Battery', tooltip: 'Fixture battery runs that grade a captain on small repository tasks (global administrators)', icon: icons.activity },
      { to: '/server', label: 'Settings', tooltip: 'Server settings, diagnostics, and tenant/user/credential administration', icon: icons.server },
    ],
  },
];

/** Section keys whose collapse-state defaults to open on first paint (daily drivers). */
export const DEFAULT_EXPANDED_SECTIONS: Record<string, boolean> = {
  operations: true,
  delivery: true,
  fleet: true,
  configuration: true,
  activity: true,
  system: true,
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
