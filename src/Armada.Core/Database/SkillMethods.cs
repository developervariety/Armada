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
    /// Skill persistence, one implementation for every provider.
    /// </summary>
    internal sealed class SkillMethods : ISkillMethods
    {
        #region Internal-Members

        /// <summary>
        /// The skills table.
        /// </summary>
        internal static readonly StoredTable<Skill> Table = new StoredTable<Skill>(
            "skills",
            new[]
            {
                "id", "tenant_id", "user_id", "name", "description", "category", "content", "is_built_in", "active", "created_utc",
                "last_update_utc"
            },
            new[] { "created_utc" },
            SkillColumns.Read,
            SkillColumns.Write);

        /// <summary>
        /// By name.
        /// </summary>
        internal const string Order = "name ASC";

        #endregion

        #region Private-Members

        private readonly StoredMethods<Skill> _Rows;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="dialect">Provider the statements run on.</param>
        internal SkillMethods(StoredDialect dialect)
        {
            _Rows = new StoredMethods<Skill>(dialect, Table);
        }

        #endregion

        #region Public-Methods

        /// <inheritdoc />
        public async Task<Skill> CreateAsync(Skill skill, CancellationToken token = default)
        {
            if (skill == null) throw new ArgumentNullException(nameof(skill));
            skill.LastUpdateUtc = DateTime.UtcNow;
            await _Rows.InsertAsync(skill, token).ConfigureAwait(false);
            return skill;
        }

        /// <inheritdoc />
        public Task<Skill?> ReadAsync(string id, SkillQuery? query = null, CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(id)) throw new ArgumentNullException(nameof(id));
            return _Rows.FirstAsync(Scope(Table.Filter().Text("id", id), query, _Rows.Dialect.Provider), token);
        }

        /// <inheritdoc />
        public async Task<Skill> UpdateAsync(Skill skill, CancellationToken token = default)
        {
            if (skill == null) throw new ArgumentNullException(nameof(skill));
            skill.LastUpdateUtc = DateTime.UtcNow;
            await _Rows.UpdateAsync(skill, token).ConfigureAwait(false);
            return skill;
        }

        /// <inheritdoc />
        public async Task DeleteAsync(string id, SkillQuery? query = null, CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(id)) throw new ArgumentNullException(nameof(id));
            await _Rows.DeleteAsync(Scope(Table.Filter().Text("id", id), query, _Rows.Dialect.Provider), token).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public Task<EnumerationResult<Skill>> EnumerateAsync(SkillQuery query, CancellationToken token = default)
        {
            query ??= new SkillQuery();
            return _Rows.PageAsync(Scope(Table.Filter(), query, _Rows.Dialect.Provider), Order, query.PageNumber, query.PageSize, token);
        }

        /// <inheritdoc />
        public Task<List<Skill>> EnumerateAllAsync(SkillQuery query, CancellationToken token = default)
        {
            return _Rows.ListAsync(Scope(Table.Filter(), query, _Rows.Dialect.Provider), Order, token);
        }

        #endregion

        #region Internal-Methods

        /// <summary>
        /// Add the tenant, user, scope, category, search, flag and creation-time conditions of a query.
        /// </summary>
        internal static StoredFilter Scope(StoredFilter filter, SkillQuery? query, DatabaseTypeEnum provider)
        {
            if (query == null) return filter;
            filter
                .Text("tenant_id", query.TenantId)
                .Text("user_id", query.UserId)
                .Text("category", query.Category);
            if (!String.IsNullOrWhiteSpace(query.Search))
            {
                // PostgreSQL matches case-insensitively with ILIKE; the other providers lower both sides.
                if (provider == DatabaseTypeEnum.Postgresql)
                {
                    string pattern = "%" + query.Search + "%";
                    filter.Condition("(name ILIKE @search OR COALESCE(description, '') ILIKE @search)", p => p.Text("@search", "name", pattern));
                }
                else
                {
                    string search = "%" + query.Search.ToLowerInvariant() + "%";
                    filter.Condition("(LOWER(name) LIKE @search OR LOWER(COALESCE(description, '')) LIKE @search)", p => p.Text("@search", "name", search));
                }
            }
            // PostgreSQL stores this creation time as text and compares it as a zoned timestamp.
            string? created = provider == DatabaseTypeEnum.Postgresql ? "created_utc::timestamptz" : null;
            return filter
                .Bool("active", "@active", query.Active)
                .Time("created_utc", ">=", "@from_utc", query.FromUtc, created)
                .Time("created_utc", "<=", "@to_utc", query.ToUtc, created);
        }

        #endregion
    }
}

