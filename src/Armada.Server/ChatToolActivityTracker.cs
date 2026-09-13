namespace Armada.Server
{
    using System;
    using System.Collections.Generic;
    using Armada.Runtimes;

    /// <summary>
    /// Turns the tool activity records of one chat turn into tool card updates. Activity records carry no
    /// call identifier, so an unfinished call is matched to its completion by tool name and detail.
    /// Not thread-safe; callers serialize access.
    /// </summary>
    public sealed class ChatToolActivityTracker
    {
        #region Private-Members

        private readonly Dictionary<string, string> _OpenCards = new Dictionary<string, string>(StringComparer.Ordinal);
        private int _CardCount = 0;

        #endregion

        #region Public-Methods

        /// <summary>
        /// Build the card update for one tool activity record.
        /// </summary>
        /// <param name="record">Parsed tool activity record.</param>
        /// <returns>The card update.</returns>
        public ChatToolActivityEvent Next(ToolActivityRecord record)
        {
            if (record == null) throw new ArgumentNullException(nameof(record));

            string key = record.Name + "\n" + (record.Detail ?? String.Empty);
            string id;
            if (_OpenCards.TryGetValue(key, out string? existing))
            {
                id = existing;
            }
            else
            {
                _CardCount++;
                id = "activity-" + _CardCount;
            }

            if (record.IsFinished) _OpenCards.Remove(key);
            else _OpenCards[key] = id;

            return new ChatToolActivityEvent
            {
                Id = id,
                Name = record.Name,
                Arguments = record.Detail,
                Phase = record.IsFinished ? "completed" : "started",
                Ok = record.Succeeded
            };
        }

        #endregion
    }
}
