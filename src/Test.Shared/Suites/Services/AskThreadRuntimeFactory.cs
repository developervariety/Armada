namespace Test.Shared.Suites.Services
{
    using System.Collections.Generic;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Runtimes;
    using Armada.Runtimes.Interfaces;
    using SyslogLogging;

    internal sealed class AskThreadRuntimeFactory : AgentRuntimeFactory
    {
        public List<AskThreadReplayRuntime> Starts { get; } = new List<AskThreadReplayRuntime>();
        public TaskCompletionSource<AskThreadReplayRuntime> Created { get; } = new TaskCompletionSource<AskThreadReplayRuntime>(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool AutoComplete { get; set; } = true;
        public string Reply { get; set; } = "Saved assistant reply";
        public List<string> Lines { get; set; } = new List<string>();

        public AskThreadRuntimeFactory(LoggingModule logging) : base(logging) { }

        public override IAgentRuntime Create(AgentRuntimeEnum runtimeType)
        {
            AskThreadReplayRuntime runtime = new AskThreadReplayRuntime { AutoComplete = AutoComplete, Reply = Reply, Lines = Lines };
            Starts.Add(runtime);
            Created.TrySetResult(runtime);
            return runtime;
        }
    }
}
