using Sandbox;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace WoWForever;

/// <summary>Casts the client's spell visuals from the explorer's character: the precast and cast effects on the
/// caster's attachment points (hands, chest, feet), then the impact on the nearest creature ahead or a point
/// in front. Effect models, their animations and particles all come from the mount.</summary>
public sealed class AzerothSpells : Component
{
    public sealed class SpellEntry { public uint Id { get; set; } public string Name { get; set; } }
    public List<SpellEntry> Spells { get; private set; } = new();
    public string Casting { get; private set; }
    public string LastError { get; private set; } = "";

    internal sealed class EffectModel { public string Path { get; set; } public string Info { get; set; } public int Slot { get; set; } public float Scale { get; set; } }
    internal sealed class SpellData { public uint Id { get; set; } public string Name { get; set; } public List<PhaseJson> Phases { get; set; } = new(); }
    internal sealed class PhaseJson { public string Phase { get; set; } public string Target { get; set; } public float Speed { get; set; } public List<EffectModel> Models { get; set; } = new(); }

    private Task<PrefabFile> catalog;
    private AzerothPlayer player;
    private AzerothParticles particles;
    private static readonly System.Text.Json.JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    protected override void OnStart() => catalog = MountLoads.Prefab("mount://wowforever/spells.prefab");

    protected override void OnUpdate()
    {
        if (catalog is { IsCompleted: true })
        {
            if (catalog.IsCompletedSuccessfully && catalog.Result?.GetMetadata("WowData") is { } json)
                Spells = System.Text.Json.JsonSerializer.Deserialize<List<SpellEntry>>(json, Json) ?? new();
            catalog = null;
        }
    }

    /// <summary>Spells whose name contains the text, shortest first; without text, every spell by name.</summary>
    public IEnumerable<SpellEntry> Search(string text, int count = 24) =>
        string.IsNullOrWhiteSpace(text) ? Spells.OrderBy(s => s.Name).Take(count)
        : Spells.Where(s => s.Name.Contains(text.Trim(), StringComparison.OrdinalIgnoreCase)).OrderBy(s => s.Name.Length).Take(count);

    public void Cast(uint spell) => _ = CastAsync(spell);

    /// <summary>A creature (or the player) the explorer's spell was aimed at: creatures answer with their own spells.</summary>
    public event Action<GameObject> Targeted;

    private async Task CastAsync(uint id)
    {
        if (!player.IsValid()) player = Scene.GetAllComponents<AzerothPlayer>().FirstOrDefault();
        var caster = player?.Body;
        if (!caster.IsValid()) { LastError = "Passe en marche pour lancer un sort."; return; }
        try
        {
            var spell = await Data(id);
            if (spell?.Phases is not { Count: > 0 }) return;
            Casting = spell.Name; LastError = "";
            await Perform(spell, caster, () =>
            {
                var target = Target(caster);
                var facing = (target.WorldPosition - caster.WorldPosition).WithZ(0);
                if (facing.Length > 1) caster.WorldRotation = Rotation.LookAt(facing.Normal);
                if (target.Name.StartsWith("PNJ ")) Targeted?.Invoke(target);
                return target;
            }, on => player.PlayCast(on), 2.5f);
        }
        catch (Exception error) { LastError = $"Sort {id} : {error.Message}"; }
        finally { Casting = null; }
    }

    /// <summary>A creature's spell: its preparation on the caster, then the projectile and the impact on the target.</summary>
    public async Task CastBy(GameObject caster, uint id, GameObject target, Action<bool> pose)
    {
        try
        {
            var spell = await Data(id);
            if (spell?.Phases is { Count: > 0 } && caster.IsValid()) await Perform(spell, caster, () => target.IsValid() ? target : caster, pose, 1.5f);
        }
        catch (Exception error) { LastError = $"Sort {id} : {error.Message}"; }
    }

    /// <summary>A lasting aura of the client (ghosts, shields, glows) on a creature, until the creature goes.</summary>
    public async Task<List<GameObject>> Aura(GameObject on, uint id)
    {
        var effects = new List<GameObject>();
        var spell = await Data(id);
        if (spell?.Phases is null || !on.IsValid()) return effects;
        var state = spell.Phases.Where(p => p.Phase == "aura").ToList();
        if (state.Count == 0) state = spell.Phases.Where(p => p.Phase == "channel").ToList();
        foreach (var p in state) effects.AddRange(await Show(p, on, 0));
        return effects;
    }

    private readonly Dictionary<uint, Task<SpellData>> data = new();
    private Task<SpellData> Data(uint id)
    {
        if (!data.TryGetValue(id, out var task)) data[id] = task = LoadData(id);
        return task;
    }

    private static async Task<SpellData> LoadData(uint id)
    {
        var prefab = await MountLoads.Prefab($"mount://wowforever/spells/{id}.prefab");
        return System.Text.Json.JsonSerializer.Deserialize<SpellData>(prefab?.GetMetadata("WowData") ?? "{}", Json);
    }

    private async Task Perform(SpellData spell, GameObject caster, Func<GameObject> aim, Action<bool> pose, float precast)
    {
        if (!particles.IsValid()) particles = Scene.GetAllComponents<AzerothParticles>().FirstOrDefault();
        pose?.Invoke(true);
        foreach (var p in spell.Phases.Where(p => p.Phase is "precast" or "cast" && p.Target == "caster")) await Show(p, caster, precast);
        await GameTask.DelaySeconds(precast);
        pose?.Invoke(false);
        if (!caster.IsValid()) return;
        var target = aim();
        // The projectile leaves the casting hand and flies to the target at the client's missile speed.
        foreach (var p in spell.Phases.Where(p => p.Phase == "missile")) await Fly(p, caster, target);
        foreach (var p in spell.Phases.Where(p => p.Phase is "impact" or "channel" or "aura"))
            await Show(p, p.Target == "caster" ? caster : target, 3f);
    }

    private async Task Fly(PhaseJson phase, GameObject caster, GameObject target)
    {
        foreach (var m in phase.Models)
        {
            var model = await MountLoads.Model(m.Path);
            if (model is null || model.IsError || !target.IsValid()) continue;
            var missile = new GameObject(Scene) { Name = "Effet de sort", Flags = GameObjectFlags.NotSaved | GameObjectFlags.NotNetworked };
            missile.WorldPosition = Point(caster, m.Slot is 0 ? 22 : m.Slot);
            missile.WorldScale = Math.Clamp(m.Scale, 0.05f, 20);
            var renderer = missile.Components.Create<SkinnedModelRenderer>();
            renderer.Model = model; renderer.UseAnimGraph = false; renderer.Sequence.Name = "idle"; renderer.Sequence.Looping = true;
            renderer.CreateBoneObjects = true;
            var info = await MountLoads.Prefab(m.Info);
            if (info?.GetMetadata("WowData") is { } json && particles.IsValid())
            {
                var asset = System.Text.Json.JsonSerializer.Deserialize<WorldAsset>(json, Json);
                if (asset?.Emitters?.Count > 0 || asset?.Ribbons?.Count > 0) particles.Build(missile, asset);
            }
            // Timed on real seconds, so the flight lasts distance / speed whatever the frame rate.
            float speed = Math.Clamp(phase.Speed, 4, 80) * AzerothWorld.InchesPerSourceUnit;
            var start = missile.WorldPosition;
            RealTimeSince flying = 0;
            while (missile.IsValid() && target.IsValid())
            {
                var end = target.WorldPosition + Vector3.Up * 40;
                float t = flying * speed / Math.Max(1, (end - start).Length);
                if (t >= 1) break;
                missile.WorldPosition = Vector3.Lerp(start, end, t);
                missile.WorldRotation = Rotation.LookAt((end - start).Normal);
                await GameTask.Yield();
            }
            _ = DestroyLater(missile, 0.3f);
        }
    }

    // The nearest creature in the view within 30 yards, otherwise a point 12 yards ahead on the ground.
    private GameObject Target(GameObject caster)
    {
        var view = Scene.Camera.IsValid() ? Scene.Camera.WorldRotation : caster.WorldRotation;
        var forward = view.Forward.WithZ(0).Normal;
        var nearest = Scene.GetAllObjects(true).Where(o => o.Name.StartsWith("PNJ "))
            .Select(o => (Object: o, To: o.WorldPosition - caster.WorldPosition))
            .Where(x => x.To.Length < 30 * 36 && Vector3.Dot(x.To.Normal, forward) > 0.6f)
            .OrderBy(x => x.To.Length).Select(x => x.Object).FirstOrDefault();
        if (nearest.IsValid()) return nearest;
        var point = new GameObject(Scene) { Name = "Point d'impact", Flags = GameObjectFlags.NotSaved | GameObjectFlags.NotNetworked };
        var ahead = caster.WorldPosition + forward * 12 * 36;
        var ground = Scene.Trace.Ray(ahead + Vector3.Up * 200, ahead + Vector3.Down * 2000).Run();
        point.WorldPosition = ground.Hit ? ground.EndPosition : ahead;
        _ = DestroyLater(point, 4);
        return point;
    }

    private async Task<List<GameObject>> Show(PhaseJson phase, GameObject on, float seconds)
    {
        var created = new List<GameObject>();
        foreach (var m in phase.Models)
        {
            var model = await MountLoads.Model(m.Path);
            if (model is null || model.IsError || !on.IsValid()) continue;
            var effect = new GameObject(Scene) { Name = "Effet de sort", Flags = GameObjectFlags.NotSaved | GameObjectFlags.NotNetworked };
            effect.SetParent(on, false);
            effect.WorldPosition = Point(on, m.Slot);
            effect.WorldScale = Math.Clamp(m.Scale, 0.05f, 20);
            // On a bone attachment the effect follows the hand, head or chest through the animation.
            if (Body(on) is { } body && Attachment(body, m.Slot) is { } attachment)
            {
                var follow = effect.Components.Create<FollowAttachment>();
                follow.Target = body; follow.Attachment = attachment;
            }
            var renderer = effect.Components.Create<SkinnedModelRenderer>();
            renderer.Model = model; renderer.UseAnimGraph = false; renderer.Sequence.Name = "idle"; renderer.Sequence.Looping = true;
            renderer.CreateBoneObjects = true; // emitters ride the effect's animated bones
            // Particles of the effect model (flames, sparkles, smoke), as the mount describes them.
            var info = await MountLoads.Prefab(m.Info);
            if (info?.GetMetadata("WowData") is { } json && particles.IsValid())
            {
                var asset = System.Text.Json.JsonSerializer.Deserialize<WorldAsset>(json, Json);
                if (asset?.Emitters?.Count > 0 || asset?.Ribbons?.Count > 0) particles.Build(effect, asset);
            }
            if (seconds > 0) _ = DestroyLater(effect, seconds);
            created.Add(effect);
        }
        return created;
    }

    // The character's own renderer: the object itself, else its body below (not effects, weapons or a mount).
    private static SkinnedModelRenderer Body(GameObject on)
    {
        if (on.Components.Get<SkinnedModelRenderer>() is { } own && own.IsValid()) return own;
        return on.Components.GetAll<SkinnedModelRenderer>(FindMode.InDescendants)
            .FirstOrDefault(r => r.GameObject.Name is not ("Effet de sort" or "Objet porté" or "Arme" or "Monture"));
    }

    /// <summary>The model attachment for a client attachment id: the id itself on WoW models (spell hands 21/22 fall
    /// back to the hands 2/1, the head 20 to the helm 11), the matching points of the s&amp;box citizen.</summary>
    private static string Attachment(SkinnedModelRenderer renderer, int slot)
    {
        if (renderer.Model is null) return null;
        int[] wow = slot switch { 21 => new[] { 21, 2 }, 22 => new[] { 22, 1 }, 20 => new[] { 20, 11 }, 18 => new[] { 18, 11 }, _ => new[] { slot } };
        foreach (var id in wow) if (renderer.GetAttachment("attach_" + id) is not null) return "attach_" + id;
        string[] citizen = slot switch
        {
            1 or 22 => new[] { "hand_R" }, 2 or 21 => new[] { "hand_L" }, 11 or 18 or 20 => new[] { "hat", "eyes" },
            15 or 16 or 23 or 34 => new[] { "chest" }, _ => Array.Empty<string>(),
        };
        return citizen.FirstOrDefault(name => renderer.GetAttachment(name) is not null);
    }

    // A point of the character: its attachment, else a height proportional to the model (base, chest, head).
    private static Vector3 Point(GameObject on, int slot)
    {
        var body = Body(on);
        if (body.IsValid())
        {
            if (Attachment(body, slot) is { } name && body.GetAttachment(name) is { } at) return at.Position;
            var bounds = body.Bounds;
            float height = bounds.Size.z;
            float fraction = slot switch { 19 => 0, 11 or 18 or 20 => 0.92f, 15 or 16 or 34 => 0.6f, _ => 0.55f };
            return new Vector3(body.WorldPosition.x, body.WorldPosition.y, bounds.Mins.z + height * fraction);
        }
        return on.WorldPosition + Vector3.Up * (slot == 19 ? 0 : 40);
    }

    private static async Task DestroyLater(GameObject o, float seconds)
    {
        await GameTask.DelaySeconds(seconds);
        if (o.IsValid()) o.Destroy();
    }
}
