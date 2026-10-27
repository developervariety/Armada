package inventory

// Truncate shortens value to at most maxLength bytes, ending in "..." when it was cut. It returns ErrEmptyValue when
// value is empty and ErrMaxLengthTooSmall when maxLength is less than 3.
func Truncate(value string, maxLength int) (string, error) {
	if value == "" {
		return "", ErrEmptyValue
	}
	if maxLength < 3 {
		return "", ErrMaxLengthTooSmall
	}
	if len(value) <= maxLength {
		return value, nil
	}
	return value[:maxLength-3] + "...", nil
}
