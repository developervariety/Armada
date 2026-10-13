#!/usr/bin/env python3
"""Append canonical persona guidance through REST; default to a read-only preview."""
import argparse
import datetime
import getpass
import hashlib
import json
import os
from pathlib import Path
import sys
import urllib.parse
import urllib.request

REPO = Path(__file__).resolve().parents[2]
ADDITIONS = REPO / 'src/Armada.Core/Prompts/persona-improvements.json'
MARKER = '## Evidence and execution contract'
METADATA_KEYS = ('Scope', 'TenantId', 'UserId', 'Description', 'IsBuiltIn', 'Category', 'Active')


def digest(text):
    return hashlib.sha256(text.encode('utf-8')).hexdigest()


def field(obj, name, default=None):
    return obj.get(name, obj.get(name[0].lower() + name[1:], default))


def request(base, token, method, path, data=None, timeout=30):
    body = None if data is None else json.dumps(data).encode('utf-8')
    headers = {'X-Token': token, 'Content-Type': 'application/json'} if token else {'Content-Type': 'application/json'}
    req = urllib.request.Request(base.rstrip('/') + path, data=body, headers=headers, method=method)
    with urllib.request.urlopen(req, timeout=timeout) as response:
        return json.load(response)


def session(args):
    if args.session_file:
        saved = json.loads(Path(args.session_file).read_text())
        if saved['base'].rstrip('/') != args.base.rstrip('/'):
            raise ValueError('Session endpoint differs from --base')
        return saved['token']
    password = os.environ.get('ARMADA_PASSWORD') or getpass.getpass('Armada password: ')
    email = os.environ.get('ARMADA_EMAIL') or input('Armada email: ')
    result = request(args.base, None, 'POST', '/api/v1/authenticate', {
        'TenantId': args.tenant, 'Email': email, 'Password': password})
    token = field(result, 'Token')
    if not field(result, 'Success') or not token:
        raise RuntimeError('Authentication failed')
    return token


def external_write(path, value):
    path = Path(path).resolve()
    if path.is_relative_to(REPO):
        raise ValueError('Secret-bearing backups must be outside the repository')
    path.parent.mkdir(parents=True, exist_ok=True)
    fd = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
    with os.fdopen(fd, 'w', encoding='utf-8') as stream:
        json.dump(value, stream, indent=2)
        stream.write('\n')


def preview(content, addition):
    if addition in content and content.count(MARKER) == 1:
        return content, 'unchanged'
    if MARKER in content:
        raise ValueError('Existing execution guidance conflicts with the canonical addition')
    return content + addition, 'append'


def payload(template, content):
    # Explicit scope preserves admin-edited templates; omitting it resets to the DTO default.
    return {'Content': content, 'Description': field(template, 'Description'), 'Scope': field(template, 'Scope', 'Tenant')}


def path_for(name):
    return '/api/v1/prompt-templates/' + urllib.parse.quote(name, safe='')


def deploy(base, token, additions, backup_path, apply=False, api=request):
    entries = []
    for name, addition in additions.items():
        old = api(base, token, 'GET', path_for(name))
        content = field(old, 'Content')
        if not isinstance(content, str):
            raise ValueError('Template content missing: ' + name)
        new, action = preview(content, addition)
        entries.append({'name': name, 'template': old, 'content': new, 'action': action,
                        'beforeSha256': digest(content), 'afterSha256': digest(new)})
    receipt = [{'name': e['name'], 'action': e['action'], 'beforeSha256': e['beforeSha256'],
                'afterSha256': e['afterSha256']} for e in entries]
    if not apply:
        return receipt
    external_write(backup_path, {'base': base, 'createdUtc': datetime.datetime.now(datetime.timezone.utc).isoformat(),
                                'entries': entries})
    changed = []
    try:
        for entry in entries:
            if entry['action'] == 'unchanged':
                continue
            current = api(base, token, 'GET', path_for(entry['name']))
            if (digest(field(current, 'Content')) != entry['beforeSha256']
                    or any(field(current, key) != field(entry['template'], key) for key in METADATA_KEYS)):
                raise RuntimeError('Template changed since preview: ' + entry['name'])
            # Record before PUT: a connection failure may occur after the server has saved.
            changed.append(entry)
            api(base, token, 'PUT', path_for(entry['name']), payload(entry['template'], entry['content']))
            saved = api(base, token, 'GET', path_for(entry['name']))
            if digest(field(saved, 'Content')) != entry['afterSha256']:
                raise RuntimeError('Read-back verification failed: ' + entry['name'])
            for key in METADATA_KEYS:
                if field(saved, key) != field(entry['template'], key):
                    raise RuntimeError('Metadata changed: ' + entry['name'] + ' / ' + key)
    except Exception:
        failures = []
        for entry in reversed(changed):
            try:
                rollback_entry(base, token, entry, api)
            except Exception:
                failures.append(entry['name'])
        if failures:
            raise RuntimeError('Apply failed; rollback needs review for: ' + ', '.join(failures)) from None
        raise
    return receipt


def rollback_entry(base, token, entry, api=request):
    current = api(base, token, 'GET', path_for(entry['name']))
    current_hash = digest(field(current, 'Content'))
    if current_hash == entry['beforeSha256']:
        return
    if (current_hash != entry['afterSha256']
            or any(field(current, key) != field(entry['template'], key) for key in METADATA_KEYS)):
        raise RuntimeError('Refusing to overwrite concurrent edits: ' + entry['name'])
    original = entry['template']
    api(base, token, 'PUT', path_for(entry['name']), payload(original, field(original, 'Content')))
    saved = api(base, token, 'GET', path_for(entry['name']))
    if digest(field(saved, 'Content')) != entry['beforeSha256']:
        raise RuntimeError('Rollback verification failed: ' + entry['name'])


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--base', required=True)
    parser.add_argument('--tenant', default='default')
    parser.add_argument('--session-file', help='External restricted session JSON; otherwise prompt or use environment credentials')
    parser.add_argument('--apply', action='store_true', help='Write changes; otherwise preview only')
    parser.add_argument('--backup', help='Required external, new backup path when applying')
    parser.add_argument('--rollback', help='External backup to restore, with concurrent-edit checks')
    args = parser.parse_args()
    if args.apply and not args.backup:
        parser.error('--apply requires --backup outside the repository')
    if args.rollback and args.apply:
        parser.error('--rollback and --apply are mutually exclusive')
    token = session(args)
    if args.rollback:
        saved = json.loads(Path(args.rollback).read_text())
        if saved['base'].rstrip('/') != args.base.rstrip('/'):
            raise ValueError('Backup endpoint differs from --base')
        for entry in reversed(saved['entries']):
            rollback_entry(args.base, token, entry)
        print('Rollback verified.')
        return
    additions = json.loads(ADDITIONS.read_text())
    receipt = deploy(args.base, token, additions, args.backup, args.apply)
    print(json.dumps({'mode': 'applied' if args.apply else 'preview', 'additionsSha256': digest(ADDITIONS.read_text()),
                      'templates': receipt}, indent=2))


if __name__ == '__main__':
    try:
        main()
    except Exception as error:
        # Do not log response bodies, credentials, session tokens, or request headers.
        print(type(error).__name__ + ': ' + str(error), file=sys.stderr)
        sys.exit(1)
