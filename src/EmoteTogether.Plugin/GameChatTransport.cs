using System.Collections.Concurrent;
using Dalamud.Game.Chat;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Plugin.Services;
using EmoteTogether.Client;
using EmoteTogether.Protocol;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;

namespace EmoteTogether.Plugin;

/// <summary>The only transport adapter: ordinary /tell commands sent on Framework.Update.</summary>
internal sealed unsafe class GameChatTransport : PacedChatTransport
{
    private readonly IChatGui chat;
    private readonly Func<long> nowMs;
    private readonly ConcurrentQueue<IncomingFrame> incoming = new();
    private readonly ConcurrentDictionary<string, long> ownFrames = new(StringComparer.Ordinal);
    private bool disposed;

    public GameChatTransport(IChatGui chat, Func<long> nowMs) : base(nowMs)
    {
        this.chat = chat;
        this.nowMs = nowMs;
        chat.ChatMessage += OnChatMessage;
    }

    public override void Poll()
    {
        while (incoming.TryDequeue(out var frame))
            ReceiveFrame(frame.Sender, frame.Text, frame.ReceivedAt);
        var now = nowMs();
        foreach (var frame in ownFrames)
            if (frame.Value < now) ownFrames.TryRemove(frame.Key, out _);
        base.Poll();
    }

    public override void Reset(string? identity)
    {
        while (incoming.TryDequeue(out _)) { }
        ownFrames.Clear();
        base.Reset(identity);
    }

    protected override void SendFrame(string recipient, string text)
    {
        if (!GameIdentity.TryNormalize(recipient, out var target) || !ChatCodec.IsFrame(text))
            throw new InvalidOperationException("Некорректное служебное сообщение /tell.");
        var module = UIModule.Instance();
        if (module == null) throw new InvalidOperationException("Игровой чат ещё не готов.");
        // Add before native submission because the outgoing chat callback may run synchronously.
        ownFrames[text] = nowMs() + 30_000;
        using var command = new Utf8String($"/tell {target} {text}");
        module->ProcessChatBoxEntry(&command, 0, false);
    }

    private void OnChatMessage(IHandleableChatMessage message)
    {
        if (disposed || LocalIdentity is null || message.LogKind is not (XivChatType.TellIncoming or XivChatType.TellOutgoing)) return;
        var text = message.OriginalMessage.ExtractText();
        if (message.LogKind == XivChatType.TellOutgoing)
        {
            if (ownFrames.TryRemove(text, out var expires) && expires >= nowMs()) message.PreventOriginal();
            return;
        }
        if (!ChatCodec.IsFrame(text)) return;
        // Never accept a sender claimed inside the payload or infer a missing home world.
        var player = SeString.Parse(message.OriginalSender.Data.Span).Payloads.OfType<PlayerPayload>().FirstOrDefault();
        if (player is null || !player.World.IsValid ||
            !GameIdentity.TryNormalize($"{player.PlayerName}@{player.World.Value.Name.ExtractText()}", out var sender)) return;
        if (incoming.Count >= 128) return;
        incoming.Enqueue(new IncomingFrame(sender, text, nowMs()));
        message.PreventOriginal();
    }

    public override void Dispose()
    {
        if (disposed) return;
        disposed = true;
        chat.ChatMessage -= OnChatMessage;
        incoming.Clear();
        ownFrames.Clear();
        base.Dispose();
    }

    private sealed record IncomingFrame(string Sender, string Text, long ReceivedAt);
}
