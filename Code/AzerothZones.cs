using Sandbox;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace WoWForever;

/// <summary>Where the explorer stands, as the client names it (zone and subzone from the terrain's area ids),
/// and the map's loading screen, both served by the mount. The HUD shows the client's zone banner on change.</summary>
public sealed class AzerothZones : Component
{
    public string Zone { get; private set; } = "";
    public string Area { get; private set; } = "";
    /// <summary>Seconds since the zone or subzone changed (the banner fades out).</summary>
    public RealTimeSince Changed { get; private set; } = 100;
    /// <summary>The map's loading screen picture, its logo and the pieces of the frame it is drawn in (by role:
    /// LeftBackground, LeftBookEnd, RightBackground, SideFill, BottomFill, Gradient), all served by the mount.</summary>
    public string LoadingScreen => screen?.Image;
    public string LoadingLogo => screen?.Logo;
    public Dictionary<string, string> LoadingFrame => screen?.Skin is { } skin && catalog?.Skins.TryGetValue(skin, out var parts) == true ? parts : null;
    private sealed class Screen { public string Image { get; set; } public string Logo { get; set; } public string Skin { get; set; } }
    private sealed class ScreenCatalog { public Dictionary<string, Screen> Screens { get; set; } = new(); public Dictionary<string, Dictionary<string, string>> Skins { get; set; } = new(); }
    private ScreenCatalog catalog;
    private Screen screen;

    private sealed class Names { public string Zone { get; set; } public string Area { get; set; } }
    private sealed class TileAreas { public float[] Corner { get; set; } public uint[] Chunks { get; set; } public Dictionary<string, Names> Names { get; set; } }
    private readonly Dictionary<string, Task<TileAreas>> jobs = new();
    private readonly Dictionary<string, TileAreas> tiles = new();
    private Task<PrefabFile> screenCatalog;
    private AzerothWorld world;
    private TimeUntil nextCheck;
    private static readonly System.Text.Json.JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    protected override void OnStart() => screenCatalog = MountLoads.Prefab("mount://wowforever/loadingscreens.prefab");

    protected override void OnUpdate()
    {
        if (screenCatalog is { IsCompleted: true })
        {
            if (screenCatalog.IsCompletedSuccessfully && screenCatalog.Result?.GetMetadata("WowData") is { } json)
                catalog = System.Text.Json.JsonSerializer.Deserialize<ScreenCatalog>(json, Json);
            screenCatalog = null;
        }
        if (!world.IsValid()) world = Scene.GetAllComponents<AzerothWorld>().FirstOrDefault();
        if (!world.IsValid() || world.Current is null) return;
        screen = catalog?.Screens.GetValueOrDefault(world.Current.Id);
        if (!nextCheck) return;
        nextCheck = 0.5f;
        var p = world.SourcePosition;
        const float half = 51200f / 3, tileSize = 1600f / 3;
        int b = (int)System.MathF.Floor((half - p.x) / tileSize), a = (int)System.MathF.Floor((half - p.y) / tileSize);
        string key = $"{world.Current.Id}/{a}_{b}";
        if (!tiles.TryGetValue(key, out var areas))
        {
            if (!jobs.ContainsKey(key)) jobs[key] = Load($"mount://wowforever/maps/{world.Current.Id}/{a}_{b}/areas.prefab");
            if (!jobs[key].IsCompleted) return;
            tiles[key] = areas = jobs[key].IsCompletedSuccessfully ? jobs[key].Result : null;
            jobs.Remove(key);
        }
        if (areas?.Corner is not { Length: 2 } || areas.Chunks is null) return;
        const float chunk = 100f / 3;
        int row = System.Math.Clamp((int)System.MathF.Floor((areas.Corner[0] - p.x) / chunk), 0, 15);
        int col = System.Math.Clamp((int)System.MathF.Floor((areas.Corner[1] - p.y) / chunk), 0, 15);
        uint id = areas.Chunks.ElementAtOrDefault(row * 16 + col);
        if (id == 0 || areas.Names is null || !areas.Names.TryGetValue(id.ToString(), out var names)) return;
        if (names.Zone != Zone || names.Area != Area) { Zone = names.Zone; Area = names.Area; Changed = 0; }
    }

    private static async Task<TileAreas> Load(string path)
    {
        var prefab = await MountLoads.Prefab(path);
        var json = prefab?.GetMetadata("WowData");
        return json is null ? null : System.Text.Json.JsonSerializer.Deserialize<TileAreas>(json, Json);
    }
}
