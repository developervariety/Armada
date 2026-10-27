package tests;

import java.util.Objects;

/**
 * Assertions for the tests. A failed check throws AssertionError.
 */
public final class Check {
    private Check() {
    }

    /**
     * Fail unless the values are equal.
     * @param expected Expected value.
     * @param actual Actual value.
     */
    public static void equal(Object expected, Object actual) {
        if (!Objects.equals(expected, actual)) throw new AssertionError("expected " + expected + " but was " + actual);
    }

    /**
     * Fail unless the condition is true.
     * @param condition Condition.
     * @param message What was expected.
     */
    public static void isTrue(boolean condition, String message) {
        if (!condition) throw new AssertionError(message);
    }

    /**
     * Fail unless the body throws an exception of the given type (or a subtype).
     * @param <T> Exception type.
     * @param type Expected exception type.
     * @param body Code that must throw.
     * @return The exception.
     */
    public static <T extends Throwable> T throwsException(Class<T> type, TestBody body) {
        try {
            body.run();
        } catch (Throwable thrown) {
            if (type.isInstance(thrown)) return type.cast(thrown);
            throw new AssertionError("expected " + type.getSimpleName() + " but " + thrown.getClass().getSimpleName() + " was thrown", thrown);
        }

        throw new AssertionError("expected " + type.getSimpleName() + " but nothing was thrown");
    }
}
