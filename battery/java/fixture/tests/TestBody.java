package tests;

/**
 * The body of a test: returns normally when it passes, throws when it fails.
 */
@FunctionalInterface
public interface TestBody {
    /**
     * Run the test.
     * @throws Exception When the test fails.
     */
    void run() throws Exception;
}
