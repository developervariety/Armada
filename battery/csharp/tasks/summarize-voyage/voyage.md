Voyage: "Price rounding and stock removal fixes" (3 missions, vessel Inventory)

Mission 1 (Worker, Complete): "Round discounted prices to cents". Changed src/Inventory/PriceCalculator.cs so
ApplyDiscount rounds with MidpointRounding.AwayFromZero. Added PriceCalculator_ApplyDiscount_RoundsHalfUp and
registered it. Build passed; 7 tests passed. Judge verdict PASS on the first review.

Mission 2 (Worker, Complete after one rework round): "Refuse removing more units than on hand". First attempt added
the check to Stock.Remove but forgot to register the new test Stock_Remove_MoreThanAvailable_Throws, so the test
never ran. Judge verdict NEEDS_REVISION: "the new test is not in TestRegistry". The rework registered it; 8 tests
passed; Judge PASS.

Mission 3 (Worker, Failed): "Keep LegacyCodes.cs encoding". The captain rewrote src/Inventory/LegacyCodes.cs and
dropped the UTF-8 byte order mark and converted CRLF to LF. The stage check passed (the build does not care), but the
byte comparison failed. Failure classified as an encoding-preservation failure; the mission was cancelled and
re-dispatched to a different captain in a later voyage.

Landing: missions 1 and 2 merged to main with LocalMerge; mission 3 never landed.
