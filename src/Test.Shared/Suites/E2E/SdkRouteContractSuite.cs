namespace Test.Shared.Suites.E2E
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net;
    using System.Net.Http;
    using System.Reflection;
    using System.Runtime.CompilerServices;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Client;
    using Test.Shared.Infrastructure;
    using Touchstone.Core;
    using static Test.Shared.Infrastructure.Asserts;

    /// <summary>
    /// Every public async method on the typed client must call a route the Admiral serves.
    /// Methods are discovered by reflection, so a new wrapper cannot skip the check.
    /// </summary>
    public sealed class SdkRouteContractSuite : IArmadaTestSuite
    {
        #region Private-Members

        private const string SuiteId = "E2E.SdkRouteContract";
        private const string SampleId = "sample_id";

        #endregion

        #region Public-Methods

        /// <summary>
        /// Build the SDK route contract suite.
        /// </summary>
        /// <returns>The suite descriptor.</returns>
        public TestSuiteDescriptor Build()
        {
            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>
            {
                CaseAsync("every_client_method_calls_a_served_route", "Every ArmadaApiClient method calls a served Admiral route", TestTags.Positive, async () =>
                {
                    ServedRouteTable admiral = await ServedRouteTable.ReadAdmiralAsync(this).ConfigureAwait(false);
                    List<MethodInfo> methods = typeof(ArmadaApiClient)
                        .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                        .Where(method => typeof(Task).IsAssignableFrom(method.ReturnType))
                        .OrderBy(method => method.Name, StringComparer.Ordinal)
                        .ToList();
                    AssertTrue(methods.Count > 100, "The typed client must expose its wrappers; found " + methods.Count + ".");

                    List<string> failures = new List<string>();
                    int requestCount = 0;
                    foreach (MethodInfo method in methods)
                    {
                        List<RecordedRequest> recorded = await InvokeAsync(method).ConfigureAwait(false);
                        if (recorded.Count == 0)
                        {
                            failures.Add(Describe(method) + ": sent no HTTP request");
                            continue;
                        }

                        foreach (RecordedRequest request in recorded)
                        {
                            requestCount++;
                            if (!admiral.Serves(request.Method, request.Path))
                            {
                                failures.Add(Describe(method) + ": " + request.Method + " " + request.Path + " is not served");
                            }
                        }
                    }

                    AssertTrue(requestCount >= methods.Count, "Every client method must be exercised.");
                    AssertTrue(failures.Count == 0, failures.Count + " client call(s) do not reach a served route:\n" + String.Join("\n", failures));
                })
            };

            return new TestSuiteDescriptor(SuiteId, "SDK client route contract", cases);
        }

        #endregion

        #region Private-Methods

        private static async Task<List<RecordedRequest>> InvokeAsync(MethodInfo method)
        {
            RecordingHandler handler = new RecordingHandler();
            using (HttpClient http = new HttpClient(handler))
            using (ArmadaApiClient client = new ArmadaApiClient(http, "http://sdk-contract.invalid"))
            using (CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
            {
                object?[] arguments = method.GetParameters().Select(parameter => CreateArgument(parameter, timeout.Token)).ToArray();
                try
                {
                    Task task = (Task)method.Invoke(client, arguments)!;
                    await task.ConfigureAwait(false);
                }
                catch (Exception ex) when (handler.Requests.Count > 0)
                {
                    // The empty canned response may not deserialize into every result type; the
                    // request was already recorded, and the route is what this contract measures.
                    GC.KeepAlive(ex);
                }
                catch (Exception ex)
                {
                    Exception root = ex is TargetInvocationException && ex.InnerException != null ? ex.InnerException : ex;
                    throw new InvalidOperationException(Describe(method) + " failed before sending a request: " + root.GetType().Name + ": " + root.Message, root);
                }

                return handler.Requests.ToList();
            }
        }

        private static object? CreateArgument(ParameterInfo parameter, CancellationToken token)
        {
            Type type = parameter.ParameterType;
            if (type == typeof(CancellationToken)) return token;
            if (type == typeof(string)) return SampleId;

            Type? nullable = Nullable.GetUnderlyingType(type);
            Type target = nullable ?? type;
            if (target == typeof(int)) return 1;
            if (target == typeof(long)) return 1L;
            if (target == typeof(bool)) return true;
            if (target.IsEnum) return Enum.GetValues(target).GetValue(0);
            if (target.IsValueType) return Activator.CreateInstance(target);

            ConstructorInfo? constructor = target.GetConstructor(Type.EmptyTypes);
            if (constructor != null) return constructor.Invoke(Array.Empty<object>());
            return RuntimeHelpers.GetUninitializedObject(target);
        }

        private static string Describe(MethodInfo method)
        {
            return method.Name + "(" + String.Join(", ", method.GetParameters().Select(parameter => parameter.ParameterType.Name)) + ")";
        }

        private static TestCaseDescriptor CaseAsync(string id, string name, string tag, Func<Task> body)
        {
            return new TestCaseDescriptor(SuiteId, id, name, _ => body(), new List<string> { tag, TestTags.EndToEnd });
        }

        #endregion

        #region Private-Classes

        private sealed class RecordedRequest
        {
            public string Method { get; set; } = String.Empty;

            public string Path { get; set; } = String.Empty;
        }

        private sealed class RecordingHandler : HttpMessageHandler
        {
            public List<RecordedRequest> Requests { get; } = new List<RecordedRequest>();

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Requests.Add(new RecordedRequest
                {
                    Method = request.Method.Method.ToUpperInvariant(),
                    Path = Uri.UnescapeDataString(request.RequestUri!.AbsolutePath)
                });

                HttpResponseMessage response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{}", Encoding.UTF8, "application/json")
                };
                return Task.FromResult(response);
            }
        }

        #endregion
    }
}
