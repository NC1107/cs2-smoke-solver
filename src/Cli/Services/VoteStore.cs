using System.Globalization;
using System.Numerics;
using Microsoft.Data.Sqlite;

namespace SmokeSolver.Cli;

/// <summary>
/// Community votes on lineups: one per account per lineup per spot, tallied.
/// </summary>
// The solver's score is a measurement of the throw. A vote is an opinion about
// it from someone who tried it. They are kept apart on purpose - blended, both
// become unreadable - so this store holds nothing but votes, and the viewer
// shows the tally beside the score rather than inside it.
//
// SQLite rather than the JSON-file pattern the rest of the data uses, for one
// specific reason: a vote is a read-modify-write against a shared record from
// a multi-threaded server. Temp-then-rename gives atomic WRITES; two people
// voting on one lineup at once would race the read and one vote would vanish.
// The UNIQUE constraint below makes that impossible at the storage layer, and
// the single file lives in the same bind-mounted data directory, so nothing
// changes about backup or deployment. Single-writer SQLite is fine because
// compose runs exactly one replica.
public sealed class VoteStore : IDisposable
{
    readonly string _connectionString;
    // One connection, serialised: this server is one process, and SQLite is
    // fastest with a single writer anyway.
    readonly SqliteConnection _db;
    readonly SemaphoreSlim _gate = new(1, 1);

    public VoteStore(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate }.ToString();
        _db = new SqliteConnection(_connectionString);
        _db.Open();
        using var cmd = _db.CreateCommand();
        // WAL: readers never block the writer, and a crash mid-write leaves the
        // last committed state rather than a torn page.
        cmd.CommandText = """
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS votes (
                map       TEXT    NOT NULL,
                target    TEXT    NOT NULL,
                lineup_id TEXT    NOT NULL,
                steam_id  TEXT    NOT NULL,
                vote      INTEGER NOT NULL CHECK (vote IN (-1, 1)),
                ts        INTEGER NOT NULL,
                UNIQUE (map, target, lineup_id, steam_id)
            );
            CREATE INDEX IF NOT EXISTS votes_by_spot ON votes (map, target);
            """;
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// The durable name of a spot for voting: the named target a point snaps
    /// to, or a 16u cell when it is near none.
    /// </summary>
    // Deliberately coarser than the solve cache's tenth of a unit. That
    // precision exists so a re-aim is never replayed for a different click; a
    // vote has no re-aim, and needs the opposite - two people who both mean
    // "B doors" must land on one record. 16u is the grid every other part of
    // the solver already treats as "the same place".
    // Keyed on the target's ID, never its name: names are provisional until a
    // person confirms them, and a rename must not orphan every vote cast so
    // far. The id is minted once and carried across re-runs and renames.
    public static string TargetKey(Vector3 target, IReadOnlyList<(string Id, string Name, Vector3 Pos)> named, float snapRadius)
    {
        var best = named
            .Where(n => n.Id.Length > 0)
            .Select(n => (n.Id, D: Vector2.Distance(new Vector2(n.Pos.X, n.Pos.Y), new Vector2(target.X, target.Y))))
            .Where(x => x.D <= snapRadius)
            .OrderBy(x => x.D)
            .FirstOrDefault();
        return best.Id is not null
            ? "target:" + best.Id
            : string.Create(CultureInfo.InvariantCulture, $"cell:{MathF.Round(target.X / 16f):F0},{MathF.Round(target.Y / 16f):F0}");
    }

    // How many different lineups one account may vote on at one spot. Lineup
    // ids are not checked against anything a solve returned (any 16 hex digits
    // pass), so one account could otherwise write rows without end under a
    // popular target, growing the file and every visitor's download of that
    // spot's tallies. A person voting honestly on what a search showed them
    // stays far below this.
    public const int MaxVotesPerSpot = 100;

    // How many lineups' tallies a spot's read returns, most-voted first. The
    // viewer lists at most 400 and colours only the ones it shows.
    public const int MaxTalliesPerSpot = 500;

    /// <summary>
    /// Casts, changes, or (vote 0) withdraws one account's vote. False when it
    /// would be the account's vote on more than MaxVotesPerSpot lineups here.
    /// </summary>
    public async Task<bool> CastAsync(string map, string targetKey, string lineupId, string steamId, int vote, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (vote != 0)
            {
                using var count = _db.CreateCommand();
                count.CommandText = "SELECT COUNT(*) FROM votes WHERE map=$m AND target=$t AND steam_id=$s AND lineup_id<>$l";
                count.Parameters.AddWithValue("$m", map);
                count.Parameters.AddWithValue("$t", targetKey);
                count.Parameters.AddWithValue("$s", steamId);
                count.Parameters.AddWithValue("$l", lineupId);
                if (Convert.ToInt64(await count.ExecuteScalarAsync(ct)) >= MaxVotesPerSpot)
                {
                    return false;
                }
            }
            using var cmd = _db.CreateCommand();
            if (vote == 0)
            {
                cmd.CommandText = "DELETE FROM votes WHERE map=$m AND target=$t AND lineup_id=$l AND steam_id=$s";
            }
            else
            {
                cmd.CommandText = """
                    INSERT INTO votes (map, target, lineup_id, steam_id, vote, ts)
                    VALUES ($m, $t, $l, $s, $v, $ts)
                    ON CONFLICT (map, target, lineup_id, steam_id) DO UPDATE SET vote = excluded.vote, ts = excluded.ts
                    """;
                cmd.Parameters.AddWithValue("$v", Math.Sign(vote));
                cmd.Parameters.AddWithValue("$ts", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            }
            cmd.Parameters.AddWithValue("$m", map);
            cmd.Parameters.AddWithValue("$t", targetKey);
            cmd.Parameters.AddWithValue("$l", lineupId);
            cmd.Parameters.AddWithValue("$s", steamId);
            await cmd.ExecuteNonQueryAsync(ct);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public sealed record Tally(int Up, int Down)
    {
        public int Score => Up - Down;
    }

    /// <summary>Every lineup's tally at a spot, and what one account voted.</summary>
    public async Task<(Dictionary<string, Tally> Tallies, Dictionary<string, int> Mine)> AtSpotAsync(
        string map, string targetKey, string? steamId, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var tallies = new Dictionary<string, Tally>(StringComparer.Ordinal);
            var mine = new Dictionary<string, int>(StringComparer.Ordinal);
            // Counted in SQL and capped: the read used to fetch every row at the
            // spot and ship every lineup it had ever seen a vote on.
            using (var cmd = _db.CreateCommand())
            {
                cmd.CommandText = """
                    SELECT lineup_id, SUM(vote > 0), SUM(vote < 0) FROM votes
                    WHERE map=$m AND target=$t
                    GROUP BY lineup_id
                    ORDER BY COUNT(*) DESC, lineup_id
                    LIMIT $n
                    """;
                cmd.Parameters.AddWithValue("$m", map);
                cmd.Parameters.AddWithValue("$t", targetKey);
                cmd.Parameters.AddWithValue("$n", MaxTalliesPerSpot);
                using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    tallies[reader.GetString(0)] = new Tally(reader.GetInt32(1), reader.GetInt32(2));
                }
            }
            if (steamId is not null)
            {
                using var cmd = _db.CreateCommand();
                cmd.CommandText = "SELECT lineup_id, vote FROM votes WHERE map=$m AND target=$t AND steam_id=$s";
                cmd.Parameters.AddWithValue("$m", map);
                cmd.Parameters.AddWithValue("$t", targetKey);
                cmd.Parameters.AddWithValue("$s", steamId);
                using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    mine[reader.GetString(0)] = reader.GetInt32(1);
                }
            }
            return (tallies, mine);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _db.Dispose();
        _gate.Dispose();
    }
}
