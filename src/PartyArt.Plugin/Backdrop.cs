using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;

namespace PartyArt.Plugin;

internal sealed class Backdrop
{
    private readonly string path = Path.Combine(Plugin.PluginInterface.AssemblyLocation.DirectoryName!, "Assets", "wolf.jpg");
    private ISharedImmediateTexture? texture;

    internal void Draw(float scale, bool enabled)
    {
        if (!enabled) return;
        var wrap = (texture ??= Plugin.Textures.GetFromFileAbsolute(path)).GetWrapOrDefault();
        if (wrap is null) return;
        var origin = ImGui.GetWindowPos();
        var size = ImGui.GetWindowSize();
        var from = origin + new Vector2(size.X * 0.36f, 24 * scale);
        var to = origin + new Vector2(size.X, Math.Min(size.Y, 280 * scale));
        var uvMin = Vector2.Zero;
        var uvMax = Vector2.One;
        var sourceRatio = (float)wrap.Width / wrap.Height;
        var targetRatio = (to.X - from.X) / (to.Y - from.Y);
        if (targetRatio > sourceRatio)
        {
            var fraction = sourceRatio / targetRatio;
            uvMin.Y = (1 - fraction) * 0.4f;
            uvMax.Y = uvMin.Y + fraction;
        }
        else
        {
            var fraction = targetRatio / sourceRatio;
            uvMin.X = 1 - fraction;
        }
        var draw = ImGui.GetWindowDrawList();
        draw.AddImage(wrap.Handle, from, to, uvMin, uvMax, Color(1, 1, 1, 0.86f));
        draw.AddRectFilledMultiColor(from, to, Color(0.035f, 0.052f, 0.079f, 1), Color(0.035f, 0.052f, 0.079f, 0.15f),
            Color(0.035f, 0.052f, 0.079f, 1), Color(0.035f, 0.052f, 0.079f, 1));
    }

    private static uint Color(float r, float g, float b, float a) => ImGui.ColorConvertFloat4ToU32(new Vector4(r, g, b, a));
}
