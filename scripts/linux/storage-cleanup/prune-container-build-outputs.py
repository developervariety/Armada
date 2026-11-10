#!/usr/bin/env python3
"""Run the guarded build/cache cleaner inside explicitly configured running containers."""
import argparse
import json
from pathlib import Path
import subprocess
import sys

from cleanup_config import add_config_argument, load_config

# The container PID namespace makes process references and scratch paths agree.
# Only this stage's configuration and public cleaner sources cross the boundary.
BOOTSTRAP = '''
import json, pathlib, subprocess, sys, tempfile
bundle = json.load(sys.stdin)
with tempfile.TemporaryDirectory(prefix='armada-storage-cleanup-') as temporary:
    root = pathlib.Path(temporary).resolve()
    for name, source in bundle['sources'].items():
        (root / name).write_text(source)
    config = bundle['config']
    config['receiptDirectory'] = str(root / 'receipts')
    config['buildOutputs']['lockPath'] = str(pathlib.Path('/tmp').resolve() / 'armada-storage-build-cleanup.lock')
    path = root / 'config.json'
    path.write_text(json.dumps(config))
    command = [sys.executable, str(root / 'prune-build-outputs.py'), '--config', str(path)]
    if bundle['apply']:
        command.append('--apply')
    result = subprocess.run(command)
    for receipt in (root / 'receipts').glob('*.json'):
        print(receipt.read_text(), flush=True)
    sys.exit(result.returncode)
'''


def clean_target(target, apply):
    """Pin the running container identity before executing; never start or stop it."""
    info = json.loads(subprocess.check_output(
        ['docker', 'inspect', target['container']], text=True, timeout=30))[0]
    if not info['State']['Running']:
        raise RuntimeError('Configured cleanup container is not running')
    section = dict(target['buildOutputs'])
    # Source trees and history are not disposable container scratch payloads.
    if section.get('scratchRoots'):
        raise ValueError('Container cleanup does not permit whole-folder retirement')
    here = Path(__file__).resolve().parent
    bundle = {'sources': {name: (here / name).read_text()
                          for name in ('cleanup_config.py', 'prune-build-outputs.py')},
              'config': {'buildOutputs': section}, 'apply': apply}
    result = subprocess.run(['docker', 'exec', '-i', '--user', '0', info['Id'],
                             'python3', '-c', BOOTSTRAP], input=json.dumps(bundle), text=True)
    return result.returncode


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--apply', action='store_true')
    add_config_argument(parser)
    args = parser.parse_args()
    settings = load_config(args.config, 'containerBuildOutputs', ['targets'])
    failed = False
    for target in settings['targets']:
        try:
            failed = clean_target(target, args.apply) != 0 or failed
        except Exception as error:
            print(json.dumps({'container': target.get('container'), 'error': str(error)}), flush=True)
            failed = True
    return 1 if failed else 0


if __name__ == '__main__':
    sys.exit(main())
