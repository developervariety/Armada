"""Exercise deployment safety without contacting a real server."""
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest

SCRIPT = Path(__file__).resolve().parents[1] / 'apply-prompt-improvements.py'
SPEC = importlib.util.spec_from_file_location('apply_prompt_improvements', SCRIPT)
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)


class DeploymentTests(unittest.TestCase):
    """Preview, metadata preservation, idempotence, and conditional rollback."""

    def setUp(self):
        self.original = {'Content': 'Operator instructions.', 'Description': 'Custom', 'Scope': 'User',
                         'TenantId': 'test', 'UserId': 'user', 'IsBuiltIn': True, 'Category': 'persona', 'Active': True}
        self.rows = {'persona.worker': dict(self.original), 'persona.judge': dict(self.original)}
        self.writes = []
        self.fail_name = None
        self.addition = '\n\n' + MODULE.MARKER + '\nTool honesty.\n'
        self.additions = {name: self.addition for name in self.rows}

    def api(self, base, token, method, path, data=None):
        name = path.rsplit('/', 1)[1]
        if method == 'GET':
            return dict(self.rows[name])
        self.writes.append(name)
        if self.fail_name == name:
            raise RuntimeError('Injected failure')
        self.rows[name].update(data)
        return dict(self.rows[name])

    def test_preview_has_no_writes(self):
        receipt = MODULE.deploy('http://test', 'secret', self.additions, None, api=self.api)
        self.assertEqual([], self.writes)
        self.assertTrue(all(r['action'] == 'append' for r in receipt))
        self.assertNotIn('secret', json.dumps(receipt))
        self.assertNotIn('Operator instructions', json.dumps(receipt))

    def test_apply_preserves_operator_prefix_and_metadata_and_is_idempotent(self):
        with tempfile.TemporaryDirectory() as folder:
            backup = Path(folder) / 'backup.json'
            MODULE.deploy('http://test', 'secret', self.additions, backup, True, self.api)
            self.assertEqual(0o600, backup.stat().st_mode & 0o777)
            for row in self.rows.values():
                self.assertEqual(self.original['Content'] + self.addition, row['Content'])
                self.assertEqual('User', row['Scope'])
                self.assertEqual('Custom', row['Description'])
            self.writes.clear()
            receipt = MODULE.deploy('http://test', 'secret', self.additions, None, api=self.api)
            self.assertTrue(all(r['action'] == 'unchanged' for r in receipt))
            self.assertEqual([], self.writes)

    def test_conflicting_guidance_preflight_aborts_before_writes(self):
        self.rows['persona.judge']['Content'] += '\n' + MODULE.MARKER + '\nDifferent.'
        with self.assertRaises(ValueError):
            MODULE.deploy('http://test', 'secret', self.additions, None, api=self.api)
        self.assertEqual([], self.writes)

    def test_failure_restores_previously_written_content(self):
        self.fail_name = 'persona.judge'
        with tempfile.TemporaryDirectory() as folder:
            with self.assertRaises(RuntimeError):
                MODULE.deploy('http://test', 'secret', self.additions, Path(folder) / 'backup.json', True, self.api)
        self.assertEqual(self.original, self.rows['persona.worker'])

    def test_backup_cannot_be_in_repository(self):
        with self.assertRaises(ValueError):
            MODULE.external_write(MODULE.REPO / 'secret-backup.json', {})

    def test_backup_does_not_overwrite_existing_file(self):
        with tempfile.TemporaryDirectory() as folder:
            p = Path(folder) / 'backup.json'
            MODULE.external_write(p, {'old': True})
            with self.assertRaises(FileExistsError):
                MODULE.external_write(p, {'new': True})
            self.assertTrue(json.loads(p.read_text())['old'])

    def test_rollback_refuses_concurrent_operator_edit(self):
        entry = {'name': 'persona.worker', 'template': self.original,
                 'beforeSha256': MODULE.digest(self.original['Content']),
                 'afterSha256': MODULE.digest(self.original['Content'] + self.addition)}
        self.rows['persona.worker']['Content'] = 'Concurrent edit'
        with self.assertRaises(RuntimeError):
            MODULE.rollback_entry('http://test', 'secret', entry, self.api)
        self.assertEqual([], self.writes)

    def test_rollback_refuses_concurrent_metadata_edit(self):
        content = self.original['Content'] + self.addition
        entry = {'name': 'persona.worker', 'template': self.original,
                 'beforeSha256': MODULE.digest(self.original['Content']), 'afterSha256': MODULE.digest(content)}
        self.rows['persona.worker']['Content'] = content
        self.rows['persona.worker']['Scope'] = 'Tenant'
        with self.assertRaises(RuntimeError):
            MODULE.rollback_entry('http://test', 'secret', entry, self.api)
        self.assertEqual([], self.writes)

    def test_existing_seeded_guidance_before_recall_is_unchanged(self):
        content = self.original['Content'] + self.addition + '\n## Recall\n'
        self.assertEqual((content, 'unchanged'), MODULE.preview(content, self.addition))


if __name__ == '__main__':
    unittest.main()
