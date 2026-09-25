namespace Armada.Core.Database
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database.Interfaces;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>
    /// Prompt template persistence, one implementation for every provider.
    /// </summary>
    internal sealed class PromptTemplateMethods : IPromptTemplateMethods
    {
        #region Internal-Members

        /// <summary>
        /// The prompt_templates table.
        /// </summary>
        internal static readonly StoredTable<PromptTemplate> Table = new StoredTable<PromptTemplate>(
            "prompt_templates",
            new[] { "id", "tenant_id", "user_id", "ownership_scope", "name", "description", "category", "content", "is_built_in", "active", "created_utc", "last_update_utc" },
            new[] { "created_utc" },
            PromptTemplateColumns.Read,
            PromptTemplateColumns.Write);

        /// <summary>
        /// By name, for the full list.
        /// </summary>
        internal const string NameOrder = "name";

        #endregion

        #region Private-Members

        private readonly StoredMethods<PromptTemplate> _Rows;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="dialect">Provider the statements run on.</param>
        internal PromptTemplateMethods(StoredDialect dialect)
        {
            _Rows = new StoredMethods<PromptTemplate>(dialect, Table);
        }

        #endregion

        #region Public-Methods

        /// <inheritdoc />
        public async Task<PromptTemplate> CreateAsync(PromptTemplate template, CancellationToken token = default)
        {
            if (template == null) throw new ArgumentNullException(nameof(template));
            template.LastUpdateUtc = DateTime.UtcNow;
            await _Rows.InsertAsync(template, token).ConfigureAwait(false);
            return template;
        }

        /// <inheritdoc />
        public Task<PromptTemplate?> ReadAsync(string id, CancellationToken token = default)
        {
            if (String.IsNullOrEmpty(id)) throw new ArgumentNullException(nameof(id));
            return _Rows.FirstAsync(Table.Filter().Key("id", id), token);
        }

        /// <inheritdoc />
        public Task<PromptTemplate?> ReadByNameAsync(string name, CancellationToken token = default)
        {
            if (String.IsNullOrEmpty(name)) throw new ArgumentNullException(nameof(name));
            return _Rows.FirstAsync(Table.Filter().Key("name", name), token);
        }

        /// <inheritdoc />
        public Task<PromptTemplate?> ReadByNameAsync(string tenantId, string name, CancellationToken token = default)
        {
            if (String.IsNullOrEmpty(tenantId)) throw new ArgumentNullException(nameof(tenantId));
            if (String.IsNullOrEmpty(name)) throw new ArgumentNullException(nameof(name));
            return _Rows.FirstAsync(Table.Filter().Key("tenant_id", tenantId).Key("name", name), token);
        }

        /// <inheritdoc />
        public async Task<PromptTemplate> UpdateAsync(PromptTemplate template, CancellationToken token = default)
        {
            if (template == null) throw new ArgumentNullException(nameof(template));
            template.LastUpdateUtc = DateTime.UtcNow;
            await _Rows.UpdateAsync(template, token).ConfigureAwait(false);
            return template;
        }

        /// <inheritdoc />
        public async Task DeleteAsync(string id, CancellationToken token = default)
        {
            if (String.IsNullOrEmpty(id)) throw new ArgumentNullException(nameof(id));
            await _Rows.DeleteAsync(Table.Filter().Key("id", id), token).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public Task<List<PromptTemplate>> EnumerateAsync(CancellationToken token = default)
        {
            return _Rows.ListAsync(Table.Filter(), NameOrder, token);
        }

        /// <inheritdoc />
        public async Task<EnumerationResult<PromptTemplate>> EnumerateAsync(EnumerationQuery query, CancellationToken token = default)
        {
            if (query == null) query = new EnumerationQuery();
            EnumerationResult<PromptTemplate> page = await _Rows.PageAsync(Scope(Table.Filter(), query), Order(query), query.PageNumber, query.PageSize, token).ConfigureAwait(false);
            return EnumerationResult<PromptTemplate>.Create(query, page.Objects, page.TotalRecords);
        }

        /// <inheritdoc />
        public async Task<bool> ExistsAsync(string id, CancellationToken token = default)
        {
            if (String.IsNullOrEmpty(id)) throw new ArgumentNullException(nameof(id));
            return await _Rows.CountAsync(Table.Filter().Key("id", id), token).ConfigureAwait(false) > 0;
        }

        /// <inheritdoc />
        public async Task<bool> ExistsByNameAsync(string name, CancellationToken token = default)
        {
            if (String.IsNullOrEmpty(name)) throw new ArgumentNullException(nameof(name));
            return await _Rows.CountAsync(Table.Filter().Key("name", name), token).ConfigureAwait(false) > 0;
        }

        #endregion

        #region Internal-Methods

        /// <summary>
        /// Add the creation-time conditions of a query; both bounds are exclusive.
        /// </summary>
        internal static StoredFilter Scope(StoredFilter filter, EnumerationQuery query)
        {
            return filter
                .Time("created_utc", ">", "@created_after", query.CreatedAfter)
                .Time("created_utc", "<", "@created_before", query.CreatedBefore);
        }

        /// <summary>
        /// By creation time, in the direction the query asks for.
        /// </summary>
        internal static string Order(EnumerationQuery query)
        {
            return "created_utc " + (query.Order == EnumerationOrderEnum.CreatedAscending ? "ASC" : "DESC");
        }

        #endregion
    }
}
