namespace Armada.Test.Shared
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Reflection;
    using Armada.Test.Shared.Infrastructure;
    using Touchstone.Core;

    /// <summary>
    /// Central aggregator for every Armada test suite. Discovers all
    /// <see cref="IArmadaTestSuite"/> implementations in this assembly by reflection and
    /// builds their descriptors. Reflection-based discovery replaces the previous manual
    /// registration list, eliminating the class of bug where a written suite was never run
    /// because it was not hand-registered.
    /// </summary>
    public static class ArmadaTestSuites
    {
        #region Public-Members

        /// <summary>
        /// All discovered suites, ordered deterministically by suite identifier so runs are
        /// stable across hosts and machines.
        /// </summary>
        public static IReadOnlyList<TestSuiteDescriptor> All
        {
            get { return _Build(); }
        }

        #endregion

        #region Private-Methods

        private static IReadOnlyList<TestSuiteDescriptor> _Build()
        {
            List<TestSuiteDescriptor> descriptors = new List<TestSuiteDescriptor>();

            IEnumerable<Type> suiteTypes = typeof(ArmadaTestSuites).Assembly
                .GetTypes()
                .Where(t => !t.IsAbstract && !t.IsInterface && typeof(IArmadaTestSuite).IsAssignableFrom(t));

            foreach (Type suiteType in suiteTypes)
            {
                ConstructorInfo? ctor = suiteType.GetConstructor(Type.EmptyTypes);
                if (ctor == null)
                {
                    throw new InvalidOperationException(
                        "Test suite " + suiteType.FullName + " must expose a public parameterless constructor.");
                }

                IArmadaTestSuite suite = (IArmadaTestSuite)ctor.Invoke(Array.Empty<object>());
                descriptors.Add(suite.Build());
            }

            return descriptors
                .OrderBy(d => d.SuiteId, StringComparer.Ordinal)
                .ToList();
        }

        #endregion
    }
}
