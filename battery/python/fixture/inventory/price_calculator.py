"""Price arithmetic."""

from decimal import ROUND_HALF_UP, Decimal

_CENT = Decimal("0.01")


def apply_discount(price: Decimal, percent: Decimal) -> Decimal:
    """Apply a percentage discount (0 to 100) to a price, rounded to cents.

    Raises ValueError when price is negative or percent is outside 0 to 100.
    """
    if price < 0:
        raise ValueError("price must not be negative")
    if percent < 0 or percent > 100:
        raise ValueError("percent must be 0 to 100")
    return (price - percent).quantize(_CENT, rounding=ROUND_HALF_UP)


def line_total(unit_price: Decimal, quantity: int) -> Decimal:
    """Total of a line: unit price times quantity.

    Raises ValueError when either value is negative.
    """
    if unit_price < 0:
        raise ValueError("unit_price must not be negative")
    if quantity < 0:
        raise ValueError("quantity must not be negative")
    return unit_price * quantity
