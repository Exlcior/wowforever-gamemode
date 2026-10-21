using Sandbox;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace WoWForever;

public sealed class AmbienceCatalog
{
    public int SchemaVersion { get; set; }
    public Dictionary<string, AmbienceMap> Continents { get; set; } = new();
    public Dictionary<string, List<AmbienceKey>> Params { get; set; } = new();
}
public sealed class AmbienceMap
{
    public int? Default { get; set; }
    public List<AmbienceLight> Lights { get; set; } = new();
}
public sealed class AmbienceLight
{
    public int Id { get; set; }
    public float[] Position { get; set; }
    public float Inner { get; set; }
    public float Outer { get; set; }
    public int Param { get; set; }
}
public sealed class AmbienceKey
{
    public float Time { get; set; }
    public float FogEnd { get; set; }
    public float[] Direct { get; set; }
    public float[] Ambient { get; set; }
    public float[] SkyTop { get; set; }
    public float[] SkyMiddle { get; set; }
    public float[] SkyBand1 { get; set; }
    public float[] SkyBand2 { get; set; }
    public float[] SkySmog { get; set; }
    public float[] SkyFog { get; set; }
    public float[] Sun { get; set; }
}

/// <summary>Zone lighting like the client: nearby lights blend over the map's default by their falloff,
/// at a time of day, and drive the sun, ambient light, sky bands and distance haze.</summary>
public sealed class AzerothAmbience : Component
{
    [Property] public string CatalogFile { get; set; } = "mount://wowforever/ambience.prefab";
    /// <summary>The client's banded sky, served by the mount with its own shader.</summary>
    [Property] public string SkyMaterialFile { get; set; } = "mount://wowforever/materials/sky.vmat";
    /// <summary>Hours, 0 to 24. Noon by default; FollowClock uses the computer's local time like the client's server clock.</summary>
    [Property, Range(0, 24)] public float TimeOfDay { get; set; } = 12;
    [Property] public bool FollowClock { get; set; }
    /// <summary>How much of the client's light colours replace the scene's neutral ones (1 = exact).</summary>
    [Property, Range(0, 1)] public float Fidelity { get; set; } = 1;
    [Property] public float SunIntensity { get; set; } = 0.9f;
    [Property] public float AmbientIntensity { get; set; } = 1.5f;
    [Property] public float TransitionSeconds { get; set; } = 1.5f;
    public string ActiveLights { get; private set; } = "";
    public string LastError { get; private set; } = "";
    private AmbienceCatalog catalog;
    private State current, target;
    private bool hasCurrent;
    private float untilRefresh;
    private Material sky;
    private AzerothWorld world;
    private DirectionalLight sun;
    private GradientFog[] fogs = Array.Empty<GradientFog>();
    private Color sceneLight = Color.White, sceneSky = new(0.25f, 0.32f, 0.35f);

    private struct State
    {
        public Vector3 Direct, Ambient, Top, Middle, Band1, Band2, Smog, Fog, Sun;
        public static State Lerp(State a, State b, float t) => new()
        {
            Direct = Vector3.Lerp(a.Direct, b.Direct, t), Ambient = Vector3.Lerp(a.Ambient, b.Ambient, t),
            Top = Vector3.Lerp(a.Top, b.Top, t), Middle = Vector3.Lerp(a.Middle, b.Middle, t),
            Band1 = Vector3.Lerp(a.Band1, b.Band1, t), Band2 = Vector3.Lerp(a.Band2, b.Band2, t),
            Smog = Vector3.Lerp(a.Smog, b.Smog, t), Fog = Vector3.Lerp(a.Fog, b.Fog, t), Sun = Vector3.Lerp(a.Sun, b.Sun, t),
        };
    }

    protected override void OnStart()
    {
        catalogTask = MountLoads.Prefab(CatalogFile);
        sun = Scene.GetAllComponents<DirectionalLight>().FirstOrDefault();
        if (sun.IsValid()) { sceneLight = sun.LightColor; sceneSky = sun.SkyColor; }
        var box = Scene.GetAllComponents<SkyBox2D>().FirstOrDefault();
        if (box.IsValid())
        {
            var source = Material.Load(SkyMaterialFile);
            if (source is not null) { sky = source.CreateCopy(); box.SkyMaterial = sky; }
        }
    }

    private System.Threading.Tasks.Task<PrefabFile> catalogTask;

    private void Open(string json)
    {
        try
        {
            catalog = JsonSerializer.Deserialize<AmbienceCatalog>(json ?? throw new Exception("données d’ambiance absentes"), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (catalog?.SchemaVersion != 1) throw new Exception("Catalogue d’ambiance inconnu");
        }
        catch (Exception error) { catalog = null; LastError = $"Ambiance : {error.Message}"; Log.Warning(LastError); }
    }

    protected override void OnUpdate()
    {
        if (catalogTask is { IsCompleted: true })
        {
            Open(catalogTask.IsCompletedSuccessfully ? catalogTask.Result?.GetMetadata("WowData") : null);
            catalogTask = null;
        }
        if (!world.IsValid()) world = Scene.GetAllComponents<AzerothWorld>().FirstOrDefault();
        if (catalog is null || !world.IsValid() || world.Current is null) return;
        untilRefresh -= Time.Delta;
        if (untilRefresh <= 0)
        {
            untilRefresh = 0.2f;
            target = Sample(world, out var next) ? next : Neutral(world);
            if (!hasCurrent) { current = target; hasCurrent = true; }
        }
        if (!hasCurrent) return;
        // Zone changes fade like the client's light transitions.
        current = State.Lerp(current, target, 1 - MathF.Exp(-Time.Delta * 3 / Math.Max(TransitionSeconds, 0.05f)));
        Apply(world);
    }

    private bool Sample(AzerothWorld world, out State state)
    {
        state = default;
        if (!catalog.Continents.TryGetValue(world.Current.Id, out var map)) { ActiveLights = "neutre (carte sans lumière d’origine)"; return false; }
        float hours = FollowClock ? (float)DateTime.Now.TimeOfDay.TotalHours : TimeOfDay;
        var position = world.SourcePosition;
        // Client order: the strongest local lights first, the map's default takes what remains.
        var weighted = new List<(AmbienceLight Light, float Weight)>();
        foreach (var light in map.Lights)
        {
            var p = light.Position;
            var delta = new Vector3(p[0] - position.x, p[1] - position.y, p[2] == 0 ? 0 : p[2] - position.z);
            float distance = delta.Length;
            if (distance >= light.Outer) continue;
            float weight = distance <= light.Inner ? 1 : 1 - (distance - light.Inner) / (light.Outer - light.Inner);
            weighted.Add((light, weight));
        }
        float remaining = 1;
        var total = new State();
        var names = new List<string>();
        foreach (var (light, weight) in weighted.OrderByDescending(x => x.Weight))
        {
            if (remaining <= 0.001f) break;
            if (!Evaluate(light.Param, hours, out var s)) continue;
            float w = weight * remaining;
            total = Add(total, s, w); remaining -= w;
            names.Add($"{light.Id}:{w:0.00}");
        }
        if (remaining > 0.001f)
        {
            if (map.Default is int fallback && Evaluate(fallback, hours, out var s)) total = Add(total, s, remaining);
            else if (names.Count == 0) { ActiveLights = "neutre"; return false; }
            else total = Scale(total, 1 / (1 - remaining));
        }
        ActiveLights = names.Count == 0 ? $"défaut {map.Default}" : string.Join(" ", names);
        state = total;
        return true;
    }

    // Maps without an original light keep the scene's neutral sun and the haze colour of the world.
    private State Neutral(AzerothWorld world)
    {
        static Vector3 L(Color c) => new(MathF.Pow(c.r, 2.2f), MathF.Pow(c.g, 2.2f), MathF.Pow(c.b, 2.2f));
        var fog = L(world.HorizonColor);
        return new State { Direct = L(sceneLight * (1 / SunIntensity)), Ambient = L(sceneSky), Top = new Vector3(0, 0.012f, 0.067f),
            Middle = new Vector3(0.042f, 0.36f, 0.62f), Band1 = new Vector3(0.32f, 0.72f, 0.91f), Band2 = fog * 1.1f, Smog = fog, Fog = fog,
            Sun = new Vector3(1, 0.93f, 0.73f) };
    }

    private bool Evaluate(int param, float hours, out State state)
    {
        state = default;
        if (!catalog.Params.TryGetValue(param.ToString(), out var keys) || keys.Count == 0) return false;
        // Keys wrap around midnight.
        int next = keys.FindIndex(k => k.Time > hours);
        var b = keys[next < 0 ? 0 : next];
        var a = keys[next <= 0 ? keys.Count - 1 : next - 1];
        float span = (b.Time - a.Time + 24) % 24, t = span <= 0 ? 0 : ((hours - a.Time + 24) % 24) / span;
        state = State.Lerp(ToState(a), ToState(b), Math.Clamp(t, 0, 1));
        return true;
    }

    private static State ToState(AmbienceKey k) => new()
    {
        Direct = Linear(k.Direct), Ambient = Linear(k.Ambient), Top = Linear(k.SkyTop), Middle = Linear(k.SkyMiddle),
        Band1 = Linear(k.SkyBand1), Band2 = Linear(k.SkyBand2), Smog = Linear(k.SkySmog), Fog = Linear(k.SkyFog), Sun = Linear(k.Sun),
    };
    // Client colours are sRGB bytes; lighting and the sky shader work in linear space.
    private static Vector3 Linear(float[] c) => c?.Length == 3 ? new Vector3(MathF.Pow(c[0], 2.2f), MathF.Pow(c[1], 2.2f), MathF.Pow(c[2], 2.2f)) : Vector3.Zero;
    private static State Add(State a, State b, float w) => new()
    {
        Direct = a.Direct + b.Direct * w, Ambient = a.Ambient + b.Ambient * w, Top = a.Top + b.Top * w, Middle = a.Middle + b.Middle * w,
        Band1 = a.Band1 + b.Band1 * w, Band2 = a.Band2 + b.Band2 * w, Smog = a.Smog + b.Smog * w, Fog = a.Fog + b.Fog * w, Sun = a.Sun + b.Sun * w,
    };
    private static State Scale(State a, float s) => Add(new State(), a, s);
    private static Color ToColor(Vector3 v) => new(v.x, v.y, v.z, 1);
    private static Color Gamma(Vector3 v) => new(MathF.Pow(v.x, 1 / 2.2f), MathF.Pow(v.y, 1 / 2.2f), MathF.Pow(v.z, 1 / 2.2f), 1);

    private void Apply(AzerothWorld world)
    {
        if (!sun.IsValid()) sun = Scene.GetAllComponents<DirectionalLight>().FirstOrDefault();
        if (sun.IsValid())
        {
            sun.LightColor = Color.Lerp(sceneLight, Gamma(current.Direct) * SunIntensity, Fidelity);
            sun.SkyColor = Color.Lerp(sceneSky, Gamma(current.Ambient) * AmbientIntensity, Fidelity);
        }
        // The haze takes the sky's fog band (both linear, checked on screen), so far terrain fades into the horizon colour.
        if (fogs.Length == 0) fogs = Scene.GetAllComponents<GradientFog>().ToArray();
        foreach (var fog in fogs) if (fog.IsValid()) fog.Color = ToColor(current.Fog);
        if (sky is not null)
        {
            sky.Set("g_vSkyTop", current.Top); sky.Set("g_vSkyMiddle", current.Middle);
            sky.Set("g_vSkyBand1", current.Band1); sky.Set("g_vSkyBand2", current.Band2);
            sky.Set("g_vSkySmog", current.Smog); sky.Set("g_vSkyFog", current.Fog); sky.Set("g_vSunColor", current.Sun);
        }
    }
}
