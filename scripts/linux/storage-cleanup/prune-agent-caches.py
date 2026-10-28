#!/usr/bin/env python3
"""Prune disposable CLI caches; leave raw history to verified archival."""
import argparse
from contextlib import closing
import fcntl
import json
import os
from pathlib import Path
import shutil
import sqlite3
import stat
import sys
import time

from cleanup_config import add_config_argument, load_config

DEFAULTS = {
    'lockPath': '/run/lock/armada-prune-agent-caches.lock',
    'opencodeCli': '/usr/local/bin/opencode',
    'opencodeBackupDirectory': None,
    'outputRetentionDays': 30,
    'nugetHttpCacheRetentionDays': 7,
    'opencodeBackupRetentionDays': 7,
    'maintenanceRetentionHours': 48,
}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--apply', action='store_true')
    parser.add_argument('--sessions-only', action='store_true')
    parser.add_argument('--opencode-db-only', action='store_true')
    parser.add_argument('--opencode-cli', help='Overrides agentCaches.opencodeCli')
    parser.add_argument('--opencode-backup-dir', type=Path, help='Overrides agentCaches.opencodeBackupDirectory')
    parser.add_argument('--home', type=Path, help='Overrides agentCaches.home')
    parser.add_argument('--lock', type=Path, help='Overrides agentCaches.lockPath')
    add_config_argument(parser)
    args = parser.parse_args()
    settings = load_config(args.config, 'agentCaches', ['home'], DEFAULTS)
    args.home = args.home or Path(settings['home'])
    args.lock = args.lock or Path(settings['lockPath'])
    args.opencode_cli = args.opencode_cli or settings['opencodeCli']
    if args.opencode_backup_dir is None and settings['opencodeBackupDirectory']:
        args.opencode_backup_dir = Path(settings['opencodeBackupDirectory'])
    args.output_retention_days = float(settings['outputRetentionDays'])
    args.nuget_cache_retention_days = float(settings['nugetHttpCacheRetentionDays'])
    args.opencode_backup_retention_days = float(settings['opencodeBackupRetentionDays'])
    args.maintenance_retention_hours = float(settings['maintenanceRetentionHours'])
    home = args.home.absolute()
    receipt = {'apply': args.apply, 'candidates': [], 'removed': [], 'skipped': [{'reason': 'history_archive_owned'}], 'errors': []}
    lock_fd = os.open(args.lock, os.O_CREAT | os.O_WRONLY | os.O_NOFOLLOW, 0o600)
    with os.fdopen(lock_fd, 'w') as lock:
        try:
            fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        except BlockingIOError:
            receipt['skipped'].append({'reason': 'cleanup_already_running'})
            print(json.dumps(receipt)); return 1 if receipt['errors'] else 0
        active, process_names, build_active, scan_errors = scan_processes(home.stat().st_uid)
        if scan_errors:
            receipt['errors'] += scan_errors
            receipt['skipped'].append({'reason': 'process_scan_incomplete'})
            print(json.dumps(receipt)); return 1 if receipt['errors'] else 0
        now = time.time()
        if args.opencode_db_only:
            prune_opencode_databases(home, args, receipt)
            print(json.dumps(receipt)); return 1 if receipt['errors'] else 0

        def protected(path):
            path = str(path)
            return any(p == path or p.startswith(path + '/') for p in active)

        def safe_tree(root):
            # Reject a link in any ancestor, including an overridden home.
            return all(not p.is_symlink() for p in [root, *root.parents])

        def prune_file(path, cutoff, kind):
            if kind != 'nuget_http_cache':
                receipt['errors'].append({'path': str(path), 'error': 'Only disposable HTTP cache files may be removed'})
                return
            try:
                if not safe_tree(path) or protected(path): return
                s = path.lstat()
                if not stat.S_ISREG(s.st_mode) or s.st_mtime >= cutoff: return
                item = {'kind': kind, 'path': str(path), 'bytes': s.st_size}
                receipt['candidates'].append(item)
                if args.apply:
                    current = path.lstat()
                    if (current.st_ino, current.st_dev, current.st_mtime_ns, current.st_size) != (s.st_ino, s.st_dev, s.st_mtime_ns, s.st_size):
                        receipt['skipped'].append({'path': str(path), 'reason': 'changed_during_scan'}); return
                    path.unlink()
                    receipt['removed'].append(item)
            except FileNotFoundError:
                pass
            except OSError as exc:
                receipt['errors'].append({'path': str(path), 'error': str(exc)})

        if args.sessions_only:
            receipt['candidate_bytes'] = sum(x['bytes'] for x in receipt['candidates'])
            receipt['removed_bytes'] = sum(x['bytes'] for x in receipt['removed'])
            print(json.dumps(receipt)); return 1 if receipt['errors'] else 0

        roots = [home / '.local/share/opencode']
        accounts = home / '.armada/accounts'
        if safe_tree(accounts) and accounts.is_dir():
            roots += [p / 'opencode' for p in accounts.iterdir() if p.name.startswith('opencode-') and p.is_dir() and safe_tree(p)]
        opencode_active = any('opencode' in name.lower() for name in process_names)
        for root in roots:
            snapshots = root / 'snapshot'
            db = root / 'opencode.db'
            if not safe_tree(snapshots) or not snapshots.is_dir(): continue
            if opencode_active:
                receipt['skipped'].append({'path': str(snapshots), 'reason': 'opencode_process_active'}); continue
            if not safe_tree(db) or not db.is_file(): continue
            try:
                with closing(sqlite3.connect(db.as_uri() + '?mode=ro', uri=True)) as conn:
                    projects_by_id = dict(conn.execute('SELECT id, worktree FROM project'))
            except (sqlite3.Error, OSError) as exc:
                receipt['errors'].append({'path': str(db), 'error': str(exc)}); continue
            for snapshot in snapshots.iterdir():
                if snapshot.name == 'global' or not snapshot.is_dir() or not safe_tree(snapshot): continue
                worktree = projects_by_id.get(snapshot.name)
                if not worktree or not os.path.isabs(worktree) or os.path.lexists(worktree) or protected(snapshot): continue
                # Dock roots are shared with the admiral. Host absence is meaningful only there.
                if not Path(worktree).is_relative_to(home / '.armada/docks') or '..' in Path(worktree).parts: continue
                # Every entry must be old. A link or recent file blocks the whole tree.
                entries = [snapshot]
                for folder, dirs, files in os.walk(snapshot, followlinks=False):
                    entries.extend(Path(folder) / name for name in dirs + files)
                try:
                    stats = [p.lstat() for p in entries]
                    if any(stat.S_ISLNK(s.st_mode) or s.st_mtime >= now - args.output_retention_days * 86400 for s in stats): continue
                    size = sum(s.st_size for s in stats if stat.S_ISREG(s.st_mode))
                    item = {'kind': 'opencode_orphan_snapshot', 'path': str(snapshot), 'bytes': size}
                    receipt['candidates'].append(item)
                    if args.apply:
                        # Re-check process state and source worktree immediately before removal.
                        latest_active, latest_names, _, latest_errors = scan_processes(home.stat().st_uid)
                        if latest_errors or any('opencode' in name.lower() for name in latest_names) or os.path.lexists(worktree) or any(p == str(snapshot) or p.startswith(str(snapshot) + '/') for p in latest_active):
                            receipt['skipped'].append({'path': str(snapshot), 'reason': 'activity_changed'}); continue
                        with closing(sqlite3.connect(db.as_uri() + '?mode=ro', uri=True)) as conn:
                            association = conn.execute('SELECT worktree FROM project WHERE id=?', (snapshot.name,)).fetchone()
                        if association != (worktree,): continue
                        if any((p.lstat().st_ino, p.lstat().st_mtime_ns) != (s.st_ino, s.st_mtime_ns) for p, s in zip(entries, stats)): continue
                        shutil.rmtree(snapshot)
                        receipt['removed'].append(item)
                except (OSError, ValueError, sqlite3.Error) as exc:
                    receipt['errors'].append({'path': str(snapshot), 'error': str(exc)})

        prune_opencode_databases(home, args, receipt)

        http_cache = home / '.local/share/NuGet/http-cache'
        if build_active:
            receipt['skipped'].append({'path': str(http_cache), 'reason': 'restore_or_build_active'})
        elif safe_tree(http_cache):
            for path in regular_files(http_cache):
                prune_file(path, now - args.nuget_cache_retention_days * 86400, 'nuget_http_cache')
        receipt['candidate_bytes'] = sum(x['bytes'] for x in receipt['candidates'])
        receipt['removed_bytes'] = sum(x['bytes'] for x in receipt['removed'])
        print(json.dumps(receipt))
        return 1 if receipt['errors'] else 0


def prune_opencode_databases(home, args, receipt):
    """Retain native history, backups and maintenance copies for the archive owner."""
    receipt['opencode_databases'] = []
    receipt['skipped'].append({'reason': 'history_archive_owned', 'scope': 'opencode_databases_and_backups'})


def regular_files(root):
    if not root.is_dir() or root.is_symlink(): return
    for folder, dirs, files in os.walk(root, followlinks=False):
        dirs[:] = [name for name in dirs if not (Path(folder) / name).is_symlink()]
        for name in files:
            p = Path(folder) / name
            if not p.is_symlink(): yield p


def scan_processes(target_uid):
    active, names, errors = set(), [], []
    build_active = False
    uids = {target_uid, 0} if os.getuid() == 0 else {target_uid}
    for proc in Path('/proc').iterdir():
        if not proc.name.isdigit(): continue
        try:
            if proc.stat().st_uid not in uids: continue
            comm = (proc / 'comm').read_text().strip()
            names.append(comm)
            cmd = (proc / 'cmdline').read_bytes().split(b'\0')
            tokens = [t.decode(errors='replace') for t in cmd]
            for token in tokens:
                value = token.split('=', 1)[1] if token.startswith('--') and '=' in token else token
                if value.startswith('/'):
                    active.add(os.path.normpath(value))
            if comm.lower() in ('dotnet', 'msbuild', 'nuget') and any(t.lower() in ('build', 'restore', 'msbuild', 'run', 'test', 'publish', 'pack') or 'msbuild.dll' in t.lower() or 'vbcscompiler.dll' in t.lower() for t in tokens):
                build_active = True
            cwd = os.readlink(proc / 'cwd')
            if cwd.startswith('/'): active.add(cwd.removesuffix(' (deleted)'))
            for fd in (proc / 'fd').iterdir():
                try:
                    target = os.readlink(fd)
                    if target.startswith('/'): active.add(target.removesuffix(' (deleted)'))
                except FileNotFoundError: pass
            for line in (proc / 'maps').read_text().splitlines():
                fields = line.split(maxsplit=5)
                if len(fields) == 6 and fields[5].startswith('/'):
                    active.add(fields[5].removesuffix(' (deleted)'))
        except (FileNotFoundError, ProcessLookupError):
            continue
        except (PermissionError, OSError) as exc:
            errors.append({'pid': proc.name, 'error': str(exc)})
    return active, names, build_active, errors


if __name__ == '__main__':
    try:
        sys.exit(main())
    except Exception as exc:
        print(json.dumps({'errors': [{'error': str(exc)}], 'apply': '--apply' in sys.argv, 'aborted': True}))
        sys.exit(1)
