namespace Armada.Test.Common
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Reflection;
    using System.Reflection.Metadata;
    using System.Reflection.PortableExecutable;
    using System.Security.Cryptography;

    /// <summary>Build-time source checksums from the executing assembly's portable symbols.</summary>
    public static class TestBuildEvidence
    {
        /// <summary>Read repository test source checksums without exposing build host paths.</summary>
        public static Dictionary<string, string> ReadSourceChecksums()
        {
            Assembly assembly = Assembly.GetEntryAssembly() ?? throw new InvalidOperationException("Missing entry assembly");
            Dictionary<string, string> sources = new Dictionary<string, string>(StringComparer.Ordinal);
            using (FileStream stream = File.OpenRead(Path.ChangeExtension(assembly.Location, ".pdb")))
            using (MetadataReaderProvider provider = MetadataReaderProvider.FromPortablePdbStream(stream))
            {
                MetadataReader reader = provider.GetMetadataReader();
                BlobContentId symbolId = new BlobContentId(reader.DebugMetadataHeader!.Id);
                bool matches = false;
                using (FileStream executable = File.OpenRead(assembly.Location))
                using (PEReader image = new PEReader(executable))
                {
                    foreach (DebugDirectoryEntry entry in image.ReadDebugDirectory())
                    {
                        if (entry.Type != DebugDirectoryEntryType.CodeView) continue;
                        CodeViewDebugDirectoryData codeView = image.ReadCodeViewDebugDirectoryData(entry);
                        matches |= codeView.Guid == symbolId.Guid && entry.Stamp == symbolId.Stamp;
                    }
                }
                if (!matches) throw new InvalidOperationException("Portable symbols do not match the executing assembly");
                foreach (DocumentHandle handle in reader.Documents)
                {
                    Document document = reader.GetDocument(handle);
                    string name = reader.GetString(document.Name).Replace('\\', '/');
                    int root = name.LastIndexOf("/test/", StringComparison.Ordinal);
                    if (root < 0) continue;
                    if (reader.GetGuid(document.HashAlgorithm) != new Guid("8829d00f-11b8-4213-878b-770e8597ac16"))
                        throw new InvalidOperationException("Test source evidence requires SHA256 symbols");
                    sources.Add(name.Substring(root + 1), Convert.ToHexString(reader.GetBlobBytes(document.Hash)).ToLowerInvariant());
                }
            }
            if (sources.Count == 0) throw new InvalidOperationException("No test source evidence in symbols");
            return sources;
        }

        /// <summary>Fingerprint the executable that produced the manifest.</summary>
        public static string ReadExecutableChecksum()
        {
            using (FileStream stream = File.OpenRead(Assembly.GetEntryAssembly()!.Location))
                return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }
    }
}
