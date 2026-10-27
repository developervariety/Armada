<?php

declare(strict_types=1);

namespace Inventory;

use InvalidArgumentException;
use LengthException;

/**
 * Text helpers.
 */
final class TextUtil
{
    /**
     * Shorten a value to at most maxLength characters, ending in "..." when it was cut.
     *
     * @param string|null $value Value.
     * @param int $maxLength Maximum length, at least 3.
     * @return string The value, or its shortened form.
     * @throws InvalidArgumentException When value is null.
     * @throws LengthException When maxLength is less than 3.
     */
    public static function truncate(?string $value, int $maxLength): string
    {
        if ($value === null) {
            throw new InvalidArgumentException('value is required');
        }
        if ($maxLength < 3) {
            throw new LengthException('maxLength must be at least 3');
        }
        if (strlen($value) <= $maxLength) {
            return $value;
        }

        return substr($value, 0, $maxLength - 3) . '...';
    }
}
