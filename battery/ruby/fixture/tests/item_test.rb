# frozen_string_literal: true

require 'minitest'
require_relative '../lib/inventory'

# Tests of Inventory::Item.
class ItemTest < Minitest::Test
  # The constructor keeps its arguments.
  def test_item_constructor_sets_values
    item = Inventory::Item.new('A-1', 'Anchor', 12.5, 4)
    assert_equal 'A-1', item.sku
    assert_equal 'Anchor', item.name
    assert_equal 12.5, item.unit_price
    assert_equal 4, item.quantity
  end

  # A negative price is refused.
  def test_item_constructor_negative_price_raises
    assert_raises(ArgumentError) { Inventory::Item.new('A-1', 'Anchor', -1, 1) }
  end
end
