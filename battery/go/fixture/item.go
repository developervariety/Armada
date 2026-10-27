package inventory

import "fmt"

// Item is a stock-keeping item.
type Item struct {
	// Sku is the stock-keeping unit code.
	Sku string

	// Name is the display name.
	Name string

	// UnitPriceCents is the price of one unit, in cents.
	UnitPriceCents int64

	// Quantity is the number of units on hand.
	Quantity int
}

// NewItem creates an item. It returns ErrInvalidArgument when sku or name is empty or when unitPriceCents or
// quantity is negative.
func NewItem(sku string, name string, unitPriceCents int64, quantity int) (*Item, error) {
	if sku == "" || name == "" {
		return nil, fmt.Errorf("%w: sku and name are required", ErrInvalidArgument)
	}
	if unitPriceCents < 0 || quantity < 0 {
		return nil, fmt.Errorf("%w: unitPriceCents and quantity must not be negative", ErrInvalidArgument)
	}
	return &Item{Sku: sku, Name: name, UnitPriceCents: unitPriceCents, Quantity: quantity}, nil
}
