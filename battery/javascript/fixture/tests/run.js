'use strict';

// Runs every registered test. Exit code 0 when all pass, 1 when any fails, 2 when a required test is not registered.
// Arguments: --list prints the registered names; --require NAME fails unless NAME is registered (repeatable).

const registry = require('./registry');

/**
 * Run the registered tests.
 * @param {string[]} args Command-line arguments.
 * @returns {number} Exit code.
 */
function main(args) {
  const tests = registry.all();
  const names = new Set(tests.map((test) => test.name));

  let listOnly = false;
  for (let i = 0; i < args.length; i++) {
    if (args[i] === '--list') {
      listOnly = true;
    } else if (args[i] === '--require' && i + 1 < args.length) {
      const required = args[++i];
      if (!names.has(required)) {
        console.log('MISSING ' + required + ' is not registered in tests/registry.js.');
        return 2;
      }
    }
  }

  if (listOnly) {
    for (const test of tests) console.log(test.name);
    return 0;
  }

  let failed = 0;
  for (const test of tests) {
    try {
      if (typeof test.body !== 'function') throw new TypeError('the registered body is not a function');
      test.body();
      console.log('PASS ' + test.name);
    } catch (err) {
      failed++;
      const name = err && err.name ? err.name : 'Error';
      const message = err && err.message ? err.message : String(err);
      console.log('FAIL ' + test.name + ': ' + name + ': ' + message.split('\n')[0]);
    }
  }

  console.log((tests.length - failed) + ' passed, ' + failed + ' failed');
  return failed === 0 ? 0 : 1;
}

process.exitCode = main(process.argv.slice(2));
