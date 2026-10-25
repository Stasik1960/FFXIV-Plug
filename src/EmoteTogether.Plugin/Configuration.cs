using Dalamud.Configuration;

namespace EmoteTogether.Plugin;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 2;
}
