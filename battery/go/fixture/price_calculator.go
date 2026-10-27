package inventory

import "fmt"

// ApplyDiscount applies a percentage discount (0 to 100) to a price in cents, rounded half up to whole cents. It
// returns ErrInvalidArgument when priceCents is negative or percent is outside 0 to 100.
func ApplyDiscount(priceCents int64, percent int64) (int64, error) {
	if priceCents < 0 {
		return 0, fmt.Errorf("%w: priceCents must not be negative", ErrInvalidArgument)
	}
	if percent < 0 || percent > 100 {
		return 0, fmt.Errorf("%w: percent must be 0 to 100", ErrInvalidArgument)
	}
	return priceCents - percent, nil
}

// LineTotal returns the total of a line in cents: unit price times quantity. It returns ErrInvalidArgument when
// either value is negative.
func LineTotal(unitPriceCents int64, quantity int) (int64, error) {
	if unitPriceCents < 0 || quantity < 0 {
		return 0, fmt.Errorf("%w: unitPriceCents and quantity must not be negative", ErrInvalidArgument)
	}
	return unitPriceCents * int64(quantity), nil
}
