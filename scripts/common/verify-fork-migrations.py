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
DATABASE = 'src/Armada.Core/Database'
SOURCES = {
    provider: f'{DATABASE}/{provider}/Queries/TableQueries.cs'
    for provider in ('Sqlite', 'Postgresql', 'SqlServer')
}
SOURCES['Mysql'] = f'{DATABASE}/Mysql/MysqlDatabaseDriver.cs'
# The class that holds each provider's declarations; bare member names in a declaration resolve there.
CONTEXT_CLASS = {'Sqlite': 'TableQueries', 'Postgresql': 'TableQueries', 'SqlServer': 'TableQueries', 'Mysql': 'MysqlDatabaseDriver'}
INITIALIZERS = {'Mysql': 'private static List<SchemaMigration> GetMigrations()', 'SqlServer': 'GetMigrations()'}
# The migration runner creates its ledger table from this member before any migration runs.
LEDGER = ['TableQueries', '.', 'SchemaMigrations']
HEAD_FIELDS = ('version', 'description', 'sha256')
TOKEN = re.compile(r'//[^\n]*|/\*[\s\S]*?\*/|@"(?:[^"]|"")*"|"(?:\\.|[^"\\])*"|\x27(?:\\.|[^\x27\\])*\x27|[A-Za-z_][A-Za-z_0-9]*|[0-9]+|[^\s]', re.MULTILINE)
DIRECTIVE = re.compile(r'^[ \t]*#[ \t]*(?:region|endregion|if|elif|else|endif|pragma|nullable|define|undef|warning|error|line)\b.*$', re.MULTILINE)
IDENT = re.compile(r'[A-Za-z_][A-Za-z_0-9]*$')
MODIFIERS = {'public', 'private', 'internal', 'protected', 'static', 'readonly', 'const', 'partial', 'override',
             'virtual', 'sealed', 'extern', 'unsafe', 'volatile', 'abstract', 'async'}
KEYWORDS = {'return', 'new', 'in', 'case', 'throw', 'else', 'is', 'as', 'out', 'ref', 'await', 'yield', 'using',
            'goto', 'when', 'where', 'select', 'from', 'not', 'and', 'or', 'typeof', 'nameof', 'sizeof', 'default',
            'checked', 'unchecked', 'lock', 'if', 'while', 'for', 'foreach', 'switch', 'catch', 'do', 'try', 'finally',
            'break', 'continue', 'operator', 'this', 'base', 'null', 'true', 'false', 'orderby', 'group', 'by',
            'into', 'let', 'join', 'on', 'equals', 'ascending', 'descending'}


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


def read_source(path, ref, root):
    if ref:
        return subprocess.check_output(['git', 'show', f'{ref}:{path}'], cwd=root).decode()
    return (root / path).read_text()


def read_database_tree(ref, root):
    """Every C# source under the database folder, from a Git tree or the working files."""
    if not ref:
        return {path.relative_to(root).as_posix(): path.read_text()
                for path in (root / DATABASE).rglob('*.cs') if not path.name.startswith('._')}
    names = [name for name in subprocess.check_output(['git', 'ls-tree', '-r', '--name-only', ref, '--', DATABASE],
                                                      cwd=root, text=True).splitlines() if name.endswith('.cs')]
    output = subprocess.run(['git', 'cat-file', '--batch'], input=''.join(f'{ref}:{name}\n' for name in names).encode(),
                            cwd=root, capture_output=True, check=True).stdout
    files = {}
    position = 0
    for name in names:
        end = output.index(b'\n', position)
        header = output[position:end].split()
        if header[-1] == b'missing':
            raise ValueError(f'{name} is missing from {ref}')
        size = int(header[2])
        files[name] = output[end + 1:end + 1 + size].decode()
        position = end + 1 + size + 1
    return files


def matching_brace(items, opening):
    depth = 0
    for index in range(opening, len(items)):
        if items[index] == '{':
            depth += 1
        elif items[index] == '}':
            depth -= 1
            if not depth:
                return index
    raise ValueError('Unbalanced braces')


def class_bodies(items, name):
    return [(items.index('{', index + 2), matching_brace(items, items.index('{', index + 2)))
            for index in range(len(items) - 1) if items[index] == 'class' and items[index + 1] == name]


def name_before(items, index):
    """Index of the identifier before position index, skipping a generic parameter list."""
    index -= 1
    if items[index] == '>':
        depth = 0
        while index >= 0:
            if items[index] == '>':
                depth += 1
            elif items[index] == '<':
                depth -= 1
                if not depth:
                    break
            index -= 1
        index -= 1
    return index


def split_members(items, opening, closing):
    """Yield (name, tokens without leading modifiers, name index) for each member of a class body."""
    index = opening + 1
    while index < closing:
        start = index
        depth = 0
        assigned = False
        name = None
        while index < closing:
            item = items[index]
            if item == '{' and not depth and not assigned:
                if name is None:
                    name = name_before(items, index)
                index = matching_brace(items, index) + 1
                if index < closing and items[index] == '=':
                    assigned = True
                    continue
                break
            if item in ('(', '[', '{'):
                if item == '(' and not depth and name is None and not assigned:
                    name = name_before(items, index)
                depth += 1
            elif item in (')', ']', '}'):
                depth -= 1
            elif not depth and item == '=' and not assigned:
                assigned = True
                if name is None:
                    name = index - 1
            elif not depth and item == ';':
                if name is None:
                    name = index - 1
                index += 1
                break
            index += 1
        if name is None or name < start:
            continue
        skip = start
        while skip < name and items[skip] in MODIFIERS:
            skip += 1
        yield items[name], items[skip:index], name - skip


def generic_close(items, index):
    depth = 0
    for position in range(index, max(index - 16, -1), -1):
        item = items[position]
        if item == '>':
            depth += 1
        elif item == '<':
            depth -= 1
            if not depth:
                return position > 0 and IDENT.match(items[position - 1]) is not None
        elif not (IDENT.match(item) or item in (',', '.', '[', ']', '?')):
            return False
    return False


def declared_locals(items):
    """Names declared inside a member: locals, parameters and single lambda parameters."""
    found = []
    for index in range(1, len(items) - 1):
        item = items[index]
        if not IDENT.match(item) or item in KEYWORDS or items[index - 1] == '.':
            continue
        before, after = items[index - 1], items[index + 1]
        further = items[index + 2] if index + 2 < len(items) else ''
        if after == '=' and further == '>':
            declared = before in ('(', ',')
        elif after == '=' and further == '=':
            declared = False
        elif after in ('=', ';', ',', ')', 'in'):
            declared = ((IDENT.match(before) is not None and before not in KEYWORDS) or before == ']'
                        or (before == '>' and generic_close(items, index - 1)))
        else:
            declared = False
        if declared and item not in found:
            found.append(item)
    return found


class DatabaseTree:
    """Resolves class members referenced by migration declarations within one source tree."""

    def __init__(self, ref, root):
        self.files = read_database_tree(ref, root)
        self._items = {}
        self._candidates = None
        self._classes = {}
        self._members = {}

    def text(self, path):
        if path not in self.files:
            raise ValueError(f'{path} not found')
        return self.files[path]

    def items(self, path):
        if path not in self._items:
            self._items[path] = tokens(DIRECTIVE.sub('', self.text(path)))
        return self._items[path]

    def resolve_class(self, name, provider):
        if self._candidates is None:
            self._candidates = {}
            for path, text in self.files.items():
                for candidate in set(re.findall(r'\bclass\s+([A-Za-z_][A-Za-z_0-9]*)', text)):
                    self._candidates.setdefault(candidate, []).append(path)
        key = (name, provider)
        if key not in self._classes:
            paths = sorted(path for path in self._candidates.get(name, ())
                           if class_bodies(self.items(path), name))
            scoped = [path for path in paths if path.startswith(f'{DATABASE}/{provider}/')] or paths
            folders = {path.rsplit('/', 1)[0] for path in scoped}
            if len(folders) > 1:
                raise ValueError(f'Class {name} is ambiguous for {provider}: {", ".join(sorted(folders))}')
            self._classes[key] = (name, tuple(scoped)) if scoped else None
        return self._classes[key]

    def members(self, cls):
        if cls not in self._members:
            result = {}
            name, paths = cls
            for path in paths:
                items = self.items(path)
                for opening, closing in class_bodies(items, name):
                    for member, member_items, name_index in split_members(items, opening, closing):
                        result.setdefault(member, []).append((member_items, name_index))
            self._members[cls] = result
        return self._members[cls]

    def references(self, items, provider, context):
        """Yield (qualifier index or None, member index, class, member) for each class member reference."""
        context_members = self.members(context)
        for index, item in enumerate(items):
            if not IDENT.match(item) or item in KEYWORDS or (index and items[index - 1] == '.'):
                continue
            if items[index + 1:index + 2] == ['.'] and index + 2 < len(items) and IDENT.match(items[index + 2]):
                cls = self.resolve_class(item, provider)
                if cls is not None:
                    yield index, index + 2, cls, items[index + 2]
                    continue
            if item in context_members:
                yield None, index, context, item

    def normalize(self, provider, cls, items, name_index, enqueue):
        """Replace member references, the member's own name and names it declares with positional placeholders."""
        output = list(items)
        replaced = set()
        placeholders = {}
        edges = []
        for qualifier, index, target, member in self.references(items, provider, cls):
            if index == name_index:
                continue
            key = (target, member)
            if key not in placeholders:
                placeholders[key] = f'R{len(placeholders) + 1}'
                edges.append(str(enqueue(key)))
            if qualifier is not None:
                output[qualifier] = 'CLASS'
                replaced.add(qualifier)
            output[index] = placeholders[key]
            replaced.add(index)
        own = None
        if name_index is not None:
            own = items[name_index]
            output[name_index] = 'SELF'
            replaced.add(name_index)
        local_names = {}
        for name in declared_locals(items):
            if name != own:
                local_names.setdefault(name, f'L{len(local_names) + 1}')
        for index, item in enumerate(items):
            if index not in replaced and item in local_names and (not index or items[index - 1] != '.'):
                output[index] = local_names[item]
        return output, edges

    def statements(self, provider, root_items, context):
        """Digest the statement content a root reaches and list each referenced member in discovery order."""
        order = []
        positions = {}

        def enqueue(key):
            if key not in positions:
                positions[key] = len(order)
                order.append(key)
            return positions[key]

        root, edges = self.normalize(provider, context, root_items, None, enqueue)
        shape = root + ['|'] + edges
        entries = []
        position = 0
        while position < len(order):
            cls, member = order[position]
            position += 1
            display = f'{cls[0]}.{member}'
            parts = self.members(cls).get(member)
            if parts is None:
                entries.append({'member': display, 'sha256': None, 'contentSha256': None})
                shape += ['|', 'missing']
                continue
            exact, content, member_edges = [], [], []
            for member_items, name_index in parts:
                exact += member_items
                normalized, part_edges = self.normalize(provider, cls, member_items, name_index, enqueue)
                content += normalized
                member_edges += part_edges
            entry = {'member': display, 'sha256': digest(exact), 'contentSha256': digest(content)}
            entries.append(entry)
            shape += ['|', entry['contentSha256']] + member_edges
        return digest(shape), entries


def collect(ref, root, limits=None):
    """Declarations per provider, with statement content for versions at or below limits (all when None)."""
    tree = DatabaseTree(ref, root)
    result = {}
    for provider, path in SOURCES.items():
        source = tree.text(path)
        context = tree.resolve_class(CONTEXT_CLASS[provider], provider)
        if context is None:
            raise ValueError(f'{CONTEXT_CLASS[provider]} not found for {provider}')
        limit = None if limits is None else limits.get(provider)
        rows = []
        for row in declarations(source):
            entry = {field: row[field] for field in HEAD_FIELDS}
            if limit is None or row['version'] <= limit:
                statement_items = row['tokens'][:5] + row['tokens'][6:]
                entry['statementSha256'], entry['statements'] = tree.statements(provider, statement_items, context)
            rows.append(entry)
        initial = None
        if provider in INITIALIZERS:
            items = tokens(source.split(INITIALIZERS[provider], 1)[1].split('new SchemaMigration', 1)[0])
            statement_sha, entries = tree.statements(provider, items + LEDGER, context)
            initial = {'initializerSha256': digest(items), 'statementSha256': statement_sha, 'statements': entries}
        result[provider] = {'rows': rows, 'initial': initial}
    return result


def compare_statements(label, saved, now):
    """Name each referenced statement member that changed, disappeared or was renamed."""
    old, new = saved['statements'], now['statements']
    failures = []
    if len(old) == len(new):
        for before, after in zip(old, new):
            if after['sha256'] is None:
                failures.append(f"{label} statement member {after['member']} disappeared")
            elif before['member'] != after['member']:
                if before['contentSha256'] == after['contentSha256']:
                    failures.append(f"{label} statement member {before['member']} renamed to {after['member']}; statement content unchanged")
                else:
                    failures.append(f"{label} statement member {before['member']} changed and renamed to {after['member']}")
            elif before['contentSha256'] != after['contentSha256']:
                failures.append(f"{label} statement member {before['member']} changed")
            elif before['sha256'] != after['sha256']:
                failures.append(f"{label} statement member {before['member']} changed names only; statement content unchanged")
    else:
        found = {entry['member']: entry for entry in new}
        for before in old:
            after = found.get(before['member'])
            if after is None or after['sha256'] is None:
                failures.append(f"{label} statement member {before['member']} disappeared")
            elif before['contentSha256'] != after['contentSha256']:
                failures.append(f"{label} statement member {before['member']} changed")
        known = {entry['member'] for entry in old}
        failures += [f"{label} references new statement member {entry['member']}" for entry in new if entry['member'] not in known]
    if not failures and saved['statementSha256'] != now['statementSha256']:
        failures.append(f'{label} statement content changed outside referenced member bodies')
    return failures


def explain(label, saved, now):
    """Classify a changed declaration: description, names, or statement content."""
    if now is None:
        return f'EXPLAIN: {label}: declaration disappeared'
    old, new = saved['statements'], now['statements']
    parts = []
    if saved['statementSha256'] == now['statementSha256']:
        parts.append('statement content unchanged')
    else:
        if len(old) == len(new):
            changed = [after['member'] for before, after in zip(old, new) if before['contentSha256'] != after['contentSha256']]
        else:
            changed = sorted({entry['member'] for entry in new} ^ {entry['member'] for entry in old})
        parts.append('statement content changed in ' + (', '.join(changed) if changed else 'the declaration or its references'))
    if 'description' in saved:
        parts.append('description changed' if saved['description'] != now['description'] else 'description unchanged')
    renames = [f"{before['member']} -> {after['member']}" for before, after in zip(old, new)
               if len(old) == len(new) and before['member'] != after['member']]
    parts.append('names changed: ' + (', '.join(renames) if renames else 'none'))
    local = [after['member'] for before, after in zip(old, new) if len(old) == len(new) and after['sha256']
             and before['contentSha256'] == after['contentSha256'] and before['sha256'] != after['sha256']]
    if local:
        parts.append('declared names changed inside: ' + ', '.join(local))
    return f"EXPLAIN: {label}: {'; '.join(parts)}"


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--ref', help='Check a Git tree instead of working files')
    parser.add_argument('--root', type=Path, default=ROOT, help='Source checkout or isolated candidate tree')
    parser.add_argument('--write-manifest', action='store_true', help='Generate the initial manifest from an explicit fixed --ref')
    parser.add_argument('--explain', action='store_true',
                        help='Classify each changed protected declaration as a description, name or statement content change')
    args = parser.parse_args()
    if args.write_manifest and not args.ref:
        parser.error('--write-manifest requires an explicit fixed --ref')
    try:
        if args.write_manifest:
            current = collect(args.ref, args.root)
            commit = subprocess.check_output(['git', 'rev-parse', args.ref], cwd=args.root, text=True).strip()
            providers = {}
            for provider, found in current.items():
                providers[provider] = {'source': SOURCES[provider], 'maximumVersion': found['rows'][-1]['version'],
                                       'migrations': found['rows']}
                if found['initial'] is not None:
                    providers[provider]['initialStatements'] = found['initial']
            MANIFEST.write_text(json.dumps({'baselineCommit': commit, 'scope': 'Immutable declarations; not provider runtime proof',
                                            'providers': providers}, indent=2) + '\n')
            print('Wrote fixed migration manifest')
            return 0
        baseline = json.loads(MANIFEST.read_text())
        limits = {provider: saved['maximumVersion'] for provider, saved in baseline['providers'].items()}
        current = collect(args.ref, args.root, limits)
        failures = []
        explanations = []
        for provider, saved in baseline['providers'].items():
            found = {row['version']: row for row in current[provider]['rows']}
            expected = {row['version']: row for row in saved['migrations']}
            members = set()
            for version, row in expected.items():
                now = found.get(version)
                label = f'{provider} historical migration {version}'
                changed = []
                if now is None or {field: now[field] for field in HEAD_FIELDS} != {field: row[field] for field in HEAD_FIELDS}:
                    changed.append(f'{label} changed or disappeared')
                if now is not None:
                    changed += compare_statements(label, row, now)
                    members.update(entry['member'] for entry in now['statements'])
                failures += changed
                if args.explain and changed:
                    explanations.append(explain(f'{provider} {version}', row, now))
            for version in found.keys() - expected.keys():
                if version <= saved['maximumVersion']:
                    failures.append(f'{provider} reuses historical migration slot {version}')
            print(f"{provider}: {len(found)} declarations; maximum {max(found)}; protected {len(expected)}; "
                  f"statement members {len(members)}")
            if 'initialStatements' in saved:
                label = f'{provider} initial statements'
                now = current[provider]['initial']
                changed = []
                if now['initializerSha256'] != saved['initialStatements']['initializerSha256']:
                    changed.append(f'{provider} historical initial statement assembly changed')
                changed += compare_statements(label, saved['initialStatements'], now)
                failures += changed
                if args.explain and changed:
                    explanations.append(explain(label, saved['initialStatements'], now))
        for line in explanations:
            print(line)
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
