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
        var attributeInteractAs = phys.CollisionAttributes
            .Select(kv =>
            {
                try
                {
                    return kv.GetArray<string>("m_InteractAsStrings") ?? [];
                }
                catch (KeyNotFoundException)
                {
                    return [];
                }
            })
            .ToArray();
        if (attributeNames.Length > byte.MaxValue)
        {
            throw new InvalidDataException($"{attributeNames.Length} collision attributes exceed the byte-sized attribute index");
        }

        var vertices = new List<float>();
        var indices = new List<int>();
        var triangleAttributes = new List<byte>();
        var names = attributeNames.ToList();
        var interactAs = attributeInteractAs.ToList();

        AppendPhys(phys, vertices, indices, triangleAttributes, i => (byte)i, v => v);
        AppendSolidEntityModels(package, vertices, indices, triangleAttributes, names, interactAs);

        return new CollisionMesh
        {
            MapName = mapName,
            GameBuildId = gameBuildId,
            Vertices = [.. vertices],
            Indices = [.. indices],
            TriangleAttributes = [.. triangleAttributes],
            AttributeNames = [.. names],
            AttributeInteractAs = [.. interactAs],
        };
    }

    static void AppendPhys(
        PhysAggregateData phys,
        List<float> vertices,
        List<int> indices,
        List<byte> triangleAttributes,
        Func<int, byte> attributeMap,
        Func<Vector3, Vector3> transform)
    {
        foreach (var part in phys.Parts)
        {
            foreach (var meshDescriptor in part.Shape.Meshes)
            {
                var mesh = meshDescriptor.Shape;
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
    static readonly string[] SolidEntityClasses = ["func_brush", "func_clip_vphysics", "func_door", "func_door_rotating", "func_breakable"];

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
        List<float> vertices,
        List<int> indices,
        List<byte> triangleAttributes,
        List<string> names,
        List<string[]> interactAs)
    {
        // Lazy: built once, replacing an O(entities x entries) rescan per entity.
        Dictionary<string, SteamDatabase.ValvePak.PackageEntry>? modelsByPath = null;
        foreach (var lumpEntry in FindEntries(package, "vents_c"))
        {
            package.ReadEntry(lumpEntry, out var lumpRaw);
            using var lumpResource = new Resource();
            lumpResource.Read(new MemoryStream(lumpRaw));
            foreach (var entity in ((EntityLump)lumpResource.DataBlock!).GetEntities())
            {
                var className = entity.GetStringProperty("classname") ?? string.Empty;
                var model = entity.GetStringProperty("model") ?? string.Empty;
                if (!SolidEntityClasses.Contains(className) || model.Length == 0)
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
                modelsByPath ??= FindEntries(package, "vmdl_c")
                    .ToDictionary(e => e.GetFullPath().ToLowerInvariant(), e => e);
                modelsByPath.TryGetValue((model + "_c").ToLowerInvariant(), out var modelEntry);
                if (modelEntry == null)
                {
                    continue;
                }
                package.ReadEntry(modelEntry, out var raw);
                using var resource = new Resource();
                resource.Read(new MemoryStream(raw));
                var phys = ((Model)resource.DataBlock!).GetEmbeddedPhys();
                if (phys == null)
                {
                    continue;
                }

                var origin = entity.GetVector3Property("origin", Vector3.Zero);
                var angles = entity.GetVector3Property("angles", Vector3.Zero);
                var rotation = SourceAngleMatrix(angles);

                // Entity geometry gets its own attribute entries instead of
                // merging into the world's "default" group: func_clip_vphysics
                // blocks grenades but NOT vision or bullets, so sightline
                // consumers (which select groups by name) must be able to
                // exclude it while the grenade filter keeps it solid.
                var attrName = className == "func_clip_vphysics" ? "EntityPhysicsClip" : "EntitySolid";
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
                    v => Vector3.Transform(v, rotation) + origin);
            }
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
