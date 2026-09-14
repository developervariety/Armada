namespace Armada.Server.Routes
{
    using System.IO;
    using System.Text.Json;
    using WatsonWebserver;
    using WatsonWebserver.Core;
    using WatsonWebserver.Core.OpenApi;
    using Armada.Server;
    using Armada.Core;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;

    /// <summary>
    /// REST API routes for backup management.
    /// </summary>
    public class BackupRoutes
    {
        private readonly DatabaseBackupService _backups;
        private readonly JsonSerializerOptions _jsonOptions;

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="backups">Shared provider-aware backup and restore service.</param>
        /// <param name="jsonOptions">JSON serializer options.</param>
        public BackupRoutes(
            DatabaseBackupService backups,
            JsonSerializerOptions jsonOptions)
        {
            _backups = backups ?? throw new ArgumentNullException(nameof(backups));
            _jsonOptions = jsonOptions;
        }

        /// <summary>
        /// Register routes with the application.
        /// </summary>
        /// <param name="app">Webserver.</param>
        /// <param name="authenticate">Authentication middleware.</param>
        /// <param name="authz">Authorization service.</param>
        public void Register(
            Webserver app,
            Func<WatsonWebserver.Core.HttpContextBase, Task<AuthContext>> authenticate,
            IAuthorizationService authz)
        {
            // Backup & Restore
            app.Get("/api/v1/backup", async (ApiRequest req) =>
            {
                AuthContext ctx = await authenticate(req.Http).ConfigureAwait(false);
                if (!authz.IsAuthorized(ctx, req.Http.Request.Method.ToString(), req.Http.Request.Url.RawWithoutQuery))
                {
                    req.Http.Response.StatusCode = ctx.IsAuthenticated ? 403 : 401;
                    return new ApiErrorResponse { Error = ctx.IsAuthenticated ? ApiResultEnum.BadRequest : ApiResultEnum.BadRequest, Message = ctx.IsAuthenticated ? "You do not have permission to perform this action" : "Authentication required" };
                }
                DatabaseBackupResult backupResult;
                try
                {
                    backupResult = await _backups.BackupAsync(null).ConfigureAwait(false);
                }
                catch (DatabaseBackupException ex)
                {
                    req.Http.Response.StatusCode = ex.Refused ? 409 : 500;
                    return new ApiErrorResponse { Error = ApiResultEnum.BadRequest, Message = ex.FailureReason };
                }
                string zipPath = backupResult.Path;
                byte[] fileBytes = await File.ReadAllBytesAsync(zipPath).ConfigureAwait(false);
                string filename = Path.GetFileName(zipPath);
                req.Http.Response.ContentType = "application/zip";
                req.Http.Response.Headers.Add("Content-Disposition", "attachment; filename=\"" + filename + "\"");
                await req.Http.Response.Send(fileBytes).ConfigureAwait(false);
                return null;
            },
            api => api
                .WithTag("Backup")
                .WithSummary("Download backup")
                .WithDescription("Creates a verified provider-native backup of the configured database and streams it as a ZIP with settings and a provider manifest. Returns 500 with a named reason when the native backup or its isolated restore check fails.")
                .WithSecurity("ApiKey"));

            app.Post("/api/v1/restore", async (ApiRequest req) =>
            {
                AuthContext ctx = await authenticate(req.Http).ConfigureAwait(false);
                if (!authz.IsAuthorized(ctx, req.Http.Request.Method.ToString(), req.Http.Request.Url.RawWithoutQuery))
                {
                    req.Http.Response.StatusCode = ctx.IsAuthenticated ? 403 : 401;
                    return new ApiErrorResponse { Error = ctx.IsAuthenticated ? ApiResultEnum.BadRequest : ApiResultEnum.BadRequest, Message = ctx.IsAuthenticated ? "You do not have permission to perform this action" : "Authentication required" };
                }
                byte[] body = req.Http.Request.DataAsBytes;
                if (body == null || body.Length == 0)
                    return (object)new ApiErrorResponse { Error = ApiResultEnum.BadRequest, Message = "Request body must contain the ZIP file" };

                string tempZipPath = Path.Combine(Path.GetTempPath(), "armada-upload-" + Guid.NewGuid().ToString("N") + ".zip");
                try
                {
                    await File.WriteAllBytesAsync(tempZipPath, body).ConfigureAwait(false);
                    string? originalFilename = req.Http.Request.Headers.Get("X-Original-Filename");
                    DatabaseRestoreResult result = await _backups.RestoreAsync(tempZipPath, originalFilename).ConfigureAwait(false);
                    return result;
                }
                catch (DatabaseBackupException ex)
                {
                    req.Http.Response.StatusCode = ex.Refused ? 409 : 500;
                    return (object)new ApiErrorResponse { Error = ApiResultEnum.BadRequest, Message = ex.FailureReason };
                }
                finally
                {
                    if (File.Exists(tempZipPath))
                    {
                        try { File.Delete(tempZipPath); }
                        catch { /* best effort */ }
                    }
                }
            },
            api => api
                .WithTag("Backup")
                .WithSummary("Restore from backup")
                .WithDescription("Accepts a ZIP backup file and restores a SQLite database and settings after a verified safety backup. Returns 409 restore_unsupported_for_provider_<type> on PostgreSQL, MySQL and SQL Server, and 409 backup_provider_mismatch for an archive from another provider. Server restart recommended after restore.")
                .WithSecurity("ApiKey"));
        }
    }
}
