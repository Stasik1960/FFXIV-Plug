using Dalamud.Configuration;

namespace EmoteTogether.Plugin;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 3;
    public int CountdownSeconds { get; set; } = 3;
    public bool ShowTigerBackground { get; set; } = true;
}
