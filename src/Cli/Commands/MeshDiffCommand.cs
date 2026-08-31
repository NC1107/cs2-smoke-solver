using System.Collections.Concurrent;
using System.Globalization;
using System.Numerics;
using System.Text.Json;
using SmokeSolver.Sim;
using SmokeSolver.Solver;

using static SmokeSolver.Cli.CliParsing;
using static SmokeSolver.Cli.MeshSetup;
namespace SmokeSolver.Cli;

/// <summary>
/// Compares the map's PHYSICS collision mesh (what a grenade actually bounces
/// off) against its RENDER geometry (what a player actually sees), over an XY
/// grid bounded to the walkable nav band. A mismatch is either a grenade
/// flying through a wall the player can see (render has a surface, physics
/// does not - the dangerous kind) or a grenade bouncing off something
/// invisible (physics has a surface, render does not - a phantom bounce).
///
/// This is what found the motivating bug: a de_nuke corridor throw settles
/// ~800u short of its real in-game rest point, and nothing in the solver's
/// own trajectory math explained it - the physics mesh has a surface there
/// the render mesh does not.
/// </summary>
public static class MeshDiffCommand
{
    // Grenade-clip-family layers a mapper places on purpose with no visual
    // twin (invisible blockers sealing a window or route) - these are
    // expected physics/render mismatches, not bugs, so they are excluded from
    // the diff outright rather than reported as findings. playerclip/npcclip
    // are already excluded by GrenadeSolidFilter (they do not stop a
    // grenade); these two do stop one on purpose.
    static readonly string[] ByDesignInvisibleLayers = ["csgo_grenadeclip", "blocksound"];

    // Editor helpers and particle cards, which the 3D viewer also strips out of
    // the textured GLB (see viewer/js/textured-scene.js) - without this a
    // level-wide "toolsblocklight" plane or a smoke VFX card reads as a phantom
    // render-only surface across half the map.
    //
    // materials/dev/ is deliberately NOT in this list, unlike the viewer's copy.
    // Those are placeholder and reflectivity-checker materials, and on de_nuke
    // they are painted on real floors people walk on. Dropping them left the
    // render side of the diff with no surface under large stretches of ground,
    // so the tool reported the map's actual floor as phantom physics geometry -
    // orange across whole walkways. A material being ugly does not make the
    // surface it is on imaginary.
    static bool IsJunkMaterial(string materialName, string vmatPath) =>
        materialName.StartsWith("tools", StringComparison.OrdinalIgnoreCase) ||
        vmatPath.StartsWith("materials/effects/", StringComparison.OrdinalIgnoreCase) ||
        vmatPath.StartsWith("materials/tools/", StringComparison.OrdinalIgnoreCase) ||
        vmatPath.StartsWith("models/ui/", StringComparison.OrdinalIgnoreCase);

    // The vertical band scanned around each column's nav ground height: a
    // little below the floor (catches steps/slopes) and above head height
    // (catches arches, low ceilings, catwalks) without scanning the whole
    // map's Z extent and picking up unrelated upper floors or roofs.
    //
    // 320u above was high enough to sweep in structures nobody plays on - the
    // reactor shell, the scaffolding over B - and report every one of their
    // surfaces as a finding. A smoke that matters is one thrown into space a
    // player occupies or a grenade rests in, so the band stops a bit over
    // jump-plus-throw height instead.
    const float BandBelow = 96f;
    const float BandAbove = 192f;
    // How far from an actual walkable polygon a column may sit and still be
    // worth diffing. NavGapReach (96u) exists to bridge the slivers between
    // adjacent nav quads; using it here also reached out over railings into
    // out-of-bounds structures, so the diff keeps to the playable surface.
    const float NavProximity = 40f;
    // The flood that decides whether a mismatch is in play; matches the
    // solver's own sweep resolution so "open air here" means the same thing in
    // both tools.
    const float ReachVoxel = 16f;
    const int MaxCrossingsPerColumn = 16;

    public static int Run(Dictionary<string, string> options)
    {
        var mesh = CollisionMesh.Load(Require(options, "geo"));
        var navAreasPath = options.GetValueOrDefault("nav", DefaultNavAreasPath(options, mesh));
        var navAreas = LoadJson<List<NavAreaJson>>(navAreasPath, "nav areas");
        var step = float.Parse(options.GetValueOrDefault("step", "16"), CultureInfo.InvariantCulture);
        var threshold = float.Parse(options.GetValueOrDefault("threshold", "8"), CultureInfo.InvariantCulture);
        var outPath = options.GetValueOrDefault("out", $"data/{mesh.MapName}.meshdiff.json");

        // The render export is a large, fully regenerable intermediate (a raw
        // VRF export is hundreds of MB), so it is cached like the solver's
        // result cache rather than rebuilt on every run - pass --vpk again
        // (or --rebuild) only when the map's render geometry actually changed.
        var renderGlbPath = options.GetValueOrDefault("render-cache", $"data/cache/{mesh.MapName}.meshdiff-render.glb");
        if (options.TryGetValue("vpk", out var vpkPath) && (!File.Exists(renderGlbPath) || options.ContainsKey("rebuild")))
        {
            // Materials stay on (junk-material filtering needs each triangle's
            // vmat path) but texture adaptation is skipped - meshdiff only ever
            // looks at triangle positions, and adapting/encoding every texture
            // is most of exportgltf's cost for no benefit here.
            ExportGltfCommand.ExportGeometry(Path.GetFullPath(vpkPath), renderGlbPath, exportMaterials: true, adaptTextures: false, exportExtras: true,
                new Progress<string>(s => Console.WriteLine($"  {s}")));
        }
        if (!File.Exists(renderGlbPath))
        {
            Console.Error.WriteLine($"no render mesh at {renderGlbPath}; pass --vpk <map.vpk> to build one");
            return 2;
        }

        Console.WriteLine($"loading render mesh from {renderGlbPath}...");
        var (renderMesh, junkTriangles) = LoadRenderMesh(renderGlbPath);
        Console.WriteLine($"  {renderMesh.TriangleCount} render triangles ({junkTriangles} tools/dev/VFX triangles dropped)");

        // Bounded to the physics mesh's own extent, not the render mesh's: the
        // render export also carries the 3D skybox (a scaled miniature sitting
        // far outside the playable coordinates), which would otherwise blow up
        // the query volume for no useful comparison.
        var (physMin, physMax) = mesh.ComputeBounds();
        var margin = new Vector3(256f);
        var lo = physMin - margin;
        var hi = physMax + margin;

        var physicsCollider = new TriangleCollider(mesh, lo, hi, DiffPhysicsFilter(mesh));
        var renderCollider = new TriangleCollider(renderMesh, lo, hi, null);

        // Bucketed nav-corner lookup, the same technique viewerdata's radar
        // uses: gives each column a nearby ground height without an
        // O(nav areas) scan per column.
        const float BucketSize = 256f;
        var bw = (int)MathF.Ceiling((hi.X - lo.X) / BucketSize) + 1;
        var bh = (int)MathF.Ceiling((hi.Y - lo.Y) / BucketSize) + 1;
        var buckets = new List<float[][]>[bw * bh];
        foreach (var area in navAreas)
        {
            var c = area.Corners;
            var bx0 = Math.Clamp((int)((c.Min(p => p[0]) - lo.X - LineupSolver.NavGapReach) / BucketSize), 0, bw - 1);
            var bx1 = Math.Clamp((int)((c.Max(p => p[0]) - lo.X + LineupSolver.NavGapReach) / BucketSize), 0, bw - 1);
            var by0 = Math.Clamp((int)((c.Min(p => p[1]) - lo.Y - LineupSolver.NavGapReach) / BucketSize), 0, bh - 1);
            var by1 = Math.Clamp((int)((c.Max(p => p[1]) - lo.Y + LineupSolver.NavGapReach) / BucketSize), 0, bh - 1);
            for (var by = by0; by <= by1; by++)
            {
                for (var bx = bx0; bx <= bx1; bx++)
                {
                    (buckets[by * bw + bx] ??= []).Add(c);
                }
            }
        }

        var gw = (int)MathF.Ceiling((hi.X - lo.X) / step);
        var gh = (int)MathF.Ceiling((hi.Y - lo.Y) / step);
        Console.WriteLine($"scanning {gw}x{gh} columns at {step}u...");

        var cells = new ConcurrentBag<float[]>();
        var groundSeeds = new ConcurrentBag<Vector3>();
        var columnsScanned = 0;
        Parallel.For(0, gh, Cpu.Bound, gy =>
        {
            for (var gx = 0; gx < gw; gx++)
            {
                var wx = lo.X + (gx + 0.5f) * step;
                var wy = lo.Y + (gy + 0.5f) * step;
                var bucket = buckets[
                    Math.Clamp((int)((wy - lo.Y) / BucketSize), 0, bh - 1) * bw +
                    Math.Clamp((int)((wx - lo.X) / BucketSize), 0, bw - 1)];
                // Outside the nav band entirely - not a place a grenade rest
                // point or the player is ever expected to be, so there is
                // nothing worth diffing at this column.
                if (bucket == null || LineupSolver.NavGroundZWithin(bucket, wx, wy, NavProximity) is not { } ground)
                {
                    continue;
                }
                Interlocked.Increment(ref columnsScanned);
                groundSeeds.Add(new Vector3(wx, wy, ground));
                var zLo = ground - BandBelow;
                var zHi = ground + BandAbove;
                var physicsHeights = CrossingHeights(physicsCollider, wx, wy, zLo, zHi);
                var renderHeights = CrossingHeights(renderCollider, wx, wy, zLo, zHi);
                foreach (var ph in physicsHeights)
                {
                    if (!renderHeights.Any(rh => MathF.Abs(rh - ph) <= threshold))
                    {
                        // kind 1: physics has a surface render lacks - a phantom bounce.
                        cells.Add([MathF.Round(wx, 1), MathF.Round(wy, 1), MathF.Round(ph, 1), 1]);
                    }
                }
                foreach (var rh in renderHeights)
                {
                    if (!physicsHeights.Any(ph => MathF.Abs(rh - ph) <= threshold))
                    {
                        // kind 0: render has a surface physics lacks - grenades fly through it.
                        cells.Add([MathF.Round(wx, 1), MathF.Round(wy, 1), MathF.Round(rh, 1), 0]);
                    }
                }
            }
        });

        var cellList = cells.ToList();
        var beforeReach = cellList.Count;
        cellList = KeepReachable(cellList, mesh, lo, hi, groundSeeds, step);

        var kind0 = cellList.Count(c => c[3] == 0);
        var kind1 = cellList.Count(c => c[3] == 1);
        Console.WriteLine(
            $"  {columnsScanned} columns in the nav band, {cellList.Count} mismatched cells " +
            $"({kind0} render-only [grenades fly through], {kind1} physics-only [phantom bounces]); " +
            $"{beforeReach - cellList.Count} dropped as out of bounds");

        // The surfaces themselves, not just the columns they were found in. A
        // cell says "the meshes disagree here"; a player looking at the overlay
        // wants to see the wall the solver is missing, in the shape it actually
        // has. Gathered from whichever mesh owns the surface: render for the
        // ones grenades fly through, physics for the phantom bounces.
        var renderTris = TrianglesNear(renderMesh, null, cellList, kind: 0, lo, step);
        var physicsTris = TrianglesNear(mesh, DiffPhysicsFilter(mesh), cellList, kind: 1, lo, step);
        Console.WriteLine(
            $"  surfaces: {renderTris.Count / 9} render-only triangles, {physicsTris.Count / 9} physics-only triangles");

        var payload = new { map = mesh.MapName, step, cells = cellList, renderTris, physicsTris };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        File.WriteAllText(outPath, JsonSerializer.Serialize(payload));
        Console.WriteLine($"wrote {outPath}");
        return 0;
    }

    // Every triangle of <paramref name="source"/> that passes through a cell
    // the diff flagged with <paramref name="kind"/>, flattened to nine floats
    // each for the viewer.
    //
    // Walked per triangle rather than per cell because a triangle's own AABB
    // is the cheap thing to quantise, and the flagged set is a hash lookup.
    // A few triangles in a map are enormous (a whole hangar floor); those get
    // their corners and centre tested instead of a span that would be most of
    // the grid, which can only ever miss a surface, never invent one.
    const int MaxCellSpan = 4096;

    static List<float> TrianglesNear(
        CollisionMesh source, Func<byte, bool>? filter, List<float[]> cells, float kind, Vector3 lo, float step)
    {
        var flagged = new HashSet<(int, int, int)>();
        foreach (var c in cells)
        {
            if (c[3] == kind)
            {
                flagged.Add(CellKey(new Vector3(c[0], c[1], c[2]), lo, step));
            }
        }
        if (flagged.Count == 0)
        {
            return [];
        }

        var verts = source.Vertices;
        var idx = source.Indices;
        var hits = new bool[source.TriangleCount];
        Parallel.For(0, source.TriangleCount, Cpu.Bound, t =>
        {
            if (filter != null && !filter(source.TriangleAttributes[t]))
            {
                return;
            }
            var a = VertexAt(verts, idx[t * 3]);
            var b = VertexAt(verts, idx[t * 3 + 1]);
            var c = VertexAt(verts, idx[t * 3 + 2]);
            var min = Vector3.Min(a, Vector3.Min(b, c));
            var max = Vector3.Max(a, Vector3.Max(b, c));
            var (x0, y0, z0) = CellKey(min, lo, step);
            var (x1, y1, z1) = CellKey(max, lo, step);
            var span = (long)(x1 - x0 + 1) * (y1 - y0 + 1) * (z1 - z0 + 1);
            if (span > MaxCellSpan)
            {
                var centre = (a + b + c) / 3f;
                hits[t] = flagged.Contains(CellKey(a, lo, step)) || flagged.Contains(CellKey(b, lo, step)) ||
                          flagged.Contains(CellKey(c, lo, step)) || flagged.Contains(CellKey(centre, lo, step));
                return;
            }
            for (var z = z0; z <= z1 && !hits[t]; z++)
            {
                for (var y = y0; y <= y1 && !hits[t]; y++)
                {
                    for (var x = x0; x <= x1; x++)
                    {
                        if (flagged.Contains((x, y, z)))
                        {
                            hits[t] = true;
                            break;
                        }
                    }
                }
            }
        });

        var outp = new List<float>();
        for (var t = 0; t < hits.Length; t++)
        {
            if (!hits[t])
            {
                continue;
            }
            for (var k = 0; k < 3; k++)
            {
                var v = VertexAt(verts, idx[t * 3 + k]);
                // Whole units: this is an overlay painted over the surface it
                // describes, and a tenth of a unit of extra precision costs
                // more payload than the eye can use.
                outp.Add(MathF.Round(v.X));
                outp.Add(MathF.Round(v.Y));
                outp.Add(MathF.Round(v.Z));
            }
        }
        return outp;
    }

    static Vector3 VertexAt(float[] verts, int i) => new(verts[i * 3], verts[i * 3 + 1], verts[i * 3 + 2]);

    static (int, int, int) CellKey(Vector3 p, Vector3 lo, float step) => (
        (int)MathF.Floor((p.X - lo.X) / step),
        (int)MathF.Floor((p.Y - lo.Y) / step),
        (int)MathF.Floor((p.Z - lo.Z) / step));

    // Drops the mismatches a grenade could never reach: cells sealed inside
    // walls and props, and cells in the dead space outside the playable map.
    //
    // Standing near a walkable polygon is not the same as being in play. The
    // column filter admits a whole vertical band around each nav point, so it
    // also admits the inside of the wall beside it and the void under a
    // catwalk - places where the two meshes disagree constantly and where no
    // smoke will ever land. Flooding open air outward from the walkable
    // surface answers the only question that matters for a finding: can a
    // grenade actually be in this space? Everything the flood never reaches is
    // out of bounds by construction, with no hand-tuned distance involved.
    static List<float[]> KeepReachable(
        List<float[]> cells, CollisionMesh mesh, Vector3 lo, Vector3 hi,
        IEnumerable<Vector3> groundSeeds, float step)
    {
        var grid = VoxelGrid.Build(mesh, ReachVoxel, lo, hi, DiffPhysicsFilter(mesh));
        var reached = new bool[grid.Nx * grid.Ny * grid.Nz];
        var queue = new Queue<int>();

        void Seed(Vector3 point)
        {
            var (x, y, z) = grid.CellOf(point);
            if (!grid.InBounds(x, y, z))
            {
                return;
            }
            var index = grid.Index(x, y, z);
            if (reached[index] || grid.IsSolid(index))
            {
                return;
            }
            reached[index] = true;
            queue.Enqueue(index);
        }

        // Seeded a voxel above the floor, not on it: the ground height sits on
        // the surface, which usually quantises into the solid voxel underneath.
        foreach (var ground in groundSeeds)
        {
            Seed(ground with { Z = ground.Z + ReachVoxel });
        }

        var layerXY = grid.Nx * grid.Ny;
        while (queue.Count > 0)
        {
            var cell = queue.Dequeue();
            var z = cell / layerXY;
            var rem = cell - z * layerXY;
            var y = rem / grid.Nx;
            var x = rem - y * grid.Nx;

            void Visit(int nx, int ny, int nz)
            {
                if (nx < 0 || ny < 0 || nz < 0 || nx >= grid.Nx || ny >= grid.Ny || nz >= grid.Nz)
                {
                    return;
                }
                var index = grid.Index(nx, ny, nz);
                if (reached[index] || grid.IsSolid(index))
                {
                    return;
                }
                reached[index] = true;
                queue.Enqueue(index);
            }

            Visit(x - 1, y, z);
            Visit(x + 1, y, z);
            Visit(x, y - 1, z);
            Visit(x, y + 1, z);
            Visit(x, y, z - 1);
            Visit(x, y, z + 1);
        }

        bool OpenAt(Vector3 point)
        {
            var (x, y, z) = grid.CellOf(point);
            return grid.InBounds(x, y, z) && reached[grid.Index(x, y, z)];
        }

        // Checked on both sides of the surface: a phantom physics wall fills
        // its own voxel, so the reachable air is the cell in front of it, and
        // which side that is depends on which mesh drew the surface.
        return cells
            .Where(c =>
            {
                var point = new Vector3(c[0], c[1], c[2]);
                return OpenAt(point with { Z = point.Z + step }) ||
                       OpenAt(point with { Z = point.Z - step }) ||
                       OpenAt(point with { X = point.X + step }) ||
                       OpenAt(point with { X = point.X - step }) ||
                       OpenAt(point with { Y = point.Y + step }) ||
                       OpenAt(point with { Y = point.Y - step });
            })
            .ToList();
    }

    // Walks straight down from the top of the band, recording every surface
    // crossing, then resumes just below it - a single FirstHit only finds the
    // nearest one, and a column can have several (a catwalk over a floor).
    static List<float> CrossingHeights(TriangleCollider collider, float wx, float wy, float zLo, float zHi)
    {
        var heights = new List<float>();
        var top = zHi;
        while (top > zLo && heights.Count < MaxCrossingsPerColumn)
        {
            if (collider.FirstHit(new Vector3(wx, wy, top), new Vector3(wx, wy, zLo)) is not { } hit)
            {
                break;
            }
            var z = top + (zLo - top) * hit.T;
            heights.Add(z);
            top = z - 1f;
        }
        return heights;
    }

    // What a grenade actually bounces off, minus the layers that are
    // invisible by design and so are expected to have no render twin.
    static Func<byte, bool> DiffPhysicsFilter(CollisionMesh mesh)
    {
        var grenadeSolid = mesh.GrenadeSolidFilter();
        var solid = new bool[mesh.AttributeNames.Length];
        for (var i = 0; i < solid.Length; i++)
        {
            solid[i] = grenadeSolid((byte)i) &&
                !mesh.AttributeInteractAs[i].Any(layer => ByDesignInvisibleLayers.Contains(layer, StringComparer.OrdinalIgnoreCase)) &&
                !mesh.AttributeNames[i].Equals("EntityPhysicsClip", StringComparison.Ordinal);
        }
        return a => solid[a];
    }

    // Walks the render GLB's default scene, converting VRF's meter-space
    // Y-up export back into Hammer units (the confirmed axis permutation from
    // viewer/js/textured-scene.js: Hammer_X=raw_z, Hammer_Y=raw_x,
    // Hammer_Z=raw_y, scaled by 1/0.0254) and dropping the same editor/debug/
    // VFX materials the 3D viewer already strips before rendering.
    static (CollisionMesh Mesh, int JunkTriangles) LoadRenderMesh(string glbPath)
    {
        const float MetresToHammer = 1f / 0.0254f;
        var root = SharpGLTF.Schema2.ModelRoot.Load(glbPath);
        var vertices = new List<float>();
        var indices = new List<int>();
        var junkTriangles = 0;
        foreach (var node in SharpGLTF.Schema2.Node.Flatten(root.DefaultScene))
        {
            if (node.Mesh == null)
            {
                continue;
            }
            var worldMatrix = node.WorldMatrix;
            foreach (var primitive in node.Mesh.Primitives)
            {
                var triangleCount = primitive.GetTriangleIndices().Count();
                if (IsJunkMaterial(primitive.Material?.Name ?? "", VmatPath(primitive.Material)))
                {
                    junkTriangles += triangleCount;
                    continue;
                }
                if (primitive.GetVertexAccessor("POSITION")?.AsVector3Array() is not { } positions)
                {
                    continue;
                }
                var baseIndex = vertices.Count / 3;
                foreach (var local in positions)
                {
                    var world = Vector3.Transform(local, worldMatrix);
                    var hammer = new Vector3(world.Z, world.X, world.Y) * MetresToHammer;
                    vertices.Add(hammer.X);
                    vertices.Add(hammer.Y);
                    vertices.Add(hammer.Z);
                }
                foreach (var (a, b, c) in primitive.GetTriangleIndices())
                {
                    indices.Add(baseIndex + a);
                    indices.Add(baseIndex + b);
                    indices.Add(baseIndex + c);
                }
            }
        }
        var collisionMesh = new CollisionMesh
        {
            MapName = "render",
            GameBuildId = "",
            Vertices = [.. vertices],
            Indices = [.. indices],
            TriangleAttributes = new byte[indices.Count / 3],
            AttributeNames = ["Render"],
            AttributeInteractAs = [[]],
        };
        return (collisionMesh, junkTriangles);
    }

    // The original .vmat path VRF preserves as material extras - the same
    // field rig/glb-lib.mjs's vmatName() reads and the 3D viewer's material
    // filter keys off. A material whose extras are not the expected shape
    // (no vmat block at all) has nothing to classify by; treat it as kept.
    static string VmatPath(SharpGLTF.Schema2.Material? material)
    {
        try
        {
            return material?.Extras?["vmat"]?["Name"]?.ToString() ?? "";
        }
        catch (InvalidOperationException)
        {
            return "";
        }
    }
}
