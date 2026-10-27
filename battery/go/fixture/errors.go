package inventory

import "errors"

var (
	// ErrInvalidArgument is returned when an argument is empty, nil or out of range.
	ErrInvalidArgument = errors.New("invalid argument")

	// ErrUnknownSku is returned when a SKU is not in stock.
	ErrUnknownSku = errors.New("unknown SKU")

	// ErrInsufficientStock is returned when fewer units are on hand than requested.
	ErrInsufficientStock = errors.New("insufficient stock")

	// ErrEmptyValue is returned by Truncate when the value is empty.
	ErrEmptyValue = errors.New("value is required")

	// ErrMaxLengthTooSmall is returned by Truncate when maxLength is less than 3.
	ErrMaxLengthTooSmall = errors.New("maxLength must be at least 3")
)
