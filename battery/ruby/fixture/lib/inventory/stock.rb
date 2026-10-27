# frozen_string_literal: true

module Inventory
  # Items on hand, keyed by SKU.
  class Stock
    # Create an empty stock.
    def initialize
      @items = {}
    end

    # Number of distinct items.
    def count
      @items.size
    end

    # Add an item, or add its quantity to the item with the same SKU. Raises TypeError when item is not an Item.
    def add(item)
      raise TypeError, 'item must be an Item' unless item.is_a?(Item)

      existing = @items[item.sku]
      if existing
        existing.quantity += item.quantity
        return
      end

      @items[item.sku] = item
    end

    # Remove units of an item. Raises TypeError when sku is not a String, ArgumentError when quantity is not a
    # positive Integer, UnknownSkuError when the SKU is unknown and InsufficientStockError when fewer units are on
    # hand than requested.
    def remove(sku, quantity)
      raise TypeError, 'sku must be a String' unless sku.is_a?(String)
      raise ArgumentError, 'quantity must be a positive Integer' unless quantity.is_a?(Integer) && quantity.positive?

      existing = @items[sku]
      raise UnknownSkuError, sku if existing.nil?
      raise InsufficientStockError.new(sku, existing.quantity) if existing.quantity < quantity

      existing.quantity -= quantity
    end

    # Find an item by SKU; nil when it is unknown. Raises TypeError when sku is not a String.
    def find(sku)
      raise TypeError, 'sku must be a String' unless sku.is_a?(String)

      @items[sku]
    end

    # Units on hand across every item.
    def total_units
      @items.values.sum(&:quantity)
    end
  end
end
