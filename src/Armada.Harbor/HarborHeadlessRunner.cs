namespace Armada.Harbor
{
    using System;
    using System.Runtime.InteropServices;
    using System.Threading;
    using System.Threading.Tasks;
    using SyslogLogging;

    /// <summary>
    /// Runs Harbor's link to the Admiral without a window or tray icon: for <c>--headless</c>, and as the fallback when
    /// the desktop UI cannot start (for example a Mac whose display is asleep fails Avalonia's render timer with -6661).
    /// Work keeps flowing to this computer either way; restart Harbor normally to get the window back.
    /// </summary>
    public static class HarborHeadlessRunner
    {
        /// <summary>
        /// Command-line flag that starts Harbor headless.
        /// </summary>
        public const string HeadlessFlag = "--headless";

        /// <summary>
        /// Load the settings and keep the link up until SIGTERM, SIGINT or Ctrl-C.
        /// </summary>
        /// <param name="reason">Why Harbor is running headless, for the log.</param>
        /// <returns>Process exit code.</returns>
        public static int Run(string reason)
        {
            HarborAppSettings settings = HarborAppSettings.Load();
            LoggingModule logging = MainWindow.CreateLogging();
            logging.Info("[Harbor] running headless: " + reason);

            using CancellationTokenSource stop = new CancellationTokenSource();
            Console.CancelKeyPress += (sender, args) => { args.Cancel = true; stop.Cancel(); };
            using PosixSignalRegistration term = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context => { context.Cancel = true; stop.Cancel(); });

            HarborLinkHost host = new HarborLinkHost(settings, logging);
            host.OnLinkState = state => logging.Info("[Harbor] link " + state);
            host.OnDetail = detail => logging.Info("[Harbor] " + detail);

            try
            {
                host.RunAsync(stop.Token).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
            }

            logging.Info("[Harbor] headless run stopped");
            return 0;
        }
    }
}
