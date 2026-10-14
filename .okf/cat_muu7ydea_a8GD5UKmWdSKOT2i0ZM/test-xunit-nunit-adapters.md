---
type: "Reference"
title: "Same suites via the xUnit and NUnit adapters"
description: "dotnet test src/Test.Xunit or src/Test.Nunit --framework net10.0 runs the shared Touchstone descriptors through VSTest for IDE integration."
timestamp: "2026-10-05T05:26:12.0222090Z"
created: "2026-10-05T05:26:11.7961480Z"
slug: "test-xunit-nunit-adapters"
category: "cat_muu7ydea_a8GD5UKmWdSKOT2i0ZM"
links: ["test-console-runner-touchstone", "test-temp-profile-sandbox"]
version: "1"
salience: "0.5"
---
Commands:
  dotnet test src/Test.Xunit --framework net10.0
  dotnet test src/Test.Nunit --framework net10.0
(net8.0 also works.) src/Test.Xunit/ArmadaXunitTests.cs derives from Touchstone.XunitAdapter TouchstoneFactBase; Test.Nunit uses the Touchstone NUnit adapter. Both execute the same ArmadaTestSuites.All descriptors as Test.Automated, so a test is written once in src/Test.Shared and runs under all three runners. CI (ci.yml) only runs Test.Automated; the adapters are for VSTest/IDE use. ARMADA_TEST_SUITES filtering applies to the adapters too because the filter lives in ArmadaTestSuites._Build. The TestTemp module initializer (profile sandbox, temp sweep, git pinning) runs for every runner because it is a module initializer in Test.Shared.
