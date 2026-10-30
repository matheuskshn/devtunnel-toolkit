using System.Diagnostics;
using Microsoft.DevTunnels.Connections;
using Microsoft.DevTunnels.Management;
using Microsoft.DevTunnels.Ssh;
using Microsoft.DevTunnels.Ssh.Events;
using Microsoft.DevTunnels.Ssh.Messages;
using Microsoft.DevTunnels.Ssh.Tcp.Events;

namespace DevTunnelToolkit.DevTunnels;

/// <summary>Adapter for SDK 1.3.60 / SSH 3.12.42. With local forwarding disabled the SDK raises
/// the custom stream event for V1, but skips it for V2. Use supported session hooks to restore
/// that hand-off, preserving the SDK's encrypted stream and extended channel confirmation.</summary>
internal sealed class MappedTunnelRelayHost(ITunnelManagementClient management, TraceSource trace)
    : TunnelRelayTunnelHost(management, trace)
{
    protected override void SubscribeSessionEvents(SshClientSession session)
    {
        base.SubscribeSessionEvents(session);
        session.ChannelOpening += Opening;
    }

    protected override void UnsubscribeSessionEvents(SshClientSession session)
    {
        session.ChannelOpening -= Opening;
        base.UnsubscribeSessionEvents(session);
    }

    private void Opening(object? sender, SshChannelOpeningEventArgs args)
    {
        if (ConnectionProtocol != WebSocketSubProtocolV2 || ForwardConnectionsToLocalPorts || !args.IsRemoteRequest ||
            args.Request.ChannelType != "forwarded-tcpip" || args.FailureReason != SshChannelOpenFailureReason.None) return;
        OpenMappedChannel(args);
    }

    internal void OpenMappedChannel(SshChannelOpeningEventArgs args)
    {
        var request = args.Request.ConvertTo<PortForwardChannelOpenMessage>();
        var forwarding = new ForwardedPortConnectingEventArgs((int)request.Port, true, new SshStream(args.Channel), args.Cancellation);
        var confirmation = args.OpeningTask ?? Task.FromResult<ChannelMessage>(new ChannelOpenConfirmationMessage());
        // The base implementation sets SecureStream when E2E encryption was requested.
        base.OnForwardedPortConnecting(this, forwarding);
        args.OpeningTask = CompleteOpeningAsync(confirmation, forwarding);
    }

    internal static async Task<ChannelMessage> CompleteOpeningAsync(Task<ChannelMessage> confirmation, ForwardedPortConnectingEventArgs forwarding)
    {
        try
        {
            var response = await confirmation;
            if (response is ChannelOpenFailureMessage) { forwarding.Stream.Dispose(); return response; }
            var stream = forwarding.TransformTask is { } transform ? await transform : forwarding.Stream;
            if (stream is not null) return response;
        }
        catch (Exception) { /* Do not expose connection, token or provider exception details to a remote client. */ }
        forwarding.Stream.Dispose();
        return new ChannelOpenFailureMessage
        { ReasonCode = SshChannelOpenFailureReason.AdministrativelyProhibited, Description = "Selected local mapping is unavailable." };
    }
}
