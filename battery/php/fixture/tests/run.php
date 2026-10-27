<?php

declare(strict_types=1);

// Runs every registered test. Exit code 0 when all pass, 1 when any fails, 2 when a required test is not registered,
// 3 when assertions are disabled. Run from the repository root: php -d zend.assertions=1 tests/run.php
// Arguments: --list prints the registered names; --require NAME fails unless NAME is registered (repeatable).

require __DIR__ . '/bootstrap.php';

use Inventory\Tests\Registry;

/**
 * Run the registered tests.
 *
 * @param list<string> $args Command-line arguments.
 * @return int Exit code.
 */
function main(array $args): int
{
    $tests = Registry::all();
    $names = array_keys($tests);

    $listOnly = false;
    for ($i = 0; $i < count($args); $i++) {
        if ($args[$i] === '--list') {
            $listOnly = true;
        } elseif ($args[$i] === '--require' && $i + 1 < count($args)) {
            $required = $args[++$i];
            if (!in_array($required, $names, true)) {
                echo 'MISSING ' . $required . ' is not registered in tests/Registry.php.' . PHP_EOL;
                return 2;
            }
        }
    }

    if ($listOnly) {
        foreach ($names as $name) {
            echo $name . PHP_EOL;
        }

        return 0;
    }

    if (ini_get('zend.assertions') !== '1') {
        echo 'Assertions are disabled; run php -d zend.assertions=1 tests/run.php' . PHP_EOL;
        return 3;
    }

    // A warning or notice fails the test instead of scrolling past.
    set_error_handler(static function (int $severity, string $message, string $file, int $line): bool {
        throw new ErrorException($message, 0, $severity, $file, $line);
    });

    $failed = 0;
    foreach ($tests as $name => $body) {
        try {
            if (!is_callable($body)) {
                throw new TypeError('the registered body is not callable');
            }

            $body();
            echo 'PASS ' . $name . PHP_EOL;
        } catch (Throwable $thrown) {
            $failed++;
            $message = explode("\n", $thrown->getMessage())[0];
            echo 'FAIL ' . $name . ': ' . get_class($thrown) . ': ' . $message . PHP_EOL;
        }
    }

    echo (count($tests) - $failed) . ' passed, ' . $failed . ' failed' . PHP_EOL;
    return $failed === 0 ? 0 : 1;
}

exit(main(array_slice($argv, 1)));
