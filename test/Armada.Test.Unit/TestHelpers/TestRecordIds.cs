namespace Armada.Test.Unit.TestHelpers
{
    using System;

    /// <summary>
    /// Record ids with a chosen creation-time part, for fixtures that need two records to share an
    /// abbreviation or need distinct abbreviations regardless of how fast they are created.
    /// </summary>
    public static class TestRecordIds
    {
        #region Public-Methods

        /// <summary>A record id whose creation-time part encodes the given Unix milliseconds.</summary>
        /// <param name="prefix">Id prefix without the underscore, such as <c>msn</c>.</param>
        /// <param name="unixMilliseconds">Creation time in Unix milliseconds.</param>
        /// <param name="tail">Unique tail after the time part.</param>
        /// <returns>The id.</returns>
        public static string At(string prefix, long unixMilliseconds, string tail)
        {
            const string digits = "0123456789abcdefghijklmnopqrstuvwxyz";
            char[] timePart = new char[8];
            for (int i = 7; i >= 0; i--)
            {
                timePart[i] = digits[(int)(unixMilliseconds % 36)];
                unixMilliseconds /= 36;
            }
            return prefix + "_" + new string(timePart) + "_" + tail;
        }

        /// <summary>The abbreviation of a full id: its prefix and creation-time part.</summary>
        /// <param name="fullId">Full record id.</param>
        /// <returns>The abbreviation.</returns>
        public static string Abbreviate(string fullId)
        {
            return fullId.Substring(0, fullId.IndexOf('_') + 9);
        }

        #endregion
    }
}
