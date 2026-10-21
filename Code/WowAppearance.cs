using Sandbox;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace WoWForever;

/// <summary>A playable character's customization as the mount describes it (characters/&lt;key&gt;.prefab).</summary>
public sealed class WowCharacterInfo
{
    public sealed class Section { public int Type { get; set; } public int X { get; set; } public int Y { get; set; } public int Width { get; set; } public int Height { get; set; } }
    public sealed class Size { public int Width { get; set; } public int Height { get; set; } }
    public sealed class Element
    {
        public uint? Related { get; set; } public int? Geoset { get; set; } public string Texture { get; set; } public uint File { get; set; }
        public int? TextureType { get; set; } public int Layer { get; set; } public int Blend { get; set; } public uint SectionMask { get; set; }
    }
    public sealed class Choice { public uint Id { get; set; } public string Name { get; set; } public List<Element> Elements { get; set; } = new(); }
    public sealed class Option { public uint Id { get; set; } public string Name { get; set; } public uint Category { get; set; } public List<Choice> Choices { get; set; } = new(); }
    /// <summary>An option category of the client's creation screen (body, head, hair...), with its icons (atlas pieces).</summary>
    public sealed class Category { public uint Id { get; set; } public string Name { get; set; } public string Icon { get; set; } public string IconSelected { get; set; } }
    /// <summary>A class this race and sex can be, with its starting outfit (item ids).</summary>
    public sealed class ClassChoice { public uint Id { get; set; } public string Name { get; set; } public string File { get; set; } public List<uint> Items { get; set; } = new(); }
    public string Key { get; set; }
    public int Race { get; set; }
    public int Sex { get; set; }
    /// <summary>The client's creation screen scale for this model and its camera step back (yards).</summary>
    public float CustomizeScale { get; set; } = 1;
    public float CameraDistanceOffset { get; set; }
    public Size Layout { get; set; } = new();
    public List<Section> Sections { get; set; } = new();
    public List<Option> Options { get; set; } = new();
    public List<Category> Categories { get; set; } = new();
    public List<ClassChoice> Classes { get; set; } = new();
    public Dictionary<string, List<int>> BodyGroups { get; set; } = new();
    /// <summary>The mount's dressable version of the model (one material per geoset).</summary>
    public string Dressable { get; set; }
}

/// <summary>An item display as the mount describes it (items/&lt;display&gt;.prefab): attached models per race and sex,
/// the geosets it switches, the texture pieces it paints on the body and the groups a helmet hides.</summary>
public sealed class WowItemDisplay
{
    public sealed class Variant { public uint File { get; set; } public int? Sex { get; set; } public int? Race { get; set; } public int? Position { get; set; } public string Path { get; set; } }
    public sealed class ModelSet { public int Index { get; set; } public List<Variant> Variants { get; set; } = new(); }
    public sealed class Piece { public int Section { get; set; } public List<Variant> Variants { get; set; } = new(); }
    public sealed class Hide { public int Sex { get; set; } public int Race { get; set; } public int Group { get; set; } }
    public uint Display { get; set; }
    public int[] Geosets { get; set; } = new int[6];
    public List<string> Textures { get; set; } = new();
    public List<ModelSet> Models { get; set; } = new();
    public List<Piece> Pieces { get; set; } = new();
    public List<Hide> HideGroups { get; set; } = new();

    /// <summary>The variant made for this race and sex, else for the sex, else any.</summary>
    public static Variant Pick(IEnumerable<Variant> variants, int race, int sex, int? position = null) =>
        variants.Where(v => position is null || v.Position is null || v.Position == position)
            .OrderBy(v => (v.Race == race ? 0 : v.Race is null or 0 ? 1 : 3) + (v.Sex == sex ? 0 : v.Sex is null or > 1 ? 1 : 3))
            .FirstOrDefault(v => (v.Race is null or 0 || v.Race == race) && (v.Sex is null or > 1 || v.Sex == sex));
}

/// <summary>The client's wearable items and their displays, loaded once from the mount.</summary>
public static class WowItems
{
    public sealed class Entry { public uint Id { get; set; } public string Name { get; set; } public int Slot { get; set; } public int Quality { get; set; } public uint Display { get; set; } }
    private static Task<List<Entry>> all;
    private static Dictionary<uint, Entry> byId;
    private static readonly Dictionary<uint, Task<WowItemDisplay>> displays = new();
    private static readonly Dictionary<string, Task<WowCharacterInfo>> characters = new();
    private static readonly System.Text.Json.JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public static Task<List<Entry>> All => all ??= Load<List<Entry>>("mount://wowforever/items.prefab");
    public static List<Entry> Loaded => all is { IsCompletedSuccessfully: true } ? all.Result ?? new() : new();

    public static async Task<Entry> Item(uint id)
    {
        var list = await All;
        byId ??= list?.GroupBy(i => i.Id).ToDictionary(g => g.Key, g => g.First()) ?? new();
        return byId.GetValueOrDefault(id);
    }

    public static Task<WowItemDisplay> Display(uint id)
    {
        if (!displays.TryGetValue(id, out var task)) displays[id] = task = Load<WowItemDisplay>($"mount://wowforever/items/{id}.prefab");
        return task;
    }

    public static Task<WowCharacterInfo> Character(string key)
    {
        if (!characters.TryGetValue(key, out var task)) characters[key] = task = Load<WowCharacterInfo>($"mount://wowforever/characters/{key}.prefab");
        return task;
    }

    private static async Task<T> Load<T>(string path) where T : class
    {
        var prefab = await MountLoads.Prefab(path);
        var json = prefab?.GetMetadata("WowData");
        return json is null ? null : await GameTask.RunInThreadAsync(() => System.Text.Json.JsonSerializer.Deserialize<T>(json, Json));
    }

    /// <summary>Where an item sits: the slot it fills (robes count as chests, weapons by hand) and its attachment points.</summary>
    public static int SlotOf(int inventoryType) => inventoryType switch
    {
        20 => 5, 13 or 17 or 21 => 21, 14 or 22 or 23 => 22, 25 or 26 => 15, _ => inventoryType,
    };

    /// <summary>The client's attachment ids for an item's models: helm 11, shoulders 6 (left) and 5 (right), right hand 1,
    /// left hand 2, shield 0 (named "mount" on the mount's models), back 12.</summary>
    public static int[] Attachments(int inventoryType) => inventoryType switch
    {
        1 => new[] { 11 }, 3 => new[] { 6, 5 }, 13 or 17 or 21 or 25 or 26 => new[] { 1 }, 22 or 23 => new[] { 2 }, 14 => new[] { 0 }, 15 => new[] { 2 },
        _ => Array.Empty<int>(),
    };

    public static string AttachmentName(int id) => id == 0 ? "mount" : "attach_" + id;

    /// <summary>Body geoset groups an item switches, by its GeosetGroup slots (wowdev ItemDisplayInfo).</summary>
    public static int[] GeosetGroups(int inventoryType) => inventoryType switch
    {
        4 or 5 or 20 => new[] { 8, 10, 13 }, 6 => new[] { 18 }, 7 => new[] { 11, 9, 13 }, 8 => new[] { 5, 20 },
        10 => new[] { 4, 23 }, 16 => new[] { 15 }, 19 => new[] { 12 }, _ => Array.Empty<int>(),
    };
}

/// <summary>Keeps an attached object on its renderer's attachment point.</summary>
public sealed class FollowAttachment : Component
{
    public SkinnedModelRenderer Target { get; set; }
    public string Attachment { get; set; }

    protected override void OnPreRender()
    {
        if (!Target.IsValid() || Target.GetAttachment(Attachment) is not { } at) return;
        WorldPosition = at.Position; WorldRotation = at.Rotation;
    }
}

/// <summary>Dresses one of the mount's playable characters: its customization (skin, face, hair...) and equipment
/// (attached models, body geosets, armour pieces painted into the skin atlas), all read from the client through
/// the mount. The same equipment adapts to every race and sex, as transmogrification does.</summary>
public sealed class WowAppearance : Component
{
    [Property] public SkinnedModelRenderer Renderer { get; set; }
    /// <summary>characters/&lt;Key&gt; of the mount (human_male, orc_female...).</summary>
    [Property] public string Key { get; set; }
    /// <summary>Option → chosen choice; options left out take their first choice.</summary>
    public Dictionary<uint, uint> Choices { get; set; } = new();
    /// <summary>Slot (see <see cref="WowItems.SlotOf"/>) → item id.</summary>
    public Dictionary<int, uint> Equipment { get; set; } = new();
    public WowCharacterInfo Info { get; private set; }
    public string LastError { get; private set; } = "";

    private readonly List<GameObject> attached = new();
    private Texture atlas;
    private readonly Dictionary<int, Material> dressed = new();
    private int version;

    protected override void OnStart() => _ = Apply();
    protected override void OnDestroy() { foreach (var o in attached) if (o.IsValid()) o.Destroy(); attached.Clear(); }

    /// <summary>Raised when the player changes a choice or an item (the explorer saves the character then).</summary>
    public event Action Changed;
    public void Choose(uint option, uint choice) { Choices[option] = choice; Changed?.Invoke(); _ = Apply(); }
    public void Equip(int slot, uint item) { if (item == 0) Equipment.Remove(slot); else Equipment[slot] = item; Changed?.Invoke(); _ = Apply(); }

    /// <summary>Elements of the current choices, keeping those tied to another choice only when it is chosen.</summary>
    public List<WowCharacterInfo.Element> Elements()
    {
        if (Info is null) return new();
        var chosen = Info.Options.Select(o => o.Choices.FirstOrDefault(c => c.Id == Choices.GetValueOrDefault(o.Id)) ?? o.Choices.FirstOrDefault()).Where(c => c is not null).ToList();
        var ids = chosen.Select(c => c.Id).ToHashSet();
        return chosen.SelectMany(c => c.Elements).Where(e => e.Related is null || ids.Contains(e.Related.Value)).ToList();
    }

    public async Task Apply()
    {
        int mine = ++version;
        try
        {
            Info ??= await WowItems.Character(Key);
            if (Info is null || !Renderer.IsValid() || mine != version) return;
            var elements = Elements();
            var items = new List<(WowItems.Entry Item, WowItemDisplay Display)>();
            foreach (var id in Equipment.Values)
                if (await WowItems.Item(id) is { } item && await WowItems.Display(item.Display) is { } display) items.Add((item, display));
            if (mine != version || !Renderer.IsValid()) return;
            // The dressable model of the mount: every geoset with its own material, hidden or shown here.
            if (!string.IsNullOrEmpty(Info.Dressable) && Renderer.Model?.ResourcePath != Info.Dressable
                && await MountLoads.Model(Info.Dressable) is { IsError: false } dressable && mine == version && Renderer.IsValid())
            {
                // Same skeleton and clips: the playing sequence carries over to the swapped model.
                var sequence = Renderer.Sequence.Name; var looping = Renderer.Sequence.Looping;
                Renderer.Model = dressable; dressed.Clear();
                if (!Renderer.UseAnimGraph && !string.IsNullOrEmpty(sequence)) { Renderer.Sequence.Name = sequence; Renderer.Sequence.Looping = looping; }
            }
            ApplyGeosets(elements, items);
            textures.Clear();
            await ApplyTextures(elements, items, mine);
            if (mine != version || !Renderer.IsValid()) return;
            ApplyMaterials();
            Attach(items);
            LastError = "";
        }
        catch (Exception error) { LastError = error.Message; Log.Warning($"Apparence {Key} : {error}"); }
    }

    private void ApplyGeosets(List<WowCharacterInfo.Element> elements, List<(WowItems.Entry Item, WowItemDisplay Display)> items)
    {
        var wanted = new Dictionary<int, int>();
        foreach (var e in elements) if (e.Geoset is { } g) wanted[g / 100] = g;
        // Equipment over the body, in the client's order: robes and chests after legs, then gloves, belts, tabards.
        foreach (var (item, display) in items.OrderBy(i => Order(i.Item.Slot)))
        {
            var groups = WowItems.GeosetGroups(item.Slot);
            for (int k = 0; k < groups.Length && k < display.Geosets.Length; k++)
                if (display.Geosets[k] > 0 || groups[k] is 15) wanted[groups[k]] = groups[k] * 100 + 1 + display.Geosets[k];
            foreach (var hide in display.HideGroups.Where(h => h.Sex == Info.Sex && (h.Race == Info.Race || h.Race == 0))) wanted[hide.Group] = 0;
        }
        // What one piece covers, the others under it do not show through (the client's own precedence):
        // a robe hides kneepads, trouser flares and boot tops; gloves hide the sleeve ends; boots hide trouser flares.
        if (wanted.TryGetValue(13, out var robe) && robe % 100 > 1) { wanted[9] = 0; wanted[11] = 1101; wanted[5] = 501; }
        if (wanted.TryGetValue(4, out var gloves) && gloves % 100 > 1) wanted[8] = 801;
        if (wanted.TryGetValue(5, out var boots) && boots % 100 > 1 && !(robe % 100 > 1)) wanted[11] = 1101;
        // One variant per group: the wanted one when the model has it, else the group's default (first choice).
        shown.Clear();
        foreach (var (name, choices) in Info.BodyGroups)
        {
            int group = int.Parse(name[1..]);
            shown[group] = wanted.TryGetValue(group, out var geoset) && (geoset == 0 || choices.Contains(geoset)) ? geoset : choices[0];
        }
    }

    private readonly Dictionary<int, int> shown = new();
    private readonly Dictionary<int, Texture> textures = new();
    private Material hidden;

    // Each material of the dressable model: hidden when its geoset is not the one shown in its group, otherwise
    // drawn with the composed or chosen texture of its type, or as the mount built it.
    private void ApplyMaterials()
    {
        if (hidden is null)
        {
            hidden = Material.Create("wowforever_hidden.vmat", "shaders/complex.shader");
            hidden.SetFeature("F_ALPHA_TEST", 1);
            // Alpha test reads the colour map's alpha: a fully transparent colour discards every pixel.
            var clear = Texture.Create(1, 1).WithData(new byte[] { 0, 0, 0, 0 }).Finish();
            hidden.Set("g_tColor", clear);
            hidden.Set("g_tTranslucency", clear);
            hidden.Set("g_flAlphaTestReference", 0.5f);
        }
        var materials = Renderer.Materials;
        for (int i = 0; i < materials.Count; i++)
        {
            var original = materials.GetOriginal(i);
            if (original is null) continue;
            var name = original.Name;
            int geoset = Number(name, "_g"), type = Number(name, "_t");
            if (geoset > 0 && shown.TryGetValue(geoset / 100, out var visible) && visible != geoset) { materials.SetOverride(i, hidden); continue; }
            if (type >= 0 && textures.TryGetValue(type, out var texture)) { materials.SetOverride(i, Dressed(i, original, texture)); continue; }
            materials.SetOverride(i, null);
        }
    }

    private static int Number(string name, string tag)
    {
        int at = name.LastIndexOf(tag + "", StringComparison.Ordinal);
        while (at >= 0)
        {
            int start = at + tag.Length, end = start;
            while (end < name.Length && char.IsDigit(name[end])) end++;
            if (end > start && end < name.Length && name[end] == '_') return int.Parse(name[start..end]);
            at = at > 0 ? name.LastIndexOf(tag, at - 1, StringComparison.Ordinal) : -1;
        }
        return -1;
    }

    private static int Order(int slot) => slot switch { 4 => 0, 9 => 1, 7 => 2, 8 => 3, 5 or 20 => 4, 10 => 5, 6 => 6, 19 => 7, 16 => 8, _ => 9 };

    // The skin atlas (texture type 1) is painted layer by layer like the client: base skin, face, markings and
    // underwear from the customization, then the armour pieces; other types (hair, cape...) take one texture.
    private async Task ApplyTextures(List<WowCharacterInfo.Element> elements, List<(WowItems.Entry Item, WowItemDisplay Display)> items, int mine)
    {
        var layers = elements.Where(e => e.TextureType == 1 && e.Texture is not null).OrderBy(e => e.Layer).ToList();
        var pieces = new List<(int Section, string Path)>();
        foreach (var (item, display) in items.OrderBy(i => Order(i.Item.Slot)))
            foreach (var piece in display.Pieces)
                if (WowItemDisplay.Pick(piece.Variants, Info.Race, Info.Sex) is { } v) pieces.Add((piece.Section, v.Path));
        if (layers.Count > 0)
        {
            // Pixels are read back on the main thread; the painting itself runs on a worker.
            var sources = new Dictionary<string, (Color32[] Pixels, int Width, int Height)>();
            foreach (var path in layers.Select(l => l.Texture).Concat(pieces.Select(p => p.Path)).Distinct())
                if (await Texture.LoadAsync(path) is { Width: > 1 } texture) sources[path] = (texture.GetPixels(0), texture.Width, texture.Height);
            if (mine != version || !sources.TryGetValue(layers[0].Texture, out var baseTexture)) return;
            float scale = Math.Max(1, baseTexture.Width / (float)Math.Max(1, Info.Layout.Width));
            int width = (int)(Info.Layout.Width * scale), height = (int)(Info.Layout.Height * scale);
            var canvas = await GameTask.RunInThreadAsync(() =>
            {
                var pixels = new Color32[width * height];
                foreach (var layer in layers)
                    if (sources.TryGetValue(layer.Texture, out var source)) Paint(pixels, width, height, source, Rect(layer.SectionMask, scale, width, height), layer == layers[0]);
                foreach (var (section, path) in pieces)
                    if (sources.TryGetValue(path, out var source)) Paint(pixels, width, height, source, Rect(1u << section, scale, width, height), false);
                return pixels;
            });
            if (mine != version || !Renderer.IsValid()) return;
            var bytes = new byte[canvas.Length * 4];
            for (int i = 0; i < canvas.Length; i++) { bytes[i * 4] = canvas[i].r; bytes[i * 4 + 1] = canvas[i].g; bytes[i * 4 + 2] = canvas[i].b; bytes[i * 4 + 3] = canvas[i].a; }
            atlas = Texture.Create(width, height).WithData(bytes).WithMips().Finish();
            textures[1] = atlas;
        }
        foreach (var type in elements.Where(e => e.TextureType is not null and not 1 && e.Texture is not null).GroupBy(e => e.TextureType.Value))
            if (await Texture.LoadAsync(type.OrderBy(e => e.Layer).Last().Texture) is { } chosen) textures[type.Key] = chosen;
        // A cloak paints the body's cape pieces with the display's own texture.
        if (items.FirstOrDefault(i => i.Item.Slot == 16).Display is { Textures.Count: > 0 } cloak && await Texture.LoadAsync(cloak.Textures[0]) is { } cape) textures[2] = cape;
    }

    private (int X, int Y, int W, int H) Rect(uint mask, float scale, int width, int height)
    {
        if (mask is 0 or uint.MaxValue) return (0, 0, width, height);
        var sections = Info.Sections.Where(s => s.Type < 32 && (mask & (1u << s.Type)) != 0).ToList();
        if (sections.Count == 0) return (0, 0, width, height);
        int x0 = sections.Min(s => s.X), y0 = sections.Min(s => s.Y), x1 = sections.Max(s => s.X + s.Width), y1 = sections.Max(s => s.Y + s.Height);
        return ((int)(x0 * scale), (int)(y0 * scale), (int)((x1 - x0) * scale), (int)((y1 - y0) * scale));
    }

    // Scales the source into the rectangle; opaque layers replace, the others blend by their alpha.
    private static void Paint(Color32[] canvas, int width, int height, (Color32[] Pixels, int Width, int Height) source, (int X, int Y, int W, int H) rect, bool opaque)
    {
        if (source.Pixels is null || rect.W <= 0 || rect.H <= 0) return;
        var pixels = source.Pixels;
        int sw = source.Width, sh = source.Height;
        for (int y = 0; y < rect.H && rect.Y + y < height; y++)
        {
            int sy = y * sh / rect.H;
            for (int x = 0; x < rect.W && rect.X + x < width; x++)
            {
                var s = pixels[sy * sw + x * sw / rect.W];
                int d = (rect.Y + y) * width + rect.X + x;
                if (opaque) { canvas[d] = new Color32(s.r, s.g, s.b, 255); continue; }
                int a = s.a; var c = canvas[d];
                canvas[d] = new Color32((byte)((s.r * a + c.r * (255 - a)) / 255), (byte)((s.g * a + c.g * (255 - a)) / 255), (byte)((s.b * a + c.b * (255 - a)) / 255), 255);
            }
        }
    }

    // A material drawn with another texture: one copy per material slot, reused when the look changes again.
    private Material Dressed(int index, Material original, Texture texture)
    {
        if (!dressed.TryGetValue(index, out var copy) || copy is null)
        {
            // The mount's materials are built at run time: a new one takes their shader, features and maps.
            dressed[index] = copy = Material.Create($"wowdressed_{GameObject.Id:N}_{index}.vmat", original.ShaderName ?? "shaders/complex.shader");
            foreach (var feature in new[] { "F_ALPHA_TEST", "F_RENDER_BACKFACES", "F_TRANSLUCENT", "F_ADDITIVE_BLEND" }) copy.SetFeature(feature, original.GetFeature(feature));
            foreach (var map in new[] { "g_tNormal", "g_tRoughness", "g_tMetalness", "g_tAmbientOcclusion", "g_tTranslucency" })
                if (original.GetTexture(map) is { } t) copy.Set(map, t);
        }
        copy.Set("g_tColor", texture);
        return copy;
    }

    private void Attach(List<(WowItems.Entry Item, WowItemDisplay Display)> items)
    {
        foreach (var o in attached) if (o.IsValid()) o.Destroy();
        attached.Clear();
        foreach (var (item, display) in items)
        {
            var points = WowItems.Attachments(item.Slot);
            for (int k = 0; k < points.Length; k++)
            {
                // Shoulders: a model per side, or one file per position (0 left, 1 right).
                var set = display.Models.FirstOrDefault(m => m.Index == k) ?? display.Models.FirstOrDefault();
                if (set is null) continue;
                var variant = WowItemDisplay.Pick(set.Variants, Info.Race, Info.Sex, points.Length > 1 ? k : null);
                if (variant is null) continue;
                _ = AttachModel(variant.Path, WowItems.AttachmentName(points[k]));
            }
        }
    }

    private async Task AttachModel(string path, string attachment)
    {
        int mine = version;
        var model = await MountLoads.Model(path);
        if (model is null || model.IsError || mine != version || !Renderer.IsValid()) return;
        var go = new GameObject(Scene) { Name = "Objet porté", Parent = GameObject, Flags = GameObjectFlags.NotSaved | GameObjectFlags.NotNetworked };
        go.WorldScale = Renderer.WorldScale;
        var renderer = go.Components.Create<SkinnedModelRenderer>();
        renderer.Model = model; renderer.UseAnimGraph = false; renderer.Sequence.Name = "idle"; renderer.Sequence.Looping = true;
        var follow = go.Components.Create<FollowAttachment>();
        follow.Target = Renderer; follow.Attachment = attachment;
        attached.Add(go);
    }
}
