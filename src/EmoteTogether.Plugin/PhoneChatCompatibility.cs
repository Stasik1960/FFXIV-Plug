using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;

namespace EmoteTogether.Plugin;

/// <summary>Consumes private protocol frames before chat listeners can archive or notify them.</summary>
internal sealed unsafe class PhoneChatCompatibility : IDisposable
{
    private readonly Func<XivChatType, SeString, SeString, bool> consume;
    private readonly Action<Exception> reportFailure;
    private readonly Hook<PrintMessageDelegate> hook;
    private bool reportedFailure;
    private volatile bool disposed;

    public PhoneChatCompatibility(IGameInteropProvider interop,
        Func<XivChatType, SeString, SeString, bool> consume, Action<Exception> reportFailure)
    {
        this.consume = consume;
        this.reportFailure = reportFailure;
        hook = interop.HookFromAddress<PrintMessageDelegate>(
            (nint)RaptureLogModule.MemberFunctionPointers.PrintMessage, OnPrintMessage);
        try { hook.Enable(); }
        catch { hook.Dispose(); throw; }
    }

    private delegate uint PrintMessageDelegate(RaptureLogModule* module, LogInfo info,
        Utf8String* sender, Utf8String* message, int timestamp, bool silent);

    private uint OnPrintMessage(RaptureLogModule* module, LogInfo info,
        Utf8String* sender, Utf8String* message, int timestamp, bool silent)
    {
        var type = (XivChatType)info.LogKind;
        if (!disposed && message != null &&
            type is XivChatType.TellIncoming or XivChatType.TellOutgoing)
        {
            try
            {
                var data = message->AsSpan();
                // All other chat bypasses parsing and the consumer entirely.
                if (data.StartsWith("[ET"u8))
                {
                    // Outgoing messages are matched against our exact send cache and need no sender.
                    var senderString = type == XivChatType.TellIncoming && sender != null
                        ? SeString.Parse(sender->AsSpan()) : SeString.Empty;
                    if (consume(type, senderString, SeString.Parse(data))) return 0;
                }
            }
            catch (Exception error)
            {
                // A compatibility error must not lose an ordinary game message.
                if (!reportedFailure)
                {
                    reportedFailure = true;
                    try { reportFailure(error); } catch { }
                }
            }
        }
        return hook.OriginalDisposeSafe(module, info, sender, message, timestamp, silent);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        hook.Dispose();
    }
}
