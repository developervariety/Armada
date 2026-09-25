namespace Armada.Core.Context
{
    using System;
    using System.Collections.Generic;
    using System.Text;

    /// <summary>
    /// Splits brief material into dock files that each fit one read. Runtimes that read a file through a
    /// shell or a read tool truncate a single result near 10 KiB or 256 lines, so every file stays under
    /// both bounds. Text is split on line boundaries and never shortened: joining the parts in order gives
    /// back every line.
    /// </summary>
    public static class BriefFilePacker
    {
        #region Public-Members

        /// <summary>Largest brief file, in UTF-8 bytes.</summary>
        public const int MaxFileBytes = 9000;

        /// <summary>Largest brief file, in lines.</summary>
        public const int MaxFileLines = 200;

        #endregion

        #region Public-Methods

        /// <summary>
        /// Whether a text, written under a heading, fits one file.
        /// </summary>
        /// <param name="text">The text.</param>
        /// <param name="heading">The heading the file starts with.</param>
        /// <returns>True when it fits.</returns>
        public static bool Fits(string text, string heading)
        {
            return Fits(text ?? String.Empty, HeaderBytes(heading), HeaderLines);
        }

        /// <summary>
        /// Splits one text into consecutive parts on line boundaries so each part, under the heading, fits
        /// one file. A single line longer than a file is cut at the byte bound and continues in the next part.
        /// </summary>
        /// <param name="text">The text to split.</param>
        /// <param name="heading">The heading each file starts with.</param>
        /// <returns>The parts, in order; empty for empty text.</returns>
        public static List<string> Split(string text, string heading)
        {
            List<string> parts = new List<string>();
            if (String.IsNullOrEmpty(text)) return parts;

            int headerBytes = HeaderBytes(heading);
            StringBuilder part = new StringBuilder();
            string[] lines = text.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string line = i < lines.Length - 1 ? lines[i] + "\n" : lines[i];
                if (line.Length == 0) continue;

                while (!Fits(line, headerBytes, HeaderLines))
                {
                    if (part.Length > 0)
                    {
                        parts.Add(part.ToString());
                        part.Clear();
                    }
                    int take = Math.Max(1, (MaxFileBytes - headerBytes) / 4);
                    parts.Add(line.Substring(0, Math.Min(take, line.Length)) + "\n");
                    line = line.Substring(Math.Min(take, line.Length));
                }

                if (!Fits(part.ToString() + line, headerBytes, HeaderLines))
                {
                    parts.Add(part.ToString());
                    part.Clear();
                }
                part.Append(line);
            }
            if (part.Length > 0) parts.Add(part.ToString());
            return parts;
        }

        /// <summary>
        /// Writes one text as numbered files under a folder: <c>01-&lt;kind&gt;.md</c>, <c>02-&lt;kind&gt;.md</c>, and so on.
        /// </summary>
        /// <param name="folder">Dock-relative folder, using forward slashes.</param>
        /// <param name="kind">File-name suffix.</param>
        /// <param name="heading">The heading each file starts with.</param>
        /// <param name="text">The text.</param>
        /// <returns>The files, in reading order; empty for empty text.</returns>
        public static List<ContextBriefFile> Pack(string folder, string kind, string heading, string text)
        {
            List<ContextBriefFile> files = new List<ContextBriefFile>();
            List<string> parts = Split(text, heading);
            for (int i = 0; i < parts.Count; i++)
            {
                string title = parts.Count == 1
                    ? heading
                    : heading + " (part " + (i + 1) + " of " + parts.Count + ")";
                files.Add(new ContextBriefFile
                {
                    RelativePath = folder + "/" + (i + 1).ToString("00") + "-" + kind + ".md",
                    Content = "# " + title + "\n\n" + parts[i],
                    ReadFirst = true
                });
            }
            return files;
        }

        #endregion

        #region Internal-Methods

        /// <summary>Lines a file heading takes.</summary>
        internal const int HeaderLines = 3;

        /// <summary>Bytes a file heading takes, with room for a part suffix.</summary>
        internal static int HeaderBytes(string heading)
        {
            return Encoding.UTF8.GetByteCount("# " + (heading ?? String.Empty) + "\n\n") + 64;
        }

        /// <summary>Whether a text fits one file after a heading of the given size.</summary>
        internal static bool Fits(string text, int headerBytes, int headerLines)
        {
            return Encoding.UTF8.GetByteCount(text) + headerBytes <= MaxFileBytes
                && CountLines(text) + headerLines <= MaxFileLines;
        }

        private static int CountLines(string text)
        {
            int count = 0;
            foreach (char ch in text) if (ch == '\n') count++;
            return count;
        }

        #endregion
    }
}
