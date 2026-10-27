"""Runs every registered test. Exit code 0 when all pass, 1 when any fails, 2 when a required test is not registered.

Arguments: --list prints the registered names; --require NAME fails unless NAME is registered (repeatable).
Run from the repository root: python3 -B -m tests.run
"""

import sys
import unittest
from typing import List

from tests import registry


def main(args: List[str]) -> int:
    """Run the registered tests and return the exit code."""
    tests = registry.all_tests()
    names = {test._testMethodName for test in tests}

    list_only = False
    i = 0
    while i < len(args):
        if args[i] == "--list":
            list_only = True
        elif args[i] == "--require" and i + 1 < len(args):
            i += 1
            if args[i] not in names:
                print("MISSING " + args[i] + " is not registered in tests/registry.py.")
                return 2
        i += 1

    if list_only:
        for test in tests:
            print(test._testMethodName)
        return 0

    failed = 0
    for test in tests:
        result = unittest.TestResult()
        test.run(result)
        problems = result.errors + result.failures + [(test, "unexpected success")] * len(result.unexpectedSuccesses)
        if problems:
            failed += 1
            detail = problems[0][1].strip().splitlines()[-1]
            print("FAIL " + test._testMethodName + ": " + detail)
        else:
            print("PASS " + test._testMethodName)

    print(str(len(tests) - failed) + " passed, " + str(failed) + " failed")
    return 0 if failed == 0 else 1


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
