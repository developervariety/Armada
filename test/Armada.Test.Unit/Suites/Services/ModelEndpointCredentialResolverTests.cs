namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Settings;
    using Armada.Runtimes;
    using Armada.Test.Common;
    using SyslogLogging;

    /// <summary>
    /// A mapped model endpoint reads its API key from a server-only credential file into an in-memory copy, and
    /// refuses by name any file that is not a single-line, owner-read-only regular file in the configured directory.
    /// The stored endpoint never carries the key, and no refusal repeats file content.
    /// </summary>
    public class ModelEndpointCredentialResolverTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "Model Endpoint Credential Resolver";

        private const string _Key = "test-credential-value-0001";

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("A mapped endpoint gets a copy carrying the file key and the stored endpoint keeps none", () =>
            {
                using (Scratch scratch = new Scratch())
                {
                    scratch.WriteKey("api.key", _Key + "\n", UnixFileMode.UserRead);
                    ModelEndpoint endpoint = NewEndpoint();
                    ModelEndpointCredentialResolver resolver = scratch.Resolver(endpoint.Id, "api.key");

                    ModelEndpoint resolved = resolver.Resolve(endpoint);

                    AssertFalse(ReferenceEquals(endpoint, resolved), "a copy, not the stored endpoint");
                    AssertEqual(_Key, resolved.ApiKey, "the copy carries the trimmed file key");
                    AssertNull(endpoint.ApiKey, "the stored endpoint never carries the file key");
                    AssertEqual(endpoint.Id, resolved.Id, "the copy is the same endpoint");
                }
            }).ConfigureAwait(false);

            await RunTest("An unmapped endpoint is returned unchanged", () =>
            {
                using (Scratch scratch = new Scratch())
                {
                    ModelEndpoint endpoint = NewEndpoint();
                    endpoint.ApiKey = "stored-value";
                    ModelEndpointCredentialResolver resolver = scratch.Resolver("mep_other", "api.key");

                    AssertTrue(ReferenceEquals(endpoint, resolver.Resolve(endpoint)), "same instance");
                }
            }).ConfigureAwait(false);

            if (OperatingSystem.IsWindows()) return;

            List<(string Label, string Reason, Action<Scratch, ModelEndpoint> Arrange, string FileName)> refusals =
                new List<(string, string, Action<Scratch, ModelEndpoint>, string)>
            {
                ("group readable", "mode must be 0400", (s, e) => s.WriteKey("api.key", _Key, UnixFileMode.UserRead | UnixFileMode.GroupRead), "api.key"),
                ("owner writable", "mode must be 0400", (s, e) => s.WriteKey("api.key", _Key, UnixFileMode.UserRead | UnixFileMode.UserWrite), "api.key"),
                ("symbolic link", "is a symbolic link", (s, e) => s.LinkKey("api.key", "real.key", _Key), "api.key"),
                ("missing", "does not exist", (s, e) => { }, "api.key"),
                ("empty", "is empty", (s, e) => s.WriteKey("api.key", "  \n", UnixFileMode.UserRead), "api.key"),
                ("two lines", "more than one line", (s, e) => s.WriteKey("api.key", _Key + "\n" + _Key, UnixFileMode.UserRead), "api.key"),
                ("parent path", "not a single file name", (s, e) => { }, "../api.key"),
                ("nested path", "not a single file name", (s, e) => { }, "sub/api.key"),
                ("stored key too", "both a stored API key and a credential file",
                    (s, e) => { s.WriteKey("api.key", _Key, UnixFileMode.UserRead); e.ApiKey = "stored-value"; }, "api.key"),
            };

            foreach ((string label, string reason, Action<Scratch, ModelEndpoint> arrange, string fileName) in refusals)
            {
                await RunTest("A credential file that is " + label + " is refused by name without its content", () =>
                {
                    using (Scratch scratch = new Scratch())
                    {
                        ModelEndpoint endpoint = NewEndpoint();
                        arrange(scratch, endpoint);
                        ModelEndpointCredentialResolver resolver = scratch.Resolver(endpoint.Id, fileName);

                        ModelEndpointCredentialException? refused = null;
                        try
                        {
                            resolver.Resolve(endpoint);
                        }
                        catch (ModelEndpointCredentialException ex)
                        {
                            refused = ex;
                        }

                        AssertNotNull(refused, "refused");
                        AssertContains(reason, refused!.Message, "named reason");
                        AssertContains(endpoint.Id, refused.Message, "names the endpoint");
                        AssertFalse(refused.Message.Contains(_Key, StringComparison.Ordinal), "never repeats file content");
                    }
                }).ConfigureAwait(false);
            }

            await RunTest("A relative credential directory is refused", () =>
            {
                ModelEndpoint endpoint = NewEndpoint();
                ModelEndpointCredentialSettings settings = new ModelEndpointCredentialSettings { Directory = "relative/dir" };
                settings.Files[endpoint.Id] = "api.key";

                AssertThrows<ModelEndpointCredentialException>(() => new ModelEndpointCredentialResolver(settings).Resolve(endpoint));
            }).ConfigureAwait(false);

            await RunTest("The runtime factory builds an API runtime only through the credential resolver", () =>
            {
                using (Scratch scratch = new Scratch())
                {
                    ModelEndpoint endpoint = NewEndpoint();
                    AgentRuntimeFactory factory = new AgentRuntimeFactory(new LoggingModule());
                    factory.EndpointCredentials = scratch.Resolver(endpoint.Id, "missing.key");

                    AssertThrows<ModelEndpointCredentialException>(() => factory.Create(endpoint),
                        "a mapped endpoint with no usable file never builds a runtime");
                }
            }).ConfigureAwait(false);
        }

        private static ModelEndpoint NewEndpoint()
        {
            return new ModelEndpoint
            {
                Provider = ModelProviderEnum.Anthropic,
                Kind = ModelEndpointKindEnum.Inference,
                BaseUrl = "https://api.example.invalid",
                Model = "example-model",
                Enabled = true
            };
        }

        private sealed class Scratch : IDisposable
        {
            public string Directory { get; } = Path.Combine(Path.GetTempPath(), "armada-cred-" + Guid.NewGuid().ToString("N"));

            public Scratch()
            {
                System.IO.Directory.CreateDirectory(Directory);
            }

            public ModelEndpointCredentialResolver Resolver(string endpointId, string fileName)
            {
                ModelEndpointCredentialSettings settings = new ModelEndpointCredentialSettings { Directory = Directory };
                settings.Files[endpointId] = fileName;
                return new ModelEndpointCredentialResolver(settings);
            }

            public void WriteKey(string name, string content, UnixFileMode mode)
            {
                string path = Path.Combine(Directory, name);
                File.WriteAllText(path, content);
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, mode);
            }

            public void LinkKey(string name, string target, string content)
            {
                WriteKey(target, content, UnixFileMode.UserRead);
                File.CreateSymbolicLink(Path.Combine(Directory, name), Path.Combine(Directory, target));
            }

            public void Dispose()
            {
                try
                {
                    foreach (string file in System.IO.Directory.GetFiles(Directory))
                        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                    System.IO.Directory.Delete(Directory, true);
                }
                catch (IOException)
                {
                    // A scratch directory left in the temp root does not affect any later test.
                }
            }
        }
    }
}
