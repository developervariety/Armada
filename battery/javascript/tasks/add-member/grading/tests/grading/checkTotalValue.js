'use strict';

// Hidden check for the add-member task: item.totalValue is unitPrice times quantity and follows quantity changes.

const { Item } = require('../../src/item');

const item = new Item('A-1', 'Anchor', 2.5, 4);
const first = item.totalValue;
item.quantity = 6;
const second = item.totalValue;
const empty = new Item('B-1', 'Buoy', 9.99, 0);
const ok = first === 10 && second === 15 && empty.totalValue === 0;
console.log(ok ? 'PASS totalValue' : 'FAIL totalValue: ' + first + ', ' + second + ', ' + empty.totalValue);
process.exitCode = ok ? 0 : 1;
