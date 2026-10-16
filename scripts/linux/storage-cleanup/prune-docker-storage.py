#!/usr/bin/env python3
"""Retire redundant rollback tags, unused disposable tags and old dangling layers; never prune all volumes."""
import argparse
import datetime
import fcntl
import fnmatch
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import time

from cleanup_config import add_config_argument, load_config

DEFAULTS = {
    'lockPath': '/run/lock/armada-prune-docker-storage.lock',
    'retainedSetsRequired': 3,
    'danglingPruneUntil': '48h',
    'disposableVolumes': [],
    'unusedImageTagPatterns': [],
    'unusedImageMinAgeDays': 7,
    'protectedImageTags': [],
    'protectedTagPatterns': [],
}


def run(args):
    return subprocess.check_output(args, text=True, stderr=subprocess.PIPE, timeout=300)


def inspect(kind, ids):
    return json.loads(run(['docker', kind, 'inspect', *ids])) if ids else []


def created_at(image):
    """Parse Docker's Created stamp; nanosecond fractions are cut to microseconds."""
    value = image['Created'].replace('Z', '+00:00')
    match = re.fullmatch(r'(.*T\d{2}:\d{2}:\d{2})(?:\.(\d+))?([+-]\d{2}:\d{2})?', value)
    if match:
        fraction = '.' + match.group(2)[:6].ljust(6, '0') if match.group(2) else ''
        value = match.group(1) + fraction + (match.group(3) or '+00:00')
    return datetime.datetime.fromisoformat(value)


def retained_pattern(repository):
    return re.escape(repository) + r':armada-retained-(?:running|tag)-(\d{8}T\d{6}Z)-[a-z0-9]+'


def rollback_pattern(repository):
    return re.escape(repository) + r':rollback-before-[a-f0-9]+'


def tag_matches(tag, patterns):
    """A glob matches the full reference, the repository, or the tag after the last ':'."""
    repository, separator, name = tag.rpartition(':')
    if not separator or '/' in name:
        repository, name = tag, ''
    forms = [tag, repository] + ([name] if name else [])
    return any(fnmatch.fnmatchcase(form, pattern) for pattern in patterns for form in forms)


def select_unused_tags(images, containers, settings, now):
    """Pure selection of disposable tags whose image no container uses and no protected tag names."""
    patterns = settings.get('unusedImageTagPatterns') or []
    if not patterns:
        return []
    used = {c['Image'] for c in containers}
    tags = {t: image for image in images for t in image.get('RepoTags') or []}
    protected_ids = {tags[t]['Id'] for t in settings.get('protectedImageTags') or [] if t in tags}
    repository = settings['imageRepository']
    cutoff = now - datetime.timedelta(days=float(settings['unusedImageMinAgeDays']))
    selected = []
    for tag, image in sorted(tags.items()):
        if not tag_matches(tag, patterns) or tag_matches(tag, settings.get('protectedTagPatterns') or []):
            continue
        # Rollback and retention tags belong to the rollback retirement rule alone.
        if re.fullmatch(rollback_pattern(repository), tag) or re.fullmatch(retained_pattern(repository), tag):
            continue
        if image['Id'] in used or image['Id'] in protected_ids:
            continue
        if created_at(image) >= cutoff:
            continue
        selected.append({'tag': tag, 'image': image['Id'], 'bytes': int(image.get('Size') or 0)})
    return selected


def plan(settings):
    images = inspect('image', list(set(run(['docker', 'image', 'ls', '-aq']).split())))
    containers = inspect('container', run(['docker', 'ps', '-aq']).split())
    used = {c['Image'] for c in containers}
    tags = {t: image for image in images for t in image.get('RepoTags') or []}
    repository = settings['imageRepository']
    required = int(settings['retainedSetsRequired'])
    sets = sorted({m.group(1) for t in tags if (m := re.fullmatch(retained_pattern(repository), t))}, reverse=True)
    retired = []
    # Independently dated retention sets must exist before an older tag can go.
    if len(sets) >= required:
        floor = datetime.datetime.strptime(sets[required - 1], '%Y%m%dT%H%M%SZ').replace(tzinfo=datetime.timezone.utc)
        for tag, image in tags.items():
            if not re.fullmatch(rollback_pattern(repository), tag):
                continue
            created = created_at(image)
            other_tags = [t for t in image.get('RepoTags') or [] if t != tag]
            if image['Id'] not in used and not other_tags and created < floor:
                retired.append({'tag': tag, 'image': image['Id']})
    unused = select_unused_tags(images, containers, settings, datetime.datetime.now(datetime.timezone.utc))
    return retired, sets[:required], unused


def volume_active(mount):
    for proc in Path('/proc').glob('[0-9]*'):
        if int(proc.name) == os.getpid():
            continue
        try:
            paths = [os.readlink(proc / 'cwd')]
            for fd in (proc / 'fd').iterdir():
                try:
                    paths.append(os.readlink(fd))
                except (FileNotFoundError, ProcessLookupError):
                    pass
            for line in (proc / 'maps').read_text().splitlines():
                fields = line.split(None, 5)
                if len(fields) == 6:
                    paths.append(fields[5])
            if any(p == mount or p.startswith(mount + '/') for p in paths):
                return True
        except (FileNotFoundError, ProcessLookupError):
            continue
    return False


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--apply', action='store_true')
    add_config_argument(parser)
    args = parser.parse_args()
    settings = load_config(args.config, 'docker', ['imageRepository'], DEFAULTS)
    report = {'mode': 'apply' if args.apply else 'plan', 'retired_tags': [], 'removed_unused_tags': [],
              'removed_volumes': [], 'errors': []}
    with open(settings['lockPath'], 'a') as lock:
        fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        try:
            candidates, retained, unused = plan(settings)
            report.update(candidate_tags=candidates, retained_sets=retained, unused_tag_candidates=unused,
                          unused_tag_candidate_bytes=sum(item['bytes'] for item in unused))
            for item in candidates:
                if not args.apply:
                    continue
                current, _, _ = plan(settings)
                if item not in current:
                    continue
                run(['docker', 'image', 'rm', item['tag']])
                report['retired_tags'].append(item)
            for item in unused:
                if not args.apply:
                    continue
                # Re-read containers and tags immediately before each removal.
                _, _, current = plan(settings)
                if item not in current:
                    continue
                # Untag only; without -f Docker refuses an image a container still uses.
                run(['docker', 'rmi', item['tag']])
                report['removed_unused_tags'].append(item)
            # Explicitly named, reviewed disposable volumes only. Unknown orphan data stays.
            existing = set(run(['docker', 'volume', 'ls', '-q']).split())
            for name in settings['disposableVolumes']:
                if name not in existing:
                    continue
                if not re.fullmatch(r'[a-f0-9]{64}', name):
                    raise ValueError('Disposable volume must have an exact anonymous ID')
                info = inspect('volume', [name])[0]
                if run(['docker', 'ps', '-aq', '--filter', 'volume=' + name]).strip():
                    continue
                mount = info['Mountpoint']
                if not (Path(mount) / 'PG_VERSION').is_file() or volume_active(mount):
                    continue
                report.setdefault('candidate_volumes', []).append(name)
                if args.apply:
                    # Docker refuses any volume referenced by a running or stopped container.
                    run(['docker', 'volume', 'rm', name])
                    report['removed_volumes'].append(name)
            if args.apply:
                report['dangling_prune'] = run(['docker', 'image', 'prune', '-f', '--filter',
                                                'until=' + settings['danglingPruneUntil']])
        except Exception as error:
            report['errors'].append(str(error))
        report['removed_unused_tag_bytes'] = sum(item['bytes'] for item in report['removed_unused_tags'])
        print(json.dumps(report, indent=2))
        return 1 if report['errors'] else 0


if __name__ == '__main__':
    sys.exit(main())
