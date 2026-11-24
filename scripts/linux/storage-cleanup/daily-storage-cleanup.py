#!/usr/bin/env python3
"""Run independent, guarded storage cleaners and retain their receipts."""
import argparse
import datetime
from contextlib import contextmanager
import os
import stat
import uuid
import fcntl
import json
from pathlib import Path
import shutil
import subprocess
import sys

from cleanup_config import add_config_argument, load_config

STAGES = [
    ('build', 'prune-build-outputs.py'),
    ('container-build', 'prune-container-build-outputs.py'),
    ('agents', 'prune-agent-caches.py'),
    ('docker', 'prune-docker-storage.py'),
    ('test-databases', 'prune-test-databases.py'),
    ('deploy-dumps', 'prune-deploy-dumps.py'),
]
# A host opts in to deploy-dump rotation by naming the stage, because it needs its own config section.
DEFAULT_STAGES = ['build', 'agents', 'docker', 'test-databases']
DEFAULTS = {
    'lockPath': '/run/lock/armada-daily-storage-cleanup.lock',
    'stageTimeoutSeconds': 1800,
    'diskUsagePath': '/',
    'python': sys.executable or '/usr/bin/python3',
    'scriptDirectory': None,
    'stages': DEFAULT_STAGES,
    'minimumFreeGb': 0,
    'hostLockPath': None,
}


@contextmanager
def host_lock(path):
    """Serialize with host commands and release only this unchanged lease."""
    if path is None:
        yield
        return
    directory = Path(path)
    if not directory.is_absolute() or any(part.is_symlink() for part in [directory, *directory.parents]):
        raise ValueError('Host command lock must be an absolute path without symlinks')
    directory.mkdir(mode=0o755)
    identity = directory.lstat()
    owner = directory / 'owner'
    payload = 'pid=' + str(os.getpid()) + '\noperator=storage-cleanup\ntoken=' + uuid.uuid4().hex + '\n'
    owner_identity = None
    complete = False
    try:
        with owner.open('x') as handle:
            owner_identity = os.fstat(handle.fileno())
            handle.write(payload)
            handle.flush()
            os.fsync(handle.fileno())
        complete = True
        yield
    finally:
        current = directory.lstat()
        if ((current.st_dev, current.st_ino, current.st_uid, current.st_gid) !=
                (identity.st_dev, identity.st_ino, identity.st_uid, identity.st_gid) or
                not stat.S_ISDIR(current.st_mode)):
            raise RuntimeError('Host command lock identity changed; retained for inspection')
        if owner_identity is None:
            if os.path.lexists(owner):
                raise RuntimeError('Host command lock owner identity changed; retained for inspection')
        else:
            def same_owner(value):
                return ((value.st_dev, value.st_ino, value.st_uid, value.st_gid) ==
                        (owner_identity.st_dev, owner_identity.st_ino,
                         owner_identity.st_uid, owner_identity.st_gid) and
                        stat.S_ISREG(value.st_mode) and value.st_nlink == 1)

            if not same_owner(owner.lstat()):
                raise RuntimeError('Host command lock owner identity changed; retained for inspection')
            descriptor = os.open(owner, os.O_RDONLY | os.O_NOFOLLOW)
            with os.fdopen(descriptor, 'rb') as handle:
                if not same_owner(os.fstat(handle.fileno())):
                    raise RuntimeError('Host command lock owner identity changed; retained for inspection')
                actual = handle.read(len(payload.encode()) + 1)
            expected = payload.encode()
            if ((actual != expected if complete else not expected.startswith(actual)) or
                    not same_owner(owner.lstat())):
                raise RuntimeError('Host command lock owner identity changed; retained for inspection')
            owner.unlink()
        directory.rmdir()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--apply', action='store_true')
    add_config_argument(parser)
    args = parser.parse_args()
    config_path = str(Path(args.config).absolute())
    settings = load_config(config_path, 'orchestrator', ['receiptDirectory'], DEFAULTS)
    directory = Path(settings['receiptDirectory'])
    directory.mkdir(mode=0o750, parents=True, exist_ok=True)
    scripts = Path(settings['scriptDirectory'] or Path(__file__).resolve().parent)
    stamp = datetime.datetime.now(datetime.timezone.utc).strftime('%Y%m%dT%H%M%S.%fZ')
    report = {'started_utc': stamp, 'apply': args.apply,
              'free_before': shutil.disk_usage(settings['diskUsagePath']).free, 'stages': []}
    unknown = [name for name in settings['stages'] if name not in dict(STAGES)]
    if unknown:
        raise ValueError('Unknown storage cleanup stage(s): ' + ', '.join(unknown))
    stages = [(name, [settings['python'], str(scripts / script), '--config', config_path])
              for name, script in STAGES if name in settings['stages']]
    with host_lock(settings['hostLockPath']), open(settings['lockPath'], 'a') as lock:
        fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        for name, command in stages:
            if args.apply:
                command.append('--apply')
            stdout = directory / (stamp + '-' + name + '.stdout')
            stderr = directory / (stamp + '-' + name + '.stderr')
            result = {'name': name, 'stdout': str(stdout), 'stderr': str(stderr)}
            print('Starting ' + name, flush=True)
            try:
                with stdout.open('w') as output, stderr.open('w') as errors:
                    process = subprocess.run(command, stdout=output, stderr=errors,
                                             timeout=settings['stageTimeoutSeconds'])
                result['exit_code'] = process.returncode
            except Exception as error:
                result.update(exit_code=1, error=str(error))
            report['stages'].append(result)
            print(name + ' exit=' + str(result['exit_code']), flush=True)
        report['free_after'] = shutil.disk_usage(settings['diskUsagePath']).free
        report['free_change_bytes'] = report['free_after'] - report['free_before']
        report['low_space'] = report['free_after'] < float(settings['minimumFreeGb']) * 1024**3
        print('Free bytes: before=' + str(report['free_before']) + ' after=' + str(report['free_after'])
              + ' change=' + str(report['free_change_bytes']), flush=True)
        if report['low_space']:
            print('Storage remains below minimumFreeGb; inspect protected and uncovered storage.',
                  file=sys.stderr, flush=True)
        report['finished_utc'] = datetime.datetime.now(datetime.timezone.utc).isoformat()
        path = directory / ('daily-' + stamp + '.json')
        path.write_text(json.dumps(report, indent=2) + '\n')
        print('Receipt: ' + str(path), flush=True)
        # Receipts and stage output are history. Only the verified archive owner may retire them.
        return 1 if report['low_space'] or any(s['exit_code'] != 0 for s in report['stages']) else 0


if __name__ == '__main__':
    sys.exit(main())
