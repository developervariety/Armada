import { describe, expect, it } from 'vitest';
import {
  canEdit,
  canEditOwned,
  canView,
  canWrite,
  resolveCreateScope,
  OWNED_RECORD_WRITE_LEVEL,
  type ScopeViewer,
} from './scoping';

const admin: ScopeViewer = { isAdmin: true, isTenantAdmin: true, tenantId: 'ten_a', userId: 'usr_admin' };
const tenantAdmin: ScopeViewer = { isAdmin: false, isTenantAdmin: true, tenantId: 'ten_a', userId: 'usr_ta' };
const user: ScopeViewer = { isAdmin: false, isTenantAdmin: false, tenantId: 'ten_a', userId: 'usr_1' };

describe('ownership rules', () => {
  it('lets a regular user edit only their own personal record', () => {
    expect(canEdit(user, { ownershipScope: 'UserSpecific', tenantId: 'ten_a', userId: 'usr_1' })).toBe(true);
    expect(canEdit(user, { ownershipScope: 'UserSpecific', tenantId: 'ten_a', userId: 'usr_2' })).toBe(false);
    expect(canEdit(user, { ownershipScope: 'TenantWide', tenantId: 'ten_a', userId: 'usr_1' })).toBe(false);
  });

  it('keeps tenant administrators inside their tenant and lets global administrators cross it', () => {
    const foreign = { ownershipScope: 'TenantWide' as const, tenantId: 'ten_b', userId: 'usr_9' };
    expect(canEdit(tenantAdmin, { ownershipScope: 'UserSpecific', tenantId: 'ten_a', userId: 'usr_1' })).toBe(true);
    expect(canEdit(tenantAdmin, foreign)).toBe(false);
    expect(canEdit(admin, foreign)).toBe(true);
  });

  it('treats a record without tenant or user as the default tenant and user, like the server', () => {
    const defaultUser: ScopeViewer = { isAdmin: false, isTenantAdmin: false, tenantId: null, userId: undefined };
    expect(canEdit(defaultUser, { ownershipScope: 'UserSpecific', tenantId: 'default', userId: 'default' })).toBe(true);
    expect(canEdit(user, { ownershipScope: 'UserSpecific', tenantId: null, userId: null })).toBe(false);
  });

  it('shares built-in records for reading but never for editing', () => {
    const builtIn = { ownershipScope: 'UserSpecific' as const, tenantId: 'ten_b', userId: 'usr_9', isBuiltIn: true };
    expect(canView(user, builtIn)).toBe(true);
    expect(canEdit(user, builtIn)).toBe(false);
  });

  it('applies the server role gate before the ownership rule', () => {
    const own = { ownershipScope: 'UserSpecific' as const, tenantId: 'ten_a', userId: 'usr_1' };
    expect(canWrite(user, OWNED_RECORD_WRITE_LEVEL.personas)).toBe(false);
    expect(canEditOwned(user, own, OWNED_RECORD_WRITE_LEVEL.personas)).toBe(false);
    expect(canEditOwned(user, own, OWNED_RECORD_WRITE_LEVEL.modelEndpoints)).toBe(true);
    expect(canWrite(tenantAdmin, OWNED_RECORD_WRITE_LEVEL.promptTemplates)).toBe(false);
    expect(canWrite(admin, OWNED_RECORD_WRITE_LEVEL.promptTemplates)).toBe(true);
  });

  it('creates personal records for regular users and lets administrators choose', () => {
    expect(resolveCreateScope(user, 'TenantWide')).toBe('UserSpecific');
    expect(resolveCreateScope(tenantAdmin)).toBe('TenantWide');
    expect(resolveCreateScope(tenantAdmin, 'UserSpecific')).toBe('UserSpecific');
  });
});
