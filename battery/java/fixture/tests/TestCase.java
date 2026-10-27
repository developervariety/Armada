package tests;

/**
 * A registered test: its name and its body.
 */
public final class TestCase {
    private final String name;
    private final TestBody body;

    /**
     * Create a test case.
     * @param name Test name, unique in the registry.
     * @param body Test body.
     */
    public TestCase(String name, TestBody body) {
        this.name = name;
        this.body = body;
    }

    /**
     * Test name.
     * @return The name.
     */
    public String getName() {
        return name;
    }

    /**
     * Test body.
     * @return The body.
     */
    public TestBody getBody() {
        return body;
    }
}
