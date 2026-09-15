#!/usr/bin/env python3
"""Check immutable fork migration declarations before selective integration.

This is a source-history gate. Provider startup and persistence tests are separate.
"""
import argparse
import hashlib
import json
from pathlib import Path
import re
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[2]
MANIFEST = ROOT / 'docs/upstream-review/fork-migrations.json'
SOURCES = {
    provider: f'src/Armada.Core/Database/{provider}/Queries/TableQueries.cs'
    for provider in ('Sqlite', 'Postgresql', 'SqlServer')
}
SOURCES['Mysql'] = 'src/Armada.Core/Database/Mysql/MysqlDatabaseDriver.cs'
MYSQL_DDL = 'src/Armada.Core/Database/Mysql/Queries/TableQueries.cs'
# The migration runner creates its ledger table from this member before any migration runs.
LEDGER_MEMBER = 'SchemaMigrations'
TOKEN = re.compile(r'//[^\n]*|/\*[\s\S]*?\*/|@"(?:[^"]|"")*"|"(?:\\.|[^"\\])*"|\x27(?:\\.|[^\x27\\])*\x27|[A-Za-z_][A-Za-z_0-9]*|[0-9]+|[^\s]', re.MULTILINE)


def tokens(source):
    return [match.group() for match in TOKEN.finditer(source)
            if not match.group().startswith(('//', '/*'))]


def digest(items):
    return hashlib.sha256('\0'.join(items).encode()).hexdigest()


def declaration_spans(items):
    for start in range(len(items) - 2):
        if items[start:start + 3] != ['new', 'SchemaMigration', '(']:
            continue
        depth = 1
        end = start + 3
        while end < len(items) and depth:
            if items[end] == '(':
                depth += 1
            elif items[end] == ')':
                depth -= 1
            end += 1
        if depth:
            raise ValueError('Unterminated SchemaMigration declaration')
        yield start, end


def declarations(source):
    items = tokens(source)
    result = []
    for start, end in declaration_spans(items):
        result.append({'version': int(items[start + 3]),
                       'description': json.loads(items[start + 5]),
                       'sha256': digest(items[start:end]),
                       'tokens': items[start:end]})
    if not result:
        raise ValueError('No migration declarations found')
    versions = [item['version'] for item in result]
    if versions != sorted(set(versions)):
        raise ValueError('Migration versions must be unique and ordered')
    return result


def members(source):
    """Map each static readonly member of the TableQueries class to its name-through-semicolon tokens."""
    items = tokens(source)
    for start in range(len(items) - 2):
        if items[start:start + 3] == ['class', 'TableQueries', '{']:
            break
    else:
        raise ValueError('TableQueries class not found')
    result = {}
    depth = 1
    index = start + 3
    while index < len(items) and depth:
        item = items[index]
        if item == '{':
            depth += 1
        elif item == '}':
            depth -= 1
        elif depth == 1 and item == 'static' and items[index + 1:index + 2] == ['readonly']:
            equals = items.index('=', index)
            name = items[equals - 1]
            nesting = 0
            end = equals
            while end < len(items) and not (items[end] == ';' and nesting == 0):
                if items[end] in '({':
                    nesting += 1
                elif items[end] in ')}':
                    nesting -= 1
                end += 1
            if end == len(items):
                raise ValueError(f'Unterminated TableQueries member {name}')
            if name in result:
                raise ValueError(f'Duplicate TableQueries member {name}')
            result[name] = items[equals - 1:end + 1]
            index = end
        index += 1
    return result


def referenced_names(items, names):
    """Member names used as `TableQueries.Name` or as a bare identifier (same-class use)."""
    found = []
    for index, item in enumerate(items):
        if item not in names or item in found:
            continue
        qualified = index >= 2 and items[index - 1] == '.' and items[index - 2] == 'TableQueries'
        if qualified or (index == 0 or items[index - 1] != '.'):
            found.append(item)
    return found


def referenced_members(ddl_source, root_items):
    """Digest every TableQueries member the roots reference, following member-to-member references."""
    bodies = members(ddl_source)
    pending = referenced_names(root_items + [LEDGER_MEMBER], bodies)
    protected = []
    while pending:
        name = pending.pop(0)
        if name in protected:
            continue
        protected.append(name)
        pending.extend(referenced_names(bodies[name][1:], bodies))
    order = list(bodies)
    return {name: digest(bodies[name]) for name in sorted(protected, key=order.index)}


def read_source(path, ref, root):
    if ref:
        return subprocess.check_output(['git', 'show', f'{ref}:{path}'], cwd=root).decode()
    return (root / path).read_text()


def initializer_items(source, marker):
    return tokens(source.split(marker, 1)[1].split('new SchemaMigration', 1)[0])


def collect(ref, root, protected_versions=None):
    """Read declarations and the referenced DDL inputs; protected_versions limits the reference roots."""
    current = {provider: declarations(read_source(path, ref, root)) for provider, path in SOURCES.items()}

    def roots(provider, initializer):
        limit = None if protected_versions is None else protected_versions[provider]
        items = list(initializer)
        for row in current[provider]:
            if limit is None or row['version'] <= limit:
                items.extend(row['tokens'])
        return items

    mysql_source = read_source(SOURCES['Mysql'], ref, root)
    mysql_initializer = initializer_items(mysql_source, 'private static List<SchemaMigration> GetMigrations()')
    sqlserver_source = read_source(SOURCES['SqlServer'], ref, root)
    sqlserver_initializer = initializer_items(sqlserver_source, 'GetMigrations()')
    referenced = {
        'referencedMysqlDdl': {
            'source': MYSQL_DDL,
            'initializerSha256': digest(mysql_initializer),
            'members': referenced_members(read_source(MYSQL_DDL, ref, root), roots('Mysql', mysql_initializer))},
        'referencedSqlServerDdl': {
            'source': SOURCES['SqlServer'],
            'initializerSha256': digest(sqlserver_initializer),
            'members': referenced_members(sqlserver_source, roots('SqlServer', sqlserver_initializer))},
    }
    return current, referenced


def ddl_members(ref, root, key):
    """Current digests of every TableQueries member, for comparison against the manifest."""
    path = MYSQL_DDL if key == 'referencedMysqlDdl' else SOURCES['SqlServer']
    return {name: digest(body) for name, body in members(read_source(path, ref, root)).items()}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--ref', help='Check a Git tree instead of working files')
    parser.add_argument('--root', type=Path, default=ROOT, help='Source checkout or isolated candidate tree')
    parser.add_argument('--write-manifest', action='store_true', help='Generate the initial manifest from an explicit fixed --ref')
    args = parser.parse_args()
    if args.write_manifest and not args.ref:
        parser.error('--write-manifest requires an explicit fixed --ref')
    try:
        if args.write_manifest:
            current, referenced = collect(args.ref, args.root)
            commit = subprocess.check_output(['git', 'rev-parse', args.ref], cwd=args.root, text=True).strip()
            providers = {provider: {'source': SOURCES[provider], 'maximumVersion': rows[-1]['version'],
                                    'migrations': [{key: row[key] for key in ('version', 'description', 'sha256')}
                                                   for row in rows]}
                         for provider, rows in current.items()}
            MANIFEST.write_text(json.dumps({'baselineCommit': commit, 'scope': 'Immutable declarations; not provider runtime proof',
                'providers': providers, **referenced}, indent=2) + '\n')
            print('Wrote fixed migration manifest')
            return 0
        baseline = json.loads(MANIFEST.read_text())
        current, referenced = collect(args.ref, args.root)
        failures = []
        for provider, saved in baseline['providers'].items():
            found = {row['version']: {key: row[key] for key in ('version', 'description', 'sha256')}
                     for row in current[provider]}
            expected = {row['version']: row for row in saved['migrations']}
            for version, row in expected.items():
                if found.get(version) != row:
                    failures.append(f'{provider} historical migration {version} changed or disappeared')
            for version in found.keys() - expected.keys():
                if version <= saved['maximumVersion']:
                    failures.append(f'{provider} reuses historical migration slot {version}')
            print(f"{provider}: {len(current[provider])} declarations; maximum {max(found)}; protected {len(expected)}")
        for key, provider, label in (('referencedMysqlDdl', 'Mysql', 'MySQL'), ('referencedSqlServerDdl', 'SqlServer', 'SQL Server')):
            saved = baseline[key]
            if referenced[key]['initializerSha256'] != saved['initializerSha256']:
                failures.append(f'{label} historical initial statement assembly changed')
            found = ddl_members(args.ref, args.root, key)
            for name, sha in saved['members'].items():
                if name not in found:
                    failures.append(f'{provider} referenced TableQueries member {name} disappeared')
                elif found[name] != sha:
                    failures.append(f'{provider} referenced TableQueries member {name} changed; append feature SQL in a new member and migration')
            print(f"{provider} TableQueries: {len(found)} members; protected {len(saved['members'])}")
        for failure in failures:
            print('FAIL: ' + failure, file=sys.stderr)
        if failures:
            return 1
        print('PASS: migration history preserved; new versions are above each provider baseline')
        return 0
    except (OSError, ValueError, KeyError, IndexError, subprocess.CalledProcessError) as error:
        print('ERROR: ' + str(error), file=sys.stderr)
        return 2


if __name__ == '__main__':
    sys.exit(main())
