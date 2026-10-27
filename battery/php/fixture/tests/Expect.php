<?php

declare(strict_types=1);

namespace Inventory\Tests;

use AssertionError;
use Throwable;

/**
 * Exception checks for the tests. Values are checked with plain assert().
 */
final class Expect
{
    /**
     * Fail unless the body throws an instance of the given class (or a subclass).
     *
     * @param string $class Expected exception class.
     * @param callable $body Code that must throw.
     * @return Throwable The exception.
     * @throws AssertionError When nothing or something else was thrown.
     */
    public static function throws(string $class, callable $body): Throwable
    {
        try {
            $body();
        } catch (Throwable $thrown) {
            if ($thrown instanceof $class) {
                return $thrown;
            }

            throw new AssertionError('expected ' . $class . ' but ' . get_class($thrown) . ' was thrown', 0, $thrown);
        }

        throw new AssertionError('expected ' . $class . ' but nothing was thrown');
    }
}
