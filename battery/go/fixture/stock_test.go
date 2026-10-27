package inventory

import (
	"errors"
	"testing"
)

// mustItem creates an item or fails the test.
func mustItem(t *testing.T, sku string, name string, unitPriceCents int64, quantity int) *Item {
	t.Helper()
	item, err := NewItem(sku, name, unitPriceCents, quantity)
	if err != nil {
		t.Fatalf("NewItem: %v", err)
	}
	return item
}

// stockAddSameSkuMergesQuantity checks that adding the same SKU twice merges the quantities.
func stockAddSameSkuMergesQuantity(t *testing.T) {
	stock := NewStock()
	if err := stock.Add(mustItem(t, "A-1", "Anchor", 100, 2)); err != nil {
		t.Fatal(err)
	}
	if err := stock.Add(mustItem(t, "A-1", "Anchor", 100, 3)); err != nil {
		t.Fatal(err)
	}
	if stock.Count() != 1 || stock.TotalUnits() != 5 {
		t.Fatalf("expected 1 item and 5 units, got %d and %d", stock.Count(), stock.TotalUnits())
	}
}

// stockRemoveReducesQuantity checks that removing units lowers the quantity.
func stockRemoveReducesQuantity(t *testing.T) {
	stock := NewStock()
	if err := stock.Add(mustItem(t, "A-1", "Anchor", 100, 5)); err != nil {
		t.Fatal(err)
	}
	if err := stock.Remove("A-1", 2); err != nil {
		t.Fatal(err)
	}
	if got := stock.Find("A-1").Quantity; got != 3 {
		t.Fatalf("expected 3 units, got %d", got)
	}
}

// stockRemoveUnknownSkuFails checks that removing an unknown SKU returns ErrUnknownSku.
func stockRemoveUnknownSkuFails(t *testing.T) {
	stock := NewStock()
	if err := stock.Remove("missing", 1); !errors.Is(err, ErrUnknownSku) {
		t.Fatalf("expected ErrUnknownSku, got %v", err)
	}
}
