# frozen_string_literal: true

module Inventory
  # Raised when a SKU is not in stock.
  class UnknownSkuError < StandardError
    # The unknown SKU.
    attr_reader :sku

    # Create the error for the unknown +sku+.
    def initialize(sku)
      super("Unknown SKU #{sku}.")
      @sku = sku
    end
  end

  # Raised when fewer units are on hand than requested.
  class InsufficientStockError < StandardError
    # The SKU.
    attr_reader :sku

    # Units on hand.
    attr_reader :on_hand

    # Create the error for +sku+ with +on_hand+ units on hand.
    def initialize(sku, on_hand)
      super("Only #{on_hand} units of #{sku} on hand.")
      @sku = sku
      @on_hand = on_hand
    end
  end
end
