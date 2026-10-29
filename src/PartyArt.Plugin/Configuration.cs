using Dalamud.Configuration;

namespace PartyArt.Plugin;

[Serializable]
internal sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;
    public double LineDelaySeconds { get; set; } = 1.5;
}
