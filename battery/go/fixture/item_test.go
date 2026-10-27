package inventory

import (
	"errors"
	"testing"
)

// itemConstructorSetsValues checks that NewItem keeps its arguments.
func itemConstructorSetsValues(t *testing.T) {
	item, err := NewItem("A-1", "Anchor", 1250, 4)
	if err != nil {
		t.Fatalf("NewItem: %v", err)
	}
	if item.Sku != "A-1" || item.Name != "Anchor" || item.UnitPriceCents != 1250 || item.Quantity != 4 {
		t.Fatalf("unexpected item %+v", *item)
	}
}

// itemConstructorNegativePriceFails checks that a negative price is refused.
func itemConstructorNegativePriceFails(t *testing.T) {
	_, err := NewItem("A-1", "Anchor", -1, 1)
	if !errors.Is(err, ErrInvalidArgument) {
		t.Fatalf("expected ErrInvalidArgument, got %v", err)
	}
}
