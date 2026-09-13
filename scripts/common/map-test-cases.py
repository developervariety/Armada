#!/usr/bin/env python3
"""Map observed fork cases to their retained executable ownership (no test execution)."""
import argparse
from functools import lru_cache
import hashlib
import json
from pathlib import Path
import re
import subprocess

ROOT = Path(__file__).resolve().parents[2]
PROGRAMS = {
    'Test.Unit': 'test/Armada.Test.Unit/Program.cs',
    'Test.Automated': 'test/Armada.Test.Automated/Program.cs',
    'Armada.Test.Runtimes': 'test/Armada.Test.Runtimes/Program.cs',
}
BASE_COUNTS = {'Test.Unit': 3982, 'Test.Automated': 907, 'Armada.Test.Runtimes': 183}


@lru_cache(maxsize=None)
def git_source(commit, path):
    result = subprocess.run(['git', 'show', commit + ':' + path], cwd=ROOT, capture_output=True)
    return result.stdout if result.returncode == 0 else None


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--manifest-directory', required=True, type=Path)
    parser.add_argument('--baseline', default='11fd66a208d9c651bc002a2dd7000a980d31f444')
    parser.add_argument('--baseline-manifest-directory', required=True, type=Path)
    parser.add_argument('--output', required=True, type=Path)
    args = parser.parse_args()
    baseline = subprocess.check_output(['git', 'rev-parse', args.baseline], cwd=ROOT, text=True).strip()
    mapped = []
    seen_runs = set()
    for path in sorted(args.manifest_directory.glob('*.json')):
        if path.name.startswith('._'):
            continue
        run = json.loads(path.read_text())
        executable = run['Executable']
        provider = run.get('Provider')
        key = (executable, provider)
        if key in seen_runs:
            raise ValueError('Duplicate executable/provider manifest: ' + str(key))
        seen_runs.add(key)
        if run.get('RunError') or run.get('RequestedFilters'):
            raise ValueError('A full successful run is required for the ownership map')
        registered = run.get('RegisteredSuites', [])
        old_suites = set()
        if executable in PROGRAMS:
            source = git_source(baseline, PROGRAMS[executable])
            if source is None:
                raise ValueError('Missing baseline entry point')
            constructors = re.findall(r'runner\.AddSuite\(new\s+([\w.]+)\(', source.decode())
            for constructor in constructors:
                matches = [row['SuiteId'] for row in registered
                           if row['SuiteId'] == constructor or row['SuiteId'].endswith('.' + constructor)]
                if len(matches) != 1:
                    raise ValueError('Baseline registration missing or ambiguous: ' + constructor)
                old_suites.add(matches[0])
            if len(old_suites) != len(constructors):
                raise ValueError('Duplicate baseline suite registration')
        elif executable != 'Armada.Test.Database':
            raise ValueError('Unknown executable: ' + executable)
        build_sources = run.get('BuildSources', {})
        if not build_sources or not run.get('ExecutableSha256'):
            raise ValueError('Missing build evidence')
        for source_path, checksum in build_sources.items():
            source = ROOT / source_path
            if not source_path.startswith('test/') or '..' in Path(source_path).parts:
                raise ValueError('Invalid build source path')
            if not source.is_file() or hashlib.sha256(source.read_bytes()).hexdigest() != checksum:
                raise ValueError('Source changed since this test build: ' + source_path)
        if executable in PROGRAMS:
            old_run = json.loads((args.baseline_manifest_directory / (executable + '.json')).read_text())
            old_cases = {(case['SuiteId'], case['CaseId']) for case in old_run['Cases']}
            if len(old_cases) != BASE_COUNTS[executable]:
                raise ValueError('Incomplete baseline case inventory')
        else:
            old_cases = set()
            for file, reversed_arguments in [('DatabaseTestRunner.cs', False), ('MultiTenantScopingTests.cs', True)]:
                source = git_source(baseline, 'test/Armada.Test.Database/' + file).decode()
                for first, second in re.findall(r'await RunTest\("([^"\n]+)", "([^"\n]+)"', source):
                    category, name = (first, first + ' / ' + second) if reversed_arguments else (second, first)
                    if name.startswith('MySQL_') and provider != 'Mysql':
                        continue
                    old_cases.add(('Armada.Test.Database.' + category, name))
        cases = []
        identities = set()
        retained = 0
        for case in run['Cases']:
            identity = (case['SuiteId'], case['CaseId'])
            if identity in identities:
                raise ValueError('Duplicate executed case: ' + str(identity))
            identities.add(identity)
            if case['Outcome'] != 'passed':
                raise ValueError('Nonpassing case requires a reviewed disposition: ' + str(identity))
            source_path = case['SourcePath']
            if not source_path.startswith('test/') or '..' in Path(source_path).parts or any(part.startswith('._') for part in Path(source_path).parts):
                raise ValueError('Invalid repository source anchor')
            current = (ROOT / source_path).read_bytes()
            if build_sources.get(source_path) != hashlib.sha256(current).hexdigest():
                raise ValueError('Case lacks matching build source evidence: ' + source_path)
            previous = git_source(baseline, source_path)
            if executable == 'Armada.Test.Database':
                ownership = 'retained' if identity in old_cases else 'new-regression'
            else:
                ownership = 'retained' if identity in old_cases else 'activated-existing' if previous else 'new-regression'
            retained += ownership == 'retained'
            cases.append({'suite': case['SuiteId'], 'case': case['CaseId'], 'ownership': ownership,
                          'source': source_path, 'line': case['SourceLine'],
                          'sourceSha256': hashlib.sha256(current).hexdigest(),
                          'baselineSourceSha256': hashlib.sha256(previous).hexdigest() if previous else None})
        if not old_cases.issubset(identities):
            raise ValueError('Baseline cases missing: ' + str(old_cases - identities))
        expected = BASE_COUNTS.get(executable, 48 if provider == 'Mysql' else 47)
        if retained != expected:
            raise ValueError(f'{executable}/{provider}: expected {expected} retained cases, observed {retained}')
        mapped.append({'executable': executable, 'provider': provider,
                       'actualDriver': run.get('ActualDriver'), 'retainedCount': retained,
                       'executedCount': len(cases), 'registeredSuites': registered,
                       'cases': sorted(cases, key=lambda row: (row['suite'], row['case']))})
    required = {(name, None) for name in PROGRAMS} | {('Armada.Test.Database', name) for name in ['Sqlite', 'Postgresql', 'Mysql', 'SqlServer']}
    if seen_runs != required:
        raise ValueError('Missing or unexpected executable/provider manifests: ' + str(required ^ seen_runs))
    args.output.write_text(json.dumps({'schemaVersion': 1, 'baseline': baseline,
        'scope': 'Baseline case identities retain executable ownership; portable-symbol checksums bind observed cases to build source. Not a line-by-line assertion equivalence claim.',
        'runs': mapped}, indent=2) + '\n')
    print('PASS: all baseline case counts and suite registrations retained across seven executable/provider runs')


if __name__ == '__main__':
    main()
