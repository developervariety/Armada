"""A stock-keeping item."""

from decimal import Decimal


class Item:
    """A stock-keeping item."""

    def __init__(self, sku: str, name: str, unit_price: Decimal, quantity: int) -> None:
        """Create an item.

        Raises TypeError when sku or name is not a string, and ValueError when unit_price or quantity is negative.
        """
        if not isinstance(sku, str):
            raise TypeError("sku must be a string")
        if not isinstance(name, str):
            raise TypeError("name must be a string")
        if unit_price < 0:
            raise ValueError("unit_price must not be negative")
        if quantity < 0:
            raise ValueError("quantity must not be negative")
        self.sku = sku
        """Stock-keeping unit code."""
        self.name = name
        """Display name."""
        self.unit_price = unit_price
        """Price of one unit."""
        self.quantity = quantity
        """Units on hand."""
