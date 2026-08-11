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
    matchers: ['/inbox', '/dispatch', '/planning', '/backlog', '/objectives', '/voyages', '/missions', '/merge-queue'],
    items: [
      { to: '/inbox', label: 'Needs You', tooltip: 'Reviews, failures, and stalls awaiting your attention', icon: icons.needsYou },
      { to: '/planning', label: 'Planning', tooltip: 'Plan with a captain, preserve the transcript, and dispatch directly from the session', icon: icons.planning },
      { to: '/dispatch', label: 'Dispatch', tooltip: 'Send work to vessels; capture and refine backlog on the Backlog tab', icon: icons.dispatch },
      { to: '/missions', label: 'Missions', tooltip: 'Work units, plus Voyages and the full Merge Queue as tabs', icon: icons.missions },
    ],
  },
  {
    key: 'delivery',
    label: 'DELIVERY',
    matchers: ['/checks', '/environments', '/deployments', '/releases', '/incidents', '/runbooks'],
    items: [
      { to: '/checks', label: 'Checks', tooltip: 'Structured build, test, deploy, and verification runs', icon: icons.checks },
      { to: '/environments', label: 'Environments', tooltip: 'Named deployment targets with metadata, URLs, approval rules, and access notes', icon: icons.environments },
      { to: '/deployments', label: 'Deployments', tooltip: 'Approve, execute, verify, and roll back deployments into named environments', icon: icons.deployments },
      { to: '/releases', label: 'Releases', tooltip: 'Draft, candidate, shipped, failed, and rolled-back release records', icon: icons.releases },
      { to: '/incidents', label: 'Incidents', tooltip: 'Operational incidents tied to deployments, rollback, hotfix planning, and postmortems', icon: icons.incidents },
      { to: '/runbooks', label: 'Runbooks', tooltip: 'Playbook-backed operational runbooks with parameters, step tracking, and history', icon: icons.runbooks },
    ],
  },
  {
    key: 'fleet',
    label: 'BUILD',
    matchers: ['/fleets', '/vessels', '/workspace', '/captains', '/docks'],
    items: [
      { to: '/vessels', label: 'Vessels', tooltip: 'Repositories grouped by fleet, plus the vessel workspace, on one surface', icon: icons.vessels },
      { to: '/captains', label: 'Captains', tooltip: 'AI coding agents that execute missions', icon: icons.captains },
    ],
  },
  {
    key: 'configuration',
    label: 'CONFIGURATION',
    matchers: ['/configuration', '/workflow-profiles', '/project-profiles', '/skills', '/personas', '/pipelines', '/prompt-templates', '/playbooks'],
    items: [
      { to: '/configuration', label: 'Configuration', tooltip: 'Workflow Profiles, Project Profiles, Skills, Personas, Pipelines, Prompts, and Playbooks as tabs', icon: icons.configuration },
    ],
  },
  {
    key: 'activity',
    label: 'ACTIVITY',
    matchers: ['/activity', '/history', '/requests', '/events', '/signals'],
    items: [
      { to: '/activity', label: 'Activity', tooltip: 'One log across requests, events, signals, and history; filter by source type', icon: icons.activity },
    ],
  },
  {
    key: 'system',
    label: 'SYSTEM',
    matchers: ['/server', '/doctor', '/settings', '/api-explorer'],
    items: [
      { to: '/api-explorer', label: 'API Explorer', tooltip: 'Browse the live OpenAPI document, execute requests, and inspect responses', icon: icons.apiExplorer },
      { to: '/server', label: 'Server', tooltip: 'Admiral server settings, configuration, and health diagnostics', icon: icons.server },
    ],
  },
  {
    key: 'security',
    label: 'SECURITY',
    matchers: ['/admin/tenants', '/admin/users', '/admin/credentials'],
    items: [
      { to: '/admin/tenants', label: 'Tenants', tooltip: 'Multi-tenant organizations within Armada', icon: icons.tenants },
      { to: '/admin/users', label: 'Users', tooltip: 'User accounts and role assignments', icon: icons.users },
      { to: '/admin/credentials', label: 'Credentials', tooltip: 'API tokens and bearer credentials for authentication', icon: icons.credentials },
    ],
  },
];

/** Section keys whose collapse-state defaults to open on first paint (daily drivers). */
export const DEFAULT_EXPANDED_SECTIONS: Record<string, boolean> = {
  operations: true,
  delivery: false,
  fleet: true,
  configuration: false,
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
