import { describe, it, expect } from 'vitest';
import { dashboardItem, askArmadaItem, navSections, flattenNavCommands } from './navConfig';

/**
 * Locks the consolidated navigation model so regressions cannot silently
 * re-add or re-nest the pages the consolidation folded away.
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
    ];
    for (const target of present) {
      expect(allTargets).toContain(target);
    }
    // 14 top-level destinations (Jobs added under Activity); admin lives as tabs under Settings.
    expect(commands).toHaveLength(14);
  });

  it('no longer surfaces the folded-away pages as nav items', () => {
    const removed = [
      '/notifications', '/docks', '/doctor', '/fleets', '/workspace',
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
