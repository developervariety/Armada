namespace Armada.Tui.Screens.Admin
{
    using System;
    using Armada.Tui.Routing;

    /// <summary>
    /// Settings, CLI Tool Permissions (<c>/server?tab=cli-permissions</c>): a nested section strip over the pending and
    /// past Requests (<c>section=requests</c>, the default; <c>request=&lt;id&gt;</c> selects one) and the allow and
    /// deny Rules (<c>section=rules</c>). The old <c>/cli-permissions?tab=...</c> links redirect here. Not thread-safe.
    /// </summary>
    public class CliPermissionsScreen : HubScreen
    {
        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="route">Route of the Settings hub.</param>
        /// <param name="context">Context.</param>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="context"/> is null.</exception>
        public CliPermissionsScreen(RouteMatch route, TuiContext context)
            : base(route, context ?? throw new ArgumentNullException(nameof(context)), RouteTable.CliPermissionSections, (m, section) =>
                section.Key == "rules" ? new CliPermissionRulesScreen(m, context) : new CliPermissionRequestsScreen(m, context))
        {
        }

        #endregion
    }
}
