using Sandbox;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace WoWForever;

/// <summary>The client's interface texts in the installed language (its GlobalStrings, by tag) and the
/// mount's places, served by the mount. Screens laid out like the client's take their words from here.</summary>
public static class WowStrings
{
    public sealed class Place { public string Key { get; set; } public string Map { get; set; } public string Title { get; set; } public float X { get; set; } public float Y { get; set; } public float Z { get; set; } }
    private static Dictionary<string, string> strings;
    private static List<Place> places;
    /// <summary>The mount's places in their order, with client coordinates (Z: highest spawn height, 0 for anywhere).</summary>
    public static List<Place> Places { get { if (places is null) loading ??= Load(); return places ?? new(); } }
    private static Task loading;
    /// <summary>Raised once the texts are read, for interfaces to redraw.</summary>
    public static int Revision { get; private set; }

    /// <summary>The client's text for a GlobalStrings tag (BACK, CUSTOMIZE, HEADSLOT...), or the fallback while
    /// it loads or without the mount.</summary>
    public static string Get(string tag, string fallback = "")
    {
        if (strings is null) { loading ??= Load(); return fallback; }
        return strings.TryGetValue(tag, out var text) && !string.IsNullOrEmpty(text) ? text : fallback;
    }

    private static async Task Load()
    {
        var json = new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var text = (await MountLoads.Prefab("mount://wowforever/strings.prefab"))?.GetMetadata("WowData");
        var served = (await MountLoads.Prefab("mount://wowforever/places.prefab"))?.GetMetadata("WowData");
        strings = text is null ? new() : System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(text);
        places = served is null ? new() : System.Text.Json.JsonSerializer.Deserialize<List<Place>>(served, json);
        Revision++;
    }
}
