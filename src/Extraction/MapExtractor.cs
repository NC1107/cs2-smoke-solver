using System.Numerics;
using SmokeSolver.Sim;
using SteamDatabase.ValvePak;
using ValveResourceFormat;
using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.Serialization.KeyValues;

namespace SmokeSolver.Extraction;

public sealed record MapEntity(string ClassName, string Name, float[] Origin, float[] Angles, string Model, string Place);

public sealed record NavAreaDump(uint Id, float[][] Corners);

public static class MapExtractor
{
    /// <summary>
    /// Pulls the world physics collision mesh out of a CS2 map VPK.
    /// Smoke and grenades collide against this data, not the render mesh.
    /// </summary>
    /// <summary>
    /// Entity classes whose models are merged into the collision mesh. The
    /// default is SolidEntityClasses; extraction experiments pass their own so
    /// the corpus replay can measure what each class does to real throws.
    /// </summary>
    public static IReadOnlyCollection<string> SolidEntityClassOverride
    {
        get => _solidEntityClassOverride ?? SolidEntityClasses;
        set => _solidEntityClassOverride = value;
    }

    static IReadOnlyCollection<string>? _solidEntityClassOverride;

    /// <summary>Receives one line per collision attribute and per merged entity when set.</summary>
    public static Action<string>? Diagnostics { get; set; }

    /// <summary>
    /// Points of interest: every hull or mesh whose bounds contain one has its
    /// descriptor properties dumped through Diagnostics. For asking "what is
    /// this surface the game did not collide with?" without a map editor.
    /// </summary>
    public static IReadOnlyList<Vector3> ProbePoints { get; set; } = [];

    public static float ProbeRadius { get; set; } = 64f;

    /// <summary>m_nFlags value -> hull count, filled while Diagnostics is set.</summary>
    public static Dictionary<long, int> HullFlagHistogram { get; } = [];

    static void ProbeShape(string kind, object descriptor, ReadOnlySpan<Vector3> positions, Func<Vector3, Vector3> transform)
    {
        if (Diagnostics == null || ProbePoints.Count == 0)
        {
            return;
        }
        var lo = new Vector3(float.MaxValue);
        var hi = new Vector3(float.MinValue);
        var n = 0;
        foreach (var raw in positions)
        {
            var v = transform(raw);
            lo = Vector3.Min(lo, v);
            hi = Vector3.Max(hi, v);
            n++;
        }
        foreach (var probe in ProbePoints)
        {
            if (probe.X < lo.X - 4 || probe.X > hi.X + 4 || probe.Y < lo.Y - 4 || probe.Y > hi.Y + 4 || probe.Z < lo.Z - 4 || probe.Z > hi.Z + 4)
            {
                continue;
            }
            var props = descriptor.GetType().GetProperties()
                .Where(pr => pr.GetIndexParameters().Length == 0 && pr.Name != "Shape")
                .Select(pr =>
                {
                    try
                    {
                        return $"{pr.Name}={FormatKv(pr.GetValue(descriptor))}";
                    }
                    catch (Exception e) when (e is System.Reflection.TargetInvocationException or NotSupportedException)
                    {
                        return $"{pr.Name}=?";
                    }
                });
            Diagnostics($"probe ({probe.X:F0},{probe.Y:F0},{probe.Z:F0}) inside {kind} verts={n} bounds ({lo.X:F0},{lo.Y:F0},{lo.Z:F0})-({hi.X:F0},{hi.Y:F0},{hi.Z:F0}) {string.Join(" ", props)}");
            if (descriptor.GetType().GetProperty("Shape")?.GetValue(descriptor) is { } shape
                && shape.GetType().GetProperty("Data")?.GetValue(shape) is ValveKeyValue.KVObject data)
            {
                Diagnostics($"  shape {shape.GetType().Name}: m_nFlags={data.GetIntegerProperty("m_nFlags")} volume={data.GetFloatProperty("m_flVolume"):F0} area={data.GetFloatProperty("m_flSurfaceArea"):F0}");
            }
            if (descriptor is ValveKeyValue.KVObject || descriptor.GetType().GetProperty("Data")?.GetValue(descriptor) is { } ddata)
            {
                var d2 = descriptor.GetType().GetProperty("Data")?.GetValue(descriptor);
                if (d2 is System.Collections.IEnumerable dpairs)
                {
                    Diagnostics($"  descriptor data: {string.Join(" ", dpairs.Cast<object>().Select(FormatKv).Select(v => v.Length > 90 ? v[..90] + "..." : v))}");
                }
            }
        }
    }

    public static CollisionMesh ExtractWorldPhysics(string mapVpkPath, string mapName, string gameBuildId)
    {
        using var package = new Package();
        package.Read(mapVpkPath);

        // CS2 packs world physics as a PHYS block embedded in world_physics.vmdl_c,
        // not as a standalone .vphys_c resource.
        var entry = FindEntries(package, "vmdl_c")
            .FirstOrDefault(e => e.GetFullPath().EndsWith("world_physics.vmdl_c", StringComparison.OrdinalIgnoreCase))
            ?? throw new FileNotFoundException($"no world_physics.vmdl_c inside {mapVpkPath}");

        package.ReadEntry(entry, out var raw);
        using var resource = new Resource();
        resource.Read(new MemoryStream(raw));
        var phys = ((Model)resource.DataBlock!).GetEmbeddedPhys()
            ?? throw new InvalidDataException($"{entry.GetFullPath()} has no embedded physics block");

        var attributeNames = phys.CollisionAttributes
            .Select(kv => kv.GetStringProperty("m_CollisionGroupString") ?? "Default")
            .ToArray();
        var attributeInteractAs = phys.CollisionAttributes.Select(kv => Strings(kv, "m_InteractAsStrings")).ToArray();
        var attributeInteractExclude = phys.CollisionAttributes.Select(kv => Strings(kv, "m_InteractExcludeStrings")).ToArray();
        if (attributeNames.Length > byte.MaxValue)
        {
            throw new InvalidDataException($"{attributeNames.Length} collision attributes exceed the byte-sized attribute index");
        }
        if (Diagnostics != null)
        {
            var hashes = phys.SurfacePropertyHashes;
            Diagnostics($"surface properties: {string.Join(" ", hashes.Select((h, i) => $"{i}:{h}({SurfaceName(h)})"))}");
            var ai = 0;
            foreach (var kv in phys.CollisionAttributes)
            {
                Diagnostics($"attribute #{ai++}: group={kv.GetStringProperty("m_CollisionGroupString")} as=[{string.Join(',', Strings(kv, "m_InteractAsStrings"))}] with=[{string.Join(',', Strings(kv, "m_InteractWithStrings"))}] exclude=[{string.Join(',', Strings(kv, "m_InteractExcludeStrings"))}]");
            }
        }

        var vertices = new List<float>();
        var indices = new List<int>();
        var triangleAttributes = new List<byte>();
        var names = attributeNames.ToList();
        var interactAs = attributeInteractAs.ToList();

        // Static prop and dynamic prop models are almost never inside the map's
        // own small VPK - only the compiled level/entity/nav data is. The actual
        // .vmdl_c/.vphys_c payloads live in the shared game content archive
        // alongside it (pak01_dir.vpk), same directory as the maps/ folder.
        var sharedVpkPath = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(mapVpkPath))!, "pak01_dir.vpk");
        using var sharedPackage = File.Exists(sharedVpkPath) ? new Package() : null;
        sharedPackage?.Read(sharedVpkPath);

        AppendPhys(phys, vertices, indices, triangleAttributes, i => (byte)i, v => v);
        if (Diagnostics != null)
        {
            Diagnostics($"world hull m_nFlags histogram: {string.Join(" ", HullFlagHistogram.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key}x{kv.Value}"))}");
        }
        AppendSolidEntityModels(package, sharedPackage, vertices, indices, triangleAttributes, names, interactAs);
        AppendStaticProps(package, sharedPackage, vertices, indices, triangleAttributes, names, interactAs);

        return new CollisionMesh
        {
            MapName = mapName,
            GameBuildId = gameBuildId,
            Vertices = [.. vertices],
            Indices = [.. indices],
            TriangleAttributes = [.. triangleAttributes],
            AttributeNames = [.. names],
            AttributeInteractAs = [.. interactAs],
            // Groups merged in from entity and prop models sit past the world
            // table and exclude nothing.
            AttributeInteractExclude = [.. attributeInteractExclude, .. Enumerable.Repeat(Array.Empty<string>(), names.Count - attributeInteractExclude.Length)],
        };
    }

    static string[] Strings(ValveKeyValue.KVObject kv, string key)
    {
        try
        {
            return kv.GetArray<string>(key) ?? [];
        }
        catch (KeyNotFoundException)
        {
            return [];
        }
    }

    static void AppendPhys(
        PhysAggregateData phys,
        List<float> vertices,
        List<int> indices,
        List<byte> triangleAttributes,
        Func<int, byte> attributeMap,
        Func<Vector3, Vector3> transform)
    {
        var partIndex = 0;
        foreach (var part in phys.Parts)
        {
            if (Diagnostics != null)
            {
                var lo = new Vector3(float.MaxValue);
                var hi = new Vector3(float.MinValue);
                var count = 0;
                foreach (var md in part.Shape.Meshes)
                {
                    foreach (var v in md.Shape.GetVertices())
                    {
                        lo = Vector3.Min(lo, transform(v));
                        hi = Vector3.Max(hi, transform(v));
                        count++;
                    }
                }
                Diagnostics($"part {partIndex++}: meshes={part.Shape.Meshes.Length} hulls={part.Shape.Hulls.Length} verts={count} bounds ({lo.X:F0},{lo.Y:F0},{lo.Z:F0})-({hi.X:F0},{hi.Y:F0},{hi.Z:F0})");
            }
            foreach (var meshDescriptor in part.Shape.Meshes)
            {
                var mesh = meshDescriptor.Shape;
                ProbeShape("mesh", meshDescriptor, mesh.GetVertices(), transform);
                if (Diagnostics != null && ProbePoints.Count > 0)
                {
                    // Per-triangle surface materials around each probe point,
                    // against the mesh-wide histogram: a phantom surface with
                    // an unusual material index stands out at once.
                    var verts = mesh.GetVertices();
                    var tris = mesh.GetTriangles();
                    var materials = mesh.Materials;
                    var all = new Dictionary<int, int>();
                    foreach (var probe in ProbePoints)
                    {
                        var near = new Dictionary<int, int>();
                        for (var ti = 0; ti < tris.Length; ti++)
                        {
                            var t = tris[ti];
                            var c = transform((verts[t.X] + verts[t.Y] + verts[t.Z]) / 3f);
                            var mat = ti < materials.Length ? materials[ti] : -1;
                            all[mat] = all.GetValueOrDefault(mat) + 1;
                            if (Vector3.Distance(c, probe) < ProbeRadius)
                            {
                                near[mat] = near.GetValueOrDefault(mat) + 1;
                            }
                        }
                        if (near.Count > 0)
                        {
                            Diagnostics($"probe ({probe.X:F0},{probe.Y:F0},{probe.Z:F0}) mesh attr#{meshDescriptor.CollisionAttributeIndex} materials within 64u: {string.Join(" ", near.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key}x{kv.Value}"))}");
                        }
                    }
                    Diagnostics($"mesh attr#{meshDescriptor.CollisionAttributeIndex} material histogram: {string.Join(" ", all.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key}x{kv.Value}"))}");
                }
                var baseIndex = vertices.Count / 3;
                foreach (var raw in mesh.GetVertices())
                {
                    var v = transform(raw);
                    vertices.Add(v.X);
                    vertices.Add(v.Y);
                    vertices.Add(v.Z);
                }
                foreach (var t in mesh.GetTriangles())
                {
                    indices.Add(baseIndex + t.X);
                    indices.Add(baseIndex + t.Y);
                    indices.Add(baseIndex + t.Z);
                    triangleAttributes.Add(attributeMap(meshDescriptor.CollisionAttributeIndex));
                }
            }
            foreach (var hullDescriptor in part.Shape.Hulls)
            {
                TriangulateHull(hullDescriptor, vertices, indices, triangleAttributes, attributeMap, transform);
            }
        }
    }

    // Brush entities whose compiled models are solid to physics objects.
    // Trigger-volume classes (buyzones, bomb targets, env_cs_place callouts,
    // post-processing volumes) also carry hulls but never block anything, so
    // this is an allowlist, not a blocklist. func_clip_vphysics blocks physics
    // objects (grenades) while letting players and bullets through - on
    // de_dust2 it seals the mid-doors gap, which is lineup-critical.
    // prop_dynamic is deliberately absent. It was added on 2026-08-30 for
    // de_nuke's vent slats (one throw from inside the vent), and the corpus
    // replay of 2026-09-04 showed the cost: 48 real throws that fall through
    // the open slats into the vent, and 83 on de_mirage, all landed on
    // prop_dynamic hulls the game does not collide grenades with. Breakables
    // follow the same intact-at-round-start baseline as func_breakable glass.
    // prop_physics* stay out: loose junk (mugs, hard
    static string FormatKv(object? value)
    {
        if (value == null)
        {
            return "null";
        }
        if (value is string s)
        {
            return s;
        }
        var t = value.GetType();
        if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(KeyValuePair<,>))
        {
            return $"{t.GetProperty("Key")!.GetValue(value)}={FormatKv(t.GetProperty("Value")!.GetValue(value))}";
        }
        if (t.Namespace == "ValveKeyValue" && t.Name != "KVObject")
        {
            // A KV leaf (number, string, vector): its ToString is the value.
            return value.ToString() ?? "";
        }
        if (t.Name.StartsWith("KV", StringComparison.Ordinal) && t.GetProperty("Value") is { } inner)
        {
            return FormatKv(inner.GetValue(value));
        }
        if (value is System.Collections.IEnumerable e)
        {
            return "[" + string.Join(",", e.Cast<object>().Select(FormatKv)) + "]";
        }
        return value.ToString() ?? "";
    }

    static readonly string[] KnownSurfaces =
    [
        "ALIENFLESH",
        "ALUMINUM",
        "ANTLION",
        "ANTLIONSAND",
        "AREAPORTAL",
        "ARMORFLESH",
        "ASPHALT",
        "Alienflesh",
        "Aluminum",
        "Antlion",
        "Antlionsand",
        "Areaportal",
        "Armorflesh",
        "Asphalt",
        "BARREL",
        "BASEROCK",
        "BEAM",
        "BLACK",
        "BLOCKLIGHT",
        "BLOCKLOS",
        "BLOODYFLESH",
        "BOULDER",
        "BOUNCY",
        "BOX",
        "BRAKINGRUBBERTIRE",
        "BRASS",
        "BRICK",
        "BRONZE",
        "BRUSH",
        "BULLETCLIP",
        "BUSH",
        "Barrel",
        "Baserock",
        "Beam",
        "Black",
        "Blocklight",
        "Blocklos",
        "Bloodyflesh",
        "Boulder",
        "Bouncy",
        "Box",
        "Brakingrubbertire",
        "Brass",
        "Brick",
        "Bronze",
        "Brush",
        "Bulletclip",
        "Bush",
        "CABLE",
        "CANISTER",
        "CARDBOARD",
        "CARPET",
        "CEILING_TILE",
        "CHAIN",
        "CHAINLINK",
        "CHROME",
        "CLIP",
        "CLOTH",
        "CLOTHSOFT",
        "COBBLESTONE",
        "COMBINE_GLASS",
        "COMBINE_METAL",
        "COMPUTER",
        "CONCRETE",
        "CONCRETE_BLOCK",
        "COPPER",
        "CORRUGATED",
        "CRATE",
        "CROWBAR",
        "Cable",
        "Canister",
        "Cardboard",
        "Carpet",
        "Ceiling_Tile",
        "Ceiling_tile",
        "Chain",
        "Chainlink",
        "Chrome",
        "Clip",
        "Cloth",
        "Clothsoft",
        "Cobblestone",
        "Combine_Glass",
        "Combine_Metal",
        "Combine_glass",
        "Combine_metal",
        "Computer",
        "Concrete",
        "Concrete_Block",
        "Concrete_block",
        "Copper",
        "Corrugated",
        "Crate",
        "Crowbar",
        "DEFAULT",
        "DEFAULT_SILENT",
        "DIRT",
        "DIRT_SOFT",
        "DOOR",
        "Default",
        "Default_Silent",
        "Default_silent",
        "Dirt",
        "Dirt_Soft",
        "Dirt_soft",
        "Door",
        "FABRIC",
        "FENCE",
        "FLESH",
        "FLOATINGSTANDABLE",
        "FLOATING_METAL_BARREL",
        "FOG",
        "FOLIAGE",
        "Fabric",
        "Fence",
        "Flesh",
        "Floating_Metal_Barrel",
        "Floating_metal_barrel",
        "Floatingstandable",
        "Fog",
        "Foliage",
        "GLASS",
        "GLASSBOTTLE",
        "GLASSFLOOR",
        "GOLD",
        "GRASS",
        "GRASS_DRY",
        "GRATE",
        "GRAVEL",
        "GRAVEL_ROAD",
        "GRENADE",
        "GRENADECLIP",
        "GRENADE_CLIP",
        "GUNSHIP",
        "Glass",
        "Glassbottle",
        "Glassfloor",
        "Gold",
        "Grass",
        "Grass_Dry",
        "Grass_dry",
        "Grate",
        "Gravel",
        "Gravel_Road",
        "Gravel_road",
        "Grenade",
        "Grenade_Clip",
        "Grenade_clip",
        "Grenadeclip",
        "Gunship",
        "HARD",
        "HAY",
        "HINT",
        "Hard",
        "Hay",
        "Hint",
        "ICE",
        "INVISIBLE",
        "IRON",
        "ITEM",
        "Ice",
        "Invisible",
        "Iron",
        "Item",
        "JEEPTIRE",
        "Jeeptire",
        "LADDER",
        "LAVA",
        "LEAD",
        "LEAVES",
        "Ladder",
        "Lava",
        "Lead",
        "Leaves",
        "MARBLE",
        "MESH",
        "METAL",
        "METALGRATE",
        "METALPANEL",
        "METALVEHICLE",
        "METALVENT",
        "METAL_BARREL",
        "METAL_BOUNCY",
        "METAL_BOUNCY_LOW",
        "METAL_BOX",
        "METAL_SAND_BARREL",
        "METAL_SEAFLOORCAR",
        "MUD",
        "MUD_DEEP",
        "Marble",
        "Mesh",
        "Metal",
        "Metal_Barrel",
        "Metal_Bouncy",
        "Metal_Bouncy_Low",
        "Metal_Box",
        "Metal_Sand_Barrel",
        "Metal_Seafloorcar",
        "Metal_barrel",
        "Metal_bouncy",
        "Metal_bouncy_low",
        "Metal_box",
        "Metal_sand_barrel",
        "Metal_seafloorcar",
        "Metalgrate",
        "Metalpanel",
        "Metalvehicle",
        "Metalvent",
        "Mud",
        "Mud_Deep",
        "Mud_deep",
        "NET",
        "NICKEL",
        "NODRAW",
        "NO_DECAL",
        "NPCCLIP",
        "NPC_CLIP",
        "Net",
        "Nickel",
        "No_Decal",
        "No_decal",
        "Nodraw",
        "Npc_Clip",
        "Npc_clip",
        "Npcclip",
        "OCCLUDER",
        "OIL",
        "ORIGIN",
        "Occluder",
        "Oil",
        "Origin",
        "PAINTCAN",
        "PAPER",
        "PAPERCARDBOARD",
        "PIPE",
        "PLANTS",
        "PLASTER",
        "PLASTIC",
        "PLASTIC_BARREL",
        "PLASTIC_BARREL_BUOYANT",
        "PLASTIC_BOUNCY",
        "PLASTIC_BOX",
        "PLAYER",
        "PLAYERCLIP",
        "PLAYER_CLIP",
        "PLAYER_CONTROL_CLIP",
        "POPCAN",
        "PORCELAIN",
        "POTTERY",
        "PUDDLE",
        "Paintcan",
        "Paper",
        "Papercardboard",
        "Pipe",
        "Plants",
        "Plaster",
        "Plastic",
        "Plastic_Barrel",
        "Plastic_Barrel_Buoyant",
        "Plastic_Bouncy",
        "Plastic_Box",
        "Plastic_barrel",
        "Plastic_barrel_buoyant",
        "Plastic_bouncy",
        "Plastic_box",
        "Player",
        "Player_Clip",
        "Player_Control_Clip",
        "Player_clip",
        "Player_control_clip",
        "Playerclip",
        "Popcan",
        "Porcelain",
        "Pottery",
        "Puddle",
        "QUICKSAND",
        "Quicksand",
        "RAILING",
        "ROCK",
        "ROLLER",
        "ROPE",
        "RUBBER",
        "RUBBERTIRE",
        "RUBBER_BOUNCY",
        "Railing",
        "Rock",
        "Roller",
        "Rope",
        "Rubber",
        "Rubber_Bouncy",
        "Rubber_bouncy",
        "Rubbertire",
        "SAND",
        "SANDBAGS",
        "SHEETMETAL",
        "SILVER",
        "SKIP",
        "SKY",
        "SKYBOX",
        "SLIDINGRUBBERTIRE",
        "SLIDINGRUBBERTIRE_FRONT",
        "SLIDINGRUBBERTIRE_REAR",
        "SLIME",
        "SLIPPERY",
        "SLIPPERYMETAL",
        "SLIPPERYSLIME",
        "SNOW",
        "SNOW_DEEP",
        "SOFT",
        "SOLIDMETAL",
        "STEEL",
        "STICKY",
        "STONE",
        "STRAW",
        "STRIDER",
        "Sand",
        "Sandbags",
        "Sheetmetal",
        "Silver",
        "Skip",
        "Sky",
        "Skybox",
        "Slidingrubbertire",
        "Slidingrubbertire_Front",
        "Slidingrubbertire_Rear",
        "Slidingrubbertire_front",
        "Slidingrubbertire_rear",
        "Slime",
        "Slippery",
        "Slipperymetal",
        "Slipperyslime",
        "Snow",
        "Snow_Deep",
        "Snow_deep",
        "Soft",
        "Solidmetal",
        "Steel",
        "Sticky",
        "Stone",
        "Straw",
        "Strider",
        "TARP",
        "TILE",
        "TIN",
        "TITANIUM",
        "TOOLS",
        "TOOLSBLOCKBULLETS",
        "TOOLSBLOCKLIGHT",
        "TOOLSBLOCKLOS",
        "TOOLSBLOCKSOUND",
        "TOOLSCLIP",
        "TOOLSGRENADECLIP",
        "TOOLSINVISIBLE",
        "TOOLSINVISIBLELADDER",
        "TOOLSNODRAW",
        "TOOLSPLAYERCLIP",
        "TOOLSSKYBOX",
        "TOOLSTRIGGER",
        "TREE",
        "TREE_TRUNK",
        "TRIGGER",
        "Tarp",
        "Tile",
        "Tin",
        "Titanium",
        "Tools",
        "Toolsblockbullets",
        "Toolsblocklight",
        "Toolsblocklos",
        "Toolsblocksound",
        "Toolsclip",
        "Toolsgrenadeclip",
        "Toolsinvisible",
        "Toolsinvisibleladder",
        "Toolsnodraw",
        "Toolsplayerclip",
        "Toolsskybox",
        "Toolstrigger",
        "Tree",
        "Tree_Trunk",
        "Tree_trunk",
        "Trigger",
        "UPHOLSTERY",
        "Upholstery",
        "VENT",
        "Vent",
        "WADE",
        "WATER",
        "WATERMELON",
        "WEAPON",
        "WET",
        "WHITE",
        "WIRE",
        "WOOD",
        "WOOD_BOX",
        "WOOD_CRATE",
        "WOOD_FURNITURE",
        "WOOD_PANEL",
        "WOOD_PLANK",
        "WOOD_SOLID",
        "Wade",
        "Water",
        "Watermelon",
        "Weapon",
        "Wet",
        "White",
        "Wire",
        "Wood",
        "Wood_Box",
        "Wood_Crate",
        "Wood_Furniture",
        "Wood_Panel",
        "Wood_Plank",
        "Wood_Solid",
        "Wood_box",
        "Wood_crate",
        "Wood_furniture",
        "Wood_panel",
        "Wood_plank",
        "Wood_solid",
        "ZINC",
        "ZOMBIEFLESH",
        "Zinc",
        "Zombieflesh",
        "alienflesh",
        "aluminum",
        "antlion",
        "antlionsand",
        "areaportal",
        "armorflesh",
        "asphalt",
        "barrel",
        "baserock",
        "beam",
        "black",
        "blocklight",
        "blocklos",
        "bloodyflesh",
        "boulder",
        "bouncy",
        "box",
        "brakingrubbertire",
        "brass",
        "brick",
        "bronze",
        "brush",
        "bulletclip",
        "bush",
        "cable",
        "canister",
        "cardboard",
        "carpet",
        "ceiling_tile",
        "chain",
        "chainlink",
        "chrome",
        "clip",
        "cloth",
        "clothsoft",
        "cobblestone",
        "combine_glass",
        "combine_metal",
        "computer",
        "concrete",
        "concrete_block",
        "copper",
        "corrugated",
        "crate",
        "crowbar",
        "default",
        "default_silent",
        "dirt",
        "dirt_soft",
        "door",
        "fabric",
        "fence",
        "flesh",
        "floating_metal_barrel",
        "floatingstandable",
        "fog",
        "foliage",
        "glass",
        "glassbottle",
        "glassfloor",
        "gold",
        "grass",
        "grass_dry",
        "grate",
        "gravel",
        "gravel_road",
        "grenade",
        "grenade_clip",
        "grenadeclip",
        "gunship",
        "hard",
        "hay",
        "hint",
        "ice",
        "invisible",
        "iron",
        "item",
        "jeeptire",
        "ladder",
        "lava",
        "lead",
        "leaves",
        "marble",
        "mesh",
        "metal",
        "metal_barrel",
        "metal_bouncy",
        "metal_bouncy_low",
        "metal_box",
        "metal_sand_barrel",
        "metal_seafloorcar",
        "metalgrate",
        "metalpanel",
        "metalvehicle",
        "metalvent",
        "mud",
        "mud_deep",
        "net",
        "nickel",
        "no_decal",
        "nodraw",
        "npc_clip",
        "npcclip",
        "occluder",
        "oil",
        "origin",
        "paintcan",
        "paper",
        "papercardboard",
        "pipe",
        "plants",
        "plaster",
        "plastic",
        "plastic_barrel",
        "plastic_barrel_buoyant",
        "plastic_bouncy",
        "plastic_box",
        "player",
        "player_clip",
        "player_control_clip",
        "playerclip",
        "popcan",
        "porcelain",
        "pottery",
        "puddle",
        "quicksand",
        "railing",
        "rock",
        "roller",
        "rope",
        "rubber",
        "rubber_bouncy",
        "rubbertire",
        "sand",
        "sandbags",
        "sheetmetal",
        "silver",
        "skip",
        "sky",
        "skybox",
        "slidingrubbertire",
        "slidingrubbertire_front",
        "slidingrubbertire_rear",
        "slime",
        "slippery",
        "slipperymetal",
        "slipperyslime",
        "snow",
        "snow_deep",
        "soft",
        "solidmetal",
        "steel",
        "sticky",
        "stone",
        "straw",
        "strider",
        "tarp",
        "tile",
        "tin",
        "titanium",
        "tools",
        "toolsblockbullets",
        "toolsblocklight",
        "toolsblocklos",
        "toolsblocksound",
        "toolsclip",
        "toolsgrenadeclip",
        "toolsinvisible",
        "toolsinvisibleladder",
        "toolsnodraw",
        "toolsplayerclip",
        "toolsskybox",
        "toolstrigger",
        "tree",
        "tree_trunk",
        "trigger",
        "upholstery",
        "vent",
        "wade",
        "water",
        "watermelon",
        "weapon",
        "wet",
        "white",
        "wire",
        "wood",
        "wood_box",
        "wood_crate",
        "wood_furniture",
        "wood_panel",
        "wood_plank",
        "wood_solid",
        "zinc",
        "zombieflesh",
    ];


    static string SurfaceName(uint hash) =>
        KnownSurfaces.FirstOrDefault(n => ValveResourceFormat.Utils.StringToken.Get(n) == hash) ?? "?";

    static readonly string[] SolidEntityClasses = ["func_brush", "func_clip_vphysics", "func_door", "func_door_rotating", "func_breakable", "prop_door_rotating", "prop_dynamic"];

    /// <summary>
    /// A prop_dynamic is merged only when its model is breakable glass: the
    /// office and nuke window props, which real grenades break through at
    /// 0.40 speed (cs_office, 2026-09-04). Solid-looking prop_dynamics that
    /// are not breakable (de_nuke's vent slats) stand open or animated in
    /// the game and cost 48 throws on nuke and 83 on mirage when merged.
    /// </summary>
    static bool BreakableModel(Model model)
    {
        try
        {
            if (model.KeyValues is not System.Collections.IEnumerable pairs)
            {
                return false;
            }
            // break_list: the model shatters into pieces when damaged (window
            // props). break_command_list: the "break" is a scripted state
            // change instead (de_nuke's vent slats, which stand open), and
            // those are not glass.
            var keys = pairs.Cast<object>().Select(o => FormatKv(o).Split('=')[0]).ToList();
            return keys.Contains("break_list") && !keys.Contains("break_command_list");
        }
        catch (Exception e) when (e is InvalidOperationException or NullReferenceException)
        {
            return false;
        }
    }

    // Retake is a separate game mode: its brushes (the tape borders walling off
    // each bombsite, e.g. de_mirage's [PR#]retake.asite/bsite func_brushes) are
    // spawned only in Retake and are non-solid in Defusal, so a Defusal lineup
    // must not bounce grenades off them. They carry the Retake prefab tag in the
    // targetname ("[PR#]retake...") and in the compiled model path
    // (entities/retake_...); either marks them for exclusion. The textured GLB
    // drops the same geometry, but by material path, which the physics mesh lacks.
    static bool IsRetakeOnly(string targetName, string model) =>
        targetName.Contains("retake", StringComparison.OrdinalIgnoreCase) ||
        model.Contains("/retake_", StringComparison.OrdinalIgnoreCase);

    // Wingman (2v2) reuses the Defusal map with parts walled off - on de_overpass
    // the whole B route is sealed by a set of [PR#]brush.blocker func_brush and
    // func_clip_vphysics entities, all flagged startdisabled=1 and never re-enabled
    // by any Defusal entity I/O (verified: zero connections target them). A
    // start-disabled brush is not solid at round start, which is the state this
    // mesh models (the same reason doors read as open and glass as broken), so
    // baking it in invents an invisible wall a smoke bounces off in Defusal.
    static bool StartsDisabled(EntityLump.Entity entity) =>
        entity.TryGetValue("startdisabled", out var v) &&
        v?.ToString() is "1" or "true" or "True";

    static void AppendSolidEntityModels(
        Package package,
        Package? sharedPackage,
        List<float> vertices,
        List<int> indices,
        List<byte> triangleAttributes,
        List<string> names,
        List<string[]> interactAs)
    {
        // Lazy: built once, replacing an O(entities x entries) rescan per entity.
        // Brush entity models are compiled into the map's own VPK; prop models
        // (prop_dynamic) live in the shared content archive, and usually
        // reference their collision as a separate .vphys_c rather than
        // embedding it - the same two-step lookup AppendStaticProps does.
        Dictionary<string, SteamDatabase.ValvePak.PackageEntry>? modelsByPath = null;
        Dictionary<string, SteamDatabase.ValvePak.PackageEntry>? sharedModelsByPath = null;
        Dictionary<string, SteamDatabase.ValvePak.PackageEntry>? physByPath = null;
        Dictionary<string, SteamDatabase.ValvePak.PackageEntry>? sharedPhysByPath = null;
        (Package Package, SteamDatabase.ValvePak.PackageEntry Entry)? Find(string path, string extension, ref Dictionary<string, SteamDatabase.ValvePak.PackageEntry>? local, ref Dictionary<string, SteamDatabase.ValvePak.PackageEntry>? shared)
        {
            local ??= FindEntries(package, extension).ToDictionary(e => e.GetFullPath().ToLowerInvariant(), e => e);
            if (local.TryGetValue(path, out var localEntry))
            {
                return (package, localEntry);
            }
            if (sharedPackage == null)
            {
                return null;
            }
            shared ??= FindEntries(sharedPackage, extension).ToDictionary(e => e.GetFullPath().ToLowerInvariant(), e => e);
            return shared.TryGetValue(path, out var sharedEntry) ? (sharedPackage, sharedEntry) : null;
        }
        foreach (var lumpEntry in FindEntries(package, "vents_c"))
        {
            package.ReadEntry(lumpEntry, out var lumpRaw);
            using var lumpResource = new Resource();
            lumpResource.Read(new MemoryStream(lumpRaw));
            foreach (var entity in ((EntityLump)lumpResource.DataBlock!).GetEntities())
            {
                var className = entity.GetStringProperty("classname") ?? string.Empty;
                var model = entity.GetStringProperty("model") ?? string.Empty;
                if (!SolidEntityClassOverride.Contains(className) || model.Length == 0)
                {
                    continue;
                }
                if (IsRetakeOnly(entity.GetStringProperty("targetname") ?? string.Empty, model))
                {
                    continue;
                }
                if (StartsDisabled(entity))
                {
                    continue;
                }
                if (Find((model + "_c").ToLowerInvariant(), "vmdl_c", ref modelsByPath, ref sharedModelsByPath) is not { } modelHit)
                {
                    continue;
                }
                modelHit.Package.ReadEntry(modelHit.Entry, out var raw);
                using var resource = new Resource();
                resource.Read(new MemoryStream(raw));
                var modelData = (Model)resource.DataBlock!;
                if (className == "prop_dynamic")
                {
                    var breakable = BreakableModel(modelData);
                    Diagnostics?.Invoke($"prop_dynamic {model} breakable={breakable} keys=[{(modelData.KeyValues is System.Collections.IEnumerable kvs ? string.Join(",", kvs.Cast<object>().Select(o => FormatKv(o).Split('=')[0])) : "")}]");
                    if (!breakable)
                    {
                        continue;
                    }
                }
                var phys = modelData.GetEmbeddedPhys();
                if (phys == null &&
                    modelData.GetReferencedPhysNames().FirstOrDefault() is { } refPhysName &&
                    Find((refPhysName + "_c").ToLowerInvariant(), "vphys_c", ref physByPath, ref sharedPhysByPath) is { } physHit)
                {
                    physHit.Package.ReadEntry(physHit.Entry, out var physRaw);
                    using var physResource = new Resource();
                    physResource.Read(new MemoryStream(physRaw));
                    phys = physResource.DataBlock as PhysAggregateData;
                }
                if (phys == null)
                {
                    continue;
                }

                var origin = entity.GetVector3Property("origin", Vector3.Zero);
                var angles = entity.GetVector3Property("angles", Vector3.Zero);
                // Props can carry a per-instance scale; brush entities never do.
                var scales = entity.GetVector3Property("scales", Vector3.One);
                var rotation = SourceAngleMatrix(angles);
                Diagnostics?.Invoke($"entity {className} '{entity.GetStringProperty("targetname")}' model {model} at ({origin.X:F0},{origin.Y:F0},{origin.Z:F0}) solid={entity.GetStringProperty("solid")} spawnflags={entity.GetStringProperty("spawnflags")} health={entity.GetStringProperty("health")} parts={phys.Parts.Length} hulls={phys.Parts.Sum(pp => pp.Shape.Hulls.Length)} meshes={phys.Parts.Sum(pp => pp.Shape.Meshes.Length)}");

                // Entity geometry gets its own attribute entries instead of
                // merging into the world's "default" group: func_clip_vphysics
                // blocks grenades but NOT vision or bullets, so sightline
                // consumers (which select groups by name) must be able to
                // exclude it while the grenade filter keeps it solid. Doors
                // and breakables get their own groups for the same reason:
                // both are solid at round start (this mesh's baseline), but a
                // round where the glass got shot out or the door stands open
                // is a different world - the solver offers that as a per-query
                // "broken" toggle by excluding these groups.
                var attrName = className switch
                {
                    "func_clip_vphysics" => "EntityPhysicsClip",
                    "func_door" or "func_door_rotating" or "prop_door_rotating" => "EntityDoor",
                    "func_breakable" => "EntityBreakable",
                    // A prop with health is breakable in game (de_nuke's vent
                    // slats, wooden shutters); one without is furniture.
                    "prop_dynamic" => "EntityBreakable",
                    _ => "EntitySolid",
                };
                var attrIndex = names.IndexOf(attrName);
                if (attrIndex < 0)
                {
                    names.Add(attrName);
                    interactAs.Add([]);
                    attrIndex = names.Count - 1;
                    if (attrIndex > byte.MaxValue)
                    {
                        throw new InvalidDataException("merged collision attribute table exceeds byte index range");
                    }
                }
                var mapped = (byte)attrIndex;

                AppendPhys(phys, vertices, indices, triangleAttributes,
                    _ => mapped,
                    v => Vector3.Transform(v * scales, rotation) + origin);
            }
        }
    }

    // Static props carry their own collision hull inside their compiled model,
    // referenced from the map's world nodes (m_renderableModel + m_vTransform
    // per placement) rather than from the entity lump - so brush-entity
    // extraction above never sees them. On brush-built maps like de_dust2 that
    // costs nothing (walls are world geometry); on prop-dressed maps like
    // de_cache, most of the actual architecture IS a static prop, so skipping
    // this left big gaps: silently-unwalkable ledges (no collision to stand
    // on) and a radar that only shows the coarse level-blocking volume instead
    // of the real walls. m_vTransform is the prop's FULL placement matrix
    // (position, rotation, and scale together), unlike a brush entity's
    // origin/angles-only transform.
    static void AppendStaticProps(
        Package package,
        Package? sharedPackage,
        List<float> vertices,
        List<int> indices,
        List<byte> triangleAttributes,
        List<string> names,
        List<string[]> interactAs)
    {
        var worldEntry = FindEntries(package, "vwrld_c")
            .FirstOrDefault(e => e.GetFullPath().EndsWith("world.vwrld_c", StringComparison.OrdinalIgnoreCase));
        if (worldEntry == null)
        {
            return;
        }
        package.ReadEntry(worldEntry, out var worldRaw);
        using var worldResource = new Resource();
        worldResource.Read(new MemoryStream(worldRaw));
        var world = (World)worldResource.DataBlock!;

        var worldNodesByPath = FindEntries(package, "vwnod_c")
            .ToDictionary(e => e.GetFullPath().ToLowerInvariant(), e => e);

        // Model and physics payloads are looked up in the map's own VPK first,
        // falling back to the shared content archive - most static prop models
        // live only in the latter (see the pak01_dir.vpk comment above).
        Dictionary<string, PackageEntry>? modelsByPath = null;
        Dictionary<string, PackageEntry>? sharedModelsByPath = null;
        (Package Package, PackageEntry Entry)? FindModel(string path)
        {
            modelsByPath ??= FindEntries(package, "vmdl_c").ToDictionary(e => e.GetFullPath().ToLowerInvariant(), e => e);
            if (modelsByPath.TryGetValue(path, out var localEntry))
            {
                return (package, localEntry);
            }
            if (sharedPackage == null)
            {
                return null;
            }
            sharedModelsByPath ??= FindEntries(sharedPackage, "vmdl_c").ToDictionary(e => e.GetFullPath().ToLowerInvariant(), e => e);
            return sharedModelsByPath.TryGetValue(path, out var sharedEntry) ? (sharedPackage, sharedEntry) : null;
        }

        Dictionary<string, PackageEntry>? physByPath = null;
        Dictionary<string, PackageEntry>? sharedPhysByPath = null;
        (Package Package, PackageEntry Entry)? FindPhys(string path)
        {
            physByPath ??= FindEntries(package, "vphys_c").ToDictionary(e => e.GetFullPath().ToLowerInvariant(), e => e);
            if (physByPath.TryGetValue(path, out var localEntry))
            {
                return (package, localEntry);
            }
            if (sharedPackage == null)
            {
                return null;
            }
            sharedPhysByPath ??= FindEntries(sharedPackage, "vphys_c").ToDictionary(e => e.GetFullPath().ToLowerInvariant(), e => e);
            return sharedPhysByPath.TryGetValue(path, out var sharedEntry) ? (sharedPackage, sharedEntry) : null;
        }

        // Same model gets placed many times (crates, trim, foliage); reading
        // and re-parsing its compiled resource per instance would be wasted
        // work the same geometry pays for every single placement.
        var physByModel = new Dictionary<string, PhysAggregateData?>();

        var attrIndex = names.IndexOf("EntitySolid");
        if (attrIndex < 0)
        {
            names.Add("EntitySolid");
            interactAs.Add([]);
            attrIndex = names.Count - 1;
            if (attrIndex > byte.MaxValue)
            {
                throw new InvalidDataException("merged collision attribute table exceeds byte index range");
            }
        }
        var mapped = (byte)attrIndex;

        foreach (var worldNodeName in world.GetWorldNodeNames())
        {
            // World.GetWorldNodeNames() returns backslash-separated paths;
            // every VPK entry (and everywhere else this codebase reads one) is
            // forward-slash.
            if (worldNodeName == null ||
                !worldNodesByPath.TryGetValue((worldNodeName.Replace('\\', '/') + ".vwnod_c").ToLowerInvariant(), out var nodeEntry))
            {
                continue;
            }
            package.ReadEntry(nodeEntry, out var nodeRaw);
            using var nodeResource = new Resource();
            nodeResource.Read(new MemoryStream(nodeRaw));
            var worldNode = (WorldNode)nodeResource.DataBlock!;

            foreach (var sceneObject in worldNode.SceneObjects)
            {
                var model = sceneObject.GetStringProperty("m_renderableModel");
                if (string.IsNullOrEmpty(model))
                {
                    continue;
                }
                if (!physByModel.TryGetValue(model, out var phys))
                {
                    phys = null;
                    if (FindModel((model + "_c").ToLowerInvariant()) is { } modelHit)
                    {
                        modelHit.Package.ReadEntry(modelHit.Entry, out var modelRaw);
                        using var modelResource = new Resource();
                        modelResource.Read(new MemoryStream(modelRaw));
                        var modelData = (Model)modelResource.DataBlock!;
                        phys = modelData.GetEmbeddedPhys();
                        // Most static props reference their collision hull as a
                        // separate compiled .vphys_c instead of embedding it -
                        // embedded phys is the exception (world_physics.vmdl_c),
                        // not the rule, for ordinary placed models.
                        var refPhysName = phys == null ? modelData.GetReferencedPhysNames().FirstOrDefault() : null;
                        if (refPhysName != null && FindPhys((refPhysName + "_c").ToLowerInvariant()) is { } physHit)
                        {
                            physHit.Package.ReadEntry(physHit.Entry, out var physRaw);
                            using var physResource = new Resource();
                            physResource.Read(new MemoryStream(physRaw));
                            phys = physResource.DataBlock as PhysAggregateData;
                        }
                    }
                    physByModel[model] = phys;
                }
                if (phys == null)
                {
                    continue;
                }

                var matrix = sceneObject.GetArray("m_vTransform").ToMatrix4x4();
                AppendPhys(phys, vertices, indices, triangleAttributes,
                    _ => mapped,
                    v => Vector3.Transform(v, matrix));
            }

            // AggregateSceneObjects (props auto-combined into one shared draw
            // call for GPU efficiency - a very common treatment for repeated
            // architectural trim/wall-kit pieces) are deliberately NOT handled
            // here. Their m_renderableModel points to a per-worldnode combined
            // VISUAL mesh; each fragment carries only a draw-call index and a
            // transform, with no reference back to the original individual
            // prop's model or physics data - that identity is discarded by
            // the aggregation step. There is no collision to recover from
            // this data at all; if an aggregated prop needs collision, CS2's
            // compiler must be relying on it already being present in
            // world_physics.vmdl_c.
        }
    }

    /// <summary>
    /// Source engine QAngle (pitch, yaw, roll in degrees) to rotation matrix,
    /// applied yaw (Z) then pitch (Y) then roll (X), matching AngleMatrix.
    /// </summary>
    static Matrix4x4 SourceAngleMatrix(Vector3 angles)
    {
        var pitch = angles.X * MathF.PI / 180f;
        var yaw = angles.Y * MathF.PI / 180f;
        var roll = angles.Z * MathF.PI / 180f;
        return Matrix4x4.CreateRotationX(roll) * Matrix4x4.CreateRotationY(pitch) * Matrix4x4.CreateRotationZ(yaw);
    }

    /// <summary>
    /// Dumps every entity from the map's entity lumps; downstream analysis filters
    /// for doors, spawns, and other sightline anchors.
    /// </summary>
    public static List<MapEntity> ExtractEntities(string mapVpkPath)
    {
        using var package = new Package();
        package.Read(mapVpkPath);

        var entities = new List<MapEntity>();
        foreach (var entry in FindEntries(package, "vents_c"))
        {
            package.ReadEntry(entry, out var raw);
            using var resource = new Resource();
            resource.Read(new MemoryStream(raw));
            var lump = (EntityLump)resource.DataBlock!;
            foreach (var entity in lump.GetEntities())
            {
                var origin = entity.GetVector3Property("origin", Vector3.Zero);
                var angles = entity.GetVector3Property("angles", Vector3.Zero);
                entities.Add(new MapEntity(
                    entity.GetStringProperty("classname") ?? string.Empty,
                    entity.GetStringProperty("targetname") ?? string.Empty,
                    [origin.X, origin.Y, origin.Z],
                    [angles.X, angles.Y, angles.Z],
                    entity.GetStringProperty("model") ?? string.Empty,
                    entity.GetStringProperty("place_name") ?? string.Empty));
            }
        }
        return entities;
    }

    /// <summary>
    /// Walkable nav areas for the standing player hull, from the map's .nav file
    /// (parsed by ValveResourceFormat, which supports the v36 format).
    /// </summary>
    public static List<NavAreaDump> ExtractNavAreas(byte[] navData)
    {
        var nav = new ValveResourceFormat.NavMesh.NavMeshFile();
        nav.Read(new MemoryStream(navData));
        var areas = new List<NavAreaDump>();
        foreach (var area in nav.GetHullAreas(0) ?? [])
        {
            areas.Add(new NavAreaDump(
                area.AreaId,
                [.. area.Corners.Select(c => new[] { c.X, c.Y, c.Z })]));
        }
        return areas;
    }

    /// <summary>Dumps the raw .nav navigation mesh file packed inside the map VPK.</summary>
    public static byte[] ExtractNavFile(string mapVpkPath, string mapName)
    {
        using var package = new Package();
        package.Read(mapVpkPath);
        var entry = FindEntries(package, "nav")
            .FirstOrDefault(e => e.GetFullPath() == $"maps/{mapName}.nav")
            ?? throw new FileNotFoundException($"no maps/{mapName}.nav inside {mapVpkPath}");
        package.ReadEntry(entry, out var raw);
        return raw;
    }

    /// <summary>
    /// Fan-triangulates each convex hull face by walking its half-edge loop.
    /// </summary>
    static void TriangulateHull(
        ValveResourceFormat.ResourceTypes.RubikonPhysics.HullDescriptor hullDescriptor,
        List<float> vertices,
        List<int> indices,
        List<byte> triangleAttributes,
        Func<int, byte> attributeMap,
        Func<Vector3, Vector3> transform)
    {
        var hull = hullDescriptor.Shape;
        var positions = hull.GetVertexPositions();
        var edges = hull.GetEdges();
        var faces = hull.GetFaces();
        ProbeShape("hull", hullDescriptor, positions, transform);
        if (Diagnostics != null)
        {
            var flags = hull.Data.GetIntegerProperty("m_nFlags");
            HullFlagHistogram[flags] = HullFlagHistogram.GetValueOrDefault(flags) + 1;
        }

        var baseIndex = vertices.Count / 3;
        foreach (var raw in positions)
        {
            var v = transform(raw);
            vertices.Add(v.X);
            vertices.Add(v.Y);
            vertices.Add(v.Z);
        }

        foreach (var face in faces)
        {
            int startEdge = face.Edge;
            int first = edges[startEdge].Origin;
            var previous = -1;
            for (var e = edges[startEdge].Next; e != startEdge; e = edges[e].Next)
            {
                int current = edges[e].Origin;
                if (previous >= 0 && previous != first && current != first)
                {
                    indices.Add(baseIndex + first);
                    indices.Add(baseIndex + previous);
                    indices.Add(baseIndex + current);
                    triangleAttributes.Add(attributeMap(hullDescriptor.CollisionAttributeIndex));
                }
                previous = current;
            }
        }
    }

    static IEnumerable<PackageEntry> FindEntries(Package package, string extension) =>
        package.Entries != null && package.Entries.TryGetValue(extension, out var entries) ? entries : [];
}
