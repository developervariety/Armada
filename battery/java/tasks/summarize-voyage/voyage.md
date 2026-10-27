Voyage: "Price rounding and stock removal fixes" (3 missions, vessel Inventory)

Mission 1 (Worker, Complete): "Round discounted prices to cents". Changed inventory/PriceCalculator.java so
applyDiscount rounds to cents with Math.round. Added priceCalculatorApplyDiscountRoundsHalfUp and registered it. 7 tests
passed. Judge verdict PASS on the first review.

Mission 2 (Worker, Complete after one rework round): "Refuse removing more units than on hand". First attempt added
the check to Stock.remove but forgot to register the new test stockRemoveMoreThanAvailableThrows, so the test never
ran. Judge verdict NEEDS_REVISION: "the new test is not in tests/TestRegistry.java". The rework registered it; 8 tests
passed; Judge PASS.

Mission 3 (Worker, Failed): "Keep legacy-codes.properties encoding". The captain rewrote
config/legacy-codes.properties and dropped the UTF-8 byte order mark and converted CRLF to LF. The stage check passed
(the tests never read that file), but the byte comparison failed. Failure classified as an encoding-preservation
failure; the mission was cancelled and re-dispatched to a different captain in a later voyage.

Landing: missions 1 and 2 merged to main with LocalMerge; mission 3 never landed.
