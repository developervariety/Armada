//! A small inventory library used by the Armada fixture battery.

pub mod error;
pub mod item;
pub mod legacy_codes;
pub mod price_calculator;
pub mod stock;
pub mod text_util;

pub use error::InventoryError;
pub use item::Item;
pub use stock::Stock;
