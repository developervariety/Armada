#!/usr/bin/env python3
"""Prune expired CLI output files and abandoned snapshot caches."""
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
    receipt = {'apply': args.apply, 'candidates': [], 'removed': [], 'skipped': [], 'errors': []}
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

        session_homes = [home / '.codex', home / '.claude']
        accounts = home / '.armada/accounts'
        if safe_tree(accounts) and accounts.is_dir():
            session_homes += [p for p in accounts.iterdir() if p.is_dir() and safe_tree(p)]
        for session_home in session_homes:
            for name in ('sessions', 'projects'):
                root = session_home / name
                if not safe_tree(root): continue
                for path in regular_files(root):
                    if path.suffix == '.jsonl':
                        prune_file(path, now - args.output_retention_days * 86400, 'agent_session')
        output_roots = [home / '.local/share/opencode']
        output_roots += [p / 'opencode' for p in session_homes if p.parent == accounts]
        for root in output_roots:
            for name in ('log', 'tool-output'):
                output = root / name
                if not safe_tree(output): continue
                for path in regular_files(output):
                    prune_file(path, now - args.output_retention_days * 86400, 'opencode_output')
        chats = home / '.cursor/chats'
        if safe_tree(chats) and chats.is_dir():
            for chat in chats.iterdir():
                if not chat.is_dir() or not safe_tree(chat) or protected(chat): continue
                entries = [chat]
                for folder, dirs, files in os.walk(chat, followlinks=False):
                    entries.extend(Path(folder) / name for name in dirs + files)
                try:
                    stats = [p.lstat() for p in entries]
                    if any(not (stat.S_ISREG(s.st_mode) or stat.S_ISDIR(s.st_mode)) or s.st_mtime >= now - args.output_retention_days * 86400 for s in stats): continue
                    item = {'kind': 'cursor_chat', 'path': str(chat), 'bytes': sum(s.st_size for s in stats if stat.S_ISREG(s.st_mode))}
                    receipt['candidates'].append(item)
                    if args.apply:
                        latest_active, _, _, latest_errors = scan_processes(home.stat().st_uid)
                        if latest_errors or any(p == str(chat) or p.startswith(str(chat) + '/') for p in latest_active): continue
                        if any((p.lstat().st_ino, p.lstat().st_mtime_ns) != (s.st_ino, s.st_mtime_ns) for p, s in zip(entries, stats)): continue
                        shutil.rmtree(chat)
                        receipt['removed'].append(item)
                except OSError as exc:
                    receipt['errors'].append({'path': str(chat), 'error': str(exc)})

        projects = home / '.cursor/projects'
        if safe_tree(projects) and projects.is_dir():
            for project in projects.iterdir():
                if not project.is_dir() or not safe_tree(project): continue
                prune_file(project / 'worker.log', now - args.output_retention_days * 86400, 'cursor_output')
                for name in ('agent-tools', 'agent-transcripts', 'terminals'):
                    for path in regular_files(project / name):
                        prune_file(path, now - args.output_retention_days * 86400, 'cursor_output')

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


def prune_maintenance_directories(home, args, receipt):
    """Remove only expired, marked private maintenance directories."""
    uid = home.stat().st_uid
    base = home / '.armada'
    marker_name = '.armada-opencode-maintenance.json'
    kind = 'armada-opencode-maintenance'
    cutoff = time.time() - args.maintenance_retention_hours * 3600
    receipt['maintenance_cleanup'] = {'candidates': [], 'removed': [], 'candidates_count': 0, 'removed_count': 0, 'candidates_bytes': 0, 'removed_bytes': 0}
    if not base.is_dir() or any(p.is_symlink() for p in [base, *base.parents]): return

    def referenced(directory, active):
        return any(p == str(directory) or p.startswith(str(directory) + '/') for p in active)

    for directory in base.glob('armada-opencode-maintenance-*'):
        try:
            info = directory.lstat()
            if not stat.S_ISDIR(info.st_mode) or info.st_uid != uid or info.st_mode & 0o077: continue
            marker = directory / marker_name
            marker_info = marker.lstat()
            if not stat.S_ISREG(marker_info.st_mode) or marker_info.st_uid not in (uid, 0) or marker_info.st_mode & 0o077: continue
            metadata = json.loads(marker.read_text())
            if metadata.get('kind') != kind or metadata.get('version') != 1 or metadata.get('owner_uid') != uid: continue
            if not isinstance(metadata.get('created_at'), (int, float)) or metadata['created_at'] >= cutoff: continue
            entries = [directory]
            for folder, dirs, files in os.walk(directory, followlinks=False):
                entries.extend(Path(folder) / name for name in dirs + files)
            stats = [p.lstat() for p in entries]
            if any(not (stat.S_ISREG(s.st_mode) or stat.S_ISDIR(s.st_mode)) or s.st_mtime >= cutoff for s in stats): continue
            active, names, _, errors = scan_processes(uid)
            if errors:
                receipt['errors'].append({'path': str(directory), 'error': 'process visibility incomplete for maintenance cleanup'}); continue
            if any('opencode' in name.lower() for name in names) or referenced(directory, active): continue
            item = {'path': str(directory), 'bytes': sum(s.st_size for s in stats if stat.S_ISREG(s.st_mode))}
            receipt['maintenance_cleanup']['candidates'].append(item)
            if args.apply:
                latest_active, latest_names, _, latest_errors = scan_processes(uid)
                if latest_errors or any('opencode' in name.lower() for name in latest_names) or referenced(directory, latest_active): continue
                if any((p.lstat().st_ino, p.lstat().st_mtime_ns) != (s.st_ino, s.st_mtime_ns) for p, s in zip(entries, stats)): continue
                shutil.rmtree(directory)
                receipt['maintenance_cleanup']['removed'].append(item)
        except FileNotFoundError:
            continue
        except (OSError, ValueError, TypeError) as exc:
            receipt['errors'].append({'path': str(directory), 'error': str(exc)})
    for key in ('candidates', 'removed'):
        receipt['maintenance_cleanup'][key + '_count'] = len(receipt['maintenance_cleanup'][key])
        receipt['maintenance_cleanup'][key + '_bytes'] = sum(item['bytes'] for item in receipt['maintenance_cleanup'][key])


def prune_opencode_databases(home, args, receipt):
    """Use native deletion, then reclaim only verified free SQLite pages."""
    import gzip
    import subprocess
    import tempfile
    prune_maintenance_directories(home, args, receipt)
    roots = [home / '.local/share/opencode']
    accounts = home / '.armada/accounts'
    if accounts.is_dir() and not accounts.is_symlink():
        roots += [p / 'opencode' for p in accounts.iterdir() if p.name.startswith('opencode-') and p.is_dir() and not p.is_symlink()]
    uid, gid = home.stat().st_uid, home.stat().st_gid
    cutoff = int((time.time() - args.output_retention_days * 86400) * 1000)
    receipt['opencode_databases'] = []
    backups = Path(args.opencode_backup_dir) if args.opencode_backup_dir else home / '.armada/backups/opencode'
    if any(p.is_symlink() for p in [backups, *backups.parents]):
        receipt['errors'].append({'path': str(backups), 'error': 'linked backup path'}); return
    if args.apply and backups.is_dir():
        for old in backups.glob('opencode-*.sqlite.gz'):
            if old.is_file() and not old.is_symlink() and old.stat().st_mtime < time.time() - args.opencode_backup_retention_days * 86400:
                old.unlink()

    if args.apply and backups.is_dir():
        stale_parts = [p for p in backups.glob('opencode-*.sqlite.gz.part')
                       if p.is_file() and not p.is_symlink() and p.stat().st_mtime < time.time() - args.maintenance_retention_hours * 3600]
        if stale_parts:
            active, names, _, errors = scan_processes(uid)
            if errors:
                receipt['errors'].append({'path': str(backups), 'error': 'process visibility incomplete for partial backup cleanup'})
            elif not any('opencode' in name.lower() for name in names):
                for partial in stale_parts:
                    if str(partial) not in active:
                        current = partial.lstat()
                        if stat.S_ISREG(current.st_mode) and current.st_mtime < time.time() - args.maintenance_retention_hours * 3600:
                            partial.unlink()

    def readonly(db):
        return sqlite3.connect(db.as_uri() + '?mode=ro', uri=True, timeout=5)

    def idle(db):
        active, names, _, errors = scan_processes(uid)
        if errors: raise RuntimeError('process visibility incomplete')
        if any('opencode' in name.lower() for name in names): return False
        return not any(str(db) + suffix in active for suffix in ('', '-wal', '-shm', '-journal'))

    def tree(conn, root_id):
        return conn.execute("WITH RECURSIVE subtree(id,time_updated) AS (SELECT id,time_updated FROM session WHERE id=? UNION SELECT s.id,s.time_updated FROM session s JOIN subtree t ON s.parent_id=t.id) SELECT id,MAX(time_updated,COALESCE((SELECT MAX(MAX(time_created,time_updated)) FROM message WHERE session_id=subtree.id),0),COALESCE((SELECT MAX(MAX(time_created,time_updated)) FROM part WHERE session_id=subtree.id),0),COALESCE((SELECT MAX(CASE WHEN json_type(data,'$.time') IN ('integer','real') THEN json_extract(data,'$.time') ELSE 0 END) FROM event WHERE aggregate_id=subtree.id),0)) FROM subtree", (root_id,)).fetchall()

    for root in roots:
        db = root / 'opencode.db'
        if not db.is_file() or any(p.is_symlink() for p in [db, *db.parents]): continue
        result = {'path': str(db), 'candidate_roots': [], 'deleted_roots': [], 'deleted_sessions': 0, 'freed_bytes': 0}
        receipt['opencode_databases'].append(result)
        try:
            if not idle(db):
                result['skipped'] = 'opencode_or_database_active'; continue
            with closing(readonly(db)) as conn:
                before_count = conn.execute('SELECT count(*) FROM session').fetchone()[0]
                candidates = [row[0] for row in conn.execute('SELECT id FROM session WHERE parent_id IS NULL AND time_updated < ?', (cutoff,))]
                for root_id in candidates:
                    rows = tree(conn, root_id)
                    if rows and all(updated < cutoff for _, updated in rows):
                        result['candidate_roots'].append({'id': root_id, 'session_count': len(rows)})
            result['before_sessions'] = before_count
            if not args.apply or not result['candidate_roots']: continue
            if not Path(args.opencode_cli).is_file(): raise RuntimeError('OpenCode CLI missing')
            if any(p.is_symlink() for p in [home / '.armada', *(home / '.armada').parents]): raise RuntimeError('linked maintenance path')
            (home / '.armada').mkdir(parents=True, exist_ok=True, mode=0o700)
            if os.getuid() == 0: os.chown(home / '.armada', uid, gid)
            # CLI state is isolated. The database override is exact; plugins stay disabled.
            with tempfile.TemporaryDirectory(prefix='armada-opencode-maintenance-', dir=str(home / '.armada')) as temp:
                temporary = Path(temp)
                if os.getuid() == 0: os.chown(temporary, uid, gid)
                marker = temporary / '.armada-opencode-maintenance.json'
                marker.write_text(json.dumps({'kind': 'armada-opencode-maintenance', 'version': 1, 'owner_uid': uid, 'created_at': time.time()}))
                os.chmod(marker, 0o600)
                env = dict(PATH='/usr/local/bin:/usr/bin:/bin', LANG='C.UTF-8', HOME=str(home), XDG_DATA_HOME=str(root.parent), OPENCODE_DB=str(db),
                           XDG_CONFIG_HOME=str(temporary / 'config'), XDG_CACHE_HOME=str(temporary / 'cache'),
                           XDG_STATE_HOME=str(temporary / 'state'), OPENCODE_DISABLE_CHANNEL_DB='1', OPENCODE_DISABLE_MODELS_FETCH='1', OPENCODE_DISABLE_AUTOUPDATE='1')
                def native(*command):
                    kwargs = {'user': uid, 'group': gid, 'extra_groups': []} if os.getuid() == 0 else {}
                    return subprocess.run([args.opencode_cli, '--pure', *command], env=env, cwd=str(home),
                                          text=True, capture_output=True, timeout=90, check=True, **kwargs).stdout.strip()
                if not idle(db): raise RuntimeError('OpenCode became active before native routing check')
                routed = native('db', 'path')
                if routed != str(db): raise RuntimeError('native database path mismatch')
                result['cli'] = str(args.opencode_cli)
                result['cli_version'] = native('--version')
                # Back up with SQLite's snapshot API before any native delete.
                backups = Path(args.opencode_backup_dir) if args.opencode_backup_dir else home / '.armada/backups/opencode'
                if any(p.is_symlink() for p in [backups, *backups.parents]): raise RuntimeError('linked backup path')
                backups.mkdir(parents=True, exist_ok=True, mode=0o700)
                label = 'default' if root == roots[0] else root.parent.name
                stamp = time.strftime('%Y%m%dT%H%M%SZ', time.gmtime())
                backup = backups / ('opencode-' + label + '-' + stamp + '.sqlite.gz')
                if backup.exists(): raise RuntimeError('backup path already exists')
                stage = temporary / 'backup.sqlite'
                with closing(readonly(db)) as src, closing(sqlite3.connect(stage)) as dst:
                    src.backup(dst)
                    if dst.execute('PRAGMA integrity_check').fetchone()[0] != 'ok': raise RuntimeError('backup integrity check failed')
                partial = backup.with_suffix(backup.suffix + '.part')
                partial_created = False
                try:
                    with open(stage, 'rb') as src, open(partial, 'xb') as raw:
                        partial_created = True
                        os.chmod(partial, 0o600)
                        with gzip.GzipFile(fileobj=raw, mode='wb', compresslevel=6) as zipped:
                            shutil.copyfileobj(src, zipped, 1024 * 1024)
                        raw.flush(); os.fsync(raw.fileno())
                    os.replace(partial, backup)
                finally:
                    # A failed compression must not leave this run's unfinished backup.
                    if partial_created:
                        try:
                            partial.unlink()
                        except FileNotFoundError:
                            pass
                stage.unlink()
                result['backup'] = str(backup)
                result['backup_bytes'] = backup.stat().st_size
                result['native_mode'] = 'cli'
                for candidate in result['candidate_roots']:
                    if not idle(db): raise RuntimeError('OpenCode became active before deletion')
                    with closing(readonly(db)) as conn:
                        rows = tree(conn, candidate['id'])
                    if not rows or any(updated >= cutoff for _, updated in rows):
                        receipt['skipped'].append({'path': str(db), 'id': candidate['id'], 'reason': 'session_tree_changed'}); continue
                    native('session', 'delete', candidate['id'])
                    with closing(readonly(db)) as conn:
                        for session_id, _ in rows:
                            if conn.execute('SELECT count(*) FROM session WHERE id=?', (session_id,)).fetchone()[0] or conn.execute('SELECT count(*) FROM event WHERE aggregate_id=?', (session_id,)).fetchone()[0] or conn.execute('SELECT count(*) FROM event_sequence WHERE aggregate_id=?', (session_id,)).fetchone()[0]:
                                raise RuntimeError('native delete did not remove session and event ledger')
                    result['deleted_roots'].append(candidate['id'])
                    result['deleted_sessions'] += len(rows)
                    if len(result['deleted_roots']) % 100 == 0:
                        print('OpenCode expired roots removed: ' + str(len(result['deleted_roots'])), file=sys.stderr, flush=True)
                if not idle(db): raise RuntimeError('OpenCode became active before database maintenance')
                old_size = db.stat().st_size
                with closing(sqlite3.connect(db, timeout=5)) as conn:
                    checkpoint = conn.execute('PRAGMA wal_checkpoint(TRUNCATE)').fetchone()
                    if checkpoint[0] != 0: raise RuntimeError('database checkpoint busy')
                    if conn.execute('PRAGMA integrity_check').fetchone()[0] != 'ok': raise RuntimeError('database integrity check failed')
                    free = conn.execute('PRAGMA freelist_count').fetchone()[0]
                    pages = conn.execute('PRAGMA page_count').fetchone()[0]
                    page_size = conn.execute('PRAGMA page_size').fetchone()[0]
                    if free * page_size >= 64 * 1024 * 1024 and free >= pages * 0.10:
                        # No rows are removed by VACUUM. Native commands own all deletion.
                        conn.execute('VACUUM')
                        result['vacuumed'] = True
                    if conn.execute('PRAGMA integrity_check').fetchone()[0] != 'ok': raise RuntimeError('post-maintenance integrity check failed')
                    result['after_sessions'] = conn.execute('SELECT count(*) FROM session').fetchone()[0]
                result['freed_bytes'] = max(0, old_size - db.stat().st_size)
        except (OSError, sqlite3.Error, subprocess.SubprocessError, RuntimeError) as exc:
            receipt['errors'].append({'path': str(db), 'error': str(exc)})


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
