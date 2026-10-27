# frozen_string_literal: true

require_relative 'item_test'
require_relative 'stock_test'
require_relative 'price_calculator_test'

# Every test that runs, in run order. A test method that is not listed here never runs.
module Registry
  # The registered tests as [test class, method name] pairs, in run order.
  def self.all
    [
      [ItemTest, :test_item_constructor_sets_values],
      [ItemTest, :test_item_constructor_negative_price_raises],
      [StockTest, :test_stock_add_same_sku_merges_quantity],
      [StockTest, :test_stock_remove_reduces_quantity],
      [StockTest, :test_stock_remove_unknown_sku_raises],
      [PriceCalculatorTest, :test_price_calculator_line_total_multiplies],
      [PriceCalculatorTest, :test_price_calculator_apply_discount_ten_percent],
      [PriceCalculatorTest, :test_price_calculator_apply_discount_zero_keeps_price]
    ]
  end
end
