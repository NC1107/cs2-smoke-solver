using System.Numerics;
using System.Text;

namespace SmokeSolver.Sim;

/// <summary>
/// Physics collision triangle soup for one map, the intermediate format between extraction and sim.
/// Keyed to the game build it was extracted from so stale data is detectable after game updates.
/// </summary>
public sealed class CollisionMesh
{
    const string Magic = "S2SSGEO2";
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

    public int TriangleCount => Indices.Length / 3;

    /// <summary>
    /// Attribute filter for grenade flight, validated against real per-tick
    /// trajectories on de_dust2 (301 real bounce events): player/NPC clips and
    /// sky volumes do not block grenades (281 and 11 observed fly-throughs);
    /// everything else, including csgo_grenadeclip and passbullets, does.
    /// </summary>
    public Func<byte, bool> GrenadeSolidFilter()
    {
        var solid = new bool[AttributeNames.Length];
        for (var i = 0; i < solid.Length; i++)
        {
            solid[i] = !AttributeInteractAs[i].Any(layer =>
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
                !layers.Any(l => l.Equals("csgo_grenadeclip", StringComparison.OrdinalIgnoreCase)) &&
                !AttributeNames[i].Equals("EntityPhysicsClip", StringComparison.Ordinal);
        }
        return a => solid[a];
    }

    public (Vector3 Min, Vector3 Max) ComputeBounds()
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        for (var i = 0; i < Vertices.Length; i += 3)
        {
            var v = new Vector3(Vertices[i], Vertices[i + 1], Vertices[i + 2]);
            min = Vector3.Min(min, v);
            max = Vector3.Max(max, v);
        }
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
        if (magic != Magic && magic != MagicV1)
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
        if (magic == Magic)
        {
            for (var i = 0; i < interactAs.Length; i++)
            {
                interactAs[i] = new string[reader.ReadInt32()];
                for (var j = 0; j < interactAs[i].Length; j++)
                {
                    interactAs[i][j] = reader.ReadString();
                }
            }
        }
        else
        {
            // V1 files lack interaction layers; re-extract to get correct
            // grenade clipping. Treat every group as plain solid until then.
            for (var i = 0; i < interactAs.Length; i++)
            {
                interactAs[i] = [];
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
            Vertices = vertices,
            Indices = indices,
            TriangleAttributes = triangleAttributes,
        };
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
