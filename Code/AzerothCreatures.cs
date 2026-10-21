using Sandbox;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace WoWForever;

/// <summary>NPCs and monsters near the explorer, as the mount places them (Classic positions, the client's
/// animated models). Idle creatures play their stand animation or the pose the server gives them (sitting,
/// sleeping, working); wanderers stroll within their spawn radius. Riders sit on their mounts, auras stay on,
/// creatures cast their out-of-combat spells now and then, and answer a spell aimed at them with their own.</summary>
public sealed class AzerothCreatures : Component
{
    /// <summary>Source units (yards).</summary>
    [Property] public float Distance { get; set; } = 140;
    [Property] public int MaxCreatures { get; set; } = 160;
    public int Live => live.Count;
    public string LastError { get; private set; } = "";

    public sealed class SpawnInfo
    {
        public string Id { get; set; } public uint Entry { get; set; } public string Name { get; set; } public string Title { get; set; }
        public string Level { get; set; } public string Model { get; set; } public float Scale { get; set; } public bool Npc { get; set; }
        public float[] Position { get; set; } public float Yaw { get; set; } public float Wander { get; set; }
        public List<uint> Spells { get; set; } = new(); public List<uint> IdleSpells { get; set; } = new(); public List<uint> Auras { get; set; } = new();
        public string Pose { get; set; } public string Mount { get; set; } public float MountScale { get; set; }
        public uint NpcFlags { get; set; }
        /// <summary>Equipped weapons (main hand, off hand, ranged), as item ids.</summary>
        public List<uint> Items { get; set; } = new();
    }

    private sealed class Creature
    {
        public GameObject Object; public SkinnedModelRenderer Renderer; public SpawnInfo Info;
        public Vector3 Home, Target; public float Wait; public bool Moving;
        /// <summary>The animation it rests in: idle, its pose, or riding.</summary>
        public string Rest = "idle";
        public TimeUntil NextSpell; public TimeUntil Fighting; public bool Casting;
        /// <summary>Called from the sandbox's bestiary: stays until dismissed.</summary>
        public bool Summoned;
    }

    private const float Inches = AzerothWorld.InchesPerSourceUnit;
    private readonly Dictionary<string, Task<List<SpawnInfo>>> tileJobs = new();
    private readonly Dictionary<string, List<SpawnInfo>> tiles = new();
    private readonly Dictionary<string, Creature> live = new();
    private readonly Dictionary<string, Task<Model>> loading = new();
    private AzerothWorld world;
    private AzerothSpells spells;
    private int revision = -1;
    private TimeUntil nextRefresh;
    private static readonly System.Text.Json.JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    protected override void OnUpdate()
    {
        if (!world.IsValid()) world = Scene.GetAllComponents<AzerothWorld>().FirstOrDefault();
        if (!world.IsValid() || world.Current is null || !world.Camera.IsValid()) return;
        if (revision != world.Revision) { Clear(); revision = world.Revision; }
        if (!spells.IsValid() && Scene.GetAllComponents<AzerothSpells>().FirstOrDefault() is { } found) { spells = found; spells.Targeted += Engage; }
        if (nextRefresh) { nextRefresh = 0.5f; Refresh(); }
        foreach (var c in live.Values) { Animate(c); Act(c); }
    }

    // Seen only by the dead in the client: spirit healers and guides (NPC flags 32 and 64), and creatures under
    // the Ghost (9036) or Shroud of Death (10848) auras.
    private static bool DeathOnly(SpawnInfo s) => (s.NpcFlags & (32 | 64)) != 0 || s.Auras.Contains(9036) || s.Auras.Contains(10848);
    private bool dead;

    private void Refresh()
    {
        bool nowDead = Scene.GetAllComponents<AzerothPlayer>().FirstOrDefault()?.Dead == true; // dead players also see the living
        dead = nowDead;
        var source = world.SourcePosition;
        foreach (var tile in world.TilesWithin(Distance))
        {
            string key = world.Current.Id + "/" + tile.Id;
            if (tiles.ContainsKey(key) || tileJobs.ContainsKey(key)) continue;
            tileJobs[key] = Load($"mount://wowforever/maps/{world.Current.Id}/{tile.Id}/creatures.prefab");
        }
        foreach (var (key, job) in tileJobs.Where(j => j.Value.IsCompleted).ToList())
        {
            tileJobs.Remove(key);
            tiles[key] = job.IsCompletedSuccessfully ? job.Result : new List<SpawnInfo>();
        }
        var wanted = tiles.Values.SelectMany(t => t)
            .Select(s => (Spawn: s, Distance: (new Vector3(s.Position[0], s.Position[1], s.Position[2]) - source).Length))
            .Where(x => x.Distance <= Distance + (live.ContainsKey(x.Spawn.Id) ? 10 : 0) && (dead || !DeathOnly(x.Spawn)))
            .OrderBy(x => x.Distance).Take(MaxCreatures).Select(x => x.Spawn).ToList();
        var keep = wanted.Select(s => s.Id).ToHashSet();
        foreach (var id in live.Keys.Where(id => !keep.Contains(id) && !live[id].Summoned).ToList()) { live[id].Object.Destroy(); live.Remove(id); }
        int created = 0;
        foreach (var s in wanted)
        {
            if (live.ContainsKey(s.Id) || created >= 12) continue;
            if (!Ready(s.Model, out var model) | (s.Mount is not null && !Ready(s.Mount, out _)) || model is null) continue;
            live[s.Id] = Spawn(s, model); created++;
        }
    }

    // Starts loading a model once; true when the load has ended (the model is null when it failed).
    private bool Ready(string path, out Model model)
    {
        model = null;
        if (!loading.TryGetValue(path, out var task)) { loading[path] = MountLoads.Model(path); return false; }
        if (!task.IsCompleted) return false;
        if (task.IsCompletedSuccessfully && task.Result is { IsError: false }) model = task.Result;
        return true;
    }

    private static async Task<List<SpawnInfo>> Load(string path)
    {
        var prefab = await MountLoads.Prefab(path);
        var json = prefab?.GetMetadata("WowData");
        return json is null ? new List<SpawnInfo>() : await GameTask.RunInThreadAsync(() => System.Text.Json.JsonSerializer.Deserialize<List<SpawnInfo>>(json, Json) ?? new());
    }

    private Creature Spawn(SpawnInfo s, Model model, Vector3? at = null)
    {
        var position = at ?? (new Vector3(s.Position[0], s.Position[1], s.Position[2]) - world.SourceOrigin) * Inches;
        var go = new GameObject(Scene) { Name = $"PNJ {s.Name}", Flags = GameObjectFlags.NotSaved | GameObjectFlags.NotNetworked };
        go.WorldPosition = position;
        go.WorldScale = s.Scale > 0 ? s.Scale : 1;
        var renderer = go.Components.Create<SkinnedModelRenderer>();
        renderer.Model = model; renderer.UseAnimGraph = false;
        renderer.Sequence.Name = "idle"; renderer.Sequence.Looping = true;
        // The mount's models face +Y; the client's orientation is a yaw around Z.
        go.WorldRotation = Rotation.FromYaw(s.Yaw - 90);
        var c = new Creature { Object = go, Renderer = renderer, Info = s, Home = position, Target = position, Wait = Random.Shared.Float(2, 8),
            NextSpell = Random.Shared.Float(6, 20) };
        if (s.Pose is { } pose && Has(renderer, pose)) c.Rest = pose;
        if (s.Mount is not null && Ready(s.Mount, out var steed) && steed is not null) Ride(c, steed);
        c.Renderer.Sequence.Name = c.Rest;
        if (spells.IsValid()) foreach (var aura in s.Auras) _ = spells.Aura(c.Renderer.GameObject, aura);
        if (s.Items.Count > 0) _ = Arm(c);
        return c;
    }

    // Weapons in hand: the main hand item on the right hand, the off hand one (or a shield) on the left.
    private async Task Arm(Creature c)
    {
        for (int k = 0; k < Math.Min(2, c.Info.Items.Count); k++)
        {
            var item = await WowItems.Item(c.Info.Items[k]);
            if (item is null || await WowItems.Display(item.Display) is not { Models.Count: > 0 } display) continue;
            var variant = WowItemDisplay.Pick(display.Models[0].Variants, 0, 0);
            var model = variant is null ? null : await MountLoads.Model(variant.Path);
            if (model is null || model.IsError || !c.Renderer.IsValid()) continue;
            var weapon = new GameObject(Scene) { Name = "Arme", Parent = c.Object, Flags = GameObjectFlags.NotSaved | GameObjectFlags.NotNetworked };
            weapon.WorldScale = c.Renderer.WorldScale;
            var renderer = weapon.Components.Create<SkinnedModelRenderer>();
            renderer.Model = model; renderer.UseAnimGraph = false; renderer.Sequence.Name = "idle"; renderer.Sequence.Looping = true;
            var follow = weapon.Components.Create<FollowAttachment>();
            follow.Target = c.Renderer; follow.Attachment = WowItems.AttachmentName(k == 0 ? 1 : item.Slot == 14 ? 0 : 2);
        }
    }

    private static bool Has(SkinnedModelRenderer renderer, string clip) => renderer.Model?.AnimationNames?.Contains(clip) == true;

    // A rider: the mount stands on the spawn, the creature moves to a child placed on its saddle point.
    private void Ride(Creature c, Model steed)
    {
        float scale = c.Object.WorldScale.x;
        c.Object.WorldScale = 1;
        var mount = c.Object.Components.Create<SkinnedModelRenderer>();
        var rider = new GameObject(Scene) { Name = "Cavalier", Parent = c.Object, Flags = GameObjectFlags.NotSaved | GameObjectFlags.NotNetworked };
        rider.LocalRotation = Rotation.Identity; rider.WorldScale = scale;
        var body = rider.Components.Create<SkinnedModelRenderer>();
        body.Model = c.Renderer.Model; body.UseAnimGraph = false; body.Sequence.Looping = true;
        c.Renderer.Destroy();
        c.Renderer = body;
        mount.Model = steed; mount.UseAnimGraph = false; mount.Sequence.Name = "idle"; mount.Sequence.Looping = true;
        mount.GameObject.WorldScale = c.Info.MountScale > 0 ? c.Info.MountScale : 1;
        rider.WorldScale = scale;
        rider.LocalPosition = mount.GetAttachment("mount") is { } seat ? c.Object.WorldTransform.PointToLocal(seat.Position) : Vector3.Up * 40;
        if (Has(body, "mount")) c.Rest = "mount";
        c.Info.Wander = 0; // riders keep their post
    }

    // Out of combat a creature casts its idle spells now and then; in a fight (a spell was aimed at it) it
    // turns to the player and casts its combat spells.
    private void Act(Creature c)
    {
        if (!spells.IsValid() || c.Casting || !c.NextSpell || !c.Object.IsValid()) return;
        bool fighting = !c.Fighting && c.Info.Spells.Count > 0;
        var list = fighting ? c.Info.Spells : c.Info.IdleSpells;
        c.NextSpell = fighting ? Random.Shared.Float(3, 6) : Random.Shared.Float(12, 30);
        if (list.Count == 0 || (c.Object.WorldPosition - world.Camera.WorldPosition).Length > 60 * Inches) return;
        var player = Scene.GetAllComponents<AzerothPlayer>().FirstOrDefault()?.Body;
        var target = fighting && player.IsValid() ? player : c.Object;
        if (fighting)
        {
            var to = (target.WorldPosition - c.Object.WorldPosition).WithZ(0);
            if (to.Length > 1) c.Object.WorldRotation = Rotation.LookAt(to.Normal) * Rotation.FromYaw(-90);
        }
        _ = Cast(c, list[Random.Shared.Int(0, list.Count - 1)], target);
    }

    private async Task Cast(Creature c, uint spell, GameObject target)
    {
        c.Casting = true; c.Moving = false;
        try
        {
            await spells.CastBy(c.Renderer.GameObject, spell, target, on =>
            {
                if (!c.Renderer.IsValid()) return;
                string clip = on ? (Has(c.Renderer, "spell_ready") ? "spell_ready" : null) : Has(c.Renderer, "spell") ? "spell" : Has(c.Renderer, "cast") ? "cast" : null;
                if (clip is null) return;
                c.Renderer.Sequence.Name = clip; c.Renderer.Sequence.Looping = on;
            });
        }
        finally
        {
            c.Casting = false;
            if (c.Renderer.IsValid()) { c.Renderer.Sequence.Name = c.Rest; c.Renderer.Sequence.Looping = true; }
        }
    }

    // A spell aimed at a creature starts a short fight: it answers with its combat spells for a while.
    private void Engage(GameObject target)
    {
        var c = live.Values.FirstOrDefault(x => x.Object == target);
        if (c is null || c.Info.Spells.Count == 0) return;
        c.Fighting = 20; c.NextSpell = 1.5f; c.Moving = false;
    }

    // Wanderers pick a point within their radius, walk there, then stand for a while.
    private void Animate(Creature c)
    {
        if (!c.Object.IsValid() || c.Info.Wander <= 0.5f || c.Casting || !c.Fighting) return;
        if (!c.Moving)
        {
            c.Wait -= Time.Delta;
            if (c.Wait > 0) return;
            var offset = Vector3.Random.WithZ(0).Normal * Random.Shared.Float(0.3f, 1) * c.Info.Wander * Inches;
            c.Target = c.Home + offset; c.Moving = true; c.Renderer.Sequence.Name = "walk";
        }
        var to = (c.Target - c.Object.WorldPosition).WithZ(0);
        float step = 2.5f * Inches * Time.Delta * (c.Object.WorldScale.x > 0 ? 1 : 1);
        if (to.Length <= step) { c.Moving = false; c.Wait = Random.Shared.Float(3, 10); c.Renderer.Sequence.Name = c.Rest; return; }
        var next = c.Object.WorldPosition + to.Normal * step;
        var ground = Scene.Trace.Ray(next + Vector3.Up * 120, next + Vector3.Down * 400).Run();
        c.Object.WorldPosition = ground.Hit ? ground.EndPosition : next;
        c.Object.WorldRotation = Rotation.LookAt(to.Normal) * Rotation.FromYaw(-90);
    }

    /// <summary>Every creature and NPC of the database, as the mount describes them (mount://wowforever/bestiary.prefab).</summary>
    public static Task<List<SpawnInfo>> Bestiary => bestiary ??= Load("mount://wowforever/bestiary.prefab");
    private static Task<List<SpawnInfo>> bestiary;
    /// <summary>The last creature summoned, whose animations the sandbox plays.</summary>
    public SkinnedModelRenderer Selected => summoned is { } c && c.Renderer.IsValid() ? c.Renderer : null;
    public string SelectedName => Selected is null ? null : summoned.Info.Name;
    public string SelectedClip => Selected?.Sequence.Name;
    public int SummonedCount => live.Values.Count(c => c.Summoned);
    private Creature summoned;
    private int summons;

    /// <summary>Summons a creature a few yards in front of the player, facing them, with its weapons, mount,
    /// auras and spells; it keeps its post and answers spells aimed at it like the world's creatures.</summary>
    public async Task Summon(SpawnInfo info)
    {
        var model = await MountLoads.Model(info.Model);
        if (model is null || model.IsError) { LastError = $"Modèle indisponible : {info.Name}"; return; }
        if (info.Mount is not null) await MountLoads.Model(info.Mount);
        if (!world.IsValid()) return;
        var body = Scene.GetAllComponents<AzerothPlayer>().FirstOrDefault()?.Body;
        var origin = body.IsValid() ? body.WorldPosition : world.Camera.WorldPosition;
        var forward = world.Camera.WorldRotation.Forward.WithZ(0).Normal; // where the player looks
        var at = origin + forward * 8 * Inches;
        // Down to the ground under that point, however high the free-flying camera is.
        var ground = Scene.Trace.Ray(at + Vector3.Up * 200, at + Vector3.Down * 40000).Run();
        if (ground.Hit) at = ground.EndPosition;
        var copy = System.Text.Json.JsonSerializer.Deserialize<SpawnInfo>(System.Text.Json.JsonSerializer.Serialize(info), Json);
        copy.Id = $"summon:{++summons}"; copy.Wander = 0;
        copy.Yaw = Rotation.LookAt(-forward).Yaw();
        var c = Spawn(copy, model, at);
        c.Object.WorldRotation = Rotation.LookAt(-forward) * Rotation.FromYaw(-90);
        c.Summoned = true; c.Home = c.Target = at;
        live[copy.Id] = c; summoned = c;
    }

    /// <summary>Plays one of the summoned creature's animations, looping, and keeps it as its resting one.</summary>
    public void Play(string clip)
    {
        if (summoned is null || !Selected.IsValid() || !Has(summoned.Renderer, clip)) return;
        summoned.Rest = clip;
        summoned.Renderer.Sequence.Name = clip; summoned.Renderer.Sequence.Looping = true;
    }

    /// <summary>Sends the summoned creatures away: the last one, or all of them.</summary>
    public void Dismiss(bool all)
    {
        foreach (var (id, c) in live.Where(x => x.Value.Summoned && (all || x.Value == summoned)).ToList())
        {
            if (c.Object.IsValid()) c.Object.Destroy();
            live.Remove(id);
        }
        summoned = all ? null : live.Values.LastOrDefault(c => c.Summoned);
    }

    public void Rebase(Vector3 delta) { foreach (var c in live.Values) { if (c.Object.IsValid()) c.Object.WorldPosition += delta; c.Home += delta; c.Target += delta; } }

    private void Clear()
    {
        foreach (var c in live.Values) if (c.Object.IsValid()) c.Object.Destroy();
        live.Clear(); tiles.Clear(); tileJobs.Clear(); summoned = null;
    }

    protected override void OnDestroy()
    {
        if (spells.IsValid()) spells.Targeted -= Engage;
        Clear();
    }
}
