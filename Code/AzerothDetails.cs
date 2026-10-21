using Sandbox;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;

namespace WoWForever;

public sealed class WorldPlacement
{
    public string Id { get; set; }
    public string Model { get; set; }
    public float[] Position { get; set; }
    public float[] Rotation { get; set; }
    public float Scale { get; set; } = 1;
    public string Type { get; set; }
    public int[] DoodadSets { get; set; } = Array.Empty<int>();
    public int[] Sets { get; set; } = Array.Empty<int>();
}
public sealed class PlacementCatalog { public List<WorldPlacement> Placements { get; set; } = new(); }
/// <summary>One tile's placements, with its buildings listed apart for far scans.</summary>
public sealed class TilePlacements
{
    public List<WorldPlacement> All = new();
    public List<WorldPlacement> Buildings = new();
    /// <summary>Parallel to All: source positions, and load ranges once the model size is known (negative until then).</summary>
    public Vector3[] Positions = Array.Empty<Vector3>();
    public float[] Ranges = Array.Empty<float>();
    public int[] BuildingIndices = Array.Empty<int>();
    public int[] AllIndices = Array.Empty<int>();
    private static readonly System.Text.Json.JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };
    /// <summary>Placements served by the mount as a data prefab; parsing runs on a worker.</summary>
    public static async System.Threading.Tasks.Task<TilePlacements> ReadMounted(string path)
    {
        var prefab = await MountLoads.Prefab(path);
        var json = prefab?.GetMetadata("WowData") ?? throw new System.IO.FileNotFoundException(path);
        return await GameTask.RunInThreadAsync(() => Parse(json));
    }

    public static TilePlacements Parse(string json)
    {
        var all = System.Text.Json.JsonSerializer.Deserialize<PlacementCatalog>(json, Options)?.Placements ?? new();
        var result = new TilePlacements { All = all, Buildings = all.Where(p => p.Type == "wmo").ToList() };
        result.Positions = all.Select(p => p.Position?.Length >= 3 ? new Vector3(p.Position[0], p.Position[1], p.Position[2]) : Vector3.Zero).ToArray();
        result.Ranges = Enumerable.Repeat(-1f, all.Count).ToArray();
        result.AllIndices = Enumerable.Range(0, all.Count).ToArray();
        result.BuildingIndices = result.AllIndices.Where(i => all[i].Type == "wmo").ToArray();
        return result;
    }
}
public sealed class AssetBounds { public float Radius { get; set; } }
public sealed class WorldAsset
{
    public bool NoDraw { get; set; }
    public string Type { get; set; }
    public List<WorldPlacement> Doodads { get; set; } = new();
    public AssetBounds Bounds { get; set; } = new();
    public List<ParticleEmitterInfo> Emitters { get; set; } = new();
    public List<RibbonInfo> Ribbons { get; set; } = new();
    /// <summary>Cards the client turns toward the camera (glows of lamps and torches), in yards.</summary>
    public List<BillboardInfo> Billboards { get; set; } = new();
    /// <summary>Reduced versions the client ships for this model (M2 LOD skins, WMO LOD groups).</summary>
    public int Lods { get; set; }
    public bool HasCollision { get; set; }
}
/// <summary>A camera-facing card of a static model: centre and width in yards, blend and opacity of its material.</summary>
public sealed class BillboardInfo
{
    public string Texture { get; set; }
    public float[] Position { get; set; }
    public float Size { get; set; }
    public int Blend { get; set; }
    public float Opacity { get; set; } = 1;
    public bool Unlit { get; set; }
}
/// <summary>An M2 ribbon (a trail behind a bone) as the mount describes it: heights in yards, lifetime in seconds.</summary>
public sealed class RibbonInfo
{
    public string Texture { get; set; }
    public int Bone { get; set; } = -1;
    public int Blend { get; set; }
    public float[] Position { get; set; }
    /// <summary>Position through its bone's resting pose: where it sits on a model that is not animated.</summary>
    public float[] Placed { get; set; }
    public float Above { get; set; }
    public float Below { get; set; }
    public float EdgesPerSecond { get; set; }
    public float Lifetime { get; set; }
    public float Gravity { get; set; }
    public float[] Color { get; set; }
    public float Alpha { get; set; } = 1;
}
/// <summary>An M2 particle emitter as the mount describes it (yards, seconds, colours 0..1).</summary>
public sealed class ParticleEmitterInfo
{
    public string Texture { get; set; }
    public int Rows { get; set; } = 1;
    public int Columns { get; set; } = 1;
    /// <summary>Bone the emitter follows (animated effects, creatures), -1 for none.</summary>
    public int Bone { get; set; } = -1;
    public float[] Position { get; set; }
    /// <summary>Position through its bone's resting pose: where it sits on a model that is not animated.</summary>
    public float[] Placed { get; set; }
    public int Blend { get; set; }
    public uint Flags { get; set; }
    public float Speed { get; set; }
    public float SpeedVariation { get; set; }
    public float VerticalRange { get; set; }
    public float HorizontalRange { get; set; }
    public float Gravity { get; set; }
    public float Lifespan { get; set; }
    public float Rate { get; set; }
    public float AreaWidth { get; set; }
    public float AreaLength { get; set; }
    public float[][] Colors { get; set; }
    public float[] Alpha { get; set; }
    public float[] Scale { get; set; }
}

/// <summary>Shared models and deduplicated placements, loaded from the nearest outwards by size-aware ranges.</summary>
public sealed class AzerothDetails : Component
{
    /// <summary>Source units. Buildings are seen from afar, props by their own size, ground cover only close by.</summary>
    [Property] public float BuildingDistance { get; set; } = 2600;
    [Property] public float PropDistance { get; set; } = 300;
    [Property] public float PropSizeFactor { get; set; } = 45;
    [Property] public float MaxPropDistance { get; set; } = 1800;
    /// <summary>Large props the client ships reduced versions for (trees, rocks) stay visible this far, drawn with
    /// their lightest version: forests and hills keep their silhouette towards the horizon at little cost.</summary>
    [Property] public float FarPropDistance { get; set; } = 2400;
    [Property] public float FoliageDistance { get; set; } = 90;
    [Property] public float SpawnBudgetMilliseconds { get; set; } = 2.5f;
    [Property] public float WarmupBudgetMilliseconds { get; set; } = 40;
    /// <summary>Yards beyond each model's range where its model is prepared in advance (not placed yet).</summary>
    [Property] public float PrefetchDistance { get; set; } = 450;
    [Property] public float UnloadMargin { get; set; } = 120;
    /// <summary>HLOD: distant copies use the client's reduced versions, from this many yards (scaled by model size).</summary>
    [Property] public float LodDistance { get; set; } = 160;
    /// <summary>Models kept in memory after their placements leave the range, least recently used dropped first.</summary>
    [Property] public int KeepModels { get; set; } = 1536;
    /// <summary>Placement lists kept for tiles already crossed.</summary>
    [Property] public int KeepPlacementTiles { get; set; } = 96;
    public int PrefetchingModels => prefetching.Count;
    /// <summary>True from an arrival until its decor is complete; then only light streaming continues.</summary>
    public bool Warming { get; private set; } = true;
    public int LoadedPlacements => objects.Count;
    public int LoadedBuildings => objects.Values.Count(o => o.IsValid() && o.Name.StartsWith("WoW wmo /"));
    public int PendingPlacements { get; private set; }
    public int CachedModels => models.Count;
    public int RegisteredTiles => tileData.Count;
    public string LastError { get; private set; } = "";
    public int FailedAssets => failed.Count;
    [Property] public float CollisionDistance { get; set; } = 180;
    public bool CollisionsReady { get; private set; }
    private readonly Dictionary<string, WorldPlacement> placed = new();
    private readonly Dictionary<string, ModelCollider> colliders = new();
    private readonly Dictionary<string, System.Threading.Tasks.Task<Model>> collisionJobs = new();
    private readonly Dictionary<string, double> collisionRetry = new();
    private float collisionRefresh;
    private readonly Dictionary<string, TilePlacements> tileData = new();
    private readonly Dictionary<string, System.Threading.Tasks.Task<TilePlacements>> tileJobs = new();
    private readonly Dictionary<string, Interiors> interiors = new();
    private sealed record Interiors(List<WorldPlacement> Items, Vector3[] Positions, float[] Ranges);
    private readonly Dictionary<string, WorldAsset> metadata = new();
    private readonly Dictionary<string, System.Threading.Tasks.Task<WorldAsset>> metadataJobs = new();
    private readonly Dictionary<string, Model> models = new();
    private readonly Dictionary<string, double> modelUse = new();
    private readonly Dictionary<string, System.Threading.Tasks.Task<Model>> prefetching = new();
    private List<string> soon = new();
    private readonly Dictionary<string, System.Threading.Tasks.Task<Model>> mountModels = new();
    private readonly Dictionary<string, GameObject> objects = new();
    private sealed class FadeState
    {
        public ModelRenderer Renderer, Previous;
        public Vector3 Source;
        public float Range, Band, Alpha, Transition = 1;
    }
    private readonly Dictionary<string, FadeState> fades = new();
    private readonly Dictionary<string, int> objectLods = new();
    private Dictionary<string, int> wantedLods = new();
    private readonly HashSet<string> failed = new();
    /// <summary>Spawned models that carry particle emitters, for AzerothParticles.</summary>
    public readonly Dictionary<string, (GameObject Object, WorldAsset Asset)> Effects = new();
    private List<WorldPlacement> pending = new();
    private bool tilesComplete;
    private int revision = -1;
    private AzerothWorld world;
    private float nextRefresh;
    private Vector3 refreshedAt = new(float.MaxValue, 0, 0);
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    protected override void OnStart()
    {
        // The emitters of the models placed here are drawn by a sibling component, created at play time.
        if (!Scene.IsEditor && !Components.Get<AzerothParticles>().IsValid()) Components.Create<AzerothParticles>();
    }

    protected override void OnUpdate()
    {
        if (!world.IsValid()) world = Scene.GetAllComponents<AzerothWorld>().FirstOrDefault();
        if (!world.IsValid() || world.Current is null) return;
        if (revision != world.Revision) { Clear(); revision = world.Revision; }
        nextRefresh -= Time.Delta;
        // Placement scans run when the camera has moved, or periodically while files are still arriving.
        if (nextRefresh <= 0 && (Warming || !tilesComplete || (world.SourcePosition - refreshedAt).Length > 12 || nextRefresh < -5))
        {
            nextRefresh = 0.2f;
            refreshedAt = world.SourcePosition;
            var watch = Stopwatch.StartNew();
            Refresh(world);
            Record(world, "details.refresh", watch);
        }
        var spawn = Stopwatch.StartNew();
        Spawn(world);
        Fade(world.SourcePosition);
        collisionRefresh -= Time.Delta;
        if (collisionRefresh <= 0) { collisionRefresh = 0.2f; UpdateCollisions(world); }
        Prefetch(world);
        Record(world, "details.spawn", spawn);
        if (Warming && !world.Warming && pending.Count == 0 && mountModels.Count == 0 && refreshedAt.x != float.MaxValue && tilesComplete) Warming = false;
    }

    private static string ModelKey(string model, int lod) => lod == 0 ? model : $"{model}_lod{lod}";

    // Full detail near the camera, then the client's own reduced versions; larger models keep detail further.
    private int LodFor(WorldAsset asset, float scale, float distance2, string placementId = null)
    {
        if (asset is null || asset.Lods <= 0) return 0;
        float near = LodDistance + 6 * asset.Bounds.Radius * scale;
        int previous = placementId is not null ? objectLods.GetValueOrDefault(placementId, -1) : -1;
        float first = near * (previous == 0 ? 1.15f : 1);
        float second = near * 2 * (previous == 1 ? 1.15f : 1);
        int lod = distance2 > second * second ? 2 : distance2 > first * first ? 1 : 0;
        return Math.Min(lod, asset.Lods);
    }

    private static void Upcoming(Dictionary<string, float> upcoming, string model, float distance2)
    {
        if (!upcoming.TryGetValue(model, out var d) || distance2 < d) upcoming[model] = distance2;
    }

    // Models of the placements about to come in range, prepared a few at a time once nothing is waiting.
    private void Prefetch(AzerothWorld world)
    {
        foreach (var (id, task) in prefetching.Where(x => x.Value.IsCompleted).ToArray())
        {
            prefetching.Remove(id);
            if (task.IsCompletedSuccessfully && task.Result is { IsError: false } model && !models.ContainsKey(id)) { models[id] = model; modelUse[id] = Time.Now; }
        }
        if (Warming || world.Warming) return;
        // Reserve one worker for the next band while ordinary streaming is busy.
        int limit = pending.Count > 0 || mountModels.Count > 0 ? 1 : 3;
        foreach (var id in soon)
        {
            if (prefetching.Count >= limit) break;
            string model = id.Split("_lod")[0];
            if (models.ContainsKey(id) || prefetching.ContainsKey(id) || failed.Contains(model)) continue;
            var asset = Metadata(world, model);
            if (asset is null || asset.NoDraw) continue;
            prefetching[id] = MountLoads.Model($"{world.Catalog.ModelsDirectory}/{id}.vmdl");
        }
    }

    private static void Record(AzerothWorld world, string name, Stopwatch watch)
    {
        double ms = watch.Elapsed.TotalMilliseconds;
        if (!world.Timings.TryGetValue(name, out var max) || ms > max) world.Timings[name] = ms;
    }

    private float Range(WorldPlacement placement, WorldAsset asset)
    {
        float radius = (asset?.Bounds.Radius ?? 0) * placement.Scale;
        if (placement.Type == "wmo") return BuildingDistance + radius;
        if (placement.Type == "foliage") return FoliageDistance;
        float range = Math.Clamp(PropDistance + PropSizeFactor * radius, PropDistance, MaxPropDistance);
        return asset?.Lods > 0 && radius >= 4 ? Math.Max(range, FarPropDistance) : range;
    }

    private void Refresh(AzerothWorld world)
    {
        var source = world.SourcePosition;
        var wanted = new Dictionary<string, WorldPlacement>();
        var upcoming = new Dictionary<string, float>();
        var lods = new Dictionary<string, int>();
        tilesComplete = true;
        var tiles = world.TilesWithin(Math.Max(BuildingDistance, Math.Max(MaxPropDistance, FarPropDistance)) + PrefetchDistance + AzerothWorld.TileSize);
        foreach (var tile in tiles)
        {
            if (string.IsNullOrWhiteSpace(tile.Placements)) continue;
            if (!tileData.TryGetValue(tile.Id, out var placements))
            {
                // Placement files are parsed by workers, nearest tile first.
                tilesComplete = false;
                if (tileJobs.TryGetValue(tile.Id, out var job))
                {
                    if (!job.IsCompleted) continue;
                    tileJobs.Remove(tile.Id);
                    if (job.IsFaulted) { LastError = job.Exception?.InnerException?.Message ?? "Placements illisibles"; placements = new(); }
                    else placements = job.Result;
                    tileData.Add(tile.Id, placements);
                }
                else
                {
                    if (tileJobs.Count < (Warming ? 16 : 2))
                    {
                        var file = tile.Placements;
                        tileJobs.Add(tile.Id, TilePlacements.ReadMounted(file));
                    }
                    continue;
                }
            }
            // Far tiles only contribute buildings; their props cannot be in range.
            float propScan = Math.Max(MaxPropDistance, FarPropDistance);
            bool propsInRange = AzerothWorld.DistanceToTile(tile, source) <= propScan + PrefetchDistance + UnloadMargin;
            float maxProp2 = MaxPropDistance * MaxPropDistance, base2 = PropDistance * PropDistance;
            float reachProp2 = (propScan + PrefetchDistance) * (propScan + PrefetchDistance);
            foreach (int index in propsInRange ? placements.AllIndices : placements.BuildingIndices)
            {
                var placement = placements.All[index];
                bool building = placement.Type == "wmo";
                float distance2 = (placements.Positions[index] - source).LengthSquared;
                if (!building && distance2 > reachProp2) continue;
                // Props inside the base range need no size; beyond it the range is cached once the model is known.
                WorldAsset asset = null;
                if (building || placement.Type == "foliage" || distance2 > base2)
                {
                    float range = placements.Ranges[index];
                    if (range < 0 || building)
                    {
                        asset = Metadata(world, placement.Model);
                        if (asset is null) { if (!building) continue; }
                        else placements.Ranges[index] = range = Range(placement, asset);
                        if (asset is null) range = Range(placement, null);
                    }
                    float retainedRange = range + (objects.ContainsKey(placement.Id) ? UnloadMargin : 0);
                    if (distance2 > retainedRange * retainedRange)
                    {
                        // Just outside its range: the model is prepared so that it appears without waiting.
                        if (distance2 <= (range + PrefetchDistance) * (range + PrefetchDistance)) Upcoming(upcoming, ModelKey(placement.Model, LodFor(asset ?? Metadata(world, placement.Model), placement.Scale, distance2)), distance2);
                        continue;
                    }
                }
                wanted.TryAdd(placement.Id, placement);
                lods[placement.Id] = LodFor(Metadata(world, placement.Model), placement.Scale, distance2, placement.Id);
                if (!building || asset is null) continue;
                float distance = MathF.Sqrt(distance2);
                // Interior and courtyard decor follows the building, filtered by its selected doodad sets.
                if (distance > MaxPropDistance + asset.Bounds.Radius * placement.Scale) continue;
                var interior = Interior(placement, asset);
                for (int k = 0; k < interior.Items.Count; k++)
                {
                    var item = interior.Items[k];
                    float childDistance2 = (interior.Positions[k] - source).LengthSquared;
                    if (childDistance2 > maxProp2) continue;
                    if (childDistance2 > base2)
                    {
                        if (interior.Ranges[k] < 0)
                        {
                            var childAsset = Metadata(world, item.Model);
                            if (childAsset is null) continue;
                            interior.Ranges[k] = Range(item, childAsset);
                        }
                        if (childDistance2 > interior.Ranges[k] * interior.Ranges[k]) continue;
                    }
                    wanted.TryAdd(item.Id, item);
                    lods[item.Id] = LodFor(Metadata(world, item.Model), item.Scale, childDistance2, item.Id);
                }
            }
        }
        foreach (var id in objects.Keys.Where(id => !wanted.ContainsKey(id)).ToArray())
        {
            if (objects[id].IsValid()) objects[id].Destroy();
            objects.Remove(id); objectLods.Remove(id); Effects.Remove(id);
            placed.Remove(id); colliders.Remove(id); fades.Remove(id);
        }
        wantedLods = lods;
        pending = wanted.Values.Where(p => (!objects.ContainsKey(p.Id) || objectLods.GetValueOrDefault(p.Id) != lods.GetValueOrDefault(p.Id)) && !failed.Contains(p.Model))
            .OrderBy(p => (Vec(p.Position) - source).LengthSquared).ToList();
        PendingPlacements = pending.Count;
        var needed = pending.Select(p => ModelKey(p.Model, lods.GetValueOrDefault(p.Id))).ToHashSet();
        foreach (var id in mountModels.Keys.Where(id => !needed.Contains(id)).ToArray()) mountModels.Remove(id); // the load itself continues in MountLoads
        soon = upcoming.Where(u => !models.ContainsKey(u.Key) && !needed.Contains(u.Key)).OrderBy(u => u.Value).Select(u => u.Key).ToList();
        double now = Time.Now;
        foreach (var id in wanted.Values.Select(p => ModelKey(p.Model, lods.GetValueOrDefault(p.Id))).Concat(upcoming.Keys)) if (models.ContainsKey(id)) modelUse[id] = now;
        if (models.Count > KeepModels)
        {
            // Crossed areas stay in memory; only the models seen least recently are dropped.
            foreach (var id in models.Keys.OrderBy(id => modelUse.GetValueOrDefault(id)).Take(models.Count - KeepModels).ToArray())
                if (modelUse.GetValueOrDefault(id) < now) { models.Remove(id); modelUse.Remove(id); }
        }
        var near = tiles.Select(t => t.Id).ToHashSet();
        if (tileData.Count > KeepPlacementTiles)
        {
            var current = world.Current;
            foreach (var id in tileData.Keys.Where(id => !near.Contains(id))
                .OrderByDescending(id => current.Tiles.FirstOrDefault(t => t.Id == id) is { } t ? AzerothWorld.DistanceToTile(t, source) : float.MaxValue)
                .Take(tileData.Count - KeepPlacementTiles).ToArray())
            {
                foreach (var placement in tileData[id].Buildings) interiors.Remove(placement.Id);
                tileData.Remove(id);
            }
        }
        foreach (var id in tileJobs.Keys.Where(id => !near.Contains(id)).ToArray()) tileJobs.Remove(id);
    }

    // Models are decoded by workers; the main thread assembles them and places instances within its budget.
    private void Spawn(AzerothWorld world)
    {
        float budget = Warming ? WarmupBudgetMilliseconds : SpawnBudgetMilliseconds;
        int maxJobs = Warming ? 16 : 4;
        var timer = Stopwatch.StartNew();
        int index = 0, kept = 0;
        for (; index < pending.Count; index++)
        {
            if (index > 0 && timer.Elapsed.TotalMilliseconds >= budget) break;
            var placement = pending[index];
            int lod = wantedLods.GetValueOrDefault(placement.Id);
            bool existing = objects.TryGetValue(placement.Id, out var current);
            if ((existing && objectLods.GetValueOrDefault(placement.Id) == lod) || failed.Contains(placement.Model)) continue;
            var asset = Metadata(world, placement.Model);
            if (asset is null) { if (!failed.Contains(placement.Model)) pending[kept++] = placement; continue; }
            if (asset.NoDraw) continue;
            try
            {
                string key = ModelKey(placement.Model, lod);
                if (!models.TryGetValue(key, out var model))
                {
                    // The mount decodes the model on its workers and builds it natively.
                    if (!mountModels.TryGetValue(key, out var task))
                    {
                        if (mountModels.Count < maxJobs) mountModels.Add(key, MountLoads.Model($"{world.Catalog.ModelsDirectory}/{key}.vmdl"));
                        pending[kept++] = placement; continue;
                    }
                    if (!task.IsCompleted) { pending[kept++] = placement; continue; }
                    mountModels.Remove(key);
                    model = task.IsCompletedSuccessfully ? task.Result : null;
                    if (model is null || model.IsError) throw new Exception("Modèle absent de l’installation");
                    models.Add(key, model);
                }
                if (existing)
                {
                    // Distance changed the level: the same object takes the other version.
                    if (current.IsValid() && fades.TryGetValue(placement.Id, out var fade))
                    {
                        if (fade.Previous.IsValid()) fade.Previous.Destroy();
                        fade.Previous = current.Components.Create<ModelRenderer>();
                        fade.Previous.Model = fade.Renderer.Model;
                        fade.Previous.RenderType = ModelRenderer.ShadowRenderType.Off;
                        fade.Renderer.Model = model;
                        fade.Transition = 0;
                    }
                    objectLods[placement.Id] = lod;
                    continue;
                }
                var obj = new GameObject(Scene) { Name = $"WoW {placement.Type} / {placement.Model}",
                    Flags = GameObjectFlags.NotSaved | GameObjectFlags.NotNetworked };
                obj.WorldPosition = (Vec(placement.Position) - world.SourceOrigin) * AzerothWorld.InchesPerSourceUnit;
                obj.WorldRotation = Rot(placement.Rotation);
                obj.WorldScale = Vector3.One * placement.Scale;
                var renderer = obj.Components.Create<ModelRenderer>();
                renderer.Model = model;
                float range = Range(placement, asset);
                fades[placement.Id] = new FadeState { Renderer = renderer, Source = Vec(placement.Position), Range = range,
                    Band = Math.Clamp(range * .12f, 18, 250), Alpha = Warming ? 1 : 0 };
                renderer.Tint = Color.White.WithAlpha(Warming ? 1 : 0);
                if (placement.Type == "foliage" || asset.Bounds.Radius * placement.Scale < 3)
                    renderer.RenderType = ModelRenderer.ShadowRenderType.Off;
                if (asset.Emitters?.Count > 0 || asset.Ribbons?.Count > 0 || asset.Billboards?.Count > 0) Effects[placement.Id] = (obj, asset);
                objects.Add(placement.Id, obj);
                placed[placement.Id] = placement;
                objectLods[placement.Id] = lod;
            }
            catch (Exception error) { failed.Add(placement.Model); LastError = $"Décor {placement.Model} : {error.Message}"; Log.Warning(LastError); }
        }
        // Waiting placements keep their distance order ahead of the ones not reached this frame.
        pending.RemoveRange(kept, index - kept);
        PendingPlacements = pending.Count;
    }

    // A building's decor is static: its world placements are computed once per building placement.
    private Interiors Interior(WorldPlacement placement, WorldAsset asset)
    {
        if (interiors.TryGetValue(placement.Id, out var cached)) return cached;
        var list = new List<WorldPlacement>();
        var parent = new Transform(Vec(placement.Position), Rot(placement.Rotation), placement.Scale);
        foreach (var child in asset.Doodads)
        {
            if (child.Sets.Length > 0 && !child.Sets.Contains(0) && !child.Sets.Any(s => placement.DoodadSets.Contains(s))) continue;
            var position = parent.PointToWorld(Vec(child.Position));
            list.Add(new WorldPlacement { Id = placement.Id + "/" + child.Id, Model = child.Model, Type = "m2", Scale = placement.Scale * child.Scale,
                Position = new[] { position.x, position.y, position.z },
                Rotation = QuaternionValues(parent.Rotation * Rot(child.Rotation)) });
        }
        var result = new Interiors(list, list.Select(p => Vec(p.Position)).ToArray(), Enumerable.Repeat(-1f, list.Count).ToArray());
        interiors[placement.Id] = result;
        return result;
    }

    // Model descriptions are read by workers; null means not known yet (or failed), and the scan comes back later.
    private WorldAsset Metadata(AzerothWorld world, string id)
    {
        if (metadata.TryGetValue(id, out var asset)) return asset;
        if (failed.Contains(id)) return null;
        if (metadataJobs.TryGetValue(id, out var job))
        {
            if (!job.IsCompleted) { tilesComplete = false; return null; }
            metadataJobs.Remove(id);
            if (job.IsFaulted || job.Result is null) { failed.Add(id); LastError = $"Modèle {id} : {job.Exception?.InnerException?.Message ?? "vide"}"; return null; }
            metadata[id] = job.Result;
            return job.Result;
        }
        tilesComplete = false;
        if (metadataJobs.Count >= (Warming ? 64 : 8)) return null;
        metadataJobs.Add(id, MountedAsset($"{world.Catalog.ModelsDirectory}/{id}.prefab"));
        return null;
    }

    private static async System.Threading.Tasks.Task<WorldAsset> MountedAsset(string path)
    {
        var prefab = await MountLoads.Prefab(path);
        var json = prefab?.GetMetadata("WowData") ?? throw new System.IO.FileNotFoundException(path);
        // Large WMO descriptions contain thousands of interior placements. Keep their
        // parsing off the game thread, just like the terrain placement lists.
        return await GameTask.RunInThreadAsync(() => JsonSerializer.Deserialize<WorldAsset>(json, JsonOptions));
    }

    private static Vector3 Vec(float[] v) => v?.Length >= 3 ? new(v[0], v[1], v[2]) : Vector3.Zero;
    private void Fade(Vector3 source)
    {
        foreach (var fade in fades.Values)
        {
            float target = Math.Clamp((fade.Range - (fade.Source - source).Length) / fade.Band, 0, 1);
            float step = Time.Delta * 3;
            float next = fade.Alpha + Math.Clamp(target - fade.Alpha, -step, step);
            float transition = Math.Min(1, fade.Transition + Time.Delta * 3);
            if (next != fade.Alpha || transition != fade.Transition)
                fade.Renderer.Tint = Color.White.WithAlpha(next * transition);
            if (fade.Previous.IsValid())
            {
                if (transition >= 1) { fade.Previous.Destroy(); fade.Previous = null; }
                else fade.Previous.Tint = Color.White.WithAlpha(next * (1 - transition));
            }
            fade.Alpha = next; fade.Transition = transition;
        }
    }
    private static Rotation Rot(float[] q) => q?.Length == 4 ? new(q[0], q[1], q[2], q[3]) : Rotation.Identity;
    private static float[] QuaternionValues(Rotation q) => new[] { q.x, q.y, q.z, q.w };
    private void UpdateCollisions(AzerothWorld world)
    {
        CollisionsReady = true;
        foreach (var (id, job) in collisionJobs.Where(x => x.Value.IsCompleted).ToArray())
        {
            collisionJobs.Remove(id);
            if (job.IsCompletedSuccessfully && job.Result is { IsError: false } model)
                models[id + "_collision"] = model;
            else
            {
                LastError = $"Collision {id} indisponible";
                // Keep the nearby player safe, but do not hammer a missing file every frame.
                collisionRetry[id] = Time.Now + 30;
            }
        }
        foreach (var (id, p) in placed)
        {
            if (!metadata.TryGetValue(p.Model, out var asset) || !asset.HasCollision || p.Type == "foliage") continue;
            float distance = (Vec(p.Position) - world.SourcePosition).Length;
            float reach = CollisionDistance + asset.Bounds.Radius * p.Scale;
            if (distance > reach + UnloadMargin)
            {
                if (colliders.Remove(id, out var old) && old.IsValid()) old.Destroy();
                continue;
            }
            if (colliders.ContainsKey(id)) continue;
            if (distance > reach) continue;
            string key = p.Model + "_collision";
            if (!models.TryGetValue(key, out var model))
            {
                if (distance <= 30 + asset.Bounds.Radius * p.Scale) CollisionsReady = false;
                if (collisionJobs.Count < 3 && !collisionJobs.ContainsKey(p.Model) && collisionRetry.GetValueOrDefault(p.Model) <= Time.Now)
                    collisionJobs[p.Model] = MountLoads.Model($"{world.Catalog.ModelsDirectory}/{key}.vmdl");
                continue;
            }
            modelUse[key] = Time.Now;
            var collider = objects[id].Components.Create<ModelCollider>();
            collider.Static = true; collider.Model = model;
            colliders[id] = collider;
        }
    }
    public void Rebase(Vector3 delta) { foreach (var obj in objects.Values) if (obj.IsValid()) obj.WorldPosition += delta; }
    private void Clear()
    {
        foreach (var obj in objects.Values) if (obj.IsValid()) obj.Destroy();
        objects.Clear(); objectLods.Clear(); Effects.Clear(); soon.Clear(); mountModels.Clear(); metadataJobs.Clear(); tileData.Clear(); tileJobs.Clear(); interiors.Clear(); failed.Clear(); pending.Clear(); LastError = "";
        placed.Clear(); colliders.Clear(); fades.Clear(); collisionRetry.Clear(); CollisionsReady = false; collisionRefresh = 0;
        refreshedAt = new(float.MaxValue, 0, 0); Warming = true;
    }
    // Models survive a teleport (bounded by KeepModels); they are only released with the component.
    protected override void OnDestroy() { Clear(); models.Clear(); modelUse.Clear(); prefetching.Clear(); }
}
