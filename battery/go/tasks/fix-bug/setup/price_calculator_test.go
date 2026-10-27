package inventory

import "testing"

// priceCalculatorLineTotalMultiplies checks that a line total is the unit price times the quantity.
func priceCalculatorLineTotalMultiplies(t *testing.T) {
	got, err := LineTotal(250, 3)
	if err != nil || got != 750 {
		t.Fatalf("expected 750, got %d (%v)", got, err)
	}
}

// priceCalculatorApplyDiscountTenPercent checks that ten percent off 8000 cents is 7200 cents.
func priceCalculatorApplyDiscountTenPercent(t *testing.T) {
	got, err := ApplyDiscount(8000, 10)
	if err != nil || got != 7200 {
		t.Fatalf("expected 7200, got %d (%v)", got, err)
	}
}

// priceCalculatorApplyDiscountZeroKeepsPrice checks that no discount keeps the price.
func priceCalculatorApplyDiscountZeroKeepsPrice(t *testing.T) {
	got, err := ApplyDiscount(1999, 0)
	if err != nil || got != 1999 {
		t.Fatalf("expected 1999, got %d (%v)", got, err)
	}
}
