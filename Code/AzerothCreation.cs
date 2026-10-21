using Sandbox;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace WoWForever;

/// <summary>The character creation screen as the client shows it: the race's own scene (interface/glues/models/ui_&lt;race&gt;),
/// animated with its particles, seen from the scene's camera, with the character standing in it. Everything comes
/// from the mount; the screen is staged high above the world while it is open.</summary>
public sealed class AzerothCreation : Component
{
    public bool IsOpen { get; private set; }
    /// <summary>The character shown on the screen, dressed like the player's.</summary>
    public WowAppearance Preview { get; private set; }
    public string LastError { get; private set; } = "";

    // Races without a scene of their own in this client use their faction's neighbour.
    private static readonly Dictionary<string, string> Scenes = new()
    {
        ["human"] = "ui_human", ["dwarf"] = "ui_dwarf", ["gnome"] = "ui_gnome", ["nightelf"] = "ui_nightelf", ["draenei"] = "ui_draenei",
        ["orc"] = "ui_orc", ["scourge"] = "ui_scourge", ["tauren"] = "ui_tauren", ["troll"] = "ui_troll", ["bloodelf"] = "ui_bloodelf",
    };
    private const float Inches = AzerothWorld.InchesPerSourceUnit;
    private sealed class SceneCamera { public float[] Position { get; set; } public float[] Target { get; set; } public float[] Stand { get; set; } public float Fov { get; set; } }
    private sealed class SceneLight { public int Type { get; set; } public float[] Position { get; set; } public float[] Ambient { get; set; } public float[] Diffuse { get; set; } public float End { get; set; } }
    private sealed class SceneInfo { public List<SceneCamera> Cameras { get; set; } = new(); public List<SceneLight> Lights { get; set; } = new(); }

    private GameObject stage;
    private AzerothPlayer player;
    private AzerothWorld world;
    private Transform view;
    private float fieldOfView = 60, previousFov;
    private float turn;
    private Vector3 spot;
    private readonly List<GradientFog> hazes = new();
    private readonly List<(DirectionalLight Light, Color Color, Rotation Rotation)> suns = new();
    private Vector3? origin;
    private int version;
    private static readonly System.Text.Json.JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public void Open() { IsOpen = true; _ = Build(); }

    public void Close()
    {
        IsOpen = false; version++; origin = null;
        if (stage.IsValid()) stage.Destroy();
        stage = null; Preview = null;
        if (world?.Camera is { } camera && previousFov > 0) { camera.FieldOfView = previousFov; previousFov = 0; }
        foreach (var fog in hazes) if (fog.IsValid()) fog.Enabled = true;
        hazes.Clear();
        foreach (var (sun, color, rotation) in suns) if (sun.IsValid()) { sun.LightColor = color; sun.WorldRotation = rotation; }
        suns.Clear();
        if (player?.Controller is { } controller) controller.UseCameraControls = true;
        if (player?.Appearance is { } appearance) _ = appearance.Apply();
        player?.Save();
    }

    /// <summary>The character's facing on the screen, as the client's rotate buttons turn it.</summary>
    public void Turn(float degrees) => turn += degrees;

    /// <summary>Rebuilds the screen for the player's current race and sex.</summary>
    public void Refresh() { if (IsOpen) _ = Build(); }

    private async Task Build()
    {
        int mine = ++version;
        player ??= Scene.GetAllComponents<AzerothPlayer>().FirstOrDefault();
        world ??= Scene.GetAllComponents<AzerothWorld>().FirstOrDefault();
        if (player is null || world?.Camera is null) return;
        if (stage.IsValid()) stage.Destroy();
        if (previousFov <= 0) previousFov = world.Camera.FieldOfView;
        // The client's screens have no world haze: the explorer's fog is off while the screen is open.
        if (hazes.Count == 0) foreach (var fog in Scene.GetAllComponents<GradientFog>().Where(f => f.Enabled)) { fog.Enabled = false; hazes.Add(fog); }
        if (player.Controller.IsValid()) player.Controller.UseCameraControls = false;
        string key = System.IO.Path.GetFileNameWithoutExtension(player.PlayerModel);
        string race = key.Split('_')[0];
        if (!Scenes.TryGetValue(race, out var scene)) { LastError = "Choisis un personnage WoW."; return; }
        spot = Vector3.Zero;
        try
        {
            stage = new GameObject(Scene) { Name = "Écran de création", Flags = GameObjectFlags.NotSaved | GameObjectFlags.NotNetworked };
            // Staged once per opening above where the player stands (the camera is the screen's own after that).
            if (!origin.HasValue) origin = world.Camera.WorldPosition.WithZ(world.Camera.WorldPosition.z + 60000);
            stage.WorldPosition = origin.Value;
            // The race's scene, animated, with its particles.
            var sceneModel = await MountLoads.Model($"mount://wowforever/glues/{scene}_static.vmdl");
            var infoPrefab = await MountLoads.Prefab($"mount://wowforever/glues/{scene}.prefab");
            if (mine != version || !stage.IsValid()) return;
            if (sceneModel is { IsError: false })
            {
                var backdrop = new GameObject(Scene) { Name = "Décor", Parent = stage, Flags = GameObjectFlags.NotSaved | GameObjectFlags.NotNetworked };
                backdrop.LocalPosition = Vector3.Zero;
                // The scene as the client models it, in its resting pose (its own particles and lights are added below).
                var renderer = backdrop.Components.Create<ModelRenderer>();
                renderer.Model = sceneModel;
                var json = infoPrefab?.GetMetadata("WowData");
                if (json is not null && Scene.GetAllComponents<AzerothParticles>().FirstOrDefault() is { } particles
                    && System.Text.Json.JsonSerializer.Deserialize<WorldAsset>(json, Json) is { } asset && (asset.Emitters.Count > 0 || asset.Ribbons?.Count > 0 || asset.Billboards?.Count > 0))
                    particles.Build(backdrop, asset);
                var info = json is null ? null : System.Text.Json.JsonSerializer.Deserialize<SceneInfo>(json, Json);
                // The scene lights its character itself (its M2 lights); the world's sun is off meanwhile.
                foreach (var light in info?.Lights ?? new())
                {
                    if (light.Position is not { Length: 3 } || light.Diffuse is not { Length: 3 }) continue;
                    var lamp = new GameObject(Scene) { Name = "Lumière du décor", Parent = stage, Flags = GameObjectFlags.NotSaved | GameObjectFlags.NotNetworked };
                    var at = new Vector3(light.Position[0], light.Position[1], light.Position[2]);
                    var color = new Color(light.Diffuse[0], light.Diffuse[1], light.Diffuse[2]);
                    if (light.Type == 0) { lamp.Destroy(); continue; }
                    lamp.LocalPosition = at * Inches;
                    var point = lamp.Components.Create<PointLight>(); point.LightColor = color; point.Radius = Math.Max(1, light.End) * Inches;
                }
                // The world's sun takes the scene's directional light (colour and direction) while the screen is open.
                if (info?.Lights.FirstOrDefault(l => l.Type == 0) is { Position.Length: 3 } keyLight && suns.Count == 0)
                    foreach (var sun in Scene.GetAllComponents<DirectionalLight>().Where(l => l.Enabled && l.GameObject.Parent != stage))
                    {
                        suns.Add((sun, sun.LightColor, sun.WorldRotation));
                        var tint = new Vector3(keyLight.Ambient?[0] ?? 0, keyLight.Ambient?[1] ?? 0, keyLight.Ambient?[2] ?? 0) + new Vector3(keyLight.Diffuse[0], keyLight.Diffuse[1], keyLight.Diffuse[2]);
                        sun.LightColor = new Color(tint.x, tint.y, tint.z) * 2;
                        sun.WorldRotation = Rotation.LookAt(-new Vector3(keyLight.Position[0], keyLight.Position[1], keyLight.Position[2]).Normal);
                    }
                if (info?.Cameras.FirstOrDefault() is { Position.Length: 3, Target.Length: 3 } camera)
                {
                    var position = stage.WorldPosition + new Vector3(camera.Position[0], camera.Position[1], camera.Position[2]) * Inches;
                    var target = stage.WorldPosition + new Vector3(camera.Target[0], camera.Target[1], camera.Target[2]) * Inches;
                    view = new Transform(position, Rotation.LookAt((target - position).Normal));
                    // The client's field of view (radians) covers the scene's backdrop; the character stands on the floor
                    // under the camera's aim (computed by the mount).
                    fieldOfView = Math.Clamp(camera.Fov * 180 / MathF.PI * 0.72f, 20, 100);
                    if (camera.Stand is { Length: 3 } at) spot = new Vector3(at[0], at[1], at[2]) * Inches;
                }
                else view = new Transform(stage.WorldPosition + new Vector3(-220, 0, 60), Rotation.LookAt(Vector3.Forward));
            }
            // The character standing in the scene, facing the camera, dressed like the player.
            var body = await MountLoads.Model(player.PlayerModel);
            if (mine != version || !stage.IsValid() || body is null || body.IsError) return;
            var standing = new GameObject(Scene) { Name = "Personnage créé", Parent = stage, Flags = GameObjectFlags.NotSaved | GameObjectFlags.NotNetworked };
            standing.LocalPosition = spot;
            var preview = standing.Components.Create<SkinnedModelRenderer>();
            preview.Model = body; preview.UseAnimGraph = false; preview.Sequence.Name = "idle"; preview.Sequence.Looping = true;
            Preview = standing.Components.Create<WowAppearance>(false);
            Preview.Renderer = preview; Preview.Key = key; Preview.Equipment = player.Equipment;
            if (!player.Customization.TryGetValue(key, out var choices)) player.Customization[key] = choices = new();
            Preview.Choices = choices;
            Preview.Changed += player.Save;
            Preview.Enabled = true;
            turn = 0;
            // The client's per-model framing (ChrModel): the character's scale on this screen, the camera stepping back.
            var look = await WowItems.Character(key);
            if (mine != version || !standing.IsValid()) return;
            if (look is not null)
            {
                if (look.CustomizeScale > 0.05f) standing.LocalScale = look.CustomizeScale;
                // A negative offset steps the camera back.
                view = new Transform(view.Position + view.Rotation.Forward * look.CameraDistanceOffset * Inches, view.Rotation);
                // The whole character inside the view kept as the client aims it (turning or stepping back would show
                // past the scene's backdrop): a model taller than the view is shown smaller until its feet and head fit.
                float vertical = 2 * MathF.Atan(MathF.Tan(fieldOfView * MathF.PI / 360) / Math.Max(1, Screen.Aspect));
                float distance = Vector3.Dot(standing.WorldPosition - view.Position, view.Rotation.Forward);
                float aim = view.Position.z + view.Rotation.Forward.z * distance, feet = standing.WorldPosition.z;
                float fits = MathF.Tan(vertical / 2) * distance * 0.95f;
                float height = body.Bounds.Size.z * standing.LocalScale.z;
                if (distance > 1 && feet < aim && feet + height - aim > fits) standing.LocalScale *= Math.Max(0.3f, (aim + fits - feet) / height);
            }
            LastError = "";
        }
        catch (Exception error) { LastError = error.Message; }
    }

    protected override void OnPreRender()
    {
        if (!IsOpen || !stage.IsValid() || world?.Camera is not { } camera) return;
        camera.WorldPosition = view.Position; camera.WorldRotation = view.Rotation; camera.FieldOfView = fieldOfView;
        if (Preview?.Renderer is { } renderer && renderer.IsValid())
        {
            // Faces the camera (the mount's models face +Y), turned by the player.
            var toCamera = (view.Position - renderer.WorldPosition).WithZ(0).Normal;
            renderer.WorldRotation = Rotation.LookAt(toCamera) * Rotation.FromYaw(-90 + turn);
        }
    }

    protected override void OnDestroy() { if (stage.IsValid()) stage.Destroy(); }
}
