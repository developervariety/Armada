# frozen_string_literal: true

module Inventory
  # Text helpers.
  module TextUtil
    # Shorten a value to at most max_length characters (at least 3), ending in "..." when it was cut. Raises
    # TypeError when value is nil and ArgumentError when max_length is less than 3.
    def self.truncate(value, max_length)
      raise TypeError, 'value is required' if value.nil?
      raise ArgumentError, 'max_length must be at least 3' if max_length < 3
      return value if value.length <= max_length

      value[0, max_length - 3] + '...'
    end
  end
end
