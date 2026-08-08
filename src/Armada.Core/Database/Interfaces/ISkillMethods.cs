namespace Armada.Core.Database.Interfaces
{
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;

    /// <summary>
    /// Database methods for skills.
    /// </summary>
    public interface ISkillMethods
    {
        /// <summary>
        /// Create a skill.
        /// </summary>
        Task<Skill> CreateAsync(Skill skill, CancellationToken token = default);

        /// <summary>
        /// Read one skill by ID within an optional scope query.
        /// </summary>
        Task<Skill?> ReadAsync(string id, SkillQuery? query = null, CancellationToken token = default);

        /// <summary>
        /// Update a skill.
        /// </summary>
        Task<Skill> UpdateAsync(Skill skill, CancellationToken token = default);

        /// <summary>
        /// Delete a skill by ID within an optional scope query.
        /// </summary>
        Task DeleteAsync(string id, SkillQuery? query = null, CancellationToken token = default);

        /// <summary>
        /// Enumerate skills with paging and filtering.
        /// </summary>
        Task<EnumerationResult<Skill>> EnumerateAsync(SkillQuery query, CancellationToken token = default);

        /// <summary>
        /// Enumerate all skills matching the query without paging.
        /// </summary>
        Task<List<Skill>> EnumerateAllAsync(SkillQuery query, CancellationToken token = default);
    }
}
