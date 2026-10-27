package inventory

import "fmt"

// Stock holds the items on hand, keyed by SKU. Create it with NewStock.
type Stock struct {
	items map[string]*Item
}

// NewStock creates an empty stock.
func NewStock() *Stock {
	return &Stock{items: map[string]*Item{}}
}

// Count returns the number of distinct items.
func (s *Stock) Count() int {
	return len(s.items)
}

// Add adds an item, or adds its quantity to the item with the same SKU. It returns ErrInvalidArgument when item is
// nil.
func (s *Stock) Add(item *Item) error {
	if item == nil {
		return fmt.Errorf("%w: item is nil", ErrInvalidArgument)
	}
	if existing, ok := s.items[item.Sku]; ok {
		existing.Quantity += item.Quantity
		return nil
	}
	s.items[item.Sku] = item
	return nil
}

// Remove removes units of an item. It returns ErrInvalidArgument when quantity is not positive, ErrUnknownSku when
// the SKU is unknown and ErrInsufficientStock when fewer units are on hand than requested.
func (s *Stock) Remove(sku string, quantity int) error {
	if quantity <= 0 {
		return fmt.Errorf("%w: quantity must be positive", ErrInvalidArgument)
	}
	existing, ok := s.items[sku]
	if !ok {
		return fmt.Errorf("%w: %s", ErrUnknownSku, sku)
	}
	if existing.Quantity < quantity {
		return fmt.Errorf("%w: only %d units of %s on hand", ErrInsufficientStock, existing.Quantity, sku)
	}
	existing.Quantity -= quantity
	return nil
}

// Find returns the item with the SKU, or nil when the SKU is unknown.
func (s *Stock) Find(sku string) *Item {
	return s.items[sku]
}

// TotalUnits returns the units on hand across every item.
func (s *Stock) TotalUnits() int {
	total := 0
	for _, item := range s.items {
		total += item.Quantity
	}
	return total
}
