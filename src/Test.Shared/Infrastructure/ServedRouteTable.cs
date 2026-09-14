namespace Test.Shared.Infrastructure
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Net;
    using System.Net.Sockets;
    using System.Text.RegularExpressions;
    using System.Threading.Tasks;
    using Armada.Proxy;
    using Armada.Proxy.Settings;
    using SyslogLogging;
    using WatsonWebserver.Core.Routing;

    /// <summary>
    /// The routes a live Admiral or proxy registered, read from the Watson routing table.
    /// Contract suites match published requests against this table instead of a hand-kept list.
    /// </summary>
    public sealed class ServedRouteTable
    {
        #region Public-Members

        /// <summary>
        /// Every registered static, parameter and dynamic route.
        /// </summary>
        public IReadOnlyList<ServedRoute> Routes
        {
            get { return _Routes; }
        }

        #endregion

        #region Private-Members

        private readonly List<ServedRoute> _Routes;

        #endregion

        #region Constructors-and-Factories

        private ServedRouteTable(List<ServedRoute> routes)
        {
            _Routes = routes;
        }

        /// <summary>
        /// Read the table of the shared in-process Admiral for a suite.
        /// </summary>
        /// <param name="suiteKey">The suite instance acquiring the server.</param>
        /// <returns>The Admiral route table.</returns>
        public static async Task<ServedRouteTable> ReadAdmiralAsync(object suiteKey)
        {
            E2EServerFixture fx = await E2EServerFixture.AcquireAsync(suiteKey).ConfigureAwait(false);
            return FromRoutes(fx.Server.RestRoutes);
        }

        /// <summary>
        /// Start a separate Admiral with Harbor and WebSockets enabled, read its table, and stop it.
        /// Harbor routes are registered only in this configuration.
        /// </summary>
        /// <returns>The Harbor-enabled Admiral route table.</returns>
        public static async Task<ServedRouteTable> ReadHarborAdmiralAsync()
        {
            E2EServerFixture fx = await E2EServerFixture.StartIsolatedAsync(settings =>
            {
                settings.Harbor.Enabled = true;
                settings.WebSocketEnabled = true;
            }).ConfigureAwait(false);
            try
            {
                return FromRoutes(fx.Server.RestRoutes);
            }
            finally
            {
                fx.Stop();
            }
        }

        /// <summary>
        /// Start a proxy on a free loopback port, read its table, and stop it.
        /// </summary>
        /// <returns>The proxy route table.</returns>
        public static async Task<ServedRouteTable> ReadProxyAsync()
        {
            string dataDirectory = TestTemp.NewDirectory("route-table-proxy");
            ProxySettings settings = new ProxySettings
            {
                Hostname = "127.0.0.1",
                Port = ReservePort(),
                Password = "route-table-password",
                DataDirectory = dataDirectory,
                LogDirectory = Path.Combine(dataDirectory, "logs")
            };
            settings.InitializeDirectories();

            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            ArmadaProxyServer proxy = new ArmadaProxyServer(logging, settings, quiet: true);
            await proxy.StartAsync().ConfigureAwait(false);
            try
            {
                return FromRoutes(proxy.Routes);
            }
            finally
            {
                proxy.Stop();
            }
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// True when a registered route serves the method and concrete or templated path.
        /// </summary>
        /// <param name="method">HTTP method.</param>
        /// <param name="path">Path without query; <c>{{variable}}</c> segments are placeholders.</param>
        /// <returns>Whether a route matches.</returns>
        public bool Serves(string method, string path)
        {
            return _Routes.Any(route => route.Matches(method, path));
        }

        #endregion

        #region Private-Methods

        private static ServedRouteTable FromRoutes(WebserverRoutes routes)
        {
            List<ServedRoute> served = new List<ServedRoute>();
            foreach (RoutingGroup group in new[] { routes.PreAuthentication, routes.PostAuthentication })
            {
                foreach (StaticRoute route in group.Static.GetAll())
                    served.Add(ServedRoute.FromTemplate(route.Method.ToString(), route.Path));
                foreach (ParameterRoute route in group.Parameter.GetAll())
                    served.Add(ServedRoute.FromTemplate(route.Method.ToString(), route.Path));
                foreach (DynamicRoute route in group.Dynamic.GetAll())
                    served.Add(ServedRoute.FromRegex(route.Method.ToString(), route.Path));
            }

            if (served.Count == 0) throw new InvalidOperationException("The server registered no routes; the contract cannot be checked.");
            return new ServedRouteTable(served);
        }

        private static int ReservePort()
        {
            TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        #endregion
    }
}
