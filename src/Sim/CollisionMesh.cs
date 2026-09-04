using System.Numerics;
using System.Text;

namespace SmokeSolver.Sim;

/// <summary>
/// Physics collision triangle soup for one map, the intermediate format between extraction and sim.
/// Keyed to the game build it was extracted from so stale data is detectable after game updates.
/// </summary>
public sealed class CollisionMesh
{
    const string Magic = "S2SSGEO3";
    const string MagicV2 = "S2SSGEO2";
    const string MagicV1 = "S2SSGEO1";

    public required string MapName { get; init; }
    public required string GameBuildId { get; init; }
    public required float[] Vertices { get; init; }
    public required int[] Indices { get; init; }
    public required byte[] TriangleAttributes { get; init; }
    public required string[] AttributeNames { get; init; }
    // Per attribute group: the physics interaction layers it participates in
    // (m_InteractAsStrings), e.g. "playerclip", "csgo_grenadeclip", "sky".
    // Group NAMES are ambiguous ("ConditionallySolid" appears for player clips
    // AND grenade clips on the same map); the layers carry the semantics.
    public required string[][] AttributeInteractAs { get; init; }
    // Per attribute group: the interaction layers it is explicitly transparent
    // to (m_InteractExcludeStrings). A railing on de_nuke carries
    // "csgo_thrown_grenade" here and real grenades fly straight through it;
    // a grenade-only clip excludes "player". Empty for groups merged in from
    // entity and prop models. Optional so V2 files and synthetic meshes read
    // as "excludes nothing".
    public string[][] AttributeInteractExclude { get; init; } = [];

    public int TriangleCount => Indices.Length / 3;

    const string ThrownGrenadeLayer = "csgo_thrown_grenade";
    const string PlayerLayer = "player";

    bool Excludes(int attribute, string layer) =>
        attribute < AttributeInteractExclude.Length &&
        AttributeInteractExclude[attribute].Any(l => l.Equals(layer, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Attribute filter for grenade flight, validated against real per-tick
    /// trajectories on de_dust2 (301 real bounce events): player/NPC clips and
    /// sky volumes do not block grenades (281 and 11 observed fly-throughs);
    /// everything else, including csgo_grenadeclip and passbullets, does -
    /// unless the group explicitly excludes thrown grenades (de_nuke's heaven
    /// railing: 22 real throws passed through it in the corpus replay).
    /// </summary>
    public Func<byte, bool> GrenadeSolidFilter()
    {
        var solid = new bool[AttributeNames.Length];
        for (var i = 0; i < solid.Length; i++)
        {
            solid[i] = !Excludes(i, ThrownGrenadeLayer) && !AttributeInteractAs[i].Any(layer =>
                layer.Equals("playerclip", StringComparison.OrdinalIgnoreCase) ||
                layer.Equals("npcclip", StringComparison.OrdinalIgnoreCase) ||
                layer.Equals("sky", StringComparison.OrdinalIgnoreCase));
        }
        return a => solid[a];
    }

    /// <summary>
    /// Attribute filter for PLAYER movement: what stops feet, not grenades.
    /// Player clips are the geometry mappers lay along railings, stairs, and
    /// ledges to steer movement, so wall/corner pin probing must see them -
    /// probing with the grenade filter made every clip-covered railing read
    /// as open ground. The two grenade-only groups go the other way: grenade
    /// clips and func_clip_vphysics block projectiles while players walk
    /// straight through, so a "pin" against one would be fictional.
    /// </summary>
    public Func<byte, bool> PlayerSolidFilter()
    {
        var solid = new bool[AttributeNames.Length];
        for (var i = 0; i < solid.Length; i++)
        {
            var layers = AttributeInteractAs[i];
            var npcOnly = layers.Any(l => l.Equals("npcclip", StringComparison.OrdinalIgnoreCase)) &&
                !layers.Any(l => l.Equals("playerclip", StringComparison.OrdinalIgnoreCase));
            solid[i] = !npcOnly &&
                !Excludes(i, PlayerLayer) &&
                !layers.Any(l => l.Equals("csgo_grenadeclip", StringComparison.OrdinalIgnoreCase)) &&
                !AttributeNames[i].Equals("EntityPhysicsClip", StringComparison.Ordinal);
        }
        return a => solid[a];
    }

    /// <summary>
    /// Per-attribute flags for the named groups, for callers that knock out a
    /// "broken" world state (shot-out glass, opened doors) from a base filter.
    /// </summary>
    public bool[] GroupMask(IReadOnlyCollection<string> groupNames)
    {
        var mask = new bool[AttributeNames.Length];
        for (var i = 0; i < mask.Length; i++)
        {
            mask[i] = groupNames.Contains(AttributeNames[i]);
        }
        return mask;
    }

    // Memoized: the vertex arrays are init-only, so the bounds never change,
    // yet every API request used to pay a full O(vertices) scan for them. The
    // unsynchronized write is benign - concurrent first callers compute the
    // same value.
    (Vector3 Min, Vector3 Max)? _bounds;

    public (Vector3 Min, Vector3 Max) ComputeBounds()
    {
        if (_bounds is { } cached)
        {
            return cached;
        }
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        for (var i = 0; i < Vertices.Length; i += 3)
        {
            var v = new Vector3(Vertices[i], Vertices[i + 1], Vertices[i + 2]);
            min = Vector3.Min(min, v);
            max = Vector3.Max(max, v);
        }
        _bounds = (min, max);
        return (min, max);
    }

    public void Save(string path)
    {
        using var writer = new BinaryWriter(File.Create(path), Encoding.UTF8);
        writer.Write(Encoding.ASCII.GetBytes(Magic));
        writer.Write(MapName);
        writer.Write(GameBuildId);
        writer.Write(AttributeNames.Length);
        foreach (var name in AttributeNames)
        {
            writer.Write(name);
        }
        foreach (var layers in AttributeInteractAs)
        {
            writer.Write(layers.Length);
            foreach (var layer in layers)
            {
                writer.Write(layer);
            }
        }
        for (var i = 0; i < AttributeNames.Length; i++)
        {
            var excludes = i < AttributeInteractExclude.Length ? AttributeInteractExclude[i] : [];
            writer.Write(excludes.Length);
            foreach (var layer in excludes)
            {
                writer.Write(layer);
            }
        }
        writer.Write(Vertices.Length);
        foreach (var v in Vertices)
        {
            writer.Write(v);
        }
        writer.Write(Indices.Length);
        foreach (var i in Indices)
        {
            writer.Write(i);
        }
        writer.Write(TriangleAttributes.Length);
        writer.Write(TriangleAttributes);
    }

    public static CollisionMesh Load(string path)
    {
        using var reader = new BinaryReader(File.OpenRead(path), Encoding.UTF8);
        var magic = Encoding.ASCII.GetString(reader.ReadBytes(Magic.Length));
        if (magic != Magic && magic != MagicV2 && magic != MagicV1)
        {
            throw new InvalidDataException($"{path} is not a SmokeSolver geometry file (magic '{magic}')");
        }
        var mapName = reader.ReadString();
        var buildId = reader.ReadString();
        var attributeNames = new string[reader.ReadInt32()];
        for (var i = 0; i < attributeNames.Length; i++)
        {
            attributeNames[i] = reader.ReadString();
        }
        var interactAs = new string[attributeNames.Length][];
        var interactExclude = new string[attributeNames.Length][];
        for (var i = 0; i < interactAs.Length; i++)
        {
            // V1 files lack interaction layers and V2 files lack excludes;
            // re-extract for correct grenade clipping. Until then a missing
            // table reads as "plain solid" / "excludes nothing".
            interactAs[i] = magic == MagicV1 ? [] : ReadStrings(reader);
            interactExclude[i] = [];
        }
        if (magic == Magic)
        {
            for (var i = 0; i < interactExclude.Length; i++)
            {
                interactExclude[i] = ReadStrings(reader);
            }
        }
        var vertices = new float[reader.ReadInt32()];
        for (var i = 0; i < vertices.Length; i++)
        {
            vertices[i] = reader.ReadSingle();
        }
        var indices = new int[reader.ReadInt32()];
        for (var i = 0; i < indices.Length; i++)
        {
            indices[i] = reader.ReadInt32();
        }
        var triangleAttributes = reader.ReadBytes(reader.ReadInt32());
        return new CollisionMesh
        {
            MapName = mapName,
            GameBuildId = buildId,
            AttributeNames = attributeNames,
            AttributeInteractAs = interactAs,
            AttributeInteractExclude = interactExclude,
            Vertices = vertices,
            Indices = indices,
            TriangleAttributes = triangleAttributes,
        };
    }

    static string[] ReadStrings(BinaryReader reader)
    {
        var strings = new string[reader.ReadInt32()];
        for (var j = 0; j < strings.Length; j++)
        {
            strings[j] = reader.ReadString();
        }
        return strings;
    }

    public void SaveObj(string path)
    {
        using var writer = new StreamWriter(path);
        for (var i = 0; i < Vertices.Length; i += 3)
        {
            writer.WriteLine($"v {Vertices[i]} {Vertices[i + 1]} {Vertices[i + 2]}");
        }
        for (var i = 0; i < Indices.Length; i += 3)
        {
            writer.WriteLine($"f {Indices[i] + 1} {Indices[i + 1] + 1} {Indices[i + 2] + 1}");
        }
    }
}
