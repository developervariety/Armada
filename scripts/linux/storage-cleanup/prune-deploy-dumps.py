#!/usr/bin/env python3
"""Keep only the newest pre-deploy database dumps in one directory. Run on the Docker host as root."""
import argparse
import errno
import fcntl
import fnmatch
import json
import os
from pathlib import Path
import stat
import sys

from cleanup_config import add_config_argument, load_config

DEFAULTS = {
    'lockPath': '/run/lock/armada-prune-deploy-dumps.lock',
    'keep': 2,
}
REQUIRED = ['directory', 'pattern']


def dumps(directory, pattern):
    """Regular files directly in directory whose name matches pattern, newest first."""
    found = []
    for entry in os.scandir(directory):
        if not fnmatch.fnmatchcase(entry.name, pattern):
            continue
        info = entry.stat(follow_symlinks=False)
        if not stat.S_ISREG(info.st_mode):
            continue
        found.append({'path': entry.path, 'bytes': info.st_size, 'mtime': info.st_mtime,
                      'inode': info.st_ino, 'device': info.st_dev})
    found.sort(key=lambda item: (item['mtime'], item['path']), reverse=True)
    return found


def open_paths():
    """Paths that any process holds open, read from the host PID namespace; fail closed."""
    result = set()
    vanished = (errno.ENOENT, errno.ESRCH, errno.ENOTDIR)
    for proc in Path('/proc').glob('[0-9]*'):
        try:
            for fd in (proc / 'fd').iterdir():
                try:
                    result.add(os.readlink(fd))
                except OSError as error:
                    if error.errno not in vanished:
                        raise
        except OSError as error:
            if error.errno not in vanished:
                raise RuntimeError('Cannot inspect process ' + proc.name + ': ' + str(error)) from error
    return result


def unchanged(candidate):
    try:
        info = os.stat(candidate['path'], follow_symlinks=False)
    except FileNotFoundError:
        return False
    return (stat.S_ISREG(info.st_mode) and info.st_size == candidate['bytes'] and info.st_mtime == candidate['mtime']
            and info.st_ino == candidate['inode'] and info.st_dev == candidate['device'])


def clean(apply, settings, report, read_open_paths=open_paths):
    keep = settings['keep']
    if not isinstance(keep, int) or isinstance(keep, bool) or keep < 1:
        raise ValueError('keep must be a whole number of at least 1, got ' + json.dumps(keep))
    found = dumps(settings['directory'], settings['pattern'])
    report['kept'] = [item['path'] for item in found[:keep]]
    candidates = found[keep:]
    if not candidates:
        return
    held = read_open_paths()
    for candidate in candidates:
        public = {key: candidate[key] for key in ('path', 'bytes')}
        if candidate['path'] in held:
            report['skipped'].append(dict(public, reason='open by a running process'))
            continue
        report['candidates'].append(public)
        if not apply:
            continue
        # The identity check runs last, so nothing that changed during the process scan is deleted.
        if candidate['path'] in read_open_paths() or not unchanged(candidate):
            report['skipped'].append(dict(public, reason='changed before delete'))
            continue
        os.unlink(candidate['path'])
        report['deleted'].append(public)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--apply', action='store_true')
    add_config_argument(parser)
    args = parser.parse_args()
    settings = load_config(args.config, 'deployDumps', REQUIRED, DEFAULTS)
    report = {'mode': 'apply' if args.apply else 'plan', 'directory': settings['directory'],
              'pattern': settings['pattern'], 'keep': settings['keep'],
              'kept': [], 'candidates': [], 'skipped': [], 'deleted': []}
    status = 0
    try:
        with open(settings['lockPath'], 'a') as lock:
            fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
            clean(args.apply, settings, report)
    except Exception as error:
        report['error'] = str(error)
        status = 1
    finally:
        report['candidate_bytes'] = sum(item['bytes'] for item in report['candidates'])
        report['deleted_bytes'] = sum(item['bytes'] for item in report['deleted'])
        print(json.dumps(report, indent=2))
    return status


if __name__ == '__main__':
    sys.exit(main())
