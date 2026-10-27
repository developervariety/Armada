//! Inventory errors.

use std::fmt;

/// Errors returned by the inventory library.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum InventoryError {
    /// An argument is empty or out of range; the text names it.
    InvalidArgument(String),
    /// The SKU is not in stock.
    UnknownSku(String),
    /// Fewer units are on hand than requested.
    InsufficientStock {
        /// The SKU.
        sku: String,
        /// Units on hand.
        on_hand: u32,
    },
    /// `truncate` was given an empty value.
    EmptyValue,
    /// `truncate` was given a `max_length` below 3.
    MaxLengthTooSmall,
}

impl fmt::Display for InventoryError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            InventoryError::InvalidArgument(what) => write!(f, "invalid argument: {}", what),
            InventoryError::UnknownSku(sku) => write!(f, "unknown SKU {}", sku),
            InventoryError::InsufficientStock { sku, on_hand } => {
                write!(f, "only {} units of {} on hand", on_hand, sku)
            }
            InventoryError::EmptyValue => write!(f, "value is required"),
            InventoryError::MaxLengthTooSmall => write!(f, "max_length must be at least 3"),
        }
    }
}

impl std::error::Error for InventoryError {}
