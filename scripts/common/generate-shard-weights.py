#!/usr/bin/env python3
"""Generate test/Armada.Test.Unit/shard-weights.json from unit test logs.

Each log is the console output of the unit runner, serial or one shard. A suite's
weight is the sum of its cases' elapsed milliseconds, in seconds. When the same
suite appears in more than one log, the largest total wins, so a quiet run and a
contended run of the same tree do not average each other away.

Pass --suites with the output of `--list-suites` to attribute nested runner output
(the runner contract suite starts inner runners that print their own headers) to
the registered suite that printed it.

Usage:
  python3 scripts/common/generate-shard-weights.py unit.log [more.log ...] \
      [--suites suites.txt] [--default 1.0] > test/Armada.Test.Unit/shard-weights.json
"""

import argparse
import json
import re
import sys

HEADER = re.compile(r'^--- (.*) ---$')
CASE = re.compile(r'(?:PASS|FAIL)\s+.*\((\d+)ms\)\s*$')
ANSI = re.compile(r'\x1b\[[0-9;]*m')


def read_log(path, registered):
    totals = {}
    current = None
    with open(path, encoding='utf-8', errors='replace') as handle:
        for raw in handle:
            line = ANSI.sub('', raw.rstrip('\n'))
            header = HEADER.match(line)
            if header:
                name = header.group(1)
                if registered is None or name in registered:
                    current = name
                    totals.setdefault(current, 0)
                continue
            case = CASE.search(line)
            if case and current is not None:
                totals[current] += int(case.group(1))
    return totals


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('logs', nargs='+')
    parser.add_argument('--suites', help='file with one registered suite name per line')
    parser.add_argument('--default', type=float, default=1.0, help='seconds for a suite no log names')
    args = parser.parse_args()

    registered = None
    if args.suites:
        with open(args.suites, encoding='utf-8') as handle:
            registered = {line.rstrip('\n') for line in handle if line.strip()}

    merged = {}
    for path in args.logs:
        for name, ms in read_log(path, registered).items():
            merged[name] = max(merged.get(name, 0), ms)

    if not merged:
        sys.exit('No suite headers found in the given logs.')

    document = {
        'DefaultSeconds': args.default,
        'Suites': {name: round(merged[name] / 1000.0, 1) for name in sorted(merged)},
    }
    json.dump(document, sys.stdout, indent=2, ensure_ascii=False)
    sys.stdout.write('\n')


if __name__ == '__main__':
    main()
