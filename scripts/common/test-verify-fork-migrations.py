#!/usr/bin/env python3
"""Negative controls for the fork migration source gate (no database access).

Each control copies the gate and the provider sources into a scratch Git
repository, writes a manifest there from a fixed commit, mutates the copy and
runs the gate. The committed manifest is not read, so a control measures the
gate's rules and not the state of the reviewed baseline.
"""
import importlib.util
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest

SCRIPT = Path(__file__).with_name('verify-fork-migrations.py')
SPEC = importlib.util.spec_from_file_location('migration_gate', SCRIPT)
GATE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(GATE)
GIT = ['git', '-c', 'user.name=gate', '-c', 'user.email=gate@example.invalid', '-c', 'commit.gpgsign=false']


class MigrationGateTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        for relative in set(GATE.SOURCES.values()) | {GATE.MYSQL_DDL}:
            target = self.root / relative
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_text((GATE.ROOT / relative).read_text())
        self.script = self.root / SCRIPT.relative_to(GATE.ROOT)
        self.script.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(SCRIPT, self.script)
        (self.root / 'docs/upstream-review').mkdir(parents=True, exist_ok=True)
        for command in (['init', '-q'], ['add', '-A'], ['commit', '-q', '-m', 'fixture']):
            subprocess.run(GIT + command, cwd=self.root, check=True, capture_output=True)
        written = subprocess.run([sys.executable, str(self.script), '--write-manifest', '--ref', 'HEAD'],
                                 cwd=self.root, capture_output=True, text=True)
        self.assertEqual(0, written.returncode, written.stdout + written.stderr)

    def path(self, relative):
        return self.root / relative

    def edit_file(self, relative, old, new):
        target = self.path(relative)
        source = target.read_text()
        self.assertTrue(old in source, 'Mutation target missing: ' + old)
        target.write_text(source.replace(old, new, 1))

    def edit(self, provider, old, new):
        self.edit_file(GATE.SOURCES[provider], old, new)

    def check(self, expected, *names):
        result = subprocess.run([sys.executable, str(self.script)], cwd=self.root,
                                capture_output=True, text=True)
        output = result.stdout + result.stderr
        self.assertEqual(expected, result.returncode, output)
        for name in names:
            self.assertIn(name, result.stderr, output)
        return output

    def member_block(self, relative, name):
        source = self.path(relative).read_text()
        start = source.index(f' {name} = ')
        start = source.rindex('\n', 0, start) + 1
        return source[start:source.index('};', start) + 2]

    def next_version(self, provider):
        return max(row['version'] for row in GATE.declarations(self.path(GATE.SOURCES[provider]).read_text())) + 1

    def test_unchanged_tree(self):
        self.check(0)

    def test_changed_historical_sql(self):
        self.edit('Sqlite', '@"CREATE TABLE', '@"CREATE TEMP TABLE')
        self.check(1)

    def test_removed_history(self):
        self.edit('Sqlite', 'new SchemaMigration(82,', 'new RemovedMigration(82,')
        self.check(1)

    def test_append_above_maximum(self):
        path = self.path(GATE.SOURCES['Sqlite'])
        # A declaration is sufficient here: this test checks numbering, not C# compilation.
        version = self.next_version('Sqlite')
        path.write_text(path.read_text() + f'\nnew SchemaMigration({version}, "sentinel", "SELECT 1");\n')
        self.check(0)

    def test_duplicate_version(self):
        path = self.path(GATE.SOURCES['Sqlite'])
        version = self.next_version('Sqlite') - 1
        path.write_text(path.read_text() + f'\nnew SchemaMigration({version}, "sentinel", "SELECT 1");\n')
        self.check(2)

    def test_reused_gap(self):
        self.edit('Postgresql', 'new SchemaMigration(26,',
                  'new SchemaMigration(14, "sentinel", "SELECT 1"),\n                new SchemaMigration(26,')
        self.check(1)

    def test_sqlserver_referenced_ddl(self):
        self.edit('SqlServer', 'CREATE TABLE tenants', 'CREATE TABLE broken_tenants')
        self.check(1, 'SqlServer referenced TableQueries member Tenants changed')

    def test_sqlserver_initial_assembly(self):
        self.edit('SqlServer', 'initialStatements.Add(index)', 'initialStatements.Add("SELECT 1")')
        self.check(1)

    def test_sqlserver_append_member_and_migration(self):
        version = self.next_version('SqlServer')
        member = f'MigrationV{version}Statements'
        self.edit('SqlServer', '        public static readonly string[] Indexes',
                  f'        public static readonly string[] {member} = new string[]\n'
                  f'        {{\n            @"ALTER TABLE missions ADD sentinel INT NULL;"\n        }};\n\n'
                  '        public static readonly string[] Indexes')
        path = self.path(GATE.SOURCES['SqlServer'])
        path.write_text(path.read_text() + f'\nnew SchemaMigration({version}, "sentinel", {member});\n')
        self.check(0)

    def test_mysql_referenced_ddl(self):
        self.edit_file(GATE.MYSQL_DDL, 'CREATE TABLE', 'CREATE TEMPORARY TABLE')
        self.check(1)

    def test_mysql_initial_assembly(self):
        self.edit('Mysql', 'initialStatements.Add(index)', 'initialStatements.Add("SELECT 1")')
        self.check(1)

    def test_mysql_append_member_and_migration(self):
        version = self.next_version('Mysql')
        member = f'MigrationV{version}Statements'
        self.edit_file(GATE.MYSQL_DDL, '        public static readonly string[] Indexes',
                       f'        public static readonly string[] {member} = new string[]\n'
                       f'        {{\n            @"ALTER TABLE missions ADD COLUMN sentinel INT NULL;"\n        }};\n\n'
                       '        public static readonly string[] Indexes')
        path = self.path(GATE.SOURCES['Mysql'])
        path.write_text(path.read_text() + f'\nnew SchemaMigration({version}, "sentinel", TableQueries.{member});\n')
        self.check(0)

    def test_mysql_edited_referenced_member_names_it(self):
        self.edit_file(GATE.MYSQL_DDL, 'public static readonly string[] MigrationV40Statements = new string[]\n        {\n',
                       'public static readonly string[] MigrationV40Statements = new string[]\n        {\n'
                       '            @"ALTER TABLE vessels ADD COLUMN sentinel INT NULL;",\n')
        self.check(1, 'Mysql referenced TableQueries member MigrationV40Statements changed')

    def test_mysql_edited_transitively_referenced_member_names_it(self):
        self.edit_file(GATE.MYSQL_DDL, 'CREATE TABLE IF NOT EXISTS prompt_templates (',
                       'CREATE TABLE IF NOT EXISTS broken_prompt_templates (')
        self.check(1, 'Mysql referenced TableQueries member PromptTemplates changed')

    def test_mysql_removed_referenced_member_names_it(self):
        block = self.member_block(GATE.MYSQL_DDL, 'MigrationV40Statements')
        self.edit_file(GATE.MYSQL_DDL, block, '')
        self.check(1, 'Mysql referenced TableQueries member MigrationV40Statements disappeared')


if __name__ == '__main__':
    unittest.main()
