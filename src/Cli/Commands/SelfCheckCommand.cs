using System.Numerics;
using SmokeSolver.Sim;

namespace SmokeSolver.Cli;

/// <summary>
/// Answers "would a solve actually return anything?" - the question a liveness
/// probe cannot ask. Exits 0 when healthy, 1 with a reason on stderr otherwise.
/// </summary>
// This server has one dominant failure mode: it starts, binds, answers every
// request promptly, and returns zero lineups for all of them. An empty --attrs
// drops world geometry; a missing navareas file disables solving for a map; a
// data volume that did not mount leaves no maps at all. In every case the
// process is "up" and a TCP or HTTP-200 healthcheck reports success.
//
// A real solve would be the honest test but costs minutes of CPU, which is not
// something to spend every few minutes forever. So this checks the chain that
// silent-zero actually breaks, and does it in seconds: maps loaded, geometry
// survived the attribute filter, nav data present, and - the part that catches
// an empty filter specifically - a grenade dropped onto the map's own geometry
// actually lands on something instead of falling through a world that is not
// there.
public static class SelfCheckCommand
{
    // Far enough above the mesh to be clear of it, short enough that the drop
    // resolves quickly.
    const float DropHeight = 256f;

    public static int Run(Dictionary<string, string> options)
    {
        var root = Path.GetFullPath(options.GetValueOrDefault("root", "."));
        var dataDir = Path.Combine(root, "data");
        if (!Directory.Exists(dataDir))
        {
            Console.Error.WriteLine($"selfcheck: no {dataDir} - the data volume is missing");
            return 1;
        }

        // Deliberately NOT LoadMaps: that loads every map plus its mesh payload
        // and gzip copy, which peaks around 2 GB - more than the container is
        // allowed - so using it here would have this healthcheck OOM-kill the
        // very server it is checking. One map answers the question.
        var geoPaths = Directory.EnumerateFiles(dataDir, "*.s2geo")
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();
        if (geoPaths.Count == 0)
        {
            Console.Error.WriteLine($"selfcheck: no maps under {dataDir} - the data volume is missing or empty");
            return 1;
        }

        // A named map if asked for, otherwise the first that has nav data:
        // without nav areas a map cannot be solved at all, so checking one of
        // those would pass while telling us nothing.
        var wanted = options.GetValueOrDefault("map", "");
        var geoPath = geoPaths.FirstOrDefault(p =>
            wanted.Length > 0
                ? Path.GetFileNameWithoutExtension(p).Equals(wanted, StringComparison.OrdinalIgnoreCase)
                : File.Exists(Path.ChangeExtension(p, null) + ".navareas.json"));
        if (geoPath is null)
        {
            Console.Error.WriteLine(wanted.Length > 0
                ? $"selfcheck: no map named {wanted} under {dataDir}"
                : $"selfcheck: {geoPaths.Count} map(s) present but none has a .navareas.json - every solve will return zero lineups");
            return 1;
        }

        var name = Path.GetFileNameWithoutExtension(geoPath);
        var mesh = CollisionMesh.Load(geoPath);

        // The empty-attrs bug lives here: the mesh still loads with all its
        // triangles, but the filter the sim uses accepts none of them, so the
        // collider is an empty world and nothing a grenade is thrown at exists.
        var filter = AttributeFilterFor(mesh, options) ?? mesh.GrenadeSolidFilter();
        var solidTriangles = 0;
        for (var t = 0; t < mesh.TriangleCount; t++)
        {
            if (filter(mesh.TriangleAttributes[t]))
            {
                solidTriangles++;
            }
        }
        if (solidTriangles == 0)
        {
            Console.Error.WriteLine(
                $"selfcheck: {name} has {mesh.TriangleCount} triangles but the attribute filter accepts none of them - " +
                "check --attrs (it must include Default and EntitySolid); every solve will return zero lineups");
            return 1;
        }

        // And the end-to-end proof that geometry is really reachable by the
        // physics: drop something onto the middle of the map and see it stop.
        var (min, max) = mesh.ComputeBounds();
        var collider = new TriangleCollider(mesh, min, max, filter);
        var centre = (min + max) / 2;
        var from = new Vector3(centre.X, centre.Y, max.Z + DropHeight);
        var to = new Vector3(centre.X, centre.Y, min.Z - DropHeight);
        if (collider.FirstHit(from, to) is null)
        {
            Console.Error.WriteLine(
                $"selfcheck: nothing solid under the centre of {name} despite {solidTriangles} filtered triangles - " +
                "the collider is not seeing the world");
            return 1;
        }

        Console.WriteLine(
            $"selfcheck: ok - {geoPaths.Count} map(s) present, checked {name}: " +
            $"{solidTriangles}/{mesh.TriangleCount} triangles solid");
        return 0;
    }

    // The same --attrs handling LoadCommon does, minus the mesh loading and the
    // rest of its work. Kept in step with it deliberately: this check is only
    // worth anything if it builds the filter the server will build.
    static Func<byte, bool>? AttributeFilterFor(CollisionMesh mesh, Dictionary<string, string> options)
    {
        if (!options.TryGetValue("attrs", out var attrs))
        {
            return null;
        }
        var requested = attrs.Split(',', StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (requested.Contains("EntitySolid"))
        {
            requested.Add("EntityDoor");
            requested.Add("EntityBreakable");
        }
        var allowed = mesh.AttributeNames
            .Select((attrName, i) => (attrName, i))
            .Where(x => requested.Contains(x.attrName))
            .Select(x => (byte)x.i)
            .ToHashSet();
        return a => allowed.Contains(a);
    }
}
