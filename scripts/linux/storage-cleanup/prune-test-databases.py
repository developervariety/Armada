#!/usr/bin/env python3
"""Remove inactive generated test databases. Run on the Docker host as root."""
import argparse
import datetime
import fcntl
import json
import os
import re
import subprocess
import sys
import time

from cleanup_config import add_config_argument, load_config

DEFAULTS = {
    'lockPath': '/run/lock/armada-prune-test-databases.lock',
    'minimumAgeHours': 48,
    'runnerContainers': {},
}
REQUIRED = ['targets', 'runnerCommandPattern', 'runnerArgumentPatterns']


def run(args):
    return subprocess.check_output(args, text=True, stderr=subprocess.PIPE)


def runners(settings):
    found = []
    sources = [('host', ['ps', '-eo', 'pid=,comm=,args='])]
    sources += [(scope, ['docker', 'exec', container, 'ps', '-eo', 'pid=,comm=,args='])
                for scope, container in settings['runnerContainers'].items()]
    for scope, command in sources:
        for line in run(command).splitlines():
            parts = line.strip().split(None, 2)
            if len(parts) != 3:
                continue
            pid, comm, args = parts
            if not re.search(settings['runnerCommandPattern'], comm, re.I):
                continue
            if all(re.search(pattern, args, re.I) for pattern in settings['runnerArgumentPatterns']):
                found.append({'scope': scope, 'pid': pid, 'reason': 'relevant test runner'})
    return found


def connection(container):
    info = json.loads(run(['docker', 'inspect', container]))[0]
    env = dict(item.split('=', 1) for item in info['Config']['Env'] if '=' in item)
    user = env.get('POSTGRES_USER', 'postgres')
    data = env.get('PGDATA', '/var/lib/postgresql/data')
    mounts = [m for m in info['Mounts'] if data == m['Destination'] or data.startswith(m['Destination'].rstrip('/') + '/')]
    if not mounts:
        raise RuntimeError('PGDATA has no host mount')
    mount = max(mounts, key=lambda m: len(m['Destination']))
    root = os.path.join(mount['Source'], os.path.relpath(data, mount['Destination']))
    return user, os.path.realpath(root)


def query(container, user, sql):
    return run(['docker', 'exec', container, 'psql', '-X', '-v', 'ON_ERROR_STOP=1', '-U', user, '-d', 'postgres', '-At', '-F', '|', '-c', sql]).strip()


def newest_file(path):
    if os.path.islink(path) or not os.path.isdir(path):
        raise RuntimeError('database directory missing or linked')
    newest = os.stat(path).st_mtime
    for folder, dirs, files in os.walk(path, followlinks=False):
        for name in dirs + files:
            file = os.path.join(folder, name)
            if os.path.islink(file):
                raise RuntimeError('linked path in database')
            newest = max(newest, os.stat(file).st_mtime)
    return newest


def clean(args, settings, report):
    active = runners(settings)
    if active:
        report['skipped'] = active
        return
    cutoff = time.time() - float(settings['minimumAgeHours']) * 3600
    for target in settings['targets']:
        container, pattern = target['container'], target['databasePattern']
        user, root = connection(container)
        rows = query(container, user, "SELECT d.oid,d.datname,r.rolname,d.dattablespace,pg_database_size(d.oid),(SELECT count(*) FROM pg_stat_activity a WHERE a.datname=d.datname) FROM pg_database d JOIN pg_roles r ON r.oid=d.datdba").splitlines()
        for row in rows:
            oid, name, owner, tablespace, size, sessions = row.split('|')
            if not re.fullmatch(pattern, name):
                continue
            if owner != user or tablespace != '1663' or sessions != '0':
                report['skipped'].append({'container': container, 'database': name, 'reason': 'owner, tablespace, or session guard'})
                continue
            try:
                newest = newest_file(os.path.join(root, 'base', oid))
            except (OSError, RuntimeError) as error:
                report['skipped'].append({'container': container, 'database': name, 'reason': str(error)})
                continue
            if newest >= cutoff:
                continue
            candidate = {'container': container, 'database': name, 'oid': int(oid), 'bytes': int(size), 'last_file_change_utc': datetime.datetime.fromtimestamp(newest, datetime.timezone.utc).isoformat()}
            report['candidates'].append(candidate)
            if args.apply:
                if runners(settings):
                    raise RuntimeError('test runner started during cleanup')
                # Re-read identity and sessions immediately before DROP. No FORCE.
                check = query(container, user, "SELECT d.oid,(SELECT count(*) FROM pg_stat_activity a WHERE a.datname=d.datname) FROM pg_database d WHERE d.datname='" + name + "' AND d.datdba=(SELECT oid FROM pg_roles WHERE rolname=current_user) AND d.dattablespace=1663")
                if check != oid + '|0' or newest_file(os.path.join(root, 'base', oid)) >= cutoff:
                    report['skipped'].append({'container': container, 'database': name, 'reason': 'changed before delete'})
                    continue
                query(container, user, 'DROP DATABASE "' + name + '"')
                if query(container, user, "SELECT count(*) FROM pg_database WHERE datname='" + name + "'") != '0':
                    raise RuntimeError('database still exists after DROP')
                report['deleted'].append(candidate)
                if len(report['deleted']) % 25 == 0:
                    print('Deleted ' + str(len(report['deleted'])) + ' old test databases', file=sys.stderr, flush=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    mode = parser.add_mutually_exclusive_group()
    mode.add_argument('--plan', action='store_true')
    mode.add_argument('--apply', action='store_true')
    add_config_argument(parser)
    args = parser.parse_args()
    settings = load_config(args.config, 'testDatabases', REQUIRED, DEFAULTS)
    report = {'mode': 'apply' if args.apply else 'plan', 'minimum_age_hours': settings['minimumAgeHours'],
              'candidates': [], 'skipped': [], 'deleted': []}
    status = 0
    try:
        # The lock covers both plans and deletes on the same Docker host.
        with open(settings['lockPath'], 'a') as lock:
            fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
            clean(args, settings, report)
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
