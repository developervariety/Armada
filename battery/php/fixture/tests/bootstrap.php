<?php

declare(strict_types=1);

// Loads classes without Composer: Inventory\X from src/X.php and Inventory\Tests\X from tests/X.php.

spl_autoload_register(static function (string $class): void {
    $roots = [
        'Inventory\\Tests\\' => __DIR__ . '/',
        'Inventory\\' => dirname(__DIR__) . '/src/',
    ];
    foreach ($roots as $prefix => $directory) {
        if (strncmp($class, $prefix, strlen($prefix)) === 0) {
            $file = $directory . str_replace('\\', '/', substr($class, strlen($prefix))) . '.php';
            if (is_file($file)) {
                require $file;
            }

            return;
        }
    }
});
