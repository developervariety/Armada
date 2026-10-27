# frozen_string_literal: true

module Inventory
  # Price arithmetic.
  module PriceCalculator
    # Apply a percentage discount (0 to 100) to a price, rounded to cents. Raises ArgumentError when price is
    # negative or percent is outside 0 to 100.
    def self.apply_discount(price, percent)
      raise ArgumentError, 'price must not be negative' if price.negative?
      raise ArgumentError, 'percent must be 0 to 100' if percent.negative? || percent > 100

      ((price - percent) * 100).round / 100.0
    end

    # Total of a line: unit price times quantity. Raises ArgumentError when either value is negative.
    def self.line_total(unit_price, quantity)
      raise ArgumentError, 'unit_price must not be negative' if unit_price.negative?
      raise ArgumentError, 'quantity must not be negative' if quantity.negative?

      unit_price * quantity
    end
  end
end
