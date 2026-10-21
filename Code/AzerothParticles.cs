using Sandbox;
using System;
using System.Collections.Generic;
using System.Linq;

namespace WoWForever;

/// <summary>The client's M2 particle emitters (braziers, torches, chimneys, magic) near the camera, as s&amp;box
/// particle effects attached to the placed models. Sprite sheets come from the mount and are cut into frames here.</summary>
public sealed class AzerothParticles : Component
{
    /// <summary>Source units (yards).</summary>
    [Property] public float Distance { get; set; } = 160;
    [Property] public int MaxEffects { get; set; } = 64;
    [Property] public int MaxEmitters { get; set; } = 192;
    [Property] public int MaxParticlesPerEmitter { get; set; } = 160;

    public int LiveEffects => live.Count;
    public int LiveEmitters { get; private set; }
    public string LastError { get; private set; } = "";

    private const float Inches = AzerothWorld.InchesPerSourceUnit;
    private readonly Dictionary<string, (GameObject Root, int Emitters)> live = new();
    private readonly Dictionary<string, Texture> sheets = new();
    private readonly Dictionary<(string, int, int), Sprite> sprites = new();
    private readonly HashSet<string> broken = new();
    private TimeUntil nextRefresh;
    private AzerothWorld world;
    private AzerothDetails details;

    protected override void OnUpdate()
    {
        if (Scene.IsEditor || !nextRefresh) return;
        nextRefresh = 0.5f;
        if (!world.IsValid()) world = Scene.GetAllComponents<AzerothWorld>().FirstOrDefault();
        if (!details.IsValid()) details = Scene.GetAllComponents<AzerothDetails>().FirstOrDefault();
        if (!world.IsValid() || !details.IsValid() || !world.Camera.IsValid()) return;
        // A teleport rebuilds the models: their effects went with them, even where the placement ids come back.
        foreach (var key in live.Where(e => !e.Value.Root.IsValid()).Select(e => e.Key).ToList()) live.Remove(key);
        var camera = world.Camera.WorldPosition;
        float range = Distance * Inches;
        var wanted = details.Effects
            .Where(e => e.Value.Object.IsValid())
            .Select(e => (e.Key, e.Value.Object, e.Value.Asset, Distance: (e.Value.Object.WorldPosition - camera).Length))
            .Where(e => e.Distance <= range + (live.ContainsKey(e.Key) ? 4 * Inches : 0))
            .OrderBy(e => e.Distance).ToList();
        var keep = new HashSet<string>(); int emitters = 0;
        foreach (var w in wanted)
        {
            if (keep.Count >= MaxEffects) break;
            int cost = w.Asset.Emitters.Count + (w.Asset.Ribbons?.Count ?? 0) + (w.Asset.Billboards?.Count ?? 0);
            if (emitters + cost > MaxEmitters) continue;
            emitters += cost; keep.Add(w.Key);
        }
        foreach (var key in live.Keys.Where(k => !keep.Contains(k)).ToList())
        {
            if (live[key].Root.IsValid()) live[key].Root.Destroy();
            live.Remove(key);
        }
        int created = 0;
        foreach (var w in wanted)
        {
            if (!keep.Contains(w.Key) || live.ContainsKey(w.Key) || created >= 8) continue;
            try { live[w.Key] = Build(w.Object, w.Asset); created++; }
            catch (Exception error) { keep.Remove(w.Key); LastError = error.Message; }
        }
        LiveEmitters = live.Values.Sum(e => e.Emitters);
    }

    protected override void OnDisabled() => Clear();
    protected override void OnDestroy() => Clear();

    private void Clear()
    {
        foreach (var e in live.Values) if (e.Root.IsValid()) e.Root.Destroy();
        live.Clear(); LiveEmitters = 0;
    }

    /// <summary>Attaches a model's emitters (for example a spell effect's) under it; returns the root and emitter count.</summary>
    public (GameObject, int) Build(GameObject model, WorldAsset asset)
    {
        var root = new GameObject(Scene) { Name = "WoW particles", Parent = model, Flags = GameObjectFlags.NotSaved | GameObjectFlags.NotNetworked };
        root.LocalPosition = Vector3.Zero; root.LocalRotation = Rotation.Identity;
        int count = 0;
        foreach (var e in asset.Emitters)
        {
            if (broken.Contains(e.Texture)) continue;
            var sprite = SpriteOf(e);
            if (sprite is null) continue;
            var skinned = model.Components.Get<SkinnedModelRenderer>();
            var go = Emitter(root, e, sprite, model.WorldScale.x, skinned.IsValid() ? e.Position : e.Placed ?? e.Position);
            // Animated models carry their emitters on bones ("skin_<index>", identity bind: model-space offsets). The
            // emitter follows the bone's position and rotation but not its scale: spell bones are often keyed to zero.
            if (skinned.IsValid() && e.Bone >= 0)
            {
                skinned.CreateBoneObjects = true;
                var bone = model.Children.SelectMany(c => c.GetAllObjects(true)).FirstOrDefault(o => o.Name == "skin_" + e.Bone);
                if (bone.IsValid()) go.Components.Create<FollowBone>().Set(bone, go.LocalPosition);
            }
            count++;
        }
        foreach (var r in asset.Ribbons ?? new())
        {
            var skinned = model.Components.Get<SkinnedModelRenderer>();
            var go = Ribbon(root, r, model.WorldScale.x, skinned.IsValid() ? r.Position : r.Placed ?? r.Position);
            if (go is null) continue;
            if (skinned.IsValid() && r.Bone >= 0)
            {
                skinned.CreateBoneObjects = true;
                var bone = model.Children.SelectMany(c => c.GetAllObjects(true)).FirstOrDefault(o => o.Name == "skin_" + r.Bone);
                if (bone.IsValid()) go.Components.Create<FollowBone>().Set(bone, go.LocalPosition);
            }
            count++;
        }
        // Glow cards of lamps and torches: the client turns them toward the camera, so they always show their face.
        foreach (var b in asset.Billboards ?? new())
        {
            var texture = Texture.Load(b.Texture);
            if (texture is null || texture.IsError) continue;
            var go = new GameObject(Scene) { Name = "Halo", Parent = root, Flags = GameObjectFlags.NotSaved | GameObjectFlags.NotNetworked };
            go.LocalPosition = V(b.Position) * Inches;
            var card = go.Components.Create<SpriteRenderer>(false);
            card.Texture = texture;
            card.Size = new Vector2(b.Size, b.Size) * Inches;
            card.Billboard = SpriteRenderer.BillboardMode.Always;
            card.Additive = b.Blend >= 3;
            card.Color = Color.White.WithAlpha(Math.Clamp(b.Opacity, 0, 1));
            card.Lighting = !b.Unlit; card.Shadows = false; card.Opaque = false; card.DepthFeather = 8;
            card.Enabled = true;
            count++;
        }
        return (root, count);
    }

    // The client's ribbon: a strip as tall as its heights above and below the bone, fading over its lifetime.
    private GameObject Ribbon(GameObject root, RibbonInfo r, float scale, float[] position)
    {
        var texture = Texture.Load(r.Texture);
        if (texture is null || texture.IsError) return null;
        var go = new GameObject(Scene) { Name = "Ribbon", Parent = root, Flags = GameObjectFlags.NotSaved | GameObjectFlags.NotNetworked };
        go.LocalPosition = V(position) * Inches;
        var trail = go.Components.Create<TrailRenderer>(false);
        trail.LifeTime = Math.Clamp(r.Lifetime, 0.05f, 5);
        trail.MaxPoints = Math.Clamp((int)(Math.Max(r.EdgesPerSecond, 10) * trail.LifeTime) + 2, 8, 64);
        trail.PointDistance = 2;
        var width = new Curve(); width.AddPoint(0, (r.Above + r.Below) * Inches * scale); width.AddPoint(1, (r.Above + r.Below) * Inches * scale);
        trail.Width = width;
        var color = r.Color is { Length: 3 } ? new Color(r.Color[0], r.Color[1], r.Color[2]) : Color.White;
        var g = new Gradient(); g.AddColor(0, color); g.AddColor(1, color); g.AddAlpha(0, Math.Clamp(r.Alpha, 0, 1)); g.AddAlpha(1, 0);
        trail.Color = g;
        trail.Texturing = trail.Texturing with { Texture = texture, Clamp = true };
        trail.BlendMode = r.Blend >= 3 ? BlendMode.Lighten : BlendMode.Normal; // glows brighten what is behind
        trail.Opaque = false; trail.CastShadows = false;
        trail.Enabled = true;
        return go;
    }

    private GameObject Emitter(GameObject root, ParticleEmitterInfo e, Sprite sprite, float scale, float[] position)
    {
        var go = new GameObject(Scene) { Name = "Emitter", Parent = root, Flags = GameObjectFlags.NotSaved | GameObjectFlags.NotNetworked };
        go.LocalPosition = V(position) * Inches;
        float life = Math.Clamp(e.Lifespan, 0.05f, 30);
        var fx = go.Components.Create<ParticleEffect>(false);
        fx.MaxParticles = Math.Clamp((int)MathF.Ceiling(e.Rate * life * 1.3f) + 2, 4, MaxParticlesPerEmitter);
        fx.Lifetime = life;
        fx.PreWarm = MathF.Min(life, 4);
        // Scale keys are in yards at birth, mid-life and death.
        float unit = Inches * scale;
        fx.Scale = Curve(e.Scale, unit * 2); // the client's keys are half widths
        fx.ApplyShape = true;
        fx.ApplyAlpha = true;
        fx.Alpha = Curve(e.Alpha, 1);
        fx.ApplyColor = true;
        var g = new Gradient();
        var colors = e.Colors is { Length: 3 } ? e.Colors : new[] { new float[] { 1, 1, 1 }, new float[] { 1, 1, 1 }, new float[] { 1, 1, 1 } };
        for (int k = 0; k < 3; k++) g.AddColor(k * 0.5f, new Color(colors[k][0], colors[k][1], colors[k][2]));
        g.AddAlpha(0, 1); g.AddAlpha(1, 1);
        fx.Gradient = new ParticleGradient { Type = ParticleGradient.ValueType.Gradient, Evaluation = ParticleGradient.EvaluationType.Life, GradientA = g };
        fx.Tint = Color.White;
        bool additive = e.Blend >= 3;
        fx.Brightness = additive ? 2 : 1;
        // Emission along the model's up axis, opened by the vertical range.
        var up = root.WorldRotation.Up;
        float speed = e.Speed * Inches * scale;
        float spread = MathF.Sin(Math.Clamp(e.VerticalRange, 0, MathF.PI / 2));
        fx.InitialVelocity = new ParticleVector3 { X = up.x * speed, Y = up.y * speed, Z = up.z * speed };
        fx.StartVelocity = new ParticleFloat(0, MathF.Max(MathF.Abs(speed) * MathF.Max(spread, e.SpeedVariation), 0));
        if (MathF.Abs(e.Gravity) > 1e-3f)
        {
            fx.Force = true;
            fx.ForceSpace = ParticleEffect.SimulationSpace.World;
            fx.ForceDirection = Vector3.Down * e.Gravity * Inches * scale;
            fx.ForceScale = 1;
        }
        fx.ApplyRotation = true;
        fx.InitialRoll = new ParticleFloat(0, 360);
        fx.Enabled = true;

        ParticleEmitter emitter;
        var area = new Vector3(e.AreaWidth, e.AreaLength, 0) * unit;
        if (area.Length > 1) { var b = go.Components.Create<ParticleBoxEmitter>(false); b.Size = area + Vector3.Up; emitter = b; }
        else { var s = go.Components.Create<ParticleSphereEmitter>(false); s.Radius = 0; s.Velocity = 0; emitter = s; }
        emitter.Loop = true;
        emitter.Rate = MathF.Min(e.Rate, MaxParticlesPerEmitter / life);
        emitter.Duration = 1;
        emitter.Enabled = true;

        var renderer = go.Components.Create<ParticleSpriteRenderer>(false);
        renderer.Sprite = sprite;
        renderer.Additive = additive;
        renderer.Lighting = !additive;
        renderer.Shadows = false;
        renderer.DepthFeather = 4;
        renderer.Alignment = ParticleSpriteRenderer.BillboardAlignment.LookAtCamera;
        renderer.Enabled = true;
        return go;
    }

    private static ParticleFloat Curve(float[] keys, float scale)
    {
        if (keys is not { Length: 3 }) return scale;
        var c = new Curve();
        c.AddPoint(0, keys[0] * scale); c.AddPoint(0.5f, keys[1] * scale); c.AddPoint(1, keys[2] * scale);
        return new ParticleFloat { Type = ParticleFloat.ValueType.Curve, Evaluation = ParticleFloat.EvaluationType.Life, CurveA = c };
    }

    /// <summary>The sheet's cells played over the particle's life (WoW sprite sheets are rows x columns).</summary>
    private Sprite SpriteOf(ParticleEmitterInfo e)
    {
        int rows = Math.Clamp(e.Rows, 1, 16), columns = Math.Clamp(e.Columns, 1, 16);
        var key = (e.Texture, rows, columns);
        if (sprites.TryGetValue(key, out var cached)) return cached;
        if (!sheets.TryGetValue(e.Texture, out var sheet))
        {
            sheet = Texture.Load(e.Texture);
            if (sheet is null || sheet.IsError || sheet.Width <= 1) { broken.Add(e.Texture); return null; }
            sheets[e.Texture] = sheet;
        }
        Sprite sprite;
        if (rows * columns == 1) sprite = Sprite.FromTexture(sheet);
        else
        {
            var pixels = sheet.GetPixels(0);
            int w = sheet.Width / columns, h = sheet.Height / rows;
            var frames = new List<Texture>();
            for (int cell = 0; cell < rows * columns; cell++)
            {
                int cx = cell % columns * w, cy = cell / columns * h;
                var data = new byte[w * h * 4];
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        var p = pixels[(cy + y) * sheet.Width + cx + x]; int o = (y * w + x) * 4;
                        data[o] = p.r; data[o + 1] = p.g; data[o + 2] = p.b; data[o + 3] = p.a;
                    }
                frames.Add(Texture.Create(w, h).WithData(data).WithMips().Finish());
            }
            sprite = Sprite.FromTextures(frames, rows * columns / Math.Clamp(e.Lifespan, 0.05f, 30));
            if (sprite.Animations.Count > 0) sprite.Animations[0].LoopMode = Sprite.LoopMode.Loop;
        }
        sprites[key] = sprite;
        return sprite;
    }

    private static Vector3 V(float[] v) => v?.Length >= 3 ? new(v[0], v[1], v[2]) : Vector3.Zero;
}

/// <summary>Keeps an emitter on an animated bone: the bone's position and rotation, the emitter's own scale.</summary>
public sealed class FollowBone : Component
{
    private GameObject bone;
    private Vector3 offset;

    public void Set(GameObject target, Vector3 local) { bone = target; offset = local; Place(); }

    protected override void OnPreRender() => Place();

    private void Place()
    {
        if (!bone.IsValid()) return;
        WorldPosition = bone.WorldPosition + bone.WorldRotation * (offset * GameObject.Parent.WorldScale.x);
        WorldRotation = bone.WorldRotation;
    }
}
