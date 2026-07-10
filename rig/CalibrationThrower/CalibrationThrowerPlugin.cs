using System.Text.Json;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Memory.DynamicFunctions;
using CounterStrikeSharp.API.Modules.Utils;

namespace CalibrationThrower;

/// <summary>
/// Calibration rig: observes every smoke grenade thrown (real or plugin-thrown)
/// and logs its server-authoritative per-tick position to JSONL.
///
/// Synthetic throwing uses the native CSmokeGrenadeProjectile::Create(), not
/// CreateEntityByName: the latter produces a "physically valid" projectile that
/// flies and bounces correctly but skips the C++ constructor logic that arms
/// the grenade, so it never detonates - confirmed against roughly a dozen failed
/// attempts poking schema fields (DetonateTime, IsLive, Thrower) after spawn.
/// The signature and calling convention below are adapted from ed0ard's
/// CS2-Bot-NadeSystem (github.com/ed0ard/CS2-Bot-NadeSystem), which documents
/// this exact requirement for smoke/HE/molotov (flash is the one grenade type
/// CreateEntityByName is sufficient for). Signatures are tied to one game build
/// and will need re-finding after a CS2 update.
/// </summary>
public class CalibrationThrowerPlugin : BasePlugin
{
    public override string ModuleName => "CalibrationThrower";
    public override string ModuleVersion => "0.4.0";
    public override string ModuleAuthor => "smokesolver";

    string CalibDir =>
        Environment.GetEnvironmentVariable("SMOKESOLVER_CALIB_DIR") ?? ModuleDirectory;
    string RequestPath => Path.Combine(CalibDir, "request.json");
    string OutputPath => Path.Combine(CalibDir, "captures.jsonl");

    readonly Dictionary<uint, TrackedThrow> _tracked = new();
    int _pollCountdown;
    bool _sawPartialRequest;
    readonly Queue<(string? Note, float[]? Predict)> _pendingMeta = new();

    // Capture persistence runs off the tick thread: records are queued here
    // and drained by a background writer holding one long-lived stream.
    // Serialized synchronously at ~74 KB a record, flushes were a guaranteed
    // multi-frame hitch whenever several smokes settled in the same tick.
    readonly System.Collections.Concurrent.BlockingCollection<TrackedThrow> _writeQueue = [];
    Thread? _writerThread;
    const long RotateBytes = 50L * 1024 * 1024;

    static bool AnyHumanConnected() =>
        Utilities.GetPlayers().Any(p => p.IsValid && !p.IsBot);

    static bool HasSmoke(CCSPlayerController player) =>
        player.PlayerPawn.Value?.WeaponServices?.MyWeapons
            .Any(w => w.Value?.DesignerName == "weapon_smokegrenade") ?? false;

    static void GiveSmokeIfMissing(CCSPlayerController player)
    {
        if (player.IsValid && !HasSmoke(player))
        {
            player.GiveNamedItem("weapon_smokegrenade");
        }
    }

    // CSmokeGrenadeProjectile::Create(pos, pos2, vel, vel2, owner, itemDef, team)
    // Build 24134959 (1.41.6.9). The previous signature ("... 45 89 CF 41 56
    // 49 89 FE") now matches the flashbang/decoy Create functions instead, so
    // a stale signature here throws the WRONG grenade type rather than failing
    // loudly - verify with a capture (designer name filter drops non-smokes).
    static readonly MemoryFunctionWithReturn<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, int, int, CSmokeGrenadeProjectile>
        SmokeCreate = new("55 4C 89 C1 48 89 E5 41 57 49 89 FF 41 56 45 89 CE 41 55 4D 89 C5 41 54 53 48 83 EC 58");

    sealed class TrackedThrow
    {
        public required float[] StartPos { get; init; }
        public required float[] StartVel { get; init; }
        public string? ThrowerName { get; init; }
        public string? Note { get; init; }
        public float[]? Predict { get; init; }
        public List<float[]> Ticks { get; } = [];
        public float[]? LastPosition;
        public bool Detonated;
    }

    public override void Load(bool hotReload)
    {
        RegisterListener<Listeners.OnEntitySpawned>(OnEntitySpawned);
        RegisterListener<Listeners.OnTick>(OnTick);
        RegisterListener<Listeners.OnMapStart>(OnMapStart);
        _writerThread = new Thread(DrainWriteQueue) { IsBackground = true, Name = "calib-capture-writer" };
        _writerThread.Start();
        Server.PrintToConsole($"[CalibrationThrower] watching {RequestPath}, writing to {OutputPath}");
        if (hotReload)
        {
            // Deferred: executing config commands synchronously inside a hot
            // reload has crashed the server (mp_restartgame mid-reload).
            AddTimer(3.0f, ApplyPracticeSettings);
        }
    }

    public override void Unload(bool hotReload)
    {
        // Flush everything still airborne so a reload mid-run cannot lose
        // captures, then stop the writer and clear world beams the next
        // instance will not know about.
        foreach (var track in _tracked.Values)
        {
            _writeQueue.Add(track);
        }
        _tracked.Clear();
        _writeQueue.CompleteAdding();
        _writerThread?.Join(TimeSpan.FromSeconds(5));
        foreach (var beam in _markerBeams.Concat(_aimBeams).Where(b => b.IsValid))
        {
            beam.Remove();
        }
        _markerBeams.Clear();
        _aimBeams.Clear();
    }

    void DrainWriteQueue()
    {
        while (!_writeQueue.IsAddingCompleted || _writeQueue.Count > 0)
        {
            try
            {
                using (var writer = new StreamWriter(OutputPath, append: true))
                {
                    foreach (var track in _writeQueue.GetConsumingEnumerable())
                    {
                        writer.WriteLine(SerializeRecord(track));
                        writer.Flush();
                        if (writer.BaseStream.Length > RotateBytes && _tracked.Count == 0)
                        {
                            break; // close the stream, rotate below, reopen
                        }
                    }
                }
                if (File.Exists(OutputPath) && new FileInfo(OutputPath).Length > RotateBytes && _tracked.Count == 0)
                {
                    RotateCaptures();
                }
            }
            catch (Exception e)
            {
                Server.NextFrame(() => Server.PrintToConsole($"[CalibrationThrower] capture writer error: {e.Message}"));
                Thread.Sleep(1000);
            }
        }
    }

    void RotateCaptures()
    {
        var rotated = Path.Combine(CalibDir, $"captures-{DateTime.UtcNow:yyyyMMdd-HHmmss}.jsonl");
        File.Move(OutputPath, rotated);
        Server.NextFrame(() => Server.PrintToConsole($"[CalibrationThrower] rotated captures to {rotated}"));
    }

    // gamemode_competitive.cfg execs after server.cfg during map spawn and
    // reverts practice settings (bots, round timer, cheats), so re-apply them
    // once the map has settled instead of fighting the exec order.
    void OnMapStart(string mapName)
    {
        AddTimer(5.0f, ApplyPracticeSettings);
    }

    // A human connecting can restart warmup (competitive gamemode behavior),
    // so end it again shortly after they're in.
    [GameEventHandler]
    public HookResult OnPlayerSpawn(CounterStrikeSharp.API.Core.EventPlayerSpawn ev, GameEventInfo info)
    {
        if (ev.Userid is { IsBot: false } player)
        {
            var slot = player.Slot;
            AddTimer(0.5f, () =>
            {
                var p = Utilities.GetPlayerFromSlot(slot);
                if (p != null) { GiveSmokeIfMissing(p); }
            });
        }
        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnPlayerConnectFull(CounterStrikeSharp.API.Core.EventPlayerConnectFull ev, GameEventInfo info)
    {
        if (ev.Userid is { IsBot: false })
        {
            AddTimer(3.0f, () => Server.ExecuteCommand("mp_warmup_end"));
        }
        return HookResult.Continue;
    }

    static void ApplyPracticeSettings()
    {
        Server.ExecuteCommand("sv_cheats 1; sv_infinite_ammo 1; sv_autobunnyhopping 1");
        Server.ExecuteCommand("bot_kick; bot_quota 0");
        Server.ExecuteCommand("mp_freezetime 0; mp_roundtime 60; mp_roundtime_defuse 60; mp_startmoney 16000; mp_maxmoney 16000; mp_buy_anywhere 1; mp_buytime 999999");
        Server.ExecuteCommand("mp_warmup_end; mp_restartgame 1");
        Server.PrintToConsole("[CalibrationThrower] practice settings applied");
    }

    void OnTick()
    {
        PollRequestFile();
        TrackProjectiles();
    }

    void PollRequestFile()
    {
        if (--_pollCountdown > 0)
        {
            return;
        }
        _pollCountdown = 8;
        if (!File.Exists(RequestPath))
        {
            return;
        }
        // Claim by rename before reading: writers use temp+rename so a visible
        // request.json is always complete, and once claimed no writer can race
        // the read/delete pair.
        var claimed = RequestPath + ".processing";
        try
        {
            File.Move(RequestPath, claimed, overwrite: true);
        }
        catch (IOException)
        {
            return; // writer or another consumer got there first; next poll retries
        }
        try
        {
            var doc = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(claimed));
            File.Delete(claimed);
            _sawPartialRequest = false;
            if (doc.TryGetProperty("cmd", out var cmdEl))
            {
                var cmd = cmdEl.GetString()!;
                Server.ExecuteCommand(cmd);
                Server.PrintToConsole($"[CalibrationThrower] executed: {cmd}");
                return;
            }
            if (doc.TryGetProperty("chat", out var chatEl))
            {
                foreach (var line in chatEl.EnumerateArray())
                {
                    Server.PrintToChatAll(line.GetString() ?? "");
                }
                if (doc.TryGetProperty("beam", out var beamEl))
                {
                    var b = beamEl.EnumerateArray().Select(e => e.GetSingle()).ToArray();
                    if (b.Length >= 3) { DrawMarkerBeam(b); }
                }
                if (doc.TryGetProperty("aimbeam", out var aimEl))
                {
                    var a = aimEl.EnumerateArray().Select(e => e.GetSingle()).ToArray();
                    if (a.Length >= 3) { DrawAimCross(a); }
                }
                if (doc.TryGetProperty("store", out var storeEl))
                {
                    _lastLineup = (
                        storeEl.GetProperty("pos").EnumerateArray().Select(e => e.GetSingle()).ToArray(),
                        storeEl.GetProperty("pitch").GetSingle(),
                        storeEl.GetProperty("yaw").GetSingle(),
                        storeEl.GetProperty("hint").GetString() ?? "",
                        storeEl.TryGetProperty("aim", out var aimStoreEl)
                            ? aimStoreEl.EnumerateArray().Select(e => e.GetSingle()).ToArray() : null);
                }
                return;
            }
            if (doc.TryGetProperty("throws", out var throwsEl))
            {
                foreach (var t in throwsEl.EnumerateArray())
                {
                    LaunchFromJson(t);
                }
                return;
            }
            LaunchFromJson(doc);
        }
        catch (JsonException e)
        {
            // One retry tolerates a legacy in-place writer; after that the
            // file is quarantined so it cannot wedge the channel forever.
            if (!_sawPartialRequest)
            {
                _sawPartialRequest = true;
                try { File.Move(claimed, RequestPath, overwrite: true); } catch (IOException) { }
                return;
            }
            _sawPartialRequest = false;
            var bad = RequestPath + ".bad";
            try
            {
                File.Move(claimed, bad, overwrite: true);
                Server.PrintToConsole($"[CalibrationThrower] malformed request quarantined to {bad}: {e.Message}");
            }
            catch (IOException) { }
        }
        catch (Exception e)
        {
            try { File.Delete(claimed); } catch (IOException) { }
            Server.PrintToConsole($"[CalibrationThrower] request failed: {e}");
        }
    }

    void LaunchFromJson(JsonElement doc)
    {
        var pos = doc.GetProperty("pos").EnumerateArray().Select(e => e.GetSingle()).ToArray();
        var vel = doc.GetProperty("vel").EnumerateArray().Select(e => e.GetSingle()).ToArray();
        var note = doc.TryGetProperty("note", out var noteEl) ? noteEl.GetString() : null;
        var predict = doc.TryGetProperty("predict", out var pEl)
            ? pEl.EnumerateArray().Select(e => e.GetSingle()).ToArray() : null;
        _pendingMeta.Enqueue((note, predict));
        if (note != null && AnyHumanConnected())
        {
            Server.PrintToChatAll($" \u0004[calib]\u0001 {note}");
        }
        ThrowSynthetic(new Vector(pos[0], pos[1], pos[2]), new Vector(vel[0], vel[1], vel[2]));
    }

    // No-owner path: sandbox test maps (e.g. cs_flatgrass) often ship without
    // spawn-point entities at all, so bot_add has nothing to spawn onto and
    // Utilities.GetPlayers() stays empty forever. The grenade's physics don't
    // need an owner, only its team-color rendering and kill-credit do, so we
    // fall back to team CT (2) and a zero owner handle when nobody's connected.
    const int FallbackTeamNum = 2;

    void ThrowSynthetic(Vector pos, Vector vel)
    {
        var owner = Utilities.GetPlayers().FirstOrDefault(p => p.IsValid && p.PlayerPawn.Value != null);
        var ownerPawn = owner?.PlayerPawn.Value;
        var teamNum = ownerPawn?.TeamNum ?? FallbackTeamNum;

        var smoke = SmokeCreate.Invoke(pos.Handle, pos.Handle, vel.Handle, vel.Handle, ownerPawn?.Handle ?? IntPtr.Zero, 45, teamNum);
        if (smoke == null || !smoke.IsValid)
        {
            Server.PrintToConsole("[CalibrationThrower] native smoke Create() returned null");
            return;
        }
        smoke.TeamNum = teamNum;
        if (ownerPawn != null)
        {
            smoke.Thrower.Raw = ownerPawn.EntityHandle.Raw;
            smoke.OriginalThrower.Raw = ownerPawn.EntityHandle.Raw;
            smoke.OwnerEntity.Raw = ownerPawn.EntityHandle.Raw;
        }
        Server.PrintToConsole($"[CalibrationThrower] synthetic throw from ({pos.X:F0},{pos.Y:F0},{pos.Z:F0}) vel ({vel.X:F0},{vel.Y:F0},{vel.Z:F0}) owner={owner?.PlayerName ?? "(none)"}");
    }

    void OnEntitySpawned(CEntityInstance entity)
    {
        if (entity.DesignerName != "smokegrenade_projectile")
        {
            return;
        }
        var projectile = entity.As<CSmokeGrenadeProjectile>();

        // Read on the next frame: at the exact OnEntitySpawned moment the
        // entity's own initial-state fields aren't reliably populated yet.
        Server.NextFrame(() =>
        {
            if (!projectile.IsValid)
            {
                return;
            }
            var startPos = projectile.InitialPosition;
            var startVel = projectile.InitialVelocity;
            if (_tracked.TryGetValue(projectile.Index, out var existing))
            {
                if (existing.StartPos[0] == startPos.X && existing.StartPos[1] == startPos.Y && existing.StartPos[2] == startPos.Z)
                {
                    return; // duplicate spawn callback for a projectile already tracked
                }
                // Entity index reused: with dozens of smokes alive at once the
                // engine hands a despawned projectile's index to a new one before
                // the per-tick reaper notices the old one is gone. Without this
                // flush the old record is never written and the new throw's ticks
                // get appended to it, corrupting both captures.
                FlushRecord(existing);
                _tracked.Remove(projectile.Index);
            }
            var thrower = projectile.Thrower.Value?.As<CCSPlayerPawn>().Controller.Value?.As<CCSPlayerController>();
            var synthetic = _pendingMeta.Count > 0;
            var meta = synthetic ? _pendingMeta.Dequeue() : default;
            _tracked[projectile.Index] = new TrackedThrow
            {
                StartPos = [startPos.X, startPos.Y, startPos.Z],
                StartVel = [startVel.X, startVel.Y, startVel.Z],
                ThrowerName = thrower?.PlayerName,
                Note = meta.Note,
                Predict = meta.Predict,
            };
            Server.PrintToConsole($"[CalibrationThrower] observed throw by {thrower?.PlayerName ?? "?"} from ({startPos.X:F0},{startPos.Y:F0},{startPos.Z:F0}) vel ({startVel.X:F0},{startVel.Y:F0},{startVel.Z:F0})");

            // sv_infinite_ammo doesn't restock equipment slots, only weapon clips,
            // so testers otherwise have to retype "give" after every throw.
            // Wait for the throw animation to finish and only give when the
            // slot is actually empty - an unconditional early give lands while
            // the weapon slot is still transitioning and drops to the ground.
            if (!synthetic && thrower is { IsValid: true })
            {
                var slot = thrower.Slot;
                AddTimer(1.2f, () =>
                {
                    var p = Utilities.GetPlayerFromSlot(slot);
                    if (p != null) { GiveSmokeIfMissing(p); }
                });
            }
        });
    }

    void TrackProjectiles()
    {
        if (_tracked.Count == 0)
        {
            return;
        }
        var alive = new HashSet<uint>();
        foreach (var projectile in Utilities.FindAllEntitiesByDesignerName<CSmokeGrenadeProjectile>("smokegrenade_projectile"))
        {
            if (!projectile.IsValid || !_tracked.TryGetValue(projectile.Index, out var track))
            {
                continue;
            }
            alive.Add(projectile.Index);
            var origin = projectile.AbsOrigin;
            var velocity = projectile.AbsVelocity;
            if (origin == null)
            {
                continue;
            }
            track.Ticks.Add([
                Server.TickCount, origin.X, origin.Y, origin.Z,
                velocity?.X ?? 0, velocity?.Y ?? 0, velocity?.Z ?? 0,
            ]);
            track.LastPosition = [origin.X, origin.Y, origin.Z];
            if (projectile.DidSmokeEffect && !track.Detonated)
            {
                track.Detonated = true;
                Server.PrintToConsole($"[CalibrationThrower] bloomed at ({origin.X:F0},{origin.Y:F0},{origin.Z:F0}) after {track.Ticks.Count} ticks");
                // Synthetic test smokes: the rest position is final at bloom,
                // so record and delete immediately instead of letting ~20s of
                // smoke volume pile up between tests.
                if (!_showTestSmokes && (track.Note != null || track.Predict != null))
                {
                    _tracked.Remove(projectile.Index);
                    FlushRecord(track);
                    projectile.Remove();
                }
            }
        }

        foreach (var (index, track) in _tracked.Where(t => !alive.Contains(t.Key)).ToList())
        {
            _tracked.Remove(index);
            FlushRecord(track);
        }
    }

    static string SerializeRecord(TrackedThrow track) => JsonSerializer.Serialize(new
    {
        thrower = track.ThrowerName,
        start = track.StartPos,
        velocity = track.StartVel,
        detonated = track.Detonated,
        rest = track.LastPosition,
        samples = track.Ticks,
    });

    void FlushRecord(TrackedThrow track)
    {
        // Persistence happens on the writer thread; the record is immutable
        // from here on because the projectile is removed from _tracked before
        // FlushRecord is called.
        _writeQueue.Add(track);
        Server.PrintToConsole($"[CalibrationThrower] capture queued ({track.Ticks.Count} ticks, detonated={track.Detonated}, rest ({track.LastPosition?[0]:F0},{track.LastPosition?[1]:F0},{track.LastPosition?[2]:F0}))");
        if (track.Note != null && AnyHumanConnected())
        {
            var err = "";
            if (track.Predict != null && track.LastPosition != null)
            {
                var dx = track.Predict[0] - track.LastPosition[0];
                var dy = track.Predict[1] - track.LastPosition[1];
                var dz = track.Predict[2] - track.LastPosition[2];
                var dist = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
                var col = dist <= 8 ? "\u0004" : dist <= 32 ? "\u0010" : "\u0002";
                err = $" {col}err {dist:F1}u\u0001";
            }
            Server.PrintToChatAll($" \u0004[calib]\u0001 {track.Note} \u0010landed\u0001 ({track.LastPosition?[0]:F0},{track.LastPosition?[1]:F0},{track.LastPosition?[2]:F0}){err}{(track.Detonated ? "" : " \u0002NO DETONATION\u0001")}");
        }
    }

    string MarkersPath => Path.Combine(CalibDir, "markers.json");
    readonly List<CBeam> _markerBeams = [];
    readonly List<CBeam> _aimBeams = [];

    Dictionary<string, float[]> LoadMarkers()
    {
        if (!File.Exists(MarkersPath))
        {
            return [];
        }
        try
        {
            var raw = JsonSerializer.Deserialize<Dictionary<string, float[]>>(File.ReadAllText(MarkersPath)) ?? [];
            var bad = raw.Where(kv => kv.Value is not { Length: 3 }).Select(kv => kv.Key).ToList();
            foreach (var key in bad)
            {
                Server.PrintToConsole($"[CalibrationThrower] dropping malformed marker '{key}' (expected 3 coordinates)");
                raw.Remove(key);
            }
            return raw;
        }
        catch (JsonException e)
        {
            Server.PrintToConsole($"[CalibrationThrower] markers.json is not valid JSON, ignoring: {e.Message}");
            return [];
        }
    }

    void SaveMarkers(Dictionary<string, float[]> markers) =>
        File.WriteAllText(MarkersPath, JsonSerializer.Serialize(markers, new JsonSerializerOptions { WriteIndented = true }));

    void DrawMarkerBeam(float[] pos)
    {
        var beam = Utilities.CreateEntityByName<CBeam>("env_beam");
        if (beam == null) { return; }
        beam.Width = 2.0f;
        beam.Render = System.Drawing.Color.FromArgb(255, 64, 200, 255);
        beam.Teleport(new Vector(pos[0], pos[1], pos[2]), new QAngle(), new Vector());
        beam.EndPos.X = pos[0]; beam.EndPos.Y = pos[1]; beam.EndPos.Z = pos[2] + 160;
        beam.DispatchSpawn();
        _markerBeams.Add(beam);
    }

    void RedrawMarkerBeams()
    {
        foreach (var b in _markerBeams.Where(b => b.IsValid))
        {
            b.Remove();
        }
        _markerBeams.Clear();
        foreach (var (_, pos) in LoadMarkers())
        {
            DrawMarkerBeam(pos);
        }
    }

    [ConsoleCommand("css_mark", "Save current position as a named test marker")]
    [CommandHelper(minArgs: 1, usage: "<name>", whoCanExecute: CommandUsage.CLIENT_ONLY)]
    public void OnMarkCommand(CCSPlayerController? player, CommandInfo command)
    {
        var pawn = player?.PlayerPawn.Value;
        if (pawn?.AbsOrigin == null) { return; }
        var name = command.GetArg(1);
        var markers = LoadMarkers();
        markers[name] = [pawn.AbsOrigin.X, pawn.AbsOrigin.Y, pawn.AbsOrigin.Z];
        SaveMarkers(markers);
        DrawMarkerBeam(markers[name]);
        player!.PrintToChat($" \u0004[calib]\u0001 marked \u0010{name}\u0001 at ({pawn.AbsOrigin.X:F0},{pawn.AbsOrigin.Y:F0},{pawn.AbsOrigin.Z:F0}) - {markers.Count} total");
    }

    [ConsoleCommand("css_marks", "List saved test markers and redraw their beams")]
    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void OnMarksCommand(CCSPlayerController? player, CommandInfo command)
    {
        var markers = LoadMarkers();
        RedrawMarkerBeams();
        void Reply(string s)
        {
            if (player != null) { player.PrintToChat(s); } else { command.ReplyToCommand(s); }
        }
        if (markers.Count == 0)
        {
            Reply(" \u0004[calib]\u0001 no markers yet - stand somewhere and use !mark <name>");
            return;
        }
        Reply($" \u0004[calib]\u0001 {markers.Count} marker(s):");
        foreach (var (name, pos) in markers)
        {
            Reply($" \u0010{name}\u0001 ({pos[0]:F0},{pos[1]:F0},{pos[2]:F0})");
        }
    }

    [ConsoleCommand("css_unmark", "Delete a named test marker")]
    [CommandHelper(minArgs: 1, usage: "<name>", whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void OnUnmarkCommand(CCSPlayerController? player, CommandInfo command)
    {
        var name = command.GetArg(1);
        var markers = LoadMarkers();
        if (markers.Remove(name))
        {
            SaveMarkers(markers);
            RedrawMarkerBeams();
            player?.PrintToChat($" \u0004[calib]\u0001 removed \u0010{name}\u0001");
        }
        else
        {
            player?.PrintToChat($" \u0002[calib]\u0001 no marker named '{name}'");
        }
    }

    string TestRequestPath => Path.Combine(CalibDir, "test-request.json");

    [ConsoleCommand("css_test", "Queue a lineup test run: marker name, or no marker = your current spot")]
    [CommandHelper(usage: "[marker] [passRadius=1] [tolerance=80] [limit=0]", whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void OnTestCommand(CCSPlayerController? player, CommandInfo command)
    {
        void Reply(string s)
        {
            if (player != null) { player.PrintToChat(s); } else { command.ReplyToCommand(s); }
        }
        var markers = LoadMarkers();
        string name;
        float[] pos;
        int numericFrom;
        // First arg may be a marker name; anything else (or nothing) means
        // "test where I'm standing" and numeric args shift left by one.
        if (command.ArgCount > 1 && markers.TryGetValue(command.GetArg(1), out var markerPos))
        {
            name = command.GetArg(1);
            pos = markerPos;
            numericFrom = 2;
        }
        else if (command.ArgCount > 1 && !float.TryParse(command.GetArg(1), out _))
        {
            Reply($" \u0002[calib]\u0001 no marker named '{command.GetArg(1)}' - use !marks to list");
            return;
        }
        else
        {
            var pawn = player?.PlayerPawn.Value;
            if (pawn?.AbsOrigin == null)
            {
                Reply(" \u0002[calib]\u0001 no marker given and no player position available");
                return;
            }
            name = "here";
            pos = [pawn.AbsOrigin.X, pawn.AbsOrigin.Y, pawn.AbsOrigin.Z];
            numericFrom = 1;
        }
        float NumArg(int i, float fallback) =>
            command.ArgCount > numericFrom + i && float.TryParse(command.GetArg(numericFrom + i), out var v) ? v : fallback;
        var pass = NumArg(0, 1f);
        var tolerance = NumArg(1, 80f);
        var limit = (int)NumArg(2, 0f);
        File.WriteAllText(TestRequestPath, JsonSerializer.Serialize(new { name, pos, pass, tolerance, limit }));
        Reply($" \u0004[calib]\u0001 test queued for \u0010{name}\u0001 ({pos[0]:F0},{pos[1]:F0},{pos[2]:F0}) - pass \u0010{pass:F0}u\u0001, tolerance \u0010{tolerance:F0}u\u0001{(limit > 0 ? $", limit \u0010{limit}\u0001" : "")}");
    }

    [ConsoleCommand("css_stop", "Stop the running test and clear remaining smokes")]
    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void OnStopCommand(CCSPlayerController? player, CommandInfo command)
    {
        File.WriteAllText(Path.Combine(CalibDir, "stop-request"), "");
        OnClearSmokesCommand(player, command);
        var msg = " \u0004[calib]\u0001 stopping test - remaining throws cancelled";
        if (player != null) { player.PrintToChat(msg); } else { command.ReplyToCommand(msg); }
    }

    string LineupRequestPath => Path.Combine(CalibDir, "lineup-request.json");
    (float[] Pos, float Pitch, float Yaw, string Hint, float[]? Aim)? _lastLineup;
    // When true, test smokes bloom and linger like real ones; when false they
    // are recorded and deleted the moment they bloom.
    bool _showTestSmokes;

    // Floating X of beams: the in-sky aim reference used by lineup maps. Put
    // the crosshair on its center from the marked stand spot and the angles
    // are correct.
    void DrawAimCross(float[] c)
    {
        // Only one aim X may exist at a time: stale crosses from previous
        // solves stay floating otherwise, and aiming at the wrong one throws
        // the smoke somewhere else entirely.
        foreach (var b in _aimBeams.Where(b => b.IsValid))
        {
            b.Remove();
        }
        _aimBeams.Clear();
        // c = [x, y, z, aimYawDeg]; the X is drawn in the plane perpendicular
        // to the aim yaw so it always faces the thrower instead of edge-on.
        const float R = 14f;
        var yawRad = c.Length > 3 ? c[3] * MathF.PI / 180f : 0f;
        var rx = -MathF.Sin(yawRad) * R;
        var ry = MathF.Cos(yawRad) * R;
        foreach (var (s, dz) in new[] { (1f, -R), (1f, R) })
        {
            var beam = Utilities.CreateEntityByName<CBeam>("env_beam");
            if (beam == null) { continue; }
            beam.Width = 1.5f;
            beam.Render = System.Drawing.Color.FromArgb(255, 255, 200, 40);
            beam.Teleport(new Vector(c[0] - rx * s, c[1] - ry * s, c[2] + dz), new QAngle(), new Vector());
            beam.EndPos.X = c[0] + rx * s; beam.EndPos.Y = c[1] + ry * s; beam.EndPos.Z = c[2] - dz;
            beam.DispatchSpawn();
            _aimBeams.Add(beam);
        }
    }

    string PlineupRequestPath => Path.Combine(CalibDir, "plineup-request.json");

    [ConsoleCommand("css_plineup", "Solve a throw from your EXACT position onto a marker")]
    [CommandHelper(minArgs: 1, usage: "<marker> [quick|deep]", whoCanExecute: CommandUsage.CLIENT_ONLY)]
    public void OnPlineupCommand(CCSPlayerController? player, CommandInfo command)
    {
        var pawn = player?.PlayerPawn.Value;
        if (pawn?.AbsOrigin == null) { return; }
        var name = command.GetArg(1);
        var markers = LoadMarkers();
        if (!markers.TryGetValue(name, out var pos))
        {
            player!.PrintToChat($" \u0002[calib]\u0001 no marker named '{name}' - use !marks to list");
            return;
        }
        var mode = command.ArgCount > 2 && command.GetArg(2).ToLowerInvariant() == "deep" ? "deep" : "quick";
        File.WriteAllText(PlineupRequestPath, JsonSerializer.Serialize(new
        {
            name,
            pos,
            mode,
            player = new[] { pawn.AbsOrigin.X, pawn.AbsOrigin.Y, pawn.AbsOrigin.Z },
        }));
        player!.PrintToChat(mode == "deep"
            ? $" \u0004[calib]\u0001 deep solve onto \u0010{name}\u0001 - full 360 sweep, ~30s..."
            : $" \u0004[calib]\u0001 solving a throw from your exact spot onto \u0010{name}\u0001 (~10s)...");
    }

    [ConsoleCommand("css_goto", "Teleport to the last !lineup spot with exact angles")]
    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_ONLY)]
    public void OnGotoCommand(CCSPlayerController? player, CommandInfo command)
    {
        var pawn = player?.PlayerPawn.Value;
        if (pawn == null) { return; }
        if (_lastLineup is not var (pos, pitch, yaw, hint, aim) || pos == null)
        {
            player!.PrintToChat(" \u0002[calib]\u0001 no lineup stored - use !lineup <marker> first");
            return;
        }
        if (aim != null)
        {
            DrawAimCross(aim);
        }
        // setpos/setang run client-side (sv_cheats is on): the engine's own
        // placement logic applies, and only the VIEW pitches - teleporting the
        // pawn with a pitched QAngle rotates the body model into the ground.
        player!.ExecuteClientCommandFromServer(FormattableString.Invariant(
            $"setpos {pos[0]:F2} {pos[1]:F2} {pos[2] + 1:F2}"));
        player.ExecuteClientCommandFromServer(FormattableString.Invariant(
            $"setang {pitch:F2} {yaw:F2} 0"));
        player.PrintToChat($" \u0004[calib]\u0001 in position - {hint}");
    }

    [ConsoleCommand("css_lineup", "Find the best throw spot for a marker near your current position")]
    [CommandHelper(minArgs: 1, usage: "<marker>", whoCanExecute: CommandUsage.CLIENT_ONLY)]
    public void OnLineupCommand(CCSPlayerController? player, CommandInfo command)
    {
        var pawn = player?.PlayerPawn.Value;
        if (pawn?.AbsOrigin == null) { return; }
        var name = command.GetArg(1);
        var markers = LoadMarkers();
        if (!markers.TryGetValue(name, out var pos))
        {
            player!.PrintToChat($" \u0002[calib]\u0001 no marker named '{name}' - use !marks to list");
            return;
        }
        File.WriteAllText(LineupRequestPath, JsonSerializer.Serialize(new
        {
            name,
            pos,
            player = new[] { pawn.AbsOrigin.X, pawn.AbsOrigin.Y, pawn.AbsOrigin.Z },
        }));
        player!.PrintToChat($" \u0004[calib]\u0001 finding best lineup for \u0010{name}\u0001 near you...");
    }

    [ConsoleCommand("css_smokes", "Toggle whether test smokes stay visible or clear instantly")]
    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void OnSmokesToggleCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (command.ArgCount > 1)
        {
            _showTestSmokes = command.GetArg(1).ToLowerInvariant() is "on" or "show" or "1" or "true";
        }
        else
        {
            _showTestSmokes = !_showTestSmokes;
        }
        var msg = _showTestSmokes
            ? " \u0004[calib]\u0001 test smokes: \u0010visible\u0001 (bloom and linger like real throws)"
            : " \u0004[calib]\u0001 test smokes: \u0010hidden\u0001 (recorded and cleared at bloom)";
        if (player != null) { player.PrintToChat(msg); } else { command.ReplyToCommand(msg); }
    }

    [ConsoleCommand("css_clearsmokes", "Delete all live smoke projectiles/volumes now")]
    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void OnClearSmokesCommand(CCSPlayerController? player, CommandInfo command)
    {
        var cleared = 0;
        foreach (var projectile in Utilities.FindAllEntitiesByDesignerName<CSmokeGrenadeProjectile>("smokegrenade_projectile"))
        {
            if (!projectile.IsValid) { continue; }
            if (_tracked.TryGetValue(projectile.Index, out var track))
            {
                _tracked.Remove(projectile.Index);
                FlushRecord(track);
            }
            projectile.Remove();
            cleared++;
        }
        player?.PrintToChat($" \u0004[calib]\u0001 cleared {cleared} smoke(s)");
    }

    [ConsoleCommand("css_help", "List calibration rig commands")]
    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void OnHelpCommand(CCSPlayerController? player, CommandInfo command)
    {
        void Reply(string s)
        {
            if (player != null) { player.PrintToChat(s); } else { command.ReplyToCommand(s); }
        }
        Reply(" \u0004[calib]\u0001 commands:");
        Reply(" \u0010!help\u0001 - this list");
        Reply(" \u0010!practice\u0001 - re-apply practice mode (cheats, no bots, 60min round, buy anywhere)");
        Reply(" \u0010!throw x y z vx vy vz\u0001 - throw a synthetic smoke from position with velocity");
        Reply(" \u0010!mark <name>\u0001 - save your position as a labeled test target (beam pillar)");
        Reply(" \u0010!marks\u0001 / \u0010!unmark <name>\u0001 - list markers / delete one");
        Reply(" \u0010!test [marker] [pass=1] [tol=80] [limit]\u0001 - test a marker (or your spot if none), live errors in chat");
        Reply(" \u0010!stop\u0001 - cancel the running test and clear smokes");
        Reply(" \u0010!clearsmokes\u0001 - delete all live smoke volumes immediately");
        Reply(" \u0010!smokes [on|off]\u0001 - toggle whether test smokes stay visible or clear at bloom");
        Reply(" \u0010!lineup <marker>\u0001 - best throw spot near you: beam at feet + yellow aim cross in the sky");
        Reply(" \u0010!plineup <marker> [deep]\u0001 - throw from your EXACT position onto the marker; deep = exhaustive 360 search");
        Reply(" \u0010!goto\u0001 - teleport into the last !lineup spot with exact angles, then just click");
        Reply(" during test runs, chat shows each throw: \u0010#n/total type click bounces -> predict (x,y,z)\u0001");
        Reply(" then \u0010landed (x,y,z)\u0001 when it settles - compare the two to judge the prediction");
    }

    [ConsoleCommand("css_practice", "Re-apply practice mode settings")]
    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void OnPracticeCommand(CCSPlayerController? player, CommandInfo command)
    {
        ApplyPracticeSettings();
        if (player != null) { player.PrintToChat(" \u0004[calib]\u0001 practice settings applied"); }
    }

    [ConsoleCommand("css_calib_throw", "Synthetic throw: x y z vx vy vz")]
    [CommandHelper(minArgs: 6, usage: "<x> <y> <z> <vx> <vy> <vz>")]
    public void OnThrowCommand(CCSPlayerController? player, CommandInfo command)
    {
        var args = command.ArgString.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var nums = args.Select(a => float.TryParse(a, out var v) ? v : (float?)null).ToArray();
        if (nums.Length < 6 || nums.Any(n => n == null))
        {
            command.ReplyToCommand("[CalibrationThrower] need 6 numbers: x y z vx vy vz");
            return;
        }
        ThrowSynthetic(new Vector(nums[0]!.Value, nums[1]!.Value, nums[2]!.Value), new Vector(nums[3]!.Value, nums[4]!.Value, nums[5]!.Value));
    }
}
