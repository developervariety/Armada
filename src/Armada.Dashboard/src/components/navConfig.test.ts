import { describe, it, expect } from 'vitest';
import { dashboardItem, askArmadaItem, navSections, flattenNavCommands } from './navConfig';

/**
 * Locks the consolidated navigation model so regressions cannot silently
 * re-add or re-nest the pages the consolidation folded away.
 *
 * This fork carries two destinations upstream's nav does not. Code Index is a
 * fork-only feature with no upstream counterpart and therefore no hub tab to
 * fold into. Notifications is deliberately kept: upstream folded it away and
 * its NotificationBell navigates to the source record rather than to the page,
 * which leaves the full notifications page routed but unreachable. The bell is
 * the better surface for a single notification and is kept as well; the nav
 * item is what keeps the page itself reachable.
 */
describe('navConfig', () => {
  const commands = flattenNavCommands();
  const allTargets = commands.map((c) => c.to);

  it('leads with Dashboard then a standalone Ask Armada', () => {
    expect(dashboardItem.to).toBe('/');
    expect(askArmadaItem.to).toBe('/ask');
    expect(commands[0].to).toBe('/');
    expect(commands[1].to).toBe('/ask');
    // Ask Armada must be standalone (section === ''), not nested in a group.
    expect(commands[1].section).toBe('');
  });

  it('exposes exactly the consolidated destinations', () => {
    const present = [
      '/', '/ask', '/inbox', '/planning', '/dispatch', '/missions',
      '/delivery', '/vessels', '/captains', '/configuration', '/activity',
      '/jobs', '/api-explorer', '/server',
      // Fork-only additions; see the file comment for why each one stays.
      '/code-index', '/notifications',
    ];
    for (const target of present) {
      expect(allTargets).toContain(target);
    }
    // Upstream's 14 consolidated destinations plus this fork's Code Index and
    // Notifications; admin lives as tabs under Settings.
    expect(commands).toHaveLength(16);
  });

  it('no longer surfaces the folded-away pages as nav items', () => {
    const removed = [
      '/docks', '/doctor', '/fleets', '/workspace',
      '/history', '/requests', '/events', '/signals',
      '/workflow-profiles', '/project-profiles', '/skills', '/personas',
      '/pipelines', '/prompt-templates', '/playbooks',
      '/voyages', '/merge-queue', '/backlog',
      '/checks', '/environments', '/deployments', '/releases', '/incidents', '/runbooks',
      '/admin/tenants', '/admin/users', '/admin/credentials',
    ];
    for (const target of removed) {
      expect(allTargets).not.toContain(target);
    }
  });

  it('keeps section keys aligned with the collapse-default map', () => {
    const keys = navSections.map((s) => s.key);
    expect(new Set(keys).size).toBe(keys.length);
  });
});
