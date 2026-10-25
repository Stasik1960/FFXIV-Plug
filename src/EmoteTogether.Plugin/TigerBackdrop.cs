using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;

namespace EmoteTogether.Plugin;

internal sealed class TigerBackdrop
{
    private readonly string path;
    private ISharedImmediateTexture? texture;

    public TigerBackdrop()
    {
        path = Path.Combine(Plugin.PluginInterface.AssemblyLocation.DirectoryName!, "Assets", "tiger-mouth.jpg");
    }

    public void Draw(float scale, bool showTiger)
    {
        var position = ImGui.GetWindowPos();
        var width = ImGui.GetWindowSize().X;
        var height = Math.Min(ImGui.GetWindowSize().Y, 350 * scale);
        var bottomRight = position + new Vector2(width, height);
        var draw = ImGui.GetWindowDrawList();
        // The shared wrap is valid for this frame only and owned by Dalamud's texture provider.
        // Do not cache or dispose it; the provider releases the underlying resource when unused.
        var wrap = showTiger ? (texture ??= Plugin.Textures.GetFromFileAbsolute(path)).GetWrapOrDefault() : null;
        if (wrap is not null)
        {
            var sourceRatio = wrap.Width / (float)wrap.Height;
            var targetRatio = width / height;
            var uvMin = Vector2.Zero;
            var uvMax = Vector2.One;
            if (targetRatio > sourceRatio)
            {
                var fraction = sourceRatio / targetRatio;
                uvMin.Y = (1 - fraction) * 0.60f;
                uvMax.Y = uvMin.Y + fraction;
            }
            else
            {
                var fraction = targetRatio / sourceRatio;
                uvMin.X = 1 - fraction;
            }
            draw.AddImage(wrap.Handle, position, bottomRight, uvMin, uvMax, ImGui.ColorConvertFloat4ToU32(new Vector4(1, 1, 1, 0.8f)));
        }
        draw.AddRectFilledMultiColor(position, bottomRight,
            Color(0.035f, 0.043f, 0.055f, 0.98f), Color(0.035f, 0.043f, 0.055f, 0.12f),
            Color(0.035f, 0.043f, 0.055f, 0.91f), Color(0.035f, 0.043f, 0.055f, 0.99f));
        draw.AddRectFilledMultiColor(position + new Vector2(0, height * 0.48f), bottomRight,
            Color(0.035f, 0.043f, 0.055f, 0), Color(0.035f, 0.043f, 0.055f, 0),
            Color(0.035f, 0.043f, 0.055f, 1), Color(0.035f, 0.043f, 0.055f, 1));
    }

    private static uint Color(float r, float g, float b, float a) => ImGui.ColorConvertFloat4ToU32(new Vector4(r, g, b, a));
}
