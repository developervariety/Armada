"""Items on hand, keyed by SKU."""

from typing import Dict, Optional

from inventory.errors import InsufficientStockError, UnknownSkuError
from inventory.item import Item


class Stock:
    """Items on hand, keyed by SKU."""

    def __init__(self) -> None:
        """Create an empty stock."""
        self._items: Dict[str, Item] = {}

    @property
    def count(self) -> int:
        """Number of distinct items."""
        return len(self._items)

    def add(self, item: Item) -> None:
        """Add an item, or add its quantity to the item with the same SKU.

        Raises TypeError when item is not an Item.
        """
        if not isinstance(item, Item):
            raise TypeError("item must be an Item")
        existing = self._items.get(item.sku)
        if existing is not None:
            existing.quantity += item.quantity
            return
        self._items[item.sku] = item

    def remove(self, sku: str, quantity: int) -> None:
        """Remove units of an item.

        Raises TypeError when sku is not a string, ValueError when quantity is not positive, UnknownSkuError when
        the SKU is unknown, and InsufficientStockError when fewer units are on hand than requested.
        """
        if not isinstance(sku, str):
            raise TypeError("sku must be a string")
        if quantity <= 0:
            raise ValueError("quantity must be positive")
        existing = self._items.get(sku)
        if existing is None:
            raise UnknownSkuError(sku)
        if existing.quantity < quantity:
            raise InsufficientStockError(sku, existing.quantity)
        existing.quantity -= quantity

    def find(self, sku: str) -> Optional[Item]:
        """Find an item; returns None when the SKU is unknown.

        Raises TypeError when sku is not a string.
        """
        if not isinstance(sku, str):
            raise TypeError("sku must be a string")
        return self._items.get(sku)

    def total_units(self) -> int:
        """Units on hand across every item."""
        total = 0
        for item in self._items.values():
            total += item.quantity
        return total
