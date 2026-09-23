namespace Armada.Test.Unit.TestHelpers
{
    using System;
    using System.Collections.Generic;
    using System.Reflection;
    using System.Runtime.ExceptionServices;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Database.Interfaces;
    using Armada.Core.Models;

    /// <summary>
    /// Mission-methods decorator that refuses every multi-row mission read except the summary
    /// projection, and delegates everything else. A list surface that hydrates full mission rows
    /// (description, diff snapshot, agent output) fails against it, however the surface shapes its
    /// response afterwards.
    /// </summary>
    public class SummaryOnlyMissionMethods : DispatchProxy
    {
        private readonly object _Lock = new object();
        private IMissionMethods? _Inner;
        private readonly List<string> _RefusedCalls = new List<string>();
        private int _SummaryCalls;

        /// <summary>Names of the full-row reads this decorator refused.</summary>
        public List<string> RefusedCalls
        {
            get { lock (_Lock) return new List<string>(_RefusedCalls); }
        }

        /// <summary>Number of summary-projection reads delegated to the real methods.</summary>
        public int SummaryCalls
        {
            get { lock (_Lock) return _SummaryCalls; }
        }

        /// <summary>
        /// Replace a driver's mission methods with a summary-only decorator.
        /// </summary>
        /// <param name="driver">Database driver to modify.</param>
        /// <returns>The installed decorator.</returns>
        public static SummaryOnlyMissionMethods Install(DatabaseDriver driver)
        {
            if (driver == null) throw new ArgumentNullException(nameof(driver));
            IMissionMethods proxy = DispatchProxy.Create<IMissionMethods, SummaryOnlyMissionMethods>();
            SummaryOnlyMissionMethods decorator = (SummaryOnlyMissionMethods)(object)proxy;
            decorator._Inner = driver.Missions;

            PropertyInfo? property = typeof(DatabaseDriver).GetProperty("Missions", BindingFlags.Instance | BindingFlags.Public);
            if (property == null) throw new InvalidOperationException("DatabaseDriver.Missions property not found");
            property.SetValue(driver, proxy);
            return decorator;
        }

        /// <inheritdoc />
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod == null) return null;
            if (_Inner == null) throw new InvalidOperationException("Decorator was not installed over real mission methods.");

            bool isSummary = targetMethod.Name.Contains("Summar", StringComparison.Ordinal);
            if (!isSummary && ReturnsMissionRows(targetMethod.ReturnType))
            {
                lock (_Lock) _RefusedCalls.Add(targetMethod.Name);
                throw new InvalidOperationException("Full-row mission read refused: " + targetMethod.Name);
            }

            if (isSummary)
            {
                lock (_Lock) _SummaryCalls++;
            }

            try
            {
                return targetMethod.Invoke(_Inner, args);
            }
            catch (TargetInvocationException ex) when (ex.InnerException != null)
            {
                ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                throw;
            }
        }

        private static bool ReturnsMissionRows(Type returnType)
        {
            if (!returnType.IsGenericType || returnType.GetGenericTypeDefinition() != typeof(Task<>)) return false;
            Type result = returnType.GenericTypeArguments[0];
            return result == typeof(List<Mission>) || result == typeof(EnumerationResult<Mission>);
        }
    }
}
