using System.Text.Json;
using System.Text.Json.Serialization;

namespace EmoteTogether.Protocol;

// Bounded chat wire protocol; only game-authenticated senders are authoritative.
public sealed record Packet
{
    public const int Version = 4;
    public const int MaxMessageBytes = 8_192;
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault,
    };

    [JsonPropertyName("t")] public required string Type { get; init; }
    [JsonPropertyName("v")] public int ProtocolVersion { get; init; } = Version;
    [JsonPropertyName("n")] public string? Name { get; init; }
    [JsonPropertyName("m")] public string? MemberId { get; init; }
    [JsonPropertyName("g")] public string? GroupId { get; init; }
    [JsonPropertyName("l")] public string? LeaderId { get; init; }
    [JsonPropertyName("u")] public Member[]? Members { get; init; }
    [JsonPropertyName("p")] public string? TargetName { get; init; }
    [JsonPropertyName("i")] public string? InviteId { get; init; }
    [JsonPropertyName("f")] public string? FromName { get; init; }
    [JsonPropertyName("y")] public bool Accepted { get; init; }
    [JsonPropertyName("r")] public string? RunId { get; init; }
    [JsonPropertyName("e")] public uint EmoteId { get; init; }
    [JsonPropertyName("q")] public bool Ready { get; init; }
    [JsonPropertyName("z")] public string? Reason { get; init; }
    [JsonPropertyName("h")] public string? Nonce { get; init; }
    [JsonPropertyName("s")] public long Revision { get; init; }
    [JsonPropertyName("a")] public long ClientSendTime { get; init; }
    [JsonPropertyName("b")] public long ServerReceiveTime { get; init; }
    [JsonPropertyName("c")] public long ServerSendTime { get; init; }
    [JsonPropertyName("d")] public long StartAtUnixMs { get; init; }
    [JsonPropertyName("k")] public int CountdownSeconds { get; init; }
    [JsonPropertyName("j")] public string? NewGroupId { get; init; }
}

public sealed record Member([property: JsonPropertyName("i")] string Id, [property: JsonPropertyName("n")] string Name);

public static class Kind
{
    public const string Hello = "hello";
    public const string Welcome = "welcome";
    public const string Ping = "ping";
    public const string Pong = "pong";
    public const string CreateGroup = "createGroup";
    public const string Group = "group";
    public const string Invite = "invite";
    public const string Invitation = "invitation";
    public const string RespondInvite = "respondInvite";
    public const string InviteResult = "inviteResult";
    public const string LeaveGroup = "leaveGroup";
    public const string GroupClosed = "groupClosed";
    public const string Start = "start";
    public const string Prepare = "prepare";
    public const string Ready = "ready";
    public const string Scheduled = "scheduled";
    public const string Cancel = "cancel";
    public const string Cancelled = "cancelled";
    public const string Error = "error";
    public const string Leave = "leave";
}
