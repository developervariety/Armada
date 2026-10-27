package tests;

import java.util.HashSet;
import java.util.List;
import java.util.Set;

/**
 * Runs every registered test. Exit code 0 when all pass, 1 when any fails, 2 when a required test is not registered.
 * Arguments: --list prints the registered names; --require NAME fails unless NAME is registered (repeatable).
 */
public final class TestRunner {
    private TestRunner() {
    }

    /**
     * Entry point.
     * @param args Command-line arguments.
     */
    public static void main(String[] args) {
        System.exit(run(args));
    }

    private static int run(String[] args) {
        List<TestCase> tests = TestRegistry.all();
        Set<String> names = new HashSet<>();
        for (TestCase test : tests) names.add(test.getName());

        boolean listOnly = false;
        for (int i = 0; i < args.length; i++) {
            if (args[i].equals("--list")) {
                listOnly = true;
            } else if (args[i].equals("--require") && i + 1 < args.length) {
                String required = args[++i];
                if (!names.contains(required)) {
                    System.out.println("MISSING " + required + " is not registered in tests/TestRegistry.java.");
                    return 2;
                }
            }
        }

        if (listOnly) {
            for (TestCase test : tests) System.out.println(test.getName());
            return 0;
        }

        int failed = 0;
        for (TestCase test : tests) {
            try {
                test.getBody().run();
                System.out.println("PASS " + test.getName());
            } catch (Throwable thrown) {
                failed++;
                String message = thrown.getMessage() == null ? "" : thrown.getMessage().split("\n")[0];
                System.out.println("FAIL " + test.getName() + ": " + thrown.getClass().getSimpleName() + ": " + message);
            }
        }

        System.out.println((tests.size() - failed) + " passed, " + failed + " failed");
        return failed == 0 ? 0 : 1;
    }
}
