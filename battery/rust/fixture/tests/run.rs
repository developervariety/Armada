//! Runs every registered test. Exit code 0 when all pass, 1 when any fails, 2 when a required test is not registered.
//! Arguments (after `cargo test --offline --`): --list prints the registered names; --require NAME fails unless NAME
//! is registered (repeatable).

mod item_tests;
mod price_calculator_tests;
mod registry;
mod stock_tests;

use std::any::Any;
use std::collections::HashSet;
use std::panic;
use std::process;

fn main() {
    let args: Vec<String> = std::env::args().skip(1).collect();
    process::exit(run(&args));
}

fn run(args: &[String]) -> i32 {
    let tests = registry::all();
    let names: HashSet<&str> = tests.iter().map(|test| test.name).collect();

    let mut list_only = false;
    let mut i = 0;
    while i < args.len() {
        if args[i] == "--list" {
            list_only = true;
        } else if args[i] == "--require" && i + 1 < args.len() {
            i += 1;
            if !names.contains(args[i].as_str()) {
                println!("MISSING {} is not registered in tests/registry.rs.", args[i]);
                return 2;
            }
        }
        i += 1;
    }

    if list_only {
        for test in &tests {
            println!("{}", test.name);
        }
        return 0;
    }

    panic::set_hook(Box::new(|_| {}));
    let mut failed = 0;
    for test in &tests {
        match panic::catch_unwind(test.body) {
            Ok(()) => println!("PASS {}", test.name),
            Err(payload) => {
                failed += 1;
                println!("FAIL {}: {}", test.name, message(payload.as_ref()));
            }
        }
    }

    println!("{} passed, {} failed", tests.len() - failed, failed);
    if failed == 0 {
        0
    } else {
        1
    }
}

fn message(payload: &(dyn Any + Send)) -> String {
    let text = if let Some(text) = payload.downcast_ref::<&str>() {
        text.to_string()
    } else if let Some(text) = payload.downcast_ref::<String>() {
        text.clone()
    } else {
        "panic".to_string()
    };
    text.lines().map(str::trim).collect::<Vec<&str>>().join("; ")
}
