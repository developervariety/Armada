namespace Armada.Core.Enums
{
    using System;

    /// <summary>
    /// Parsing helpers for <see cref="MissionModeEnum"/>. Database drivers read a nullable text column
    /// written by older builds, so an absent or unrecognized value must resolve to the historical
    /// behaviour rather than throw.
    /// </summary>
    public static class MissionModes
    {
        /// <summary>
        /// Resolves a stored or user-supplied mode name. Null, empty, whitespace, and unrecognized
        /// values all resolve to <see cref="MissionModeEnum.Implementation"/>, which is what every
        /// mission created before this column existed was.
        /// </summary>
        /// <param name="value">Stored or supplied mode name; case-insensitive.</param>
        /// <returns>The parsed mode, or Implementation when it cannot be parsed.</returns>
        public static MissionModeEnum Parse(string? value)
        {
            if (String.IsNullOrWhiteSpace(value)) return MissionModeEnum.Implementation;

            MissionModeEnum parsed;
            if (Enum.TryParse<MissionModeEnum>(value.Trim(), true, out parsed)) return parsed;

            return MissionModeEnum.Implementation;
        }

        /// <summary>
        /// Reports whether a supplied mode name is one Armada recognizes. Callers that validate user
        /// input use this to reject a typo instead of silently treating it as Implementation.
        /// </summary>
        /// <param name="value">Supplied mode name; case-insensitive.</param>
        /// <returns>True when the value names a known mode.</returns>
        public static bool IsKnown(string? value)
        {
            if (String.IsNullOrWhiteSpace(value)) return false;

            MissionModeEnum parsed;
            return Enum.TryParse<MissionModeEnum>(value.Trim(), true, out parsed) &&
                Enum.IsDefined(typeof(MissionModeEnum), parsed);
        }
    }
}
