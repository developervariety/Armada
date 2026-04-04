namespace Armada.Test.Unit.Suites.Services
{
    using Armada.Core;
    using Armada.Core.Enums;
    using Armada.Core.Settings;
    using Armada.Server;
    using Armada.Test.Common;
    using SyslogLogging;

    public class RemoteTunnelManagerTests : TestSuite
    {
        public override string Name => "Remote Tunnel Manager";

        protected override async Task RunTestsAsync()
        {
            await RunTest("TryNormalizeTunnelUrl Converts HttpAndHttps Schemes", () =>
            {
                AssertTrue(RemoteTunnelManager.TryNormalizeTunnelUrl("https://control.example.com/tunnel?x=1", out Uri? wssUri, out string? httpsError), httpsError ?? "HTTPS tunnel URL should normalize");
                AssertNotNull(wssUri);
                AssertEqual("wss", wssUri!.Scheme);
                AssertEqual("/tunnel", wssUri.AbsolutePath);
                AssertEqual("?x=1", wssUri.Query);

                AssertTrue(RemoteTunnelManager.TryNormalizeTunnelUrl("http://control.example.com/tunnel", out Uri? wsUri, out string? httpError), httpError ?? "HTTP tunnel URL should normalize");
                AssertNotNull(wsUri);
                AssertEqual("ws", wsUri!.Scheme);

                AssertTrue(RemoteTunnelManager.TryNormalizeTunnelUrl("http://control.example.com:7893", out Uri? shorthandUri, out string? shorthandError), shorthandError ?? "Base proxy URL should normalize");
                AssertNotNull(shorthandUri);
                AssertEqual("ws", shorthandUri!.Scheme);
                AssertEqual("/tunnel", shorthandUri.AbsolutePath);

                AssertTrue(RemoteTunnelManager.TryNormalizeTunnelUrl("wss://control.example.com", out Uri? shorthandSecureUri, out string? shorthandSecureError), shorthandSecureError ?? "Base websocket URL should normalize");
                AssertNotNull(shorthandSecureUri);
                AssertEqual("wss", shorthandSecureUri!.Scheme);
                AssertEqual("/tunnel", shorthandSecureUri.AbsolutePath);
            });

            await RunTest("TryNormalizeTunnelUrl Rejects Invalid Inputs", () =>
            {
                AssertFalse(RemoteTunnelManager.TryNormalizeTunnelUrl(null, out Uri? _, out string? missingError));
                AssertContains("no tunnel URL", missingError ?? String.Empty, "Missing URL should explain the error");

                AssertFalse(RemoteTunnelManager.TryNormalizeTunnelUrl("ftp://example.com/tunnel", out Uri? _, out string? schemeError));
                AssertContains("ws, wss, http, or https", schemeError ?? String.Empty, "Unsupported scheme should explain the allowed schemes");
            });

            await RunTest("BuildCapabilityManifest UsesCurrentReleaseVersion", () =>
            {
                LoggingModule logging = new LoggingModule();
                logging.Settings.EnableConsole = false;
                ArmadaSettings settings = new ArmadaSettings();
                RemoteTunnelManager manager = new RemoteTunnelManager(logging, settings);

                var manifest = manager.BuildCapabilityManifest();

                AssertEqual(Constants.RemoteTunnelProtocolVersion, manifest.ProtocolVersion);
                AssertEqual(Constants.ProductVersion, manifest.ArmadaVersion);
                AssertContains("remoteControl.handshake", String.Join(",", manifest.Features), "Handshake capability should be advertised");
                AssertContains("remoteControl.requests", String.Join(",", manifest.Features), "Request capability should be advertised");
                AssertContains("status.health", String.Join(",", manifest.Features), "Health capability should be advertised");
            });

            await RunTest("GetStatus DefaultsToDisabledWhenFeatureDisabled", () =>
            {
                LoggingModule logging = new LoggingModule();
                logging.Settings.EnableConsole = false;
                ArmadaSettings settings = new ArmadaSettings();
                settings.RemoteControl.Enabled = false;

                RemoteTunnelManager manager = new RemoteTunnelManager(logging, settings);
                var status = manager.GetStatus();

                AssertFalse(status.Enabled);
                AssertEqual(RemoteTunnelStateEnum.Disabled, status.State);
                AssertNotNull(status.CapabilityManifest);
                AssertTrue(status.InstanceId.StartsWith("armada-"), "Auto-generated instance ID should be stable and prefixed");
            });

            await RunTest("ComputeReconnectDelay HonorsConfiguredBounds", () =>
            {
                RemoteControlSettings settings = new RemoteControlSettings
                {
                    ReconnectBaseDelaySeconds = 4,
                    ReconnectMaxDelaySeconds = 10
                };

                TimeSpan delay = RemoteTunnelManager.ComputeReconnectDelay(settings, 8);
                AssertTrue(delay.TotalSeconds >= 9.0, "Jittered delay should stay near the capped maximum");
                AssertTrue(delay.TotalSeconds <= 11.0, "Jittered delay should stay within the capped maximum band");
            });
        }
    }
}
