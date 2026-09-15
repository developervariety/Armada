import type { ScopeEnum } from '../types/models';

/**
 * Frontend mirror of the server ownership rules for tenant-wide and user-specific records.
 * Keep `canView` and `canEdit` in step with `Armada.Core.Authorization.OwnershipPolicy`, and
 * `OWNED_RECORD_WRITE_LEVEL` in step with `Armada.Core.Authorization.AuthorizationConfig`.
 */

/** Tenant and user a record or caller without one belongs to, as the server resolves them. */
export const DEFAULT_TENANT_ID = 'default';
export const DEFAULT_USER_ID = 'default';

/** The caller identity needed to evaluate ownership rules (from `useAuth()`). */
export interface ScopeViewer {
  isAdmin: boolean;
  isTenantAdmin: boolean;
  tenantId: string | null | undefined;
  userId: string | null | undefined;
}

/** A record's ownership fields. */
export interface OwnedObject {
  ownershipScope: ScopeEnum;
  tenantId?: string | null;
  userId?: string | null;
  /** Server-seeded records are readable by every caller; being built in never grants edit rights. */
  isBuiltIn?: boolean;
}

/** Role a caller needs before the server runs a create, update, or delete handler at all. */
export type OwnedRecordWriteLevel = 'Authenticated' | 'TenantAdmin' | 'AdminOnly';

/** Write level per owned record type, as the server authorization table sets it. */
export const OWNED_RECORD_WRITE_LEVEL = {
  personas: 'TenantAdmin',
  pipelines: 'TenantAdmin',
  promptTemplates: 'AdminOnly',
  modelEndpoints: 'Authenticated',
  memories: 'Authenticated',
} as const satisfies Record<string, OwnedRecordWriteLevel>;

function tenantOf(tenantId: string | null | undefined): string {
  return tenantId && tenantId.trim() ? tenantId : DEFAULT_TENANT_ID;
}

function userOf(userId: string | null | undefined): string {
  return userId && userId.trim() ? userId : DEFAULT_USER_ID;
}

/** Build a viewer from the `useAuth()` result. */
export function viewerFromAuth(auth: {
  isAdmin: boolean;
  isTenantAdmin: boolean;
  user: { user: { tenantId?: string | null; id?: string | null } | null } | null;
}): ScopeViewer {
  return {
    isAdmin: auth.isAdmin,
    isTenantAdmin: auth.isTenantAdmin,
    tenantId: auth.user?.user?.tenantId,
    userId: auth.user?.user?.id,
  };
}

/**
 * Whether the viewer may read a record. Global administrators read everything and built-in records
 * are shared; nobody else crosses a tenant boundary. Tenant administrators read their whole tenant;
 * otherwise the record must be tenant-wide or owned by the viewer.
 */
export function canView(viewer: ScopeViewer, obj: OwnedObject): boolean {
  if (viewer.isAdmin) return true;
  if (obj.isBuiltIn) return true;
  if (tenantOf(obj.tenantId) !== tenantOf(viewer.tenantId)) return false;
  if (viewer.isTenantAdmin) return true;
  if (obj.ownershipScope === 'TenantWide') return true;
  return userOf(obj.userId) === userOf(viewer.userId);
}

/**
 * Whether the ownership rule lets the viewer change or delete a record. Global administrators change
 * everything; nobody else crosses a tenant boundary. Tenant administrators change their whole tenant;
 * otherwise only a user-specific record owned by the viewer.
 */
export function canEdit(viewer: ScopeViewer, obj: OwnedObject): boolean {
  if (viewer.isAdmin) return true;
  if (tenantOf(obj.tenantId) !== tenantOf(viewer.tenantId)) return false;
  if (viewer.isTenantAdmin) return true;
  if (obj.ownershipScope !== 'UserSpecific') return false;
  return userOf(obj.userId) === userOf(viewer.userId);
}

/** Whether the viewer holds the role a record type's write routes require. */
export function canWrite(viewer: ScopeViewer, level: OwnedRecordWriteLevel): boolean {
  if (level === 'AdminOnly') return viewer.isAdmin;
  if (level === 'TenantAdmin') return viewer.isAdmin || viewer.isTenantAdmin;
  return true;
}

/** Whether both the role gate and the ownership rule let the viewer change a record. */
export function canEditOwned(viewer: ScopeViewer, obj: OwnedObject, level: OwnedRecordWriteLevel): boolean {
  return canWrite(viewer, level) && canEdit(viewer, obj);
}

/**
 * The scope a new record should carry for this viewer. Regular users own user-specific records only;
 * administrators may choose and default to tenant-wide.
 */
export function resolveCreateScope(viewer: ScopeViewer, requested?: ScopeEnum | null): ScopeEnum {
  if (viewer.isAdmin || viewer.isTenantAdmin) return requested ?? 'TenantWide';
  return 'UserSpecific';
}

/** Whether the viewer may choose a new record's ownership scope (administrators only). */
export function canChooseScope(viewer: ScopeViewer): boolean {
  return viewer.isAdmin || viewer.isTenantAdmin;
}

/** Human-readable label for a scope value. */
export function scopeLabel(scope: ScopeEnum): string {
  return scope === 'UserSpecific' ? 'Personal' : 'Tenant-wide';
}
