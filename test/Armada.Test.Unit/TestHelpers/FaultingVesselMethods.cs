namespace Armada.Test.Unit.TestHelpers
{
    using System.Diagnostics;
    using System.Reflection;
    using Armada.Core.Database;
    using Armada.Core.Database.Interfaces;
    using Armada.Core.Models;

    /// <summary>
    /// Vessel-methods decorator that fails every single-vessel read made while a matching type is on
    /// the call stack, and delegates everything else. Lets a test fail exactly the vessel read a
    /// component makes while the rest of the flow reads vessels normally.
    /// </summary>
    public sealed class FaultingVesselMethods : IVesselMethods
    {
        private readonly IVesselMethods _Inner;
        private readonly Func<Type, bool> _IsFailingCaller;

        /// <summary>Number of reads this decorator failed.</summary>
        public int FailedReads { get; private set; }

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="inner">Real vessel methods.</param>
        /// <param name="isFailingCaller">True for a type on the call stack whose vessel reads fail.
        /// Compiler-generated state machines are offered with each enclosing type.</param>
        public FaultingVesselMethods(IVesselMethods inner, Func<Type, bool> isFailingCaller)
        {
            _Inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _IsFailingCaller = isFailingCaller ?? throw new ArgumentNullException(nameof(isFailingCaller));
        }

        /// <summary>
        /// Replace a driver's vessel methods with a decorator that fails reads made by
        /// <paramref name="failingCaller"/>.
        /// </summary>
        /// <param name="driver">Database driver to modify.</param>
        /// <param name="failingCaller">Type whose vessel reads fail.</param>
        /// <returns>The installed decorator.</returns>
        public static FaultingVesselMethods Install(DatabaseDriver driver, Type failingCaller)
        {
            if (failingCaller == null) throw new ArgumentNullException(nameof(failingCaller));
            return Install(driver, type => type == failingCaller);
        }

        /// <summary>
        /// Replace a driver's vessel methods with a decorator that fails reads made while a type
        /// matching <paramref name="isFailingCaller"/> is on the call stack.
        /// </summary>
        /// <param name="driver">Database driver to modify.</param>
        /// <param name="isFailingCaller">True for a type whose vessel reads fail.</param>
        /// <returns>The installed decorator.</returns>
        public static FaultingVesselMethods Install(DatabaseDriver driver, Func<Type, bool> isFailingCaller)
        {
            if (driver == null) throw new ArgumentNullException(nameof(driver));
            FaultingVesselMethods faulting = new FaultingVesselMethods(driver.Vessels, isFailingCaller);
            PropertyInfo? property = typeof(DatabaseDriver).GetProperty("Vessels", BindingFlags.Instance | BindingFlags.Public);
            if (property == null) throw new InvalidOperationException("DatabaseDriver.Vessels property not found");
            property.SetValue(driver, faulting);
            return faulting;
        }

        private void ThrowWhenCalledByFailingCaller()
        {
            // The read starts synchronously inside the caller's state machine, so the caller's frame
            // is on the stack at this point.
            StackTrace trace = new StackTrace(false);
            foreach (StackFrame frame in trace.GetFrames())
            {
                Type? declaring = frame.GetMethod()?.DeclaringType;
                while (declaring != null)
                {
                    if (_IsFailingCaller(declaring))
                    {
                        FailedReads++;
                        throw new InvalidOperationException("injected vessel read failure");
                    }

                    declaring = declaring.DeclaringType;
                }
            }
        }

        public Task<Vessel> CreateAsync(Vessel vessel, CancellationToken token = default) => _Inner.CreateAsync(vessel, token);

        public Task<Vessel?> ReadAsync(string id, CancellationToken token = default)
        {
            ThrowWhenCalledByFailingCaller();
            return _Inner.ReadAsync(id, token);
        }

        public Task<Vessel?> ReadByNameAsync(string name, CancellationToken token = default) => _Inner.ReadByNameAsync(name, token);

        public Task<Vessel> UpdateAsync(Vessel vessel, CancellationToken token = default) => _Inner.UpdateAsync(vessel, token);

        public Task DeleteAsync(string id, CancellationToken token = default) => _Inner.DeleteAsync(id, token);

        public Task<List<Vessel>> EnumerateAsync(CancellationToken token = default) => _Inner.EnumerateAsync(token);

        public Task<EnumerationResult<Vessel>> EnumerateAsync(EnumerationQuery query, CancellationToken token = default) => _Inner.EnumerateAsync(query, token);

        public Task<List<Vessel>> EnumerateByFleetAsync(string fleetId, CancellationToken token = default) => _Inner.EnumerateByFleetAsync(fleetId, token);

        public Task<bool> ExistsAsync(string id, CancellationToken token = default) => _Inner.ExistsAsync(id, token);

        public Task<Vessel?> ReadAsync(string tenantId, string id, CancellationToken token = default)
        {
            ThrowWhenCalledByFailingCaller();
            return _Inner.ReadAsync(tenantId, id, token);
        }

        public Task DeleteAsync(string tenantId, string id, CancellationToken token = default) => _Inner.DeleteAsync(tenantId, id, token);

        public Task<List<Vessel>> EnumerateAsync(string tenantId, CancellationToken token = default) => _Inner.EnumerateAsync(tenantId, token);

        public Task<EnumerationResult<Vessel>> EnumerateAsync(string tenantId, EnumerationQuery query, CancellationToken token = default) => _Inner.EnumerateAsync(tenantId, query, token);

        public Task<Vessel?> ReadByNameAsync(string tenantId, string name, CancellationToken token = default) => _Inner.ReadByNameAsync(tenantId, name, token);

        public Task<List<Vessel>> EnumerateByFleetAsync(string tenantId, string fleetId, CancellationToken token = default) => _Inner.EnumerateByFleetAsync(tenantId, fleetId, token);

        public Task<bool> ExistsAsync(string tenantId, string id, CancellationToken token = default) => _Inner.ExistsAsync(tenantId, id, token);

        public Task IncrementCalibrationCounterAsync(string vesselId, CancellationToken token = default) => _Inner.IncrementCalibrationCounterAsync(vesselId, token);

        public Task<Vessel?> ReadAsync(string tenantId, string userId, string id, CancellationToken token = default)
        {
            ThrowWhenCalledByFailingCaller();
            return _Inner.ReadAsync(tenantId, userId, id, token);
        }

        public Task DeleteAsync(string tenantId, string userId, string id, CancellationToken token = default) => _Inner.DeleteAsync(tenantId, userId, id, token);

        public Task<List<Vessel>> EnumerateAsync(string tenantId, string userId, CancellationToken token = default) => _Inner.EnumerateAsync(tenantId, userId, token);

        public Task<EnumerationResult<Vessel>> EnumerateAsync(string tenantId, string userId, EnumerationQuery query, CancellationToken token = default) => _Inner.EnumerateAsync(tenantId, userId, query, token);
    }
}
