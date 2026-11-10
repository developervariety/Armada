#!/usr/bin/env python3
"""Remove only inactive, reproducible build outputs, isolated NuGet caches and idle scratch folders."""
import argparse
import datetime
import errno
import fcntl
import glob
import json
import os
from pathlib import Path
import shutil
import stat
import subprocess
import sys
import time

from cleanup_config import add_config_argument, load_config

DEFAULTS = {
    'exclusions': [],
    'buildRetentionHours': 24,
    'cacheRetentionHours': 48,
    'lockPath': '/run/lock/armada-storage-cleanup.lock',
    'diskUsagePath': '/',
    'scratchRoots': [],
    'nugetCachePatterns': [],
    'lowSpaceThresholdGb': 0,
    'lowSpaceCacheRetentionHours': 48,
    'wholeFolderRetentionDays': 3,
}
KEEP_MARKER = '.armada-keep'


def within(path, parent):
    return path == parent or path.startswith(parent.rstrip('/') + '/')


def plain_path(path):
    """Reject symlink components, including a symlink above a configured root."""
    p = Path(path)
    for part in [p, *p.parents]:
        if part.is_symlink():
            raise ValueError('Symlink path: ' + str(part))
    return p


def inventory(path, internal_symlinks=False):
    p = plain_path(path)
    initial = p.lstat()
    if not stat.S_ISDIR(initial.st_mode):
        raise ValueError('Not a directory: ' + str(p))
    total = 0
    newest = initial.st_mtime
    for base, dirs, files in os.walk(p, followlinks=False, onerror=raise_error):
        for name in dirs + files:
            entry = Path(base) / name
            st = entry.lstat()
            if stat.S_ISLNK(st.st_mode):
                if not internal_symlinks or not within(str(entry.resolve()), str(p)):
                    raise ValueError('Symlink in candidate: ' + str(entry))
                newest = max(newest, st.st_mtime)
                continue
            if not (stat.S_ISDIR(st.st_mode) or stat.S_ISREG(st.st_mode)):
                raise ValueError('Special file in candidate: ' + str(entry))
            newest = max(newest, st.st_mtime)
            if stat.S_ISREG(st.st_mode):
                total += st.st_blocks * 512
    return {'bytes': total, 'newest_mtime': newest,
            'device': initial.st_dev, 'inode': initial.st_ino}


def raise_error(error):
    raise error


def folder_inventory(path):
    """Measure a whole scratch folder by its regular files; directory mtimes never count."""
    p = plain_path(path)
    initial = p.lstat()
    if not stat.S_ISDIR(initial.st_mode):
        raise ValueError('Not a directory: ' + str(p))
    total = 0
    newest = None
    keep = False
    for base, dirs, files in os.walk(p, followlinks=False, onerror=raise_error):
        for name in dirs + files:
            entry = Path(base) / name
            st = entry.lstat()
            if stat.S_ISLNK(st.st_mode):
                # A link is removed as a link, but one that leaves the folder blocks it.
                if not within(os.path.realpath(entry), str(p)):
                    raise ValueError('Symlink in candidate: ' + str(entry))
                continue
            if not (stat.S_ISDIR(st.st_mode) or stat.S_ISREG(st.st_mode)):
                raise ValueError('Special file in candidate: ' + str(entry))
            if stat.S_ISREG(st.st_mode):
                keep = keep or name == KEEP_MARKER
                newest = st.st_mtime if newest is None else max(newest, st.st_mtime)
                total += st.st_blocks * 512
    # A folder without files ages from its own mtime.
    return {'bytes': total, 'newest_mtime': initial.st_mtime if newest is None else newest,
            'keep': keep, 'device': initial.st_dev, 'inode': initial.st_ino}


def kept(path):
    """An explicit keep marker on the path or any ancestor protects its contents."""
    p = Path(path)
    return any((parent / KEEP_MARKER).exists() for parent in [p, *p.parents])


def nuget_cache(path):
    """Require NuGet completion records for every package version, never a source tree."""
    packages = list(Path(path).iterdir())
    if not packages:
        return False
    for package in packages:
        if not package.is_dir() or package.is_symlink():
            return False
        versions = list(package.iterdir())
        if not versions:
            return False
        for version in versions:
            if not version.is_dir() or version.is_symlink():
                return False
            if not (version / '.nupkg.metadata').is_file() or not list(version.glob('*.nupkg.sha512')):
                return False
    return True


def prune_nuget_caches(patterns, roots, excludes, hours, apply, report, skip, read_process_paths):
    """Retire complete isolated package caches; retain the surrounding source and proof."""
    seen = set()
    for pattern in patterns:
        for value in sorted(glob.glob(pattern)):
            p = str(Path(value).absolute())
            scope = str(Path(p).parent)
            if p in seen:
                continue
            seen.add(p)
            if not any(within(scope, root) and scope != root for root in roots):
                skip('cache_outside_roots')
                continue
            if any(within(p, entry) or within(entry, p) for entry in excludes):
                skip('excluded')
                continue
            try:
                plain_path(p)
                if kept(p) or any(Path(scope).rglob(KEEP_MARKER)):
                    skip('keep_marker')
                    continue
                if not nuget_cache(p):
                    skip('not_complete_nuget_cache')
                    continue
                info = folder_inventory(p)
                if info['keep'] or info['newest_mtime'] > time.time() - hours * 3600:
                    skip('recent_cache')
                    continue
                if active(scope, read_process_paths()):
                    skip('active')
                    continue
                candidate = dict(path=p, kind='isolated_nuget_cache', scope=scope,
                                 **{k: v for k, v in info.items() if k != 'keep'})
                report['candidates'].append(candidate)
                if apply:
                    current = folder_inventory(p)
                    if current != info or kept(p) or any(Path(scope).rglob(KEEP_MARKER)):
                        skip('changed_before_delete')
                        continue
                    if not nuget_cache(p) or active(scope, read_process_paths()):
                        skip('became_active_or_incomplete')
                        continue
                    if not shutil.rmtree.avoids_symlink_attacks:
                        raise RuntimeError('This Python lacks safe directory removal')
                    shutil.rmtree(p)
                    report['removed'].append(candidate)
            except ValueError as error:
                if 'Symlink' in str(error):
                    skip('symlink')
                else:
                    report['failures'].append({'path': p, 'error': str(error)})
            except Exception as error:
                report['failures'].append({'path': p, 'error': str(error)})


def prune_scratch(scratch_roots, retention_days, excludes, apply, report, skip, read_process_paths):
    """Remove whole top-level scratch folders whose newest file is older than the retention.

    Returns the candidate paths so the build-output walk does not count them twice.
    """
    handled = set()
    if not scratch_roots:
        return handled

    def guarded(path):
        # An exclusion inside the folder protects the whole folder.
        return any(within(path, entry) or within(entry, path) for entry in excludes)

    cutoff_seconds = float(retention_days) * 86400
    paths = read_process_paths()
    for root in scratch_roots:
        if not Path(root).exists():
            skip('missing_scratch_root')
            continue
        if kept(root):
            skip('keep_marker')
            continue
        if any(within(root, entry) for entry in excludes):
            skip('excluded')
            continue
        for child in sorted(Path(root).iterdir()):
            p = str(child)
            if child.name.startswith('._'):
                continue
            if child.is_symlink():
                skip('symlink')
                continue
            if not child.is_dir():
                continue
            if guarded(p):
                skip('excluded')
                continue
            try:
                info = folder_inventory(child)
                if info['keep']:
                    skip('keep_marker')
                    continue
                if info['newest_mtime'] > time.time() - cutoff_seconds:
                    skip('recent_scratch')
                    continue
                if active(p, paths):
                    skip('active')
                    continue
                candidate = dict(path=p, kind='scratch_folder', scope=p, **{k: v for k, v in info.items() if k != 'keep'})
                report['candidates'].append(candidate)
                handled.add(p)
                if apply:
                    current = folder_inventory(child)
                    if current['device'] != info['device'] or current['inode'] != info['inode']:
                        raise RuntimeError('Candidate identity changed')
                    if current['keep']:
                        skip('became_kept')
                        continue
                    if current['newest_mtime'] > time.time() - cutoff_seconds:
                        skip('became_recent')
                        continue
                    if active(p, read_process_paths()):
                        skip('became_active')
                        continue
                    if not shutil.rmtree.avoids_symlink_attacks:
                        raise RuntimeError('This Python lacks safe directory removal')
                    shutil.rmtree(child)
                    report['removed'].append(candidate)
            except ValueError as error:
                if 'Symlink' in str(error):
                    skip('symlink')
                else:
                    report['failures'].append({'path': p, 'error': str(error)})
            except Exception as error:
                report['failures'].append({'path': p, 'error': str(error)})
    return handled


def git(base, *args):
    return subprocess.run(['git', '-c', 'safe.directory=*', '-C', str(base), *args],
                          capture_output=True, text=True, timeout=60)


def git_guard(path):
    p = Path(path)
    result = git(p.parent, 'rev-parse', '--show-toplevel')
    if result.returncode:
        return None, 'no_git_repository'
    top = str(plain_path(result.stdout.strip()))
    rel = str(p.relative_to(top))
    tracked = git(top, 'ls-files', '-z', '--', rel)
    if tracked.returncode:
        raise RuntimeError('git ls-files failed: ' + tracked.stderr.strip())
    if tracked.stdout:
        return None, 'tracked_files'
    ignored = git(top, 'check-ignore', '-q', '--', rel)
    if ignored.returncode not in (0, 1):
        raise RuntimeError('git check-ignore failed: ' + ignored.stderr.strip())
    if ignored.returncode:
        return None, 'not_ignored'
    return top, None


def process_paths():
    """Read host and container processes from the host PID namespace; fail closed."""
    result = []
    vanished = (errno.ENOENT, errno.ESRCH, errno.ENOTDIR)
    for proc in Path('/proc').glob('[0-9]*'):
        if int(proc.name) == os.getpid():
            continue
        try:
            for name in ('cwd', 'exe'):
                try:
                    result.append(os.readlink(proc / name))
                except OSError as error:
                    if error.errno not in vanished:
                        raise
            for fd in (proc / 'fd').iterdir():
                try:
                    result.append(os.readlink(fd))
                except OSError as error:
                    if error.errno not in vanished:
                        raise
            result.extend((proc / 'cmdline').read_bytes().decode(errors='replace').split('\0'))
            for value in (proc / 'environ').read_bytes().split(b'\0'):
                key, separator, path = value.partition(b'=')
                if separator and key in (b'DOTNET_CLI_HOME', b'NUGET_PACKAGES', b'TMPDIR', b'TEMP'):
                    result.append(path.decode(errors='replace'))
            for line in (proc / 'maps').read_text(errors='replace').splitlines():
                parts = line.split(None, 5)
                if len(parts) == 6 and parts[5].startswith('/'):
                    result.append(parts[5])
        except OSError as error:
            if error.errno not in vanished:
                raise RuntimeError('Cannot inspect process ' + proc.name + ': ' + str(error)) from error
    return result


def active(scope, paths):
    return any(scope in item for item in paths if item)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--apply', action='store_true')
    add_config_argument(parser)
    args = parser.parse_args()
    settings = load_config(args.config, 'buildOutputs', ['roots', 'receiptDirectory'], DEFAULTS)
    roots = [str(plain_path(Path(p).absolute())) for p in settings['roots']]
    scratch_roots = [str(plain_path(Path(p).absolute())) for p in settings['scratchRoots']]
    excludes = [str(Path(p).absolute()) for p in settings['exclusions']]
    output_hours = max(24, float(settings['buildRetentionHours']))
    cache_hours = max(48, float(settings['cacheRetentionHours']))
    low_space = shutil.disk_usage(settings['diskUsagePath']).free < float(settings['lowSpaceThresholdGb']) * 1024**3
    if low_space:
        cache_hours = min(cache_hours, max(12, float(settings['lowSpaceCacheRetentionHours'])))
    scratch_days = float(settings['wholeFolderRetentionDays'])
    if scratch_roots and scratch_days <= 0:
        raise ValueError('wholeFolderRetentionDays must be above 0 when scratchRoots is set')
    receipt_dir = plain_path(settings['receiptDirectory'])
    receipt_dir.mkdir(parents=True, exist_ok=True)
    lock_path = plain_path(settings['lockPath'])
    with open(lock_path, 'a') as lock:
        try:
            fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        except BlockingIOError:
            print('Another storage cleanup is running.', file=sys.stderr)
            return 1
        report = {'started_utc': datetime.datetime.now(datetime.timezone.utc).isoformat(),
                  'mode': 'apply' if args.apply else 'plan', 'roots': roots,
                  'exclusions': excludes, 'build_retention_hours': output_hours,
                  'cache_retention_hours': cache_hours, 'scratch_roots': scratch_roots,
                  'whole_folder_retention_days': scratch_days, 'candidates': [],
                  'removed': [], 'skipped': {}, 'failures': [],
                  'free_before': shutil.disk_usage(settings['diskUsagePath']).free}

        def skip(reason):
            report['skipped'][reason] = report['skipped'].get(reason, 0) + 1

        def excluded(path):
            return any(within(str(path), entry) for entry in excludes)

        seen = set()
        try:
            scratch = prune_scratch(scratch_roots, scratch_days, excludes, args.apply,
                                    report, skip, process_paths)
            prune_nuget_caches(settings['nugetCachePatterns'], roots, excludes, cache_hours,
                               args.apply, report, skip, process_paths)
            paths = process_paths()
            for root in roots:
                if not Path(root).exists():
                    skip('missing_root')
                    continue
                for base, dirs, files in os.walk(root, followlinks=False, onerror=raise_error):
                    if excluded(base) or kept(base):
                        dirs[:] = []
                        skip('excluded')
                        continue
                    if base in scratch:
                        # The whole folder is already a candidate.
                        dirs[:] = []
                        continue
                    dirs[:] = [d for d in dirs if not d.startswith('._') and d != '.git'
                               and not Path(base, d).is_symlink()]
                    for name in list(dirs):
                        p = Path(base, name)
                        kind = None
                        if name in ('bin', 'obj') and any(f.endswith(('.csproj', '.fsproj', '.vbproj')) for f in files):
                            kind = 'build'
                        elif name == 'node_modules' and 'package.json' in files:
                            kind = 'build'
                        elif name in ('packages', 'http-cache') and 'dotnet-cli-home' in p.parts:
                            relative = p.parts[p.parts.index('dotnet-cli-home') + 1:]
                            if relative in (('.nuget', 'packages'), ('.local', 'share', 'NuGet', 'http-cache')):
                                kind = 'nuget_cache'
                        if kind is None:
                            if name in ('bin', 'obj', 'node_modules'):
                                dirs.remove(name)
                            continue
                        dirs.remove(name)
                        if str(p) in seen or excluded(p):
                            skip('excluded_or_duplicate')
                            continue
                        seen.add(str(p))
                        try:
                            top = None
                            if kind == 'build':
                                top, reason = git_guard(p)
                                if reason:
                                    skip(reason)
                                    continue
                                scope = top
                            else:
                                cli_home = Path(*p.parts[:p.parts.index('dotnet-cli-home') + 1])
                                owner = git(cli_home.parent, 'rev-parse', '--show-toplevel')
                                scope = (str(plain_path(owner.stdout.strip())) if owner.returncode == 0
                                         else str(cli_home.parent))
                            if kept(scope) or any(Path(scope).rglob(KEEP_MARKER)):
                                skip('keep_marker')
                                continue
                            info = inventory(p, internal_symlinks=name == 'node_modules')
                            hours = cache_hours if kind == 'nuget_cache' else output_hours
                            if info['newest_mtime'] > time.time() - hours * 3600:
                                skip('recent')
                                continue
                            if active(scope, paths):
                                skip('active')
                                continue
                            candidate = dict(path=str(p), kind=kind, scope=scope, **info)
                            report['candidates'].append(candidate)
                            if args.apply:
                                current = inventory(p, internal_symlinks=name == 'node_modules')
                                if current['device'] != info['device'] or current['inode'] != info['inode']:
                                    raise RuntimeError('Candidate identity changed')
                                if current['newest_mtime'] > time.time() - hours * 3600:
                                    skip('became_recent')
                                    continue
                                if kept(scope) or any(Path(scope).rglob(KEEP_MARKER)):
                                    skip('became_kept')
                                    continue
                                if kind == 'build':
                                    current_top, reason = git_guard(p)
                                    if reason or current_top != top:
                                        raise RuntimeError('Git protection changed: ' + str(reason))
                                if active(scope, process_paths()):
                                    skip('became_active')
                                    continue
                                # rmtree uses fd-relative operations when supported by Python.
                                if not shutil.rmtree.avoids_symlink_attacks:
                                    raise RuntimeError('This Python lacks safe directory removal')
                                shutil.rmtree(p)
                                report['removed'].append(candidate)
                        except ValueError as error:
                            if 'Symlink' in str(error):
                                skip('symlink')
                            else:
                                report['failures'].append({'path': str(p), 'error': str(error)})
                        except Exception as error:
                            report['failures'].append({'path': str(p), 'error': str(error)})
        except Exception as error:
            report['failures'].append({'path': None, 'error': str(error)})
        report['free_after'] = shutil.disk_usage(settings['diskUsagePath']).free
        report['candidate_bytes'] = sum(x['bytes'] for x in report['candidates'])
        report['removed_bytes'] = sum(x['bytes'] for x in report['removed'])
        report['finished_utc'] = datetime.datetime.now(datetime.timezone.utc).isoformat()
        stamp = datetime.datetime.now(datetime.timezone.utc).strftime('%Y%m%dT%H%M%S.%fZ')
        receipt = receipt_dir / ('build-cleanup-' + stamp + '-' + str(os.getpid()) + '.json')
        try:
            receipt.write_text(json.dumps(report, indent=2) + '\n')
            # Operator receipts remain with the verified history archive owner.
        except Exception as error:
            report['failures'].append({'path': str(receipt_dir), 'error': str(error)})
        print(json.dumps({k: v for k, v in report.items() if k not in ('candidates', 'removed')}, indent=2))
        print('Receipt: ' + str(receipt))
        return 1 if report['failures'] else 0


if __name__ == '__main__':
    sys.exit(main())
