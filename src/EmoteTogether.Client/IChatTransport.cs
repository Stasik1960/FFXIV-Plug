using EmoteTogether.Protocol;

namespace EmoteTogether.Client;

public sealed record ChatEnvelope(string Sender, Packet Packet, long ReceivedAtUnixMs);

// Game adapter and deterministic test bus implement the same interface.
// Send must stamp Ping.ClientSendTime / Pong.ServerSendTime immediately before native send.
public interface IChatTransport : IDisposable
{
    string? LocalIdentity { get; }
    int PendingFrames { get; }
    int SendIntervalMs { get; }
    event Action<ChatEnvelope>? MessageReceived;
    void SendTo(string recipient, Packet packet, bool urgent = false);
    int EstimatePriorityBroadcastMs(Packet packet, int recipients);
    void ForgetRun(string runId);
    void ForgetGroup(string groupId);
    void Poll();
    void Reset(string? identity);
}
