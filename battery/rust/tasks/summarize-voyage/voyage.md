Voyage: "Price rounding and stock removal fixes" (3 missions, vessel Inventory)

Mission 1 (Worker, Complete): "Round discounted prices to cents". Changed src/price_calculator.rs so apply_discount
rounds half up to whole cents. Added price_calculator_apply_discount_rounds_half_up and registered it. 7 tests
passed. Judge verdict PASS on the first review.

Mission 2 (Worker, Complete after one rework round): "Refuse removing more units than on hand". First attempt added
the check to Stock::remove but forgot to register the new test stock_remove_more_than_available_fails, so the test
never ran. Judge verdict NEEDS_REVISION: "the new test is not in tests/registry.rs". The rework registered it; 8
tests passed; Judge PASS.

Mission 3 (Worker, Failed): "Keep legacy_codes.rs encoding". The captain rewrote src/legacy_codes.rs and dropped the
UTF-8 byte order mark and converted CRLF to LF. The stage check passed (the compiler does not care), but the byte
comparison failed. Failure classified as an encoding-preservation failure; the mission was cancelled and
re-dispatched to a different captain in a later voyage.

Landing: missions 1 and 2 merged to main with LocalMerge; mission 3 never landed.
