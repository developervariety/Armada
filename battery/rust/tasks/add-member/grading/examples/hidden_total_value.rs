//! Hidden check for the add-member task: Item::total_value_cents is unit_price_cents times quantity and follows
//! quantity changes.

use inventory::Item;

fn main() {
    let mut item = Item::new("A-1", "Anchor", 250, 4).expect("valid item");
    let first: i64 = item.total_value_cents();
    item.quantity = 6;
    let second: i64 = item.total_value_cents();
    let empty = Item::new("B-1", "Buoy", 999, 0).expect("valid item");
    let ok = first == 1000 && second == 1500 && empty.total_value_cents() == 0;
    if ok {
        println!("PASS total_value_cents");
    } else {
        println!("FAIL total_value_cents: {}, {}, {}", first, second, empty.total_value_cents());
        std::process::exit(1);
    }
}
