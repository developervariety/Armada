"""Inventory errors."""


class UnknownSkuError(KeyError):
    """Raised when a SKU is not in stock."""

    def __init__(self, sku: str) -> None:
        """Create the error for an unknown SKU."""
        super().__init__("Unknown SKU " + sku + ".")
        self.sku = sku


class InsufficientStockError(Exception):
    """Raised when fewer units are on hand than requested."""

    def __init__(self, sku: str, on_hand: int) -> None:
        """Create the error for a SKU and the units on hand."""
        super().__init__("Only " + str(on_hand) + " units of " + sku + " on hand.")
        self.sku = sku
        self.on_hand = on_hand
