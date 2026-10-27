# frozen_string_literal: true

# A small inventory library: items, stock, prices and text helpers.
module Inventory
end

require_relative 'inventory/errors'
require_relative 'inventory/item'
require_relative 'inventory/stock'
require_relative 'inventory/price_calculator'
require_relative 'inventory/text_util'
