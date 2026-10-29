using Dalamud.Configuration;
using PartyArt.Core;

namespace PartyArt.Plugin;

[Serializable]
internal sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 2;
    public bool AutomationEnabled { get; set; } = true;
    public bool ShowBackground { get; set; } = true;
    public double LineDelaySeconds { get; set; } = 1.5;
    public List<PhrasePreset> Presets { get; set; } = [];
}
