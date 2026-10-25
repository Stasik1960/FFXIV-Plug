using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;

namespace SlapYourFriend.Plugin;

internal sealed class Backdrop
{
    private readonly string path = Path.Combine(Plugin.PluginInterface.AssemblyLocation.DirectoryName!, "Assets", "friendly-slap.jpg");
    private ISharedImmediateTexture? texture;

    public void Draw(float scale, bool enabled)
    {
        if (!enabled) return;
        var wrap = (texture ??= Plugin.Textures.GetFromFileAbsolute(path)).GetWrapOrDefault();
        if (wrap is null) return;
        var origin = ImGui.GetWindowPos();
        var width = ImGui.GetWindowSize().X;
        var from = origin + new Vector2(width * 0.40f, 24 * scale);
        var to = origin + new Vector2(width, 184 * scale);
        // Keep both faces and the hand in the shallow header instead of the empty left margin.
        var uvMin = new Vector2(0.25f, 0);
        var uvMax = new Vector2(1, 0.65f);
        var sourceRatio = wrap.Width * (uvMax.X - uvMin.X) / (wrap.Height * (uvMax.Y - uvMin.Y));
        var targetRatio = (to.X - from.X) / (to.Y - from.Y);
        if (targetRatio > sourceRatio)
        {
            var height = (uvMax.Y - uvMin.Y) * sourceRatio / targetRatio;
            uvMax.Y = uvMin.Y + height;
        }
        else
        {
            var widthFraction = (uvMax.X - uvMin.X) * targetRatio / sourceRatio;
            uvMin.X = uvMax.X - widthFraction;
        }
        var draw = ImGui.GetWindowDrawList();
        draw.AddImage(wrap.Handle, from, to, uvMin, uvMax, Color(1, 1, 1, 0.85f));
        draw.AddRectFilledMultiColor(from, to, Color(0.055f, 0.060f, 0.080f, 1), Color(0.055f, 0.060f, 0.080f, 0.12f),
            Color(0.055f, 0.060f, 0.080f, 0.82f), Color(0.055f, 0.060f, 0.080f, 1));
    }

    private static uint Color(float r, float g, float b, float a) => ImGui.ColorConvertFloat4ToU32(new Vector4(r, g, b, a));
}
