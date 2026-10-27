# frozen_string_literal: true

module Inventory
  # Codes kept for the legacy import format. This file is UTF-8 with a byte order mark and CRLF line endings.
  module LegacyCodes
    # Longest code the legacy format accepts.
    MAX_CODE_LENGTH = 8

    # Prefix of every legacy code.
    PREFIX = 'LG-'
  end
end
