package inventory

import "testing"

// registeredTest is a named test function.
type registeredTest struct {
	name string
	body func(t *testing.T)
}

// registeredTests returns every test that runs, in run order. A test function that is not listed here never runs.
func registeredTests() []registeredTest {
	return []registeredTest{
		{"itemConstructorSetsValues", itemConstructorSetsValues},
		{"itemConstructorNegativePriceFails", itemConstructorNegativePriceFails},
		{"stockAddSameSkuMergesQuantity", stockAddSameSkuMergesQuantity},
		{"stockRemoveReducesQuantity", stockRemoveReducesQuantity},
		{"stockRemoveUnknownSkuFails", stockRemoveUnknownSkuFails},
		{"priceCalculatorLineTotalMultiplies", priceCalculatorLineTotalMultiplies},
	}
}
