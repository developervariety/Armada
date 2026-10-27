package inventory;

/**
 * Text helpers.
 */
public final class TextUtil {
    private TextUtil() {
    }

    /**
     * Shorten a value to at most maxLength characters, ending in "..." when it was cut.
     * @param value Value.
     * @param maxLength Maximum length, at least 3.
     * @return The value, or its shortened form.
     * @throws NullPointerException When value is null.
     * @throws IllegalArgumentException When maxLength is less than 3.
     */
    public static String truncate(String value, int maxLength) {
        if (value == null) throw new NullPointerException("value is required");
        if (maxLength < 3) throw new IllegalArgumentException("maxLength must be at least 3");
        if (value.length() <= maxLength) return value;
        return value.substring(0, maxLength - 3) + "...";
    }
}
