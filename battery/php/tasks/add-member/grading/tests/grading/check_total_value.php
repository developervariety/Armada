<?php

declare(strict_types=1);

// Hidden check for the add-member task: Item::getTotalValue() is unitPrice times quantity and follows quantity changes.

require dirname(__DIR__) . '/bootstrap.php';

use Inventory\Item;

$item = new Item('A-1', 'Anchor', 2.5, 4);
$first = $item->getTotalValue();
$item->setQuantity(6);
$second = $item->getTotalValue();
$empty = new Item('B-1', 'Buoy', 9.99, 0);
$ok = $first === 10.0 && $second === 15.0 && $empty->getTotalValue() === 0.0;
echo ($ok ? 'PASS getTotalValue' : 'FAIL getTotalValue: ' . $first . ', ' . $second . ', ' . $empty->getTotalValue()) . PHP_EOL;
exit($ok ? 0 : 1);
