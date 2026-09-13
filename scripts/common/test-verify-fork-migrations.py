#!/usr/bin/env python3
"""Negative controls for the fork migration source gate (no database access)."""
import importlib.util
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

SCRIPT = Path(__file__).with_name('verify-fork-migrations.py')
SPEC = importlib.util.spec_from_file_location('migration_gate', SCRIPT)
GATE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(GATE)


class MigrationGateTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        for relative in set(GATE.SOURCES.values()) | {GATE.MYSQL_DDL}:
            target = self.root / relative
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_text((GATE.ROOT / relative).read_text())

    def edit(self, provider, old, new):
        target = self.root / GATE.SOURCES[provider]
        source = target.read_text()
        self.assertTrue(old in source, "Mutation target missing: " + old)
        target.write_text(source.replace(old, new, 1))

    def check(self, expected):
        result = subprocess.run([sys.executable, str(SCRIPT), '--root', str(self.root)],
                                capture_output=True, text=True)
        self.assertEqual(expected, result.returncode, result.stdout + result.stderr)

    def test_current_tree(self):
        self.check(0)

    def test_changed_historical_sql(self):
        self.edit('Sqlite', '@"CREATE TABLE', '@"CREATE TEMP TABLE')
        self.check(1)

    def test_removed_history(self):
        self.edit('Sqlite', 'new SchemaMigration(82,', 'new RemovedMigration(82,')
        self.check(1)

    def test_append_above_maximum(self):
        path = self.root / GATE.SOURCES['Sqlite']
        source = path.read_text()
        # A declaration is sufficient here: this test checks numbering, not C# compilation.
        version = max(row['version'] for row in GATE.declarations(source)) + 1
        path.write_text(source + f'\nnew SchemaMigration({version}, "sentinel", "SELECT 1");\n')
        self.check(0)

    def test_duplicate_version(self):
        path = self.root / GATE.SOURCES['Sqlite']
        source = path.read_text()
        version = max(row['version'] for row in GATE.declarations(source))
        path.write_text(source + f'\nnew SchemaMigration({version}, "sentinel", "SELECT 1");\n')
        self.check(2)

    def test_reused_gap(self):
        self.edit('Postgresql', 'new SchemaMigration(26,',
                  'new SchemaMigration(14, "sentinel", "SELECT 1"),\n                new SchemaMigration(26,')
        self.check(1)

    def test_sqlserver_referenced_ddl(self):
        self.edit('SqlServer', 'CREATE TABLE tenants', 'CREATE TABLE broken_tenants')
        self.check(1)

    def test_sqlserver_initial_assembly(self):
        self.edit('SqlServer', 'initialStatements.Add(index)', 'initialStatements.Add("SELECT 1")')
        self.check(1)

    def test_mysql_referenced_ddl(self):
        path = self.root / GATE.MYSQL_DDL
        path.write_text(path.read_text().replace('CREATE TABLE', 'CREATE TEMPORARY TABLE', 1))
        self.check(1)

    def test_mysql_initial_assembly(self):
        self.edit('Mysql', 'initialStatements.Add(index)', 'initialStatements.Add("SELECT 1")')
        self.check(1)


if __name__ == '__main__':
    unittest.main()
