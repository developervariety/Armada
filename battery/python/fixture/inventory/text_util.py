"""Text helpers."""


def truncate(value: str, max_length: int) -> str:
    """Shorten a value to at most max_length characters, ending in "..." when it was cut.

    Raises TypeError when value is None, and ValueError when max_length is less than 3.
    """
    if value is None:
        raise TypeError("value is required")
    if max_length < 3:
        raise ValueError("max_length must be at least 3")
    if len(value) <= max_length:
        return value
    return value[:max_length - 3] + "..."
