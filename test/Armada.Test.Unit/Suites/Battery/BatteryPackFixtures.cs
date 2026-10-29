namespace Armada.Test.Unit.Suites.Battery
{
    using System;
    using System.IO;
    using System.Text;
    using Armada.Core.Services;

    /// <summary>
    /// Builds throwaway battery packs and worktrees on disk for the battery suites.
    /// </summary>
    internal static class BatteryPackFixtures
    {
        /// <summary>A valid one-task manifest the validation cases change one field of.</summary>
        internal const string BaseManifest = @"{
  // comments and trailing commas are allowed
  ""schemaVersion"": 1,
  ""id"": ""mini"",
  ""name"": ""Mini"",
  ""version"": 1,
  ""fixture"": { ""path"": ""fixture"" },
  ""toolchain"": [ { ""name"": ""git"", ""executable"": ""git"", ""arguments"": [ ""--version"" ] }, ],
  ""tasks"": [
    {
      ""id"": ""t1"",
      ""title"": ""Task one"",
      ""prompt"": ""Do the thing."",
      ""grader"": { ""type"": ""command"", ""commands"": [ { ""executable"": ""git"", ""arguments"": [ ""--version"" ] } ] }
    }
  ]
}";

        /// <summary>Create a fresh empty directory under the temp folder.</summary>
        /// <param name="prefix">Name prefix.</param>
        /// <returns>The directory.</returns>
        internal static string NewDirectory(string prefix)
        {
            string path = Path.Combine(Path.GetTempPath(), "armada_battery_" + prefix + "_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }

        /// <summary>
        /// Write a pack into a battery directory: its manifest and the files it references.
        /// </summary>
        /// <param name="batteryDirectory">Battery directory.</param>
        /// <param name="id">Pack folder name.</param>
        /// <param name="manifest">Manifest text.</param>
        /// <param name="files">Pack-relative path and content pairs written next to the manifest.</param>
        /// <returns>The pack directory.</returns>
        internal static string WritePack(string batteryDirectory, string id, string manifest, params PackFile[] files)
        {
            string directory = Path.Combine(batteryDirectory, id);
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, BatteryPackLoader.ManifestFileName), manifest, new UTF8Encoding(false));
            foreach (PackFile file in files)
            {
                string path = Path.Combine(directory, file.Path.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, file.Content);
            }

            return directory;
        }

        /// <summary>One file of a pack.</summary>
        internal sealed class PackFile
        {
            /// <summary>Pack-relative path.</summary>
            public string Path { get; }

            /// <summary>Bytes.</summary>
            public byte[] Content { get; }

            /// <summary>Instantiate from text (UTF-8, no byte order mark).</summary>
            /// <param name="path">Pack-relative path.</param>
            /// <param name="text">Text.</param>
            public PackFile(string path, string text)
            {
                Path = path;
                Content = new UTF8Encoding(false).GetBytes(text);
            }

            /// <summary>Instantiate from bytes.</summary>
            /// <param name="path">Pack-relative path.</param>
            /// <param name="content">Bytes.</param>
            public PackFile(string path, byte[] content)
            {
                Path = path;
                Content = content;
            }
        }
    }
}
