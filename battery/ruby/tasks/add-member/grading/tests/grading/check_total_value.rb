# frozen_string_literal: true

# Hidden check for the add-member task: Item#total_value is unit_price times quantity and follows quantity changes.

require_relative '../../lib/inventory'

item = Inventory::Item.new('A-1', 'Anchor', 2.5, 4)
first = item.total_value
item.quantity = 6
second = item.total_value
empty = Inventory::Item.new('B-1', 'Buoy', 9.99, 0)
ok = first == 10.0 && second == 15.0 && empty.total_value.zero?
puts(ok ? 'PASS total_value' : "FAIL total_value: #{first}, #{second}, #{empty.total_value}")
exit(ok ? 0 : 1)
