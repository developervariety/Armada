# frozen_string_literal: true

require 'minitest'
require_relative '../lib/inventory'

# Tests of Inventory::Stock.
class StockTest < Minitest::Test
  # Adding the same SKU twice merges the quantities.
  def test_stock_add_same_sku_merges_quantity
    stock = Inventory::Stock.new
    stock.add(Inventory::Item.new('A-1', 'Anchor', 1, 2))
    stock.add(Inventory::Item.new('A-1', 'Anchor', 1, 3))
    assert_equal 1, stock.count
    assert_equal 5, stock.total_units
  end

  # Removing units lowers the quantity.
  def test_stock_remove_reduces_quantity
    stock = Inventory::Stock.new
    stock.add(Inventory::Item.new('A-1', 'Anchor', 1, 5))
    stock.remove('A-1', 2)
    assert_equal 3, stock.find('A-1').quantity
  end

  # Removing an unknown SKU raises.
  def test_stock_remove_unknown_sku_raises
    stock = Inventory::Stock.new
    assert_raises(Inventory::UnknownSkuError) { stock.remove('missing', 1) }
  end
end
