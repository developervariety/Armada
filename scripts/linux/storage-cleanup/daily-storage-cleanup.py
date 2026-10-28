#!/usr/bin/env python3
"""Run independent, guarded storage cleaners and retain their receipts."""
import argparse
import datetime
import fcntl
import json
from pathlib import Path
import shutil
import subprocess
import sys

from cleanup_config import add_config_argument, load_config

STAGES = [
    ('build', 'prune-build-outputs.py'),
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
}


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
    with open(settings['lockPath'], 'a') as lock:
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
        report['finished_utc'] = datetime.datetime.now(datetime.timezone.utc).isoformat()
        path = directory / ('daily-' + stamp + '.json')
        path.write_text(json.dumps(report, indent=2) + '\n')
        print('Receipt: ' + str(path), flush=True)
        # Receipts and stage output are history. Only the verified archive owner may retire them.
        return 1 if any(s['exit_code'] != 0 for s in report['stages']) else 0


if __name__ == '__main__':
    sys.exit(main())
