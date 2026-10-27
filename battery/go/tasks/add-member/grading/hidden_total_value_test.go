package inventory

import "testing"

// TestHiddenTotalValue is the hidden check for the add-member task: TotalValueCents is UnitPriceCents times Quantity
// and follows Quantity changes.
func TestHiddenTotalValue(t *testing.T) {
	item := &Item{Sku: "A-1", Name: "Anchor", UnitPriceCents: 250, Quantity: 4}
	first := item.TotalValueCents()
	item.Quantity = 6
	second := item.TotalValueCents()
	empty := &Item{Sku: "B-1", Name: "Buoy", UnitPriceCents: 999, Quantity: 0}
	if first != 1000 || second != 1500 || empty.TotalValueCents() != 0 {
		t.Fatalf("FAIL TotalValueCents: %d, %d, %d", first, second, empty.TotalValueCents())
	}
}
