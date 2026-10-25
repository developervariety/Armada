namespace Armada.Core
{
    using System;
    using System.IO;

    /// <summary>Diagnostic I/O must not discard an authoritative runtime exit.</summary>
    public static class RuntimeExitDiagnostics
    {
        /// <summary>Run a diagnostic without allowing a full or disposed log sink to abort exit handling.</summary>
        public static void Try(Action diagnostic)
        {
            try { diagnostic(); }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
        }
    }
}
