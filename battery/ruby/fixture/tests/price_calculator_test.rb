# frozen_string_literal: true

require 'minitest'
require_relative '../lib/inventory'

# Tests of Inventory::PriceCalculator.
class PriceCalculatorTest < Minitest::Test
  # A line total is the unit price times the quantity.
  def test_price_calculator_line_total_multiplies
    assert_equal 7.5, Inventory::PriceCalculator.line_total(2.5, 3)
  end
end
