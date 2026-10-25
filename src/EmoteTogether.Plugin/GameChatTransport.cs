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
    private readonly PhoneChatCompatibility? phoneFilter;
    private bool draining;
    private volatile bool disposed;

    public GameChatTransport(IChatGui chat, IGameInteropProvider interop, Func<long> nowMs, IPluginLog log) : base(nowMs)
    {
        this.chat = chat;
        this.nowMs = nowMs;
        chat.ChatMessage += OnChatMessage;
        try
        {
            phoneFilter = new PhoneChatCompatibility(interop, TryConsumeProtocol,
                error => log.Warning(error, "Не удалось обработать служебное сообщение до передачи в чат."));
        }
        catch (Exception error)
        {
            log.Warning(error, "Фильтр телефона недоступен; используется фильтр игрового чата.");
        }
    }

    public bool PhoneFilterActive => !disposed && phoneFilter is not null;

    public void QueueDeparture(IReadOnlyList<DepartureNotice> notices)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ClearPendingMessages();
        incoming.Clear();
        draining = true;
        // Preserve the sender's order: a departing leader notifies the successor first.
        foreach (var notice in notices) SendTo(notice.Recipient, notice.Packet);
    }

    public override void Poll()
    {
        if (disposed) return;
        while (incoming.TryDequeue(out var frame))
            if (!draining) ReceiveFrame(frame.Sender, frame.Text, frame.ReceivedAt);
        var now = nowMs();
        foreach (var frame in ownFrames)
            if (frame.Value < now) ownFrames.TryRemove(frame.Key, out _);
        base.Poll();
    }

    public override void Reset(string? identity)
    {
        while (incoming.TryDequeue(out _)) { }
        ownFrames.Clear();
        draining = false;
        base.Reset(identity);
    }

    protected override void SendFrame(string recipient, string text)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
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
        if (TryConsumeProtocol(message.LogKind, SeString.Parse(message.OriginalSender.Data.Span),
                SeString.Parse(message.OriginalMessage.Data.Span))) message.PreventOriginal();
    }

    private bool TryConsumeProtocol(XivChatType type, SeString senderString, SeString messageString)
    {
        if (disposed || LocalIdentity is null || type is not (XivChatType.TellIncoming or XivChatType.TellOutgoing)) return false;
        var text = messageString.TextValue;
        if (type == XivChatType.TellOutgoing)
            return ownFrames.TryGetValue(text, out var expires) && expires >= nowMs();

        var kind = ControlFrameClassifier.Classify(text);
        if (kind == ControlFrameKind.None) return false;
        // Sender links come from the game; a name carried in the protocol is not trusted.
        var player = senderString.Payloads.OfType<PlayerPayload>().FirstOrDefault();
        if (player is null || !player.World.IsValid ||
            !GameIdentity.TryNormalize($"{player.PlayerName}@{player.World.Value.Name.ExtractText()}", out var sender)) return false;
        if (kind == ControlFrameKind.Current && !draining && incoming.Count < 128)
            incoming.Enqueue(new IncomingFrame(sender, text, nowMs()));
        // Legacy or dropped frames stay out of logs, archives, unread counters and notifications.
        return true;
    }

    public override void Dispose()
    {
        if (disposed) return;
        disposed = true;
        phoneFilter?.Dispose();
        chat.ChatMessage -= OnChatMessage;
        incoming.Clear();
        ownFrames.Clear();
        base.Dispose();
    }

    private sealed record IncomingFrame(string Sender, string Text, long ReceivedAt);
}
