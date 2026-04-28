namespace Armada.Core.Services
{
    using System;
    using System.IO;
    using System.Reflection;
    using System.Threading;
    using System.Threading.Tasks;
    using SyslogLogging;
    using Armada.Core.Database;
    using Armada.Core.Models;

    /// <summary>
    /// On admiral startup, ensures the Architect persona's associated PromptTemplate
    /// content matches the source-controlled ArchitectSystemPrompt.md resource. Idempotent —
    /// no-op when already in sync.
    /// </summary>
    public sealed class ArchitectPersonaSyncService
    {
        #region Private-Members

        private readonly DatabaseDriver _Database;
        private readonly LoggingModule _Logging;
        private const string _Header = "[ArchitectPersonaSync] ";
        private const string _PersonaName = "Architect";

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="database">Database driver.</param>
        /// <param name="logging">Logging module.</param>
        public ArchitectPersonaSyncService(DatabaseDriver database, LoggingModule logging)
        {
            _Database = database ?? throw new ArgumentNullException(nameof(database));
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Reads the embedded ArchitectSystemPrompt.md and ensures the Architect persona's
        /// associated PromptTemplate has matching Content. Returns true if a write was performed.
        /// </summary>
        /// <param name="token">Cancellation token.</param>
        /// <returns>True if the template was updated; false if already in sync or persona not found.</returns>
        public async Task<bool> SyncAsync(CancellationToken token = default)
        {
            string promptText = LoadPromptResource();

            Persona? persona = await _Database.Personas.ReadByNameAsync(_PersonaName, token).ConfigureAwait(false);
            if (persona == null)
            {
                _Logging.Warn(_Header + "Architect persona not found; skipping sync (admin must create it via armada_create_persona first)");
                return false;
            }

            PromptTemplate? template = await _Database.PromptTemplates.ReadByNameAsync(persona.PromptTemplateName, token).ConfigureAwait(false);
            if (template == null)
            {
                _Logging.Warn(_Header + "Architect persona prompt template '" + persona.PromptTemplateName + "' not found; skipping sync");
                return false;
            }

            if (template.Content == promptText)
            {
                return false;
            }

            template.Content = promptText;
            template.LastUpdateUtc = DateTime.UtcNow;
            await _Database.PromptTemplates.UpdateAsync(template, token).ConfigureAwait(false);
            _Logging.Info(_Header + "Architect persona SystemInstructions synced (" + promptText.Length + " bytes)");
            return true;
        }

        #endregion

        #region Private-Methods

        private static string LoadPromptResource()
        {
            Assembly assembly = typeof(ArchitectPersonaSyncService).Assembly;
            using (Stream? stream = assembly.GetManifestResourceStream("Armada.Core.Resources.ArchitectSystemPrompt.md"))
            {
                if (stream == null) throw new InvalidOperationException("ArchitectSystemPrompt.md embedded resource not found");
                using (StreamReader reader = new StreamReader(stream))
                {
                    return reader.ReadToEnd();
                }
            }
        }

        #endregion
    }
}
