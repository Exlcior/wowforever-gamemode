using Sandbox;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace WoWForever;

public sealed class WorldCatalog
{
    public int SchemaVersion { get; set; } = 1;
    public string Title { get; set; } = "World of Warcraft Forever";
    public string ModelsDirectory { get; set; } = "mount://wowforever/models";
    public List<Continent> Continents { get; set; } = new();
}

public sealed class Continent
{
    public string Id { get; set; }
    public string Title { get; set; }
    public string Directory { get; set; }
    public double[] Spawn { get; set; }
    public string Build { get; set; }
    public int? ExpectedTiles { get; set; }
    public bool DetailsVerified { get; set; }
    public List<WorldTile> Tiles { get; set; } = new();
}

public sealed class WorldTile
{
    public string Id { get; set; }
    public float[] Center { get; set; } = new float[3];
    public float SpawnHeight { get; set; } = 120;
    /// <summary>Mount paths (mount://wowforever/...) of the native terrain model and its distant levels
    /// (the original lattice every 4th, then 16th vertex, with the client's baked map texture).</summary>
    public string Terrain { get; set; }
    public List<string> LodModels { get; set; } = new();
    public int LodCount => LodModels.Count;
    public string Placements { get; set; }
}

/// <summary>Independent continents, terrain rings from near to far and a movable source origin.</summary>
public sealed class AzerothWorld : Component
{
    public const float InchesPerSourceUnit = 36f;
    public const float TileSize = 1600f / 3f;
    /// <summary>The world read from the player's installed game through the wowforever mount.</summary>
    [Property] public string CatalogFile { get; set; } = "mount://wowforever/world.prefab";
    /// <summary>Maximum number of full-detail tiles (layered textures and collisions).</summary>
    [Property, Range(1, 25)] public int TileBudget { get; set; } = 9;
    /// <summary>Distances in source units (yards) from the camera to the edge of each tile.</summary>
    [Property] public float FullDetailDistance { get; set; } = 400;
    [Property] public float MediumDistance { get; set; } = 1600;
    [Property] public float HorizonDistance { get; set; } = 5000;
    /// <summary>Main-thread budget once the arrival area is loaded; files are decoded by workers.</summary>
    [Property] public Color HorizonColor { get; set; } = new Color(0.84f, 0.88f, 0.91f);
    [Property] public float LoadBudgetMilliseconds { get; set; } = 2.5f;
    /// <summary>Budget while the area around a new arrival point loads in full.</summary>
    [Property] public float WarmupBudgetMilliseconds { get; set; } = 40;
    /// <summary>Yards beyond the detailed and medium rings whose terrain is prepared in the background,
    /// so that moving or turning back finds it ready.</summary>
    [Property] public float PrefetchDistance { get; set; } = 400;
    /// <summary>Terrain kept in memory after leaving it (most recently seen first): detailed tiles hold
    /// their layer masks, distant ones are light.</summary>
    [Property] public int KeepDetailedTiles { get; set; } = 24;
    [Property] public int KeepDistantTiles { get; set; } = 320;
    public int ResidentTiles => resident.Count;
    public int PrefetchingTiles => prefetching.Count;
    public WorldCatalog Catalog { get; private set; } = new();
    public Continent Current { get; private set; }
    public string LastError { get; private set; } = "";
    public int LoadedTiles => loaded.Count;
    public int FullTiles => loaded.Values.Count(x => x.Lod == 0);
    public int MediumTiles => loaded.Values.Count(x => x.Lod == 1);
    public int FarTiles => loaded.Values.Count(x => x.Lod == 2);
    public int PendingTiles => queue.Count + inFlight.Count;
    /// <summary>Longest main-thread steps since the last reset, in milliseconds (performance checks).</summary>
    public Dictionary<string, double> Timings { get; } = new();
    private void Measure(string name, Stopwatch watch) { double ms = watch.Elapsed.TotalMilliseconds; if (!Timings.TryGetValue(name, out var max) || ms > max) Timings[name] = ms; }
    /// <summary>True from an arrival until every ring around it is loaded; streaming is then kept light.</summary>
    public bool Warming { get; private set; }
    public float WarmupProgress => loaded.Count + PendingTiles == 0 ? 0 : loaded.Count / (float)(loaded.Count + PendingTiles);
    public int Revision { get; private set; }
    /// <summary>Tiles shown at full detail.</summary>
    public IReadOnlyCollection<WorldTile> ActiveTiles => Current?.Tiles.Where(t => loaded.TryGetValue(t.Id, out var x) && x.Lod == 0).ToArray() ?? Array.Empty<WorldTile>();
    public bool HasTerrain => Current?.Tiles.Count > 0;
    public bool GroundReady => Current?.Tiles.Any(t => (t.Id == "global" || DistanceToTile(t, SourcePosition) < 1)
        && loaded.TryGetValue(t.Id, out var tile) && tile.Lod == 0) == true;
    public Vector3 SourceOrigin { get; private set; }
    public CameraComponent Camera { get; private set; }
    public Vector3 SourcePosition => Camera.IsValid()
        ? Camera.WorldPosition / InchesPerSourceUnit + SourceOrigin : SourceOrigin;
    private readonly Dictionary<string, LoadedTile> loaded = new();
    private readonly HashSet<(string, int)> failed = new();
    private readonly List<Request> queue = new();
    private readonly Dictionary<string, InFlight> inFlight = new();
    private readonly Dictionary<string, (Model Model, int Lod, double Used)> resident = new();
    private readonly Dictionary<string, (Task<Model> Task, int Lod)> prefetching = new();
    private List<(string Path, int Lod)> prefetch = new();
    private float untilRefresh;
    private Vector3 refreshedAt = new(float.MaxValue, 0, 0);

    protected override void OnStart()
    {
        Camera = Scene.GetAllComponents<CameraComponent>().FirstOrDefault(x => x.IsMainCamera);
        if (!Scene.GetAllComponents<AzerothDetails>().Any()) GameObject.Components.Create<AzerothDetails>();
        if (!Scene.GetAllComponents<AzerothAmbience>().Any()) GameObject.Components.Create<AzerothAmbience>();
        if (!Scene.GetAllComponents<AzerothCreatures>().Any()) GameObject.Components.Create<AzerothCreatures>();
        if (!Scene.GetAllComponents<AzerothZones>().Any()) GameObject.Components.Create<AzerothZones>();
        if (!Scene.GetAllComponents<AzerothSpells>().Any()) GameObject.Components.Create<AzerothSpells>();
        if (!Scene.GetAllComponents<AzerothCreation>().Any()) GameObject.Components.Create<AzerothCreation>();
        if (!Scene.GetAllComponents<AzerothPlayer>().Any()) GameObject.Components.Create<AzerothPlayer>();
        catalogTask = MountLoads.Prefab(CatalogFile);
    }

    private Task<PrefabFile> catalogTask;

    private void OpenCatalog(string json)
    {
        try
        {
            Catalog = JsonSerializer.Deserialize<WorldCatalog>(json ?? throw new Exception("Jeu non trouvé : installez World of Warcraft Forever"),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new Exception("Catalogue vide");
            if (Catalog.SchemaVersion is not (1 or 2)) throw new Exception("Version du catalogue inconnue");
            SelectContinent(Catalog.Continents.FirstOrDefault(c=>c.Id=="eastern_kingdoms")?.Id ?? Catalog.Continents.FirstOrDefault()?.Id);
        }
        catch (Exception error)
        {
            LastError = error.Message;
            Log.Error($"WoW Forever: {error.Message}");
        }
    }

    public void SelectContinent(string id)
    {
        var next = Catalog.Continents.FirstOrDefault(x => x.Id == id);
        if (next is null) return;
        Scene.GetAllComponents<AzerothPlayer>().FirstOrDefault()?.PrepareTeleport();
        Clear();
        Current = next;
        Revision++;
        LastError = "";
        SourceOrigin = next.Spawn?.Length == 3 ? new Vector3((float)next.Spawn[0],(float)next.Spawn[1],0) : next.Tiles.Count > 0 ? Position(next.Tiles[0]) : Vector3.Zero;
        if (Camera.IsValid())
        {
            ConfigureCamera();
            Camera.WorldPosition = Vector3.Up * ((next.Spawn?.Length == 3 ? (float)next.Spawn[2] : next.Tiles.FirstOrDefault()?.SpawnHeight ?? 120) * InchesPerSourceUnit);
        }
        untilRefresh = 0;
    }

    // The horizon ring decides the far plane; a larger near plane keeps depth precision over that range.
    private void ConfigureCamera()
    {
        Camera.ZFar = (HorizonDistance + TileSize) * InchesPerSourceUnit;
        Camera.ZNear = Math.Max(Camera.ZNear, 4);
        if (!Scene.GetAllComponents<GradientFog>().Any())
        {
            var fog = GameObject.Components.Create<GradientFog>();
            // Neutral haze close to the sky's horizon; zone colours need the client's Light table.
            fog.Height = 100000 * InchesPerSourceUnit;
            fog.VerticalFalloffExponent = 1;
            fog.FalloffExponent = 1.2f;
        }
        // The haze is complete just before the horizon ring ends, so its edge is never seen.
        foreach (var fog in Scene.GetAllComponents<GradientFog>())
        {
            // Measured against the sky's horizon after tonemapping (validation/2026-10-17-streaming).
            fog.Color = HorizonColor;
            fog.StartDistance = MediumDistance * 0.6f * InchesPerSourceUnit;
            fog.EndDistance = (HorizonDistance - TileSize * 0.25f) * InchesPerSourceUnit;
        }
    }

    public void VisitNextContinent()
    {
        if (Catalog.Continents.Count < 1) return;
        int index = Catalog.Continents.IndexOf(Current);
        SelectContinent(Catalog.Continents[(index + 1) % Catalog.Continents.Count].Id);
    }

    protected override void OnUpdate()
    {
        if (catalogTask is { IsCompleted: true })
        {
            var prefab = catalogTask.IsCompletedSuccessfully ? catalogTask.Result : null;
            catalogTask = null;
            OpenCatalog(prefab?.GetMetadata("WowData"));
        }
        if (!HasTerrain || !Camera.IsValid()) return;
        var source = SourcePosition;
        // Keep Source 2 coordinates near the camera while retaining original source positions.
        if ((source - SourceOrigin).WithZ(0).Length > 2000)
        {
            var delta = (SourceOrigin - source.WithZ(0)) * InchesPerSourceUnit;
            SourceOrigin = source.WithZ(0);
            Camera.WorldPosition += delta;
            foreach (var entry in loaded.Values) if (entry.Root.IsValid()) entry.Root.WorldPosition += delta;
            Scene.GetAllComponents<AzerothDetails>().FirstOrDefault()?.Rebase(delta);
            Scene.GetAllComponents<AzerothPlayer>().FirstOrDefault()?.Rebase(delta);
            Scene.GetAllComponents<AzerothCreatures>().FirstOrDefault()?.Rebase(delta);
        }
        untilRefresh -= Time.Delta;
        if (untilRefresh <= 0 || (source - refreshedAt).WithZ(0).Length > 48)
        {
            untilRefresh = 0.25f;
            refreshedAt = source;
            var watch = Stopwatch.StartNew();
            Refresh(source);
            Measure("world.refresh", watch);
        }
        Pump();
    }

    // Nearest requests first: workers decode the files, the main thread only assembles within its budget.
    private void Pump()
    {
        float budget = Warming ? WarmupBudgetMilliseconds : LoadBudgetMilliseconds;
        int maxInFlight = Warming ? 12 : 4;
        while (queue.Count > 0 && inFlight.Count < maxInFlight)
        {
            var request = queue[0]; queue.RemoveAt(0);
            if (inFlight.ContainsKey(request.Tile.Id)) continue;
            var tile = request.Tile;
            // The mount builds the terrain itself, decoding on its workers; a global-WMO map has no terrain.
            string path = request.Lod == 0 ? CollisionPath(tile.Terrain) : tile.LodModels[request.Lod - 1];
            Task<Model> model = null;
            if (path is not null)
                model = resident.TryGetValue(path, out var kept) ? Task.FromResult(kept.Model)
                    : prefetching.Remove(path, out var started) ? started.Task : MountLoads.Model(path);
            inFlight.Add(tile.Id, new InFlight(request, model, path));
        }
        Prefetch();
        var timer = Stopwatch.StartNew();
        bool worked = false;
        foreach (var job in inFlight.Values.Where(x => x.Model?.IsCompleted ?? true).OrderBy(x => x.Request.Lod == 0 ? x.Request.Distance - 100000 : x.Request.Distance).ToArray())
        {
            if (worked && timer.Elapsed.TotalMilliseconds >= budget) break;
            inFlight.Remove(job.Request.Tile.Id); worked = true;
            var watch = Stopwatch.StartNew();
            Finish(job);
            Measure($"world.finish.lod{job.Request.Lod}", watch);
        }
        if (Warming && queue.Count == 0 && inFlight.Count == 0 && refreshedAt.x != float.MaxValue) Warming = false;
    }

    // Once the visible rings are complete, the next tiles out are prepared quietly, nearest first.
    private void Prefetch()
    {
        foreach (var (path, (task, lod)) in prefetching.Where(x => x.Value.Task.IsCompleted).ToArray())
        {
            prefetching.Remove(path);
            if (task.IsCompletedSuccessfully && task.Result is { IsError: false } model) Remember(path, model, lod);
        }
        if (Warming) return;
        int limit = queue.Count > 0 || inFlight.Count > 0 ? 1 : 2;
        foreach (var (path, lod) in prefetch)
        {
            if (prefetching.Count >= limit) break;
            if (resident.ContainsKey(path) || prefetching.ContainsKey(path)) continue;
            prefetching[path] = (MountLoads.Model(path), lod);
        }
    }

    private void Remember(string path, Model model, int lod)
    {
        resident[path] = (model, lod, Time.Now);
        int keep = lod == 0 ? KeepDetailedTiles : KeepDistantTiles;
        var same = resident.Where(x => (x.Value.Lod == 0) == (lod == 0)).ToList();
        if (same.Count <= keep) return;
        // Forget the tiles seen least recently, never the ones on screen.
        var shown = new HashSet<string>(loaded.Values.Select(l => l.Path).Where(p => p is not null));
        foreach (var old in same.Where(x => !shown.Contains(x.Key)).OrderBy(x => x.Value.Used).Take(same.Count - keep))
            resident.Remove(old.Key);
    }

    private void Refresh(Vector3 source)
    {
        var desired = new Dictionary<string, Request>();
        var full = new List<(WorldTile Tile, float Distance)>();
        foreach (var tile in Current.Tiles)
        {
            float distance = tile.Id == "global" ? 0 : DistanceToTile(tile, source);
            loaded.TryGetValue(tile.Id, out var current);
            // Hysteresis: a tile already at a finer level keeps it a little longer.
            if (distance < FullDetailDistance + (current?.Lod == 0 ? 96 : 0)) full.Add((tile, distance));
            else if (tile.LodCount >= 1 && distance < MediumDistance + (current?.Lod <= 1 ? 192 : 0)) desired[tile.Id] = new Request(tile, 1, distance);
            else if (tile.LodCount >= 2 && distance < HorizonDistance + (current is not null ? 384 : 0)) desired[tile.Id] = new Request(tile, 2, distance);
        }
        int budget = Math.Clamp(TileBudget, 1, 25);
        foreach (var (tile, distance) in full.OrderBy(x => x.Distance))
        {
            if (budget-- > 0 || tile.Id == "global") desired[tile.Id] = new Request(tile, 0, distance);
            else if (tile.LodCount >= 1) desired[tile.Id] = new Request(tile, 1, distance);
        }
        foreach (var id in loaded.Keys.Where(x => !desired.ContainsKey(x)).ToArray())
        {
            if (loaded[id].Root.IsValid()) loaded[id].Root.Destroy();
            loaded.Remove(id);
        }
        // A decode for a level that is no longer wanted is abandoned; its worker result is ignored.
        foreach (var id in inFlight.Keys.Where(x => !desired.TryGetValue(x, out var r) || r.Lod != inFlight[x].Request.Lod).ToArray())
            inFlight.Remove(id);
        // Preparation band: detailed terrain just past the detailed ring, medium relief past the medium ring.
        var band = new List<(string Path, int Lod, float Distance)>();
        foreach (var tile in Current.Tiles)
        {
            if (tile.Id == "global") continue;
            float distance = DistanceToTile(tile, source);
            desired.TryGetValue(tile.Id, out var want);
            if (tile.Terrain is not null && distance < FullDetailDistance + PrefetchDistance && want?.Lod != 0) band.Add((CollisionPath(tile.Terrain), 0, distance));
            else if (tile.LodCount >= 1 && distance < MediumDistance + PrefetchDistance && want is { Lod: 2 }) band.Add((tile.LodModels[0], 1, distance));
        }
        prefetch = band.OrderBy(b => b.Distance).Take(Math.Max(0, KeepDetailedTiles - TileBudget) + 48).Select(b => (b.Path, b.Lod)).ToList();
        queue.Clear();
        queue.AddRange(desired.Values
            .Where(r => !failed.Contains((r.Tile.Id, r.Lod)) && !inFlight.ContainsKey(r.Tile.Id) && (!loaded.TryGetValue(r.Tile.Id, out var x) || x.Lod != r.Lod))
            .OrderBy(r => r.Lod == 0 ? r.Distance - 100000 : r.Distance));
    }

    private void Finish(InFlight job)
    {
        var tile = job.Request.Tile;
        GameObject root = null;
        try
        {
            if (job.Model is { IsCompletedSuccessfully: false }) throw job.Model.Exception?.InnerException ?? new Exception("Terrain indisponible");
            root = new GameObject(Scene) { Name = $"{Current.Title} / {tile.Id} / LOD{job.Request.Lod}",
                Flags = GameObjectFlags.NotSaved | GameObjectFlags.NotNetworked };
            root.WorldPosition = (Position(tile) - SourceOrigin) * InchesPerSourceUnit;
            if (job.Model is not null)
            {
                if (job.Model.Result is null || job.Model.Result.IsError) throw new Exception("Terrain absent de l’installation");
                root.Components.Create<ModelRenderer>().Model = job.Model.Result;
                if (job.Request.Lod == 0)
                {
                    var collider = root.Components.Create<ModelCollider>();
                    // Set the static mode before assigning the mesh, to avoid rebuilding its physics body.
                    collider.Static = true; collider.Model = job.Model.Result;
                }
                Remember(job.Path, job.Model.Result, job.Request.Lod);
            }
            // The previous level stays visible until its replacement exists.
            if (loaded.Remove(tile.Id, out var old) && old.Root.IsValid()) old.Root.Destroy();
            loaded.Add(tile.Id, new LoadedTile(root, job.Request.Lod, job.Path));
        }
        catch (Exception error)
        {
            root?.Destroy();
            failed.Add((tile.Id, job.Request.Lod));
            LastError = $"Zone {tile.Id} (LOD{job.Request.Lod}) : {error.Message}";
            Log.Error($"WoW Forever: {LastError}");
        }
    }

    /// <summary>Tiles whose edge lies within a horizontal distance of the camera, nearest first.</summary>
    public IReadOnlyList<WorldTile> TilesWithin(float distance)
    {
        if (Current is null) return Array.Empty<WorldTile>();
        var source = SourcePosition;
        return Current.Tiles.Select(t => (Tile: t, Distance: t.Id == "global" ? 0 : DistanceToTile(t, source)))
            .Where(x => x.Distance <= distance).OrderBy(x => x.Distance).Select(x => x.Tile).ToArray();
    }

    public void Teleport(Vector3 source)
    {
        Scene.GetAllComponents<AzerothPlayer>().FirstOrDefault()?.PrepareTeleport();
        Clear(); SourceOrigin = source.WithZ(0); Revision++;
        if (Camera.IsValid()) Camera.WorldPosition = Vector3.Up * source.z * InchesPerSourceUnit;
        untilRefresh = 0;
    }

    private static Vector3 Position(WorldTile tile) => new(tile.Center[0], tile.Center[1], tile.Center[2]);
    private static string CollisionPath(string path) => path?.Replace("/terrain.vmdl", "/terrain_collision.vmdl");
    public static float DistanceToTile(WorldTile tile, Vector3 source)
    {
        float dx = Math.Max(Math.Abs(source.x - tile.Center[0]) - TileSize / 2, 0);
        float dy = Math.Max(Math.Abs(source.y - tile.Center[1]) - TileSize / 2, 0);
        return MathF.Sqrt(dx * dx + dy * dy);
    }
    private void Clear()
    {
        foreach (var entry in loaded.Values) if (entry.Root.IsValid()) entry.Root.Destroy();
        loaded.Clear(); failed.Clear(); queue.Clear(); inFlight.Clear(); prefetch.Clear();
        refreshedAt = new(float.MaxValue, 0, 0);
        Warming = true;
    }
    protected override void OnDestroy() { Clear(); resident.Clear(); prefetching.Clear(); }

    private sealed record Request(WorldTile Tile, int Lod, float Distance);
    private sealed record LoadedTile(GameObject Root, int Lod, string Path = null);
    private sealed record InFlight(Request Request, Task<Model> Model, string Path);
}
