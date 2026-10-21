using Sandbox;
using System.Linq;
using System.Threading.Tasks;

namespace WoWForever;

/// <summary>Optional native s&box player over the streamed world. The camera remains the world's camera.
/// The player is held until terrain and nearby collision meshes are available after a teleport.</summary>
public sealed class AzerothPlayer : Component
{
    public sealed class CharacterChoice { public string Key { get; set; } public string Title { get; set; } public string Path { get; set; } public int Race { get; set; } public int Sex { get; set; } }
    public System.Collections.Generic.List<CharacterChoice> Characters { get; private set; } = new();
    /// <summary>The WoW character's equipment (slot → item), kept across races and sexes like a transmogrification.</summary>
    public System.Collections.Generic.Dictionary<int, uint> Equipment { get; } = new();
    /// <summary>Chosen customizations per character (option → choice).</summary>
    public System.Collections.Generic.Dictionary<string, System.Collections.Generic.Dictionary<uint, uint>> Customization { get; } = new();
    /// <summary>Death chosen by the player (there is no real death): the client's ghost aura on the character, the
    /// grey world of the dead, and the spirit healers and ghosts only the dead can see.</summary>
    public bool Dead { get; private set; }
    public void SetDead(bool dead) { Dead = dead; _ = ApplyGhost(); }
    private readonly System.Collections.Generic.List<GameObject> ghostEffects = new();
    private ColorAdjustments deathView;
    private Model bodyModel;
    /// <summary>The dressing of the current WoW character, null for the s&amp;box citizen.</summary>
    public WowAppearance Appearance { get; private set; }
    public sealed class MountDisplay { public uint Id { get; set; } public string Path { get; set; } public float Scale { get; set; } }
    public sealed class MountChoice { public uint Mount { get; set; } public string Name { get; set; } public System.Collections.Generic.List<MountDisplay> Displays { get; set; } = new(); }
    /// <summary>The client's mounts (Mount.db2 via MountXDisplay), served by the mount.</summary>
    public System.Collections.Generic.List<MountChoice> Mounts { get; private set; } = new();
    public string MountPath { get; private set; }
    public bool Riding => mountRenderer.IsValid();
    private Task<PrefabFile> mountCatalog;
    private Task<Model> mountJob;
    private float mountScale = 1;
    private GameObject mountObject;
    private SkinnedModelRenderer mountRenderer;
    public bool LoadingModel => modelJob is { IsCompleted: false };
    private Task<PrefabFile> characterCatalog;
    [Property] public string PlayerModel { get; set; } = "models/citizen/citizen.vmdl";
    public bool Walking { get; private set; }
    public bool WantsWalking { get; private set; } = true;
    public string LastError { get; private set; } = "";
    public PlayerController Controller { get; private set; }
    /// <summary>The character's body (its renderer's object), where spell effects attach.</summary>
    public GameObject Body => renderer.IsValid() ? renderer.GameObject : null;
    /// <summary>The casting pose of WoW characters while a spell is prepared.</summary>
    public void PlayCast(bool on) { if (wowAnimator.IsValid()) wowAnimator.Casting = on; }
    private GameObject pawn;
    private SkinnedModelRenderer renderer;
    private Task<Model> modelJob;
    private System.Threading.CancellationTokenSource dressing;
    private int revision = -1;
    private AzerothWorld world;
    private AzerothDetails details;
    private AzerothFlight flight;
    private WowCharacterAnimator wowAnimator;
    private int readyFrames;
    private float readyFor;

    /// <summary>The playable character the player made, kept between sessions (model, customization, equipment).</summary>
    private sealed class SavedCharacter
    {
        public string Model { get; set; }
        public System.Collections.Generic.Dictionary<int, uint> Equipment { get; set; } = new();
        public System.Collections.Generic.Dictionary<string, System.Collections.Generic.Dictionary<uint, uint>> Customization { get; set; } = new();
    }
    private const string SaveFile = "wowforever_personnage.json";
    /// <summary>The WoW character worn when nothing was chosen yet.</summary>
    public const string DefaultCharacter = "mount://wowforever/characters/human_male.vmdl";

    private void Load()
    {
        SavedCharacter saved = null;
        try { saved = FileSystem.Data.ReadJson<SavedCharacter>(SaveFile, null); } catch (System.Exception) { }
        PlayerModel = string.IsNullOrEmpty(saved?.Model) ? DefaultCharacter : saved.Model;
        foreach (var (slot, item) in saved?.Equipment ?? new()) Equipment[slot] = item;
        foreach (var (key, choices) in saved?.Customization ?? new()) Customization[key] = choices;
    }

    public void Save()
    {
        try { FileSystem.Data.WriteJson(SaveFile, new SavedCharacter { Model = PlayerModel, Equipment = Equipment, Customization = Customization }); }
        catch (System.Exception error) { Log.Warning($"Personnage non enregistré : {error.Message}"); }
    }

    protected override void OnStart()
    {
        Load();
        modelJob = MountLoads.Model(PlayerModel);
        characterCatalog = MountLoads.Prefab("mount://wowforever/characters.prefab");
        mountCatalog = MountLoads.Prefab("mount://wowforever/mounts.prefab");
    }

    public void ToggleMode()
    {
        WantsWalking = !WantsWalking;
        if (!WantsWalking) StopWalking();
    }

    public void SetModel(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        PlayerModel = path; LastError = ""; modelJob = MountLoads.Model(path);
        Save();
    }

    /// <summary>Ride a mount display (null to dismount). The rider sits on the mount's own seat attachment.</summary>
    public void SetMount(string path, float scale = 1)
    {
        MountPath = path; mountScale = scale <= 0 ? 1 : scale;
        if (mountObject.IsValid()) mountObject.Destroy();
        mountObject = null; mountRenderer = null;
        mountJob = string.IsNullOrEmpty(path) ? null : MountLoads.Model(path);
        if (path is null) ApplyMovement();
    }

    // Release the old camera controller before a destination writes the new camera position.
    public void PrepareTeleport() => StopWalking();

    protected override void OnUpdate()
    {
        if (characterCatalog is { IsCompleted: true })
        {
            if (characterCatalog.IsCompletedSuccessfully && characterCatalog.Result?.GetMetadata("WowData") is { } json)
                Characters = System.Text.Json.JsonSerializer.Deserialize<System.Collections.Generic.List<CharacterChoice>>(json) ?? new();
            characterCatalog = null;
        }
        if (mountCatalog is { IsCompleted: true })
        {
            if (mountCatalog.IsCompletedSuccessfully && mountCatalog.Result?.GetMetadata("WowData") is { } json)
                Mounts = System.Text.Json.JsonSerializer.Deserialize<System.Collections.Generic.List<MountChoice>>(json) ?? new();
            mountCatalog = null;
        }
        if (!world.IsValid()) world = Scene.GetAllComponents<AzerothWorld>().FirstOrDefault();
        if (!details.IsValid()) details = Scene.GetAllComponents<AzerothDetails>().FirstOrDefault();
        if (!flight.IsValid()) flight = Scene.GetAllComponents<AzerothFlight>().FirstOrDefault();
        if (!world.IsValid() || !world.Camera.IsValid()) return;
        if (revision != world.Revision) { StopWalking(); revision = world.Revision; }
        bool ready = !world.Warming && world.GroundReady && details is { Warming: false, CollisionsReady: true };
        // ModelCollider registers its shape during the engine's following physics update.
        // A completed resource alone is not enough to trace the new city's floor this frame.
        if (ready) { readyFrames++; readyFor += Time.Delta; } else { readyFrames = 0; readyFor = 0; }
        ready = ready && readyFrames >= 3 && readyFor >= .15f;
        if (WantsWalking && !Walking && ready)
        {
            // The third-person camera sits behind the desired spawn. Trace beneath the explorer itself.
            var from = flight.IsValid() && flight.Enabled ? flight.AvatarPosition : world.Camera.WorldPosition;
            var ground = Scene.Trace.Ray(from + Vector3.Up * 72, from - Vector3.Up * 36000).Run();
            if (ground.Hit && ground.Normal.z > 0.5f) StartWalking(ground.EndPosition + Vector3.Up * 4, world.Camera.WorldRotation.Angles());
            else LastError = "Aucun sol accessible ici : utilise le vol libre ou une autre destination.";
        }
        if (modelJob is { IsCompleted: true } && renderer.IsValid())
        {
            if (modelJob.IsCompletedSuccessfully && modelJob.Result is { IsError: false } model)
            {
                dressing?.Cancel();
                var previousDresser = renderer.Components.Get<Dresser>();
                if (previousDresser.IsValid()) { previousDresser.CancelDressing(); previousDresser.Clear(); previousDresser.Destroy(); }
                renderer.Model = model; bodyModel = model;
                bool wowModel = PlayerModel.StartsWith("mount://wowforever/characters/");
                Controller.UseAnimatorControls = !wowModel;
                renderer.UseAnimGraph = !wowModel;
                if (wowAnimator.IsValid()) wowAnimator.Destroy();
                if (Appearance.IsValid()) Appearance.Destroy();
                Appearance = null;
                if (wowModel)
                {
                    wowAnimator = renderer.Components.Create<WowCharacterAnimator>();
                    wowAnimator.Renderer = renderer; wowAnimator.Controller = Controller;
                    string key = System.IO.Path.GetFileNameWithoutExtension(PlayerModel);
                    if (!Customization.TryGetValue(key, out var choices)) Customization[key] = choices = new();
                    Appearance = renderer.Components.Create<WowAppearance>(false);
                    Appearance.Renderer = renderer; Appearance.Key = key; Appearance.Choices = choices; Appearance.Equipment = Equipment;
                    Appearance.Changed += Save;
                    Appearance.Enabled = true;
                }
                Controller.BodyHeight = wowModel ? System.Math.Clamp(model.Bounds.Size.z, 40, 120) : 72;
                Controller.BodyRadius = wowModel ? System.Math.Clamp(Controller.BodyHeight * .18f, 10, 24) : 16;
                Controller.DuckedHeight = Controller.BodyHeight * .5f;
                Controller.WalkSpeed = wowModel ? 100 : 180; Controller.RunSpeed = wowModel ? 250 : 320;
                if (PlayerModel == "models/citizen/citizen.vmdl") _ = Dress(renderer);
                if (Dead) _ = ApplyGhost();
            }
            else LastError = "Personnage indisponible : " + PlayerModel;
            modelJob = null;
        }
        if (!Controller.IsValid() || !Walking) return;
        UpdateMount();
        Controller.UseInputControls = ready && flight?.MenuOpen != true;
        Controller.UseLookControls = flight?.MenuOpen != true;
        Controller.ThirdPerson = flight?.ThirdPerson ?? true;
        if (Controller.Body.IsValid()) Controller.Body.MotionEnabled = ready;
    }

    private void StartWalking(Vector3 position, Angles angles)
    {
        pawn = new GameObject(Scene) { Name = "Explorateur", Flags = GameObjectFlags.NotSaved | GameObjectFlags.NotNetworked };
        pawn.WorldPosition = position;
        var body = new GameObject(Scene) { Name = "Personnage" }; body.SetParent(pawn, false);
        renderer = body.Components.Create<SkinnedModelRenderer>();
        Controller = pawn.Components.Create<PlayerController>();
        Controller.Renderer = renderer;
        Controller.EyeAngles = angles;
        Controller.WalkSpeed = 180; Controller.RunSpeed = 320; Controller.JumpSpeed = 280;
        Controller.ThirdPerson = true; Controller.ToggleCameraModeButton = "";
        Controller.RunByDefault = true; // running, as in the client; the run key walks
        Controller.UseCameraControls = true;
        Walking = true; LastError = "";
        modelJob ??= MountLoads.Model(PlayerModel);
    }

    private void UpdateMount()
    {
        if (mountJob is { IsCompleted: true } && pawn.IsValid())
        {
            if (mountJob.IsCompletedSuccessfully && mountJob.Result is { IsError: false } model)
            {
                mountObject = new GameObject(Scene) { Name = "Monture", Flags = GameObjectFlags.NotSaved | GameObjectFlags.NotNetworked };
                mountObject.SetParent(pawn, false);
                mountObject.LocalScale = mountScale;
                mountRenderer = mountObject.Components.Create<SkinnedModelRenderer>();
                mountRenderer.Model = model; mountRenderer.UseAnimGraph = false;
                var animator = mountObject.Components.Create<WowCharacterAnimator>();
                animator.Renderer = mountRenderer; animator.Controller = Controller;
            }
            else LastError = "Monture indisponible : " + MountPath;
            mountJob = null;
            ApplyMovement();
        }
        if (!mountRenderer.IsValid() || !renderer.IsValid()) return;
        // The rider follows the seat of the animated mount, and plays the client's riding pose.
        var seat = mountRenderer.GetAttachment("mount");
        renderer.WorldPosition = seat?.Position ?? mountRenderer.WorldPosition + Vector3.Up * 50;
        if (wowAnimator.IsValid()) wowAnimator.Mounted = true;
        else { renderer.WorldRotation = Rotation.FromYaw(Controller.EyeAngles.yaw); renderer.Set("sit", 1); }
    }

    // Mounted players move at the client's mount speeds (+60 % running) with a capsule fitting the mount.
    private void ApplyMovement()
    {
        if (!Controller.IsValid()) return;
        bool wowModel = PlayerModel.StartsWith("mount://wowforever/characters/");
        if (mountRenderer.IsValid())
        {
            Controller.WalkSpeed = 160; Controller.RunSpeed = 420;
            Controller.BodyRadius = System.Math.Clamp(mountRenderer.Model.Bounds.Size.WithZ(0).Length * mountScale * .25f, 16, 48);
        }
        else
        {
            Controller.WalkSpeed = wowModel ? 100 : 180; Controller.RunSpeed = wowModel ? 250 : 320;
            Controller.BodyRadius = wowModel ? System.Math.Clamp(Controller.BodyHeight * .18f, 10, 24) : 16;
            if (renderer.IsValid()) { renderer.LocalPosition = Vector3.Zero; if (!wowModel) renderer.Set("sit", 0); }
            if (wowAnimator.IsValid()) wowAnimator.Mounted = false;
        }
    }

    private void StopWalking()
    {
        if (mountObject.IsValid()) mountObject.Destroy();
        mountObject = null; mountRenderer = null;
        if (!string.IsNullOrEmpty(MountPath)) mountJob = MountLoads.Model(MountPath);
        dressing?.Cancel(); dressing?.Dispose(); dressing = null;
        if (Controller.IsValid()) { Controller.UseCameraControls = false; Controller.UseInputControls = false; }
        Walking = false;
        readyFrames = 0; readyFor = 0;
        if (pawn.IsValid()) pawn.Destroy();
        pawn = null; Controller = null; renderer = null;
    }

    public void Rebase(Vector3 delta) { if (pawn.IsValid()) pawn.WorldPosition += delta; }

    // Spell 8326 "Ghost" is the client's own aura for dead players; the world loses its colours as in the client.
    private async Task ApplyGhost()
    {
        foreach (var effect in ghostEffects) if (effect.IsValid()) effect.Destroy();
        ghostEffects.Clear();
        if (world?.Camera is { } camera)
        {
            if (Dead)
            {
                deathView ??= camera.Components.GetOrCreate<ColorAdjustments>();
                deathView.Saturation = 0; deathView.Brightness = 1.1f; deathView.Contrast = 1.05f;
            }
            else if (deathView.IsValid()) { deathView.Destroy(); deathView = null; }
        }
        if (!renderer.IsValid()) return;
        if (Dead)
        {
            // Night elves return as a wisp; the other races keep their body, drawn as a pale, glowing spirit.
            if (PlayerModel.Contains("/nightelf_") && await WispModel() is { IsError: false } wisp && renderer.IsValid() && Dead) renderer.Model = wisp;
            else GhostMaterials();
        }
        else
        {
            if (bodyModel is not null && renderer.Model != bodyModel) renderer.Model = bodyModel;
            renderer.ClearMaterialOverrides(); renderer.Tint = Color.White;
            if (Appearance.IsValid()) _ = Appearance.Apply();
        }
        var spells = Scene.GetAllComponents<AzerothSpells>().FirstOrDefault();
        if (Dead && renderer.IsValid() && spells.IsValid()) ghostEffects.AddRange(await spells.Aura(renderer.GameObject, 8326));
    }

    // Every material of the body drawn additively in the spirit's blue-white, over its own texture.
    private void GhostMaterials()
    {
        var materials = renderer.Materials;
        for (int i = 0; i < materials.Count; i++)
        {
            var source = materials.GetOverride(i) ?? materials.GetOriginal(i);
            if (source is null) continue;
            var ghost = Material.Create($"wowghost_{GameObject.Id:N}_{i}.vmat", "shaders/complex.shader");
            ghost.SetFeature("F_TRANSLUCENT", 1); ghost.SetFeature("F_ADDITIVE_BLEND", 1);
            if (source.GetTexture("g_tColor") is { } color) ghost.Set("g_tColor", color);
            materials.SetOverride(i, ghost);
        }
        renderer.Tint = new Color(0.55f, 0.75f, 1f);
    }

    private static Task<Model> wisp;
    private static async Task<Model> WispModel()
    {
        if (wisp is null)
        {
            var catalog = await MountLoads.Prefab("mount://wowforever/creatures.prefab");
            var json = catalog?.GetMetadata("WowData") ?? "[]";
            var path = System.Text.Json.JsonDocument.Parse(json).RootElement.EnumerateArray()
                .Select(e => e.GetProperty("Path").GetString()).FirstOrDefault(p => p.Contains("/wisp/"));
            wisp = path is null ? System.Threading.Tasks.Task.FromResult<Model>(null) : MountLoads.Model(path);
        }
        return await wisp;
    }
    private async Task Dress(SkinnedModelRenderer target)
    {
        dressing?.Cancel(); dressing?.Dispose(); dressing = new();
        try
        {
            if (!target.IsValid()) return;
            var outfit = ClothingContainer.CreateFromLocalUser();
            var dresser = Dresser.GetOrCreate(target);
            dresser.UpdateAppearance(outfit);
            await dresser.ApplyAsync(outfit, dressing.Token);
        }
        catch (System.OperationCanceledException) { }
        catch (System.Exception error) { Log.Warning($"Tenue indisponible : {error.Message}"); }
    }
    protected override void OnDestroy() => StopWalking();
}
