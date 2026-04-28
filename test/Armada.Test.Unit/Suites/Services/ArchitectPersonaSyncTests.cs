namespace Armada.Test.Unit.Suites.Services
{
    using System.Threading.Tasks;
    using SyslogLogging;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;

    /// <summary>Tests for ArchitectPersonaSyncService: idempotency, stale-update, and no-persona path.</summary>
    public class ArchitectPersonaSyncTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "Architect Persona Sync";

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("Sync_PersonaMissing_LogsAndReturnsFalse", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    LoggingModule logging = new LoggingModule();
                    logging.Settings.EnableConsole = false;
                    ArchitectPersonaSyncService sut = new ArchitectPersonaSyncService(testDb.Driver, logging);
                    bool result = await sut.SyncAsync().ConfigureAwait(false);
                    AssertFalse(result, "No persona present; sync should return false");
                }
            });

            await RunTest("Sync_PersonaPresent_StaleInstructions_Updates", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    PromptTemplate template = new PromptTemplate();
                    template.Name = "persona.architect";
                    template.Category = "persona";
                    template.Content = "old text";
                    await testDb.Driver.PromptTemplates.CreateAsync(template).ConfigureAwait(false);

                    Persona persona = new Persona();
                    persona.Name = "Architect";
                    persona.PromptTemplateName = "persona.architect";
                    await testDb.Driver.Personas.CreateAsync(persona).ConfigureAwait(false);

                    LoggingModule logging = new LoggingModule();
                    logging.Settings.EnableConsole = false;
                    ArchitectPersonaSyncService sut = new ArchitectPersonaSyncService(testDb.Driver, logging);

                    bool result = await sut.SyncAsync().ConfigureAwait(false);
                    AssertTrue(result, "Stale instructions should trigger update and return true");

                    PromptTemplate? read = await testDb.Driver.PromptTemplates.ReadByNameAsync("persona.architect").ConfigureAwait(false);
                    AssertNotNull(read, "Template should still exist after sync");
                    AssertContains("==DISCOVERY", read!.Content, "Template content should now contain prompt resource markers");
                }
            });

            await RunTest("Sync_PersonaPresent_AlreadyInSync_NoOp", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    PromptTemplate template = new PromptTemplate();
                    template.Name = "persona.architect";
                    template.Category = "persona";
                    template.Content = "old text";
                    await testDb.Driver.PromptTemplates.CreateAsync(template).ConfigureAwait(false);

                    Persona persona = new Persona();
                    persona.Name = "Architect";
                    persona.PromptTemplateName = "persona.architect";
                    await testDb.Driver.Personas.CreateAsync(persona).ConfigureAwait(false);

                    LoggingModule logging = new LoggingModule();
                    logging.Settings.EnableConsole = false;
                    ArchitectPersonaSyncService sut = new ArchitectPersonaSyncService(testDb.Driver, logging);

                    await sut.SyncAsync().ConfigureAwait(false);
                    bool secondRun = await sut.SyncAsync().ConfigureAwait(false);
                    AssertFalse(secondRun, "Second sync should be no-op and return false");
                }
            });
        }
    }
}
