namespace Armada.Tui.Routing
{
    using System;
    using System.Collections.Generic;
    using System.Linq;

    /// <summary>
    /// One named route mirroring a dashboard path (for example <c>/missions/:id</c>). A route either opens a screen
    /// (<see cref="ScreenName"/>), opens a hub (<see cref="Hub"/>), or redirects (<see cref="RedirectTo"/>) exactly like
    /// the dashboard's <c>Navigate</c> routes.
    /// </summary>
    public class RouteDefinition
    {
        #region Public-Members

        /// <summary>
        /// Pattern: literal segments, <c>:name</c> parameters, and <c>:name?</c> optional parameters.
        /// </summary>
        public string Pattern { get; }

        /// <summary>
        /// English title (catalog key).
        /// </summary>
        public string Title { get; }

        /// <summary>
        /// TUI screen name from the plan (for example MissionScreen); empty for redirects.
        /// </summary>
        public string ScreenName { get; }

        /// <summary>
        /// Workstream task that builds the screen (for example W3.8); empty for redirects.
        /// </summary>
        public string Workstream { get; }

        /// <summary>
        /// Redirect target (path and query), or null. Query values written as <c>:name</c> take the matched route
        /// parameter of that name (for example <c>/planning/:id</c> redirects to
        /// <c>/dispatch?tab=planning&amp;session=:id</c>); the source query is carried over for keys the target does not
        /// set.
        /// </summary>
        public string? RedirectTo { get; }

        /// <summary>
        /// Source query keys renamed before they are carried to <see cref="RedirectTo"/> (for example
        /// <c>/cli-permissions?tab=rules</c> carries <c>tab</c> as <c>section</c>). Never null.
        /// </summary>
        public IReadOnlyDictionary<string, string> RedirectQueryRenames { get; }

        /// <summary>
        /// Hub tabs, or null when the route is not a hub.
        /// </summary>
        public HubDefinition? Hub { get; }

        /// <summary>
        /// True for TUI-only routes beyond the dashboard (for example the Approvals center).
        /// </summary>
        public bool IsExtension { get; }

        /// <summary>
        /// Hub tab selected when the query names none (for example <c>/vessels/health</c> opens the Health tab), or null
        /// for the hub's first tab.
        /// </summary>
        public string? DefaultTab { get; }

        /// <summary>
        /// Parsed pattern segments.
        /// </summary>
        public IReadOnlyList<string> Segments { get; }

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="pattern">Pattern.</param>
        /// <param name="title">English title.</param>
        /// <param name="screenName">Screen name.</param>
        /// <param name="workstream">Workstream task.</param>
        /// <param name="redirectTo">Redirect target, or null.</param>
        /// <param name="hub">Hub, or null.</param>
        /// <param name="isExtension">TUI-only route.</param>
        /// <param name="defaultTab">Hub tab used when the query names none, or null.</param>
        /// <param name="redirectQueryRenames">Source query keys renamed before a redirect carries them, or null.</param>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="pattern"/> is null.</exception>
        public RouteDefinition(string pattern, string title, string screenName, string workstream, string? redirectTo = null, HubDefinition? hub = null, bool isExtension = false, string? defaultTab = null, IReadOnlyDictionary<string, string>? redirectQueryRenames = null)
        {
            Pattern = pattern ?? throw new ArgumentNullException(nameof(pattern));
            Title = title ?? "";
            ScreenName = screenName ?? "";
            Workstream = workstream ?? "";
            RedirectTo = redirectTo;
            Hub = hub;
            IsExtension = isExtension;
            DefaultTab = defaultTab;
            RedirectQueryRenames = redirectQueryRenames != null
                ? new Dictionary<string, string>(redirectQueryRenames.ToDictionary(k => k.Key, k => k.Value), StringComparer.Ordinal)
                : new Dictionary<string, string>(StringComparer.Ordinal);
            Segments = pattern.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries).ToList().AsReadOnly();
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Match path segments; on success returns a specificity score (higher is more specific) and fills parameters.
        /// </summary>
        /// <param name="pathSegments">Path segments.</param>
        /// <param name="parameters">Receives parameters.</param>
        /// <returns>The score, or -1 when the route does not match.</returns>
        public int Match(IReadOnlyList<string> pathSegments, Dictionary<string, string> parameters)
        {
            if (pathSegments == null) throw new ArgumentNullException(nameof(pathSegments));
            if (parameters == null) throw new ArgumentNullException(nameof(parameters));
            parameters.Clear();
            int required = Segments.Count(s => !s.EndsWith("?"));
            if (pathSegments.Count < required || pathSegments.Count > Segments.Count) return -1;
            int score = 0;
            for (int i = 0; i < Segments.Count; i++)
            {
                string seg = Segments[i];
                bool optional = seg.EndsWith("?");
                bool isParam = seg.StartsWith(":");
                if (i >= pathSegments.Count)
                {
                    if (!optional) return -1;
                    continue;
                }

                if (isParam)
                {
                    string name = seg.TrimStart(':').TrimEnd('?');
                    parameters[name] = Uri.UnescapeDataString(pathSegments[i]);
                    score += optional ? 1 : 2;
                }
                else
                {
                    if (!String.Equals(seg, pathSegments[i], StringComparison.OrdinalIgnoreCase)) return -1;
                    score += 3;
                }
            }

            return score;
        }

        #endregion
    }
}
