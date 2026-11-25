namespace Armada.Test.Unit.TestHelpers
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Reflection;
    using System.Runtime.ExceptionServices;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Database.Interfaces;
    using Armada.Core.Models;

    /// <summary>Return real voyage rows in PostgreSQL's newest-first order; leave every write unchanged.</summary>
    public class NewestFirstMissionMethods : DispatchProxy
    {
        private IMissionMethods _Inner = null!;

        /// <summary>Apply the production provider's ordering to an isolated SQLite lifecycle fixture.</summary>
        public static void Install(DatabaseDriver database)
        {
            IMissionMethods methods = Create<IMissionMethods, NewestFirstMissionMethods>();
            ((NewestFirstMissionMethods)(object)methods)._Inner = database.Missions;
            typeof(DatabaseDriver).GetProperty(nameof(DatabaseDriver.Missions))!.SetValue(database, methods);
        }

        /// <inheritdoc />
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method == null) throw new ArgumentNullException(nameof(method));
            object? result;
            try { result = method.Invoke(_Inner, args); }
            catch (TargetInvocationException exception) when (exception.InnerException != null)
            {
                ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
                throw;
            }
            return method.Name == nameof(IMissionMethods.EnumerateByVoyageAsync)
                ? NewestFirstAsync((Task<List<Mission>>)result!)
                : result;
        }

        private static async Task<List<Mission>> NewestFirstAsync(Task<List<Mission>> read)
        {
            List<Mission> rows = await read.ConfigureAwait(false);
            return rows.OrderByDescending(m => m.CreatedUtc).ToList();
        }
    }
}
