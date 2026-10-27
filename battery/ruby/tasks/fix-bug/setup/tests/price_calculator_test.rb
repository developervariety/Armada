# frozen_string_literal: true

require 'minitest'
require_relative '../lib/inventory'

# Tests of Inventory::PriceCalculator.
class PriceCalculatorTest < Minitest::Test
  # A line total is the unit price times the quantity.
  def test_price_calculator_line_total_multiplies
    assert_equal 7.5, Inventory::PriceCalculator.line_total(2.5, 3)
  end

  # Ten percent off 80.00 is 72.00.
  def test_price_calculator_apply_discount_ten_percent
    assert_equal 72.0, Inventory::PriceCalculator.apply_discount(80, 10)
  end

  # No discount keeps the price.
  def test_price_calculator_apply_discount_zero_keeps_price
    assert_equal 19.99, Inventory::PriceCalculator.apply_discount(19.99, 0)
  end
end
