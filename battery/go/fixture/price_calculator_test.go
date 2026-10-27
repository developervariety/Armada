package inventory

import "testing"

// priceCalculatorLineTotalMultiplies checks that a line total is the unit price times the quantity.
func priceCalculatorLineTotalMultiplies(t *testing.T) {
	got, err := LineTotal(250, 3)
	if err != nil || got != 750 {
		t.Fatalf("expected 750, got %d (%v)", got, err)
	}
}
