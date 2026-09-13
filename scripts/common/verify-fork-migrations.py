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
TOKEN = re.compile(r'//[^\n]*|/\*[\s\S]*?\*/|@"(?:[^"]|"")*"|"(?:\\.|[^"\\])*"|\x27(?:\\.|[^\x27\\])*\x27|[A-Za-z_][A-Za-z_0-9]*|[0-9]+|[^\s]', re.MULTILINE)


def tokens(source):
    return [match.group() for match in TOKEN.finditer(source)
            if not match.group().startswith(('//', '/*'))]


def digest(items):
    return hashlib.sha256('\0'.join(items).encode()).hexdigest()


def declarations(source):
    items = tokens(source)
    result = []
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
        result.append({'version': int(items[start + 3]),
                       'description': json.loads(items[start + 5]),
                       'sha256': digest(items[start:end])})
    if not result:
        raise ValueError('No migration declarations found')
    versions = [item['version'] for item in result]
    if versions != sorted(set(versions)):
        raise ValueError('Migration versions must be unique and ordered')
    return result


def read_source(path, ref, root):
    if ref:
        return subprocess.check_output(['git', 'show', f'{ref}:{path}'], cwd=root).decode()
    return (root / path).read_text()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--ref', help='Check a Git tree instead of working files')
    parser.add_argument('--root', type=Path, default=ROOT, help='Source checkout or isolated candidate tree')
    parser.add_argument('--write-manifest', action='store_true', help='Generate the initial manifest from an explicit fixed --ref')
    args = parser.parse_args()
    if args.write_manifest and not args.ref:
        parser.error('--write-manifest requires an explicit fixed --ref')
    try:
        current = {provider: declarations(read_source(path, args.ref, args.root))
                   for provider, path in SOURCES.items()}
        ddl_hash = digest(tokens(read_source(MYSQL_DDL, args.ref, args.root)))
        mysql_source = read_source(SOURCES['Mysql'], args.ref, args.root)
        initializer = mysql_source.split('private static List<SchemaMigration> GetMigrations()', 1)[1].split('new SchemaMigration', 1)[0]
        initializer_hash = digest(tokens(initializer))
        sqlserver_source = read_source(SOURCES['SqlServer'], args.ref, args.root)
        sqlserver_initializer = sqlserver_source.split('GetMigrations()', 1)[1].split('new SchemaMigration', 1)[0]
        sqlserver_ddl = sqlserver_source.split('public static readonly string SchemaMigrations', 1)[1]
        sqlserver_hashes = {'source': SOURCES['SqlServer'], 'sha256': digest(tokens(sqlserver_ddl)),
                            'initializerSha256': digest(tokens(sqlserver_initializer))}
        if args.write_manifest:
            commit = subprocess.check_output(['git', 'rev-parse', args.ref], cwd=args.root, text=True).strip()
            MANIFEST.write_text(json.dumps({'baselineCommit': commit, 'scope': 'Immutable declarations; not provider runtime proof',
                'providers': {provider: {'source': SOURCES[provider], 'maximumVersion': rows[-1]['version'], 'migrations': rows}
                              for provider, rows in current.items()},
                'referencedMysqlDdl': {'source': MYSQL_DDL, 'sha256': ddl_hash, 'initializerSha256': initializer_hash},
                'referencedSqlServerDdl': sqlserver_hashes}, indent=2) + '\n')
            print('Wrote fixed migration manifest')
            return 0
        baseline = json.loads(MANIFEST.read_text())
        failures = []
        for provider, saved in baseline['providers'].items():
            found = {row['version']: row for row in current[provider]}
            expected = {row['version']: row for row in saved['migrations']}
            for version, row in expected.items():
                if found.get(version) != row:
                    failures.append(f'{provider} historical migration {version} changed or disappeared')
            for version in found.keys() - expected.keys():
                if version <= saved['maximumVersion']:
                    failures.append(f'{provider} reuses historical migration slot {version}')
            print(f"{provider}: {len(current[provider])} declarations; maximum {max(found)}; protected {len(expected)}")
        if ddl_hash != baseline['referencedMysqlDdl']['sha256']:
            failures.append('MySQL historical TableQueries DDL changed; append feature SQL in a new migration')
        if initializer_hash != baseline['referencedMysqlDdl']['initializerSha256']:
            failures.append('MySQL historical initial statement assembly changed')
        if sqlserver_hashes != baseline['referencedSqlServerDdl']:
            failures.append('SQL Server historical initial DDL or statement assembly changed')
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
