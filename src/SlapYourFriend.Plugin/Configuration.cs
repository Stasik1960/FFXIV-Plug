using Dalamud.Configuration;

namespace SlapYourFriend.Plugin;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;
    public bool Enabled { get; set; } = true;
    public float Radius { get; set; } = 2;
    public bool ShowBackground { get; set; } = true;
    public List<PlayerSettings> Players { get; set; } = [];
}

[Serializable]
public sealed class PlayerSettings
{
    public string Identity { get; set; } = string.Empty;
    public int IntervalMinutes { get; set; } = 1;
}
