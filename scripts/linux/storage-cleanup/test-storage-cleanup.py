#!/usr/bin/env python3
"""Tests for the host storage-cleanup scripts. Standard library only; no Docker or root needed.

Run: python3 scripts/linux/storage-cleanup/test-storage-cleanup.py
"""
import datetime
import glob
import importlib.util
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import time
import unittest

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))

import cleanup_config  # noqa: E402


def load_script(name):
    spec = importlib.util.spec_from_file_location(name.replace('-', '_').replace('.py', ''), HERE / name)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


build = load_script('prune-build-outputs.py')
docker = load_script('prune-docker-storage.py')

DAY = 86400
STAGE_SECTIONS = {
    'daily-storage-cleanup.py': 'orchestrator',
    'prune-build-outputs.py': 'buildOutputs',
    'prune-agent-caches.py': 'agentCaches',
    'prune-docker-storage.py': 'docker',
    'prune-test-databases.py': 'testDatabases',
}


def write_file(path, age_days, size=4096):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(b'x' * size)
    stamp = time.time() - age_days * DAY
    os.utime(path, (stamp, stamp))


def set_dir_age(path, age_days):
    stamp = time.time() - age_days * DAY
    os.utime(path, (stamp, stamp))


class ConfigLoadingTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.path = os.path.join(self.temp.name, 'config.json')

    def tearDown(self):
        self.temp.cleanup()

    def write(self, data):
        with open(self.path, 'w') as handle:
            json.dump(data, handle)

    def test_section_merges_over_defaults_and_ignores_comment_keys(self):
        self.write({'receiptDirectory': '/r', 'docker': {'_about': 'x', 'imageRepository': 'repo/a', 'retainedSetsRequired': 5}})
        values = cleanup_config.load_config(self.path, 'docker', ['imageRepository'],
                                            {'retainedSetsRequired': 3, 'danglingPruneUntil': '48h'})
        self.assertEqual(values, {'receiptDirectory': '/r', 'imageRepository': 'repo/a',
                                  'retainedSetsRequired': 5, 'danglingPruneUntil': '48h'})

    def test_section_value_overrides_shared_receipt_directory(self):
        self.write({'receiptDirectory': '/shared', 'buildOutputs': {'receiptDirectory': '/own', 'roots': []}})
        values = cleanup_config.load_config(self.path, 'buildOutputs', ['roots', 'receiptDirectory'])
        self.assertEqual(values['receiptDirectory'], '/own')

    def test_empty_list_satisfies_a_required_key(self):
        self.write({'receiptDirectory': '/r', 'buildOutputs': {'roots': []}})
        self.assertEqual(cleanup_config.load_config(self.path, 'buildOutputs', ['roots'])['roots'], [])

    def test_missing_required_key_names_every_missing_key(self):
        self.write({'testDatabases': {'targets': []}})
        with self.assertRaises(cleanup_config.ConfigError) as caught:
            cleanup_config.load_config(self.path, 'testDatabases',
                                       ['targets', 'runnerCommandPattern', 'runnerArgumentPatterns'])
        self.assertIn('runnerCommandPattern, runnerArgumentPatterns', str(caught.exception))
        self.assertIn('testDatabases', str(caught.exception))

    def test_null_required_key_is_missing(self):
        self.write({'agentCaches': {'home': None}})
        with self.assertRaises(cleanup_config.ConfigError) as caught:
            cleanup_config.load_config(self.path, 'agentCaches', ['home'])
        self.assertIn('home', str(caught.exception))

    def test_missing_section_file_and_bad_json_fail_closed(self):
        self.write({'docker': {}})
        with self.assertRaisesRegex(cleanup_config.ConfigError, 'no "agentCaches" section'):
            cleanup_config.load_config(self.path, 'agentCaches')
        with self.assertRaisesRegex(cleanup_config.ConfigError, 'not found'):
            cleanup_config.load_config(self.path + '.absent', 'docker')
        with open(self.path, 'w') as handle:
            handle.write('{not json')
        with self.assertRaisesRegex(cleanup_config.ConfigError, 'unreadable'):
            cleanup_config.load_config(self.path, 'docker')
        with open(self.path, 'w') as handle:
            handle.write('[]')
        with self.assertRaisesRegex(cleanup_config.ConfigError, 'JSON object'):
            cleanup_config.load_config(self.path, 'docker')

    def test_every_script_refuses_to_run_without_its_section(self):
        # Each stage must stop before touching anything when its section is absent.
        self.write({'receiptDirectory': os.path.join(self.temp.name, 'receipts')})
        for script, section in STAGE_SECTIONS.items():
            with self.subTest(script=script):
                result = subprocess.run([sys.executable, str(HERE / script), '--config', self.path],
                                        capture_output=True, text=True, timeout=60)
                self.assertNotEqual(result.returncode, 0)
                # The agent stage reports its error as JSON, so the quotes arrive escaped.
                self.assertIn('no "' + section + '" section', (result.stdout + result.stderr).replace('\\"', '"'))
        self.assertFalse(os.path.exists(os.path.join(self.temp.name, 'receipts')))

    def test_example_config_satisfies_every_stage(self):
        example = str(HERE / 'storage-cleanup.example.json')
        required = {
            'orchestrator': ['receiptDirectory'],
            'buildOutputs': ['roots', 'receiptDirectory'],
            'agentCaches': ['home'],
            'docker': ['imageRepository'],
            'testDatabases': ['targets', 'runnerCommandPattern', 'runnerArgumentPatterns'],
        }
        for section, keys in required.items():
            with self.subTest(section=section):
                cleanup_config.load_config(example, section, keys)


class ScratchExpiryTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name).resolve() / 'scratch'
        self.root.mkdir()
        write_file(self.root / 'old' / 'clone' / 'a.bin', 10)
        write_file(self.root / 'old' / 'b.txt', 9)
        write_file(self.root / 'recent' / 'a.bin', 10)
        write_file(self.root / 'recent' / 'deep' / 'new.log', 0.1)
        write_file(self.root / 'kept' / 'a.bin', 10)
        write_file(self.root / 'kept' / 'nested' / '.armada-keep', 10, size=0)
        write_file(self.root / 'pinned' / 'a.bin', 10)
        self.excludes = [str(self.root / 'pinned')]

    def tearDown(self):
        self.temp.cleanup()

    def run_pass(self, apply, process_paths=()):
        report = {'candidates': [], 'removed': [], 'failures': [], 'skipped': {}}

        def skip(reason):
            report['skipped'][reason] = report['skipped'].get(reason, 0) + 1

        handled = build.prune_scratch([str(self.root)], 3, self.excludes, apply, report, skip,
                                      lambda: list(process_paths))
        return report, handled

    def names(self, items):
        return sorted(Path(item['path']).name for item in items)

    def test_dry_run_reports_with_bytes_and_removes_nothing(self):
        report, handled = self.run_pass(apply=False)
        self.assertEqual(self.names(report['candidates']), ['old'])
        self.assertEqual(report['removed'], [])
        self.assertGreater(report['candidates'][0]['bytes'], 0)
        self.assertEqual(report['candidates'][0]['kind'], 'scratch_folder')
        self.assertEqual(handled, {str(self.root / 'old')})
        for name in ('old', 'recent', 'kept', 'pinned'):
            self.assertTrue((self.root / name).is_dir(), name)
        self.assertEqual(report['skipped'], {'recent_scratch': 1, 'keep_marker': 1, 'excluded': 1})

    def test_apply_removes_only_the_old_folder(self):
        report, _ = self.run_pass(apply=True)
        self.assertEqual(self.names(report['removed']), ['old'])
        self.assertFalse((self.root / 'old').exists())
        for name in ('recent', 'kept', 'pinned'):
            self.assertTrue((self.root / name).is_dir(), name)
        self.assertEqual(report['failures'], [])

    def test_age_uses_file_mtimes_not_directory_mtimes(self):
        # Old files under freshly touched directories: expired.
        write_file(self.root / 'touched-dirs' / 'sub' / 'a.bin', 10)
        # A new file under directories stamped long ago: still in use.
        write_file(self.root / 'old-dirs' / 'sub' / 'a.bin', 0.1)
        set_dir_age(self.root / 'old-dirs' / 'sub', 30)
        set_dir_age(self.root / 'old-dirs', 30)
        info = build.folder_inventory(self.root / 'touched-dirs')
        self.assertLess(info['newest_mtime'], time.time() - 9 * DAY)
        report, _ = self.run_pass(apply=True)
        self.assertEqual(self.names(report['removed']), ['old', 'touched-dirs'])
        self.assertTrue((self.root / 'old-dirs').is_dir())

    def test_exclusion_inside_a_folder_protects_the_whole_folder(self):
        write_file(self.root / 'holder' / 'evidence' / 'a.bin', 10)
        self.excludes.append(str(self.root / 'holder' / 'evidence'))
        report, _ = self.run_pass(apply=True)
        self.assertTrue((self.root / 'holder' / 'evidence' / 'a.bin').is_file())
        self.assertEqual(self.names(report['removed']), ['old'])

    def test_process_reference_inside_the_folder_blocks_removal(self):
        report, _ = self.run_pass(apply=True, process_paths=[str(self.root / 'old' / 'clone' / 'a.bin')])
        self.assertTrue((self.root / 'old').is_dir())
        self.assertEqual(report['removed'], [])
        self.assertEqual(report['skipped'].get('active'), 1)

    def test_link_leaving_the_folder_blocks_it_and_internal_link_does_not(self):
        outside = Path(self.temp.name).resolve() / 'outside.txt'
        outside.write_text('keep me')
        (self.root / 'old' / 'out-link').symlink_to(outside)
        write_file(self.root / 'internal' / 'a.bin', 10)
        (self.root / 'internal' / 'in-link').symlink_to(self.root / 'internal' / 'a.bin')
        report, _ = self.run_pass(apply=True)
        self.assertTrue((self.root / 'old').is_dir())
        self.assertEqual(self.names(report['removed']), ['internal'])
        self.assertEqual(outside.read_text(), 'keep me')
        self.assertEqual(report['skipped'].get('symlink'), 1)

    def test_symlinked_child_is_never_followed(self):
        target = Path(self.temp.name).resolve() / 'target'
        write_file(target / 'a.bin', 10)
        (self.root / 'linked').symlink_to(target, target_is_directory=True)
        report, _ = self.run_pass(apply=True)
        self.assertTrue((target / 'a.bin').is_file())
        self.assertNotIn('linked', self.names(report['candidates']))

    @unittest.skipIf(os.path.isdir('/proc/self') and os.geteuid() != 0,
                     'the CLI process scan reads every /proc entry and needs root on Linux')
    def test_cli_plans_without_apply_and_removes_with_apply(self):
        receipts = Path(self.temp.name).resolve() / 'receipts'
        config = Path(self.temp.name) / 'config.json'
        config.write_text(json.dumps({
            'receiptDirectory': str(receipts),
            'buildOutputs': {'roots': [], 'exclusions': self.excludes, 'scratchRoots': [str(self.root)],
                             'wholeFolderRetentionDays': 3, 'diskUsagePath': self.temp.name,
                             'lockPath': str(Path(self.temp.name).resolve() / 'build.lock')}}))
        command = [sys.executable, str(HERE / 'prune-build-outputs.py'), '--config', str(config)]

        def last_receipt():
            return json.loads(Path(sorted(glob.glob(str(receipts / 'build-cleanup-*.json')))[-1]).read_text())

        plan = subprocess.run(command, capture_output=True, text=True, timeout=60)
        self.assertEqual(plan.returncode, 0, plan.stdout + plan.stderr)
        self.assertTrue((self.root / 'old').is_dir())
        receipt = last_receipt()
        self.assertEqual(receipt['mode'], 'plan')
        self.assertEqual(self.names(receipt['candidates']), ['old'])
        self.assertEqual(receipt['candidate_bytes'], receipt['candidates'][0]['bytes'])
        self.assertGreater(receipt['candidate_bytes'], 0)
        time.sleep(0.01)
        applied = subprocess.run(command + ['--apply'], capture_output=True, text=True, timeout=60)
        self.assertEqual(applied.returncode, 0, applied.stdout + applied.stderr)
        self.assertFalse((self.root / 'old').exists())
        for name in ('recent', 'kept', 'pinned'):
            self.assertTrue((self.root / name).is_dir(), name)
        receipt = last_receipt()
        self.assertEqual(receipt['mode'], 'apply')
        self.assertEqual(self.names(receipt['removed']), ['old'])
        self.assertEqual(receipt['removed_bytes'], receipt['candidate_bytes'])


class UnusedImageTagSelectionTests(unittest.TestCase):
    NOW = datetime.datetime(2026, 6, 1, tzinfo=datetime.timezone.utc)

    def image(self, image_id, tags, age_days):
        created = self.NOW - datetime.timedelta(days=age_days)
        # Docker reports nanoseconds; the parser must accept them.
        return {'Id': image_id, 'RepoTags': tags, 'Size': 1000,
                'Created': created.strftime('%Y-%m-%dT%H:%M:%S') + '.123456789Z'}

    def settings(self, **overrides):
        values = {'imageRepository': 'local/app-server',
                  'unusedImageTagPatterns': ['candidate-*', 'proof*'],
                  'unusedImageMinAgeDays': 7,
                  'protectedImageTags': ['local/app-server:1.0.0'],
                  'protectedTagPatterns': ['armada-retained-*']}
        values.update(overrides)
        return values

    def select(self, images, containers=(), **overrides):
        return [item['tag'] for item in docker.select_unused_tags(images, list(containers), self.settings(**overrides), self.NOW)]

    def test_old_unused_matching_tag_is_selected_with_its_size(self):
        selected = docker.select_unused_tags([self.image('sha256:a', ['local/app-server:candidate-1'], 10)], [],
                                             self.settings(), self.NOW)
        self.assertEqual(selected, [{'tag': 'local/app-server:candidate-1', 'image': 'sha256:a', 'bytes': 1000}])

    def test_tag_used_by_any_container_is_kept(self):
        images = [self.image('sha256:a', ['local/app-server:candidate-1'], 10)]
        self.assertEqual(self.select(images, containers=[{'Image': 'sha256:a'}]), [])

    def test_image_of_a_protected_tag_is_kept(self):
        images = [self.image('sha256:a', ['local/app-server:1.0.0', 'local/app-server:candidate-1'], 10)]
        self.assertEqual(self.select(images), [])

    def test_absent_protected_tag_protects_nothing(self):
        images = [self.image('sha256:a', ['local/app-server:candidate-1'], 10)]
        self.assertEqual(self.select(images, protectedImageTags=['local/app-server:9.9.9']),
                         ['local/app-server:candidate-1'])

    def test_recent_tag_is_kept(self):
        images = [self.image('sha256:a', ['local/app-server:candidate-1'], 6.9)]
        self.assertEqual(self.select(images), [])

    def test_protected_pattern_and_rollback_tags_are_never_selected(self):
        images = [self.image('sha256:a', ['local/app-server:armada-retained-tag-20260101T000000Z-abc'], 30),
                  self.image('sha256:b', ['local/app-server:rollback-before-abc123'], 30),
                  self.image('sha256:c', ['local/app-server:candidate-armada-retained-x'], 30)]
        self.assertEqual(self.select(images, unusedImageTagPatterns=['*']), ['local/app-server:candidate-armada-retained-x'])
        self.assertEqual(self.select(images, unusedImageTagPatterns=['*'], protectedTagPatterns=['*armada-retained-*']), [])

    def test_patterns_match_the_tag_the_repository_or_the_full_reference(self):
        images = [self.image('sha256:a', ['proof-runner:latest'], 10),
                  self.image('sha256:b', ['local/app-server:proof-2'], 10),
                  self.image('sha256:c', ['registry.example:5000/team/web:candidate-9'], 10),
                  self.image('sha256:d', ['local/app-server:latest'], 10),
                  self.image('sha256:e', ['local/other:v1'], 10)]
        self.assertEqual(self.select(images), ['local/app-server:proof-2', 'proof-runner:latest',
                                               'registry.example:5000/team/web:candidate-9'])
        self.assertEqual(self.select(images, unusedImageTagPatterns=['local/other:*']), ['local/other:v1'])

    def test_no_patterns_selects_nothing(self):
        images = [self.image('sha256:a', ['local/app-server:candidate-1'], 10)]
        self.assertEqual(self.select(images, unusedImageTagPatterns=[]), [])

    def test_untagged_images_are_ignored(self):
        images = [{'Id': 'sha256:a', 'RepoTags': None, 'Created': '2020-01-01T00:00:00Z'}]
        self.assertEqual(self.select(images, unusedImageTagPatterns=['*']), [])

    def test_created_parser_accepts_docker_stamps(self):
        for value in ('2026-01-02T03:04:05Z', '2026-01-02T03:04:05.1Z', '2026-01-02T03:04:05.123456789Z',
                      '2026-01-02T03:04:05.5+00:00'):
            with self.subTest(value=value):
                parsed = docker.created_at({'Created': value})
                self.assertEqual(parsed.replace(microsecond=0),
                                 datetime.datetime(2026, 1, 2, 3, 4, 5, tzinfo=datetime.timezone.utc))


if __name__ == '__main__':
    unittest.main(verbosity=2)
