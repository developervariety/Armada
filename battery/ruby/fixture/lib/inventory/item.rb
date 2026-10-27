# frozen_string_literal: true

module Inventory
  # A stock-keeping item.
  class Item
    # Stock-keeping unit code.
    attr_reader :sku

    # Display name.
    attr_reader :name

    # Price of one unit.
    attr_reader :unit_price

    # Units on hand.
    attr_reader :quantity

    # Create an item. Raises TypeError when sku or name is not a String and ArgumentError when unit_price or
    # quantity is negative.
    def initialize(sku, name, unit_price, quantity)
      raise TypeError, 'sku must be a String' unless sku.is_a?(String)
      raise TypeError, 'name must be a String' unless name.is_a?(String)
      raise ArgumentError, 'unit_price must not be negative' if unit_price.negative?
      raise ArgumentError, 'quantity must not be negative' if quantity.negative?

      @sku = sku
      @name = name
      @unit_price = unit_price
      @quantity = quantity
    end

    # Set the units on hand. Raises ArgumentError when quantity is negative.
    def quantity=(quantity)
      raise ArgumentError, 'quantity must not be negative' if quantity.negative?

      @quantity = quantity
    end
  end
end
