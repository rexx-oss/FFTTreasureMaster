using System;
using System.Collections.Generic;
using System.Linq;

namespace FFTTreasureMaster;

internal sealed partial class TreasureMaster : ISignature
{
    void ISignature.Tick(in TickContext ctx) => Tick(ctx.Now, ctx.InLive);

    private enum Phase { Disarmed, Arming, Armed }

    private TreasureDb            _db;
    private readonly Func<TreasureDb>    _load;
    private readonly Func<DateTime?>     _datasetStamp;
    private readonly IGameMemory  _mem;
    private readonly AddrMap      _addrs;
    private readonly Func<TreasureDb, AnchorResolution?>? _resolver;
    private readonly ArmAudit     _audit;
    private readonly TileHolder   _holder;
    private readonly MarkerWriter _markers;
    private readonly ClaimAudit   _claims;
    private readonly bool         _claimDetection;

    private readonly bool _enabled;
    private readonly FastHold _fastHold;

    private readonly CollectAudit _collect;
    private readonly bool         _collectDetection;
    private readonly Func<int, TreasureTile, bool>? _collectedOracle;
    private readonly HashSet<(int, int)> _collected = new();

    private bool _claimOkForBuild = true;
    private bool _collectOkForBuild = true;
    private bool _fingerprintOkForBuild = true;

    private readonly HashSet<(int, int)> _claimed = new();
    private readonly Dictionary<int, int> _lastCount = new();
    private readonly Dictionary<int, int> _armCount  = new();
    private readonly Dictionary<(int, int), int> _claimItem = new();
    private string?   _lastClaimDiag   = null;
    private string?   _lastStateDiag   = null;
    private string?   _lastHoldDiag    = null;

    private Phase     _phase          = Phase.Disarmed;
    private TreasureMap? _map         = null;
    private TreasureMap? _activeMap   = null;
    private int       _stableTicks    = 0;
    private byte      _stableMapId    = 0;
    private int       _armAttempts    = 0;
    private int       _revalidateTick = 0;
    private int       _badMapTicks    = 0;
    private bool      _globalIdle     = false;
    private bool      _globalIdleChecked = false;
    private bool      _naggedThisBattle  = false;
    private bool      _capLoggedThisBattle = false;
    private bool      _foreignLoggedThisBattle = false;
    private bool      _markersLoggedThisBattle = false;
    private string?   _lastMarkerProbe = null;
    private bool      _flapLoggedThisBattle = false;

    private int       _stampCheckCountdown = 0;
    private DateTime? _lastStamp = null;
    private bool      _stampInitialized = false;

    public TreasureMaster(TreasureDb db, IGameMemory? mem = null, bool? enabled = null,
        bool? claimDetection = null, bool? collectDetection = null,
        Func<int, TreasureTile, bool>? collectedOracle = null,
        Func<TreasureDb, AnchorResolution?>? resolver = null, AddrMap? addrs = null)
        : this(load: () => db, datasetStamp: () => null, mem: mem, enabled: enabled,
               claimDetection: claimDetection, collectDetection: collectDetection,
               collectedOracle: collectedOracle, resolver: resolver, addrs: addrs) { }

    public TreasureMaster(
        Func<TreasureDb> load,
        Func<DateTime?> datasetStamp,
        IGameMemory? mem = null,
        bool? enabled = null,
        bool? claimDetection = null,
        bool? collectDetection = null,
        Func<int, TreasureTile, bool>? collectedOracle = null,
        Func<TreasureDb, AnchorResolution?>? resolver = null,
        AddrMap? addrs = null)
    {
        _load           = load;
        _datasetStamp   = datasetStamp;
        _db             = TreasureDb.MakeEmpty();
        _mem            = mem ?? new LiveMemory();
        _addrs          = addrs ?? new AddrMap();
        _resolver       = resolver;
        _audit          = new ArmAudit(_mem, _addrs);
        _holder         = new TileHolder(_mem);
        _markers        = new MarkerWriter(_mem);
        _claims         = new ClaimAudit(_mem, _addrs);
        _collect        = new CollectAudit(_mem, _addrs);
        _enabled        = enabled ?? Tuning.TreasureEnabled;
        _claimDetection = claimDetection ?? Tuning.ClaimDetectionEnabled;
        _collectDetection = collectDetection ?? Tuning.CollectDetectionEnabled;
        _collectedOracle  = collectedOracle
            ?? (_collectDetection ? (Func<int, TreasureTile, bool>)_collect.IsCollected : null);
        _fastHold       = new FastHold(_holder, Tuning.TreasureFastHoldMs);
    }

    public void StartFastHold() => _fastHold.Start();
    internal FastHold FastHold => _fastHold;

    public void ResetBattle()
    {
        _phase           = Phase.Disarmed;
        _map             = null;
        _activeMap       = null;
        _stableTicks     = 0;
        _stableMapId     = 0;
        _armAttempts     = 0;
        _revalidateTick  = 0;
        _badMapTicks     = 0;
        _naggedThisBattle           = false;
        _capLoggedThisBattle        = false;
        _foreignLoggedThisBattle    = false;
        _markersLoggedThisBattle    = false;
        _lastMarkerProbe            = null;
        _flapLoggedThisBattle       = false;
        _claimed.Clear();
        _collected.Clear();
        _lastCount.Clear();
        _armCount.Clear();
        _claimItem.Clear();
        _lastClaimDiag              = null;
        _lastStateDiag              = null;
        _lastHoldDiag               = null;
        _fastHold.Publish(null);
    }

    public void Tick(DateTime now, bool inLive)
    {
        if (!_stampInitialized)
        {
            _db               = _load();
            _stampInitialized = true;
            _lastStamp        = _datasetStamp();
            _stampCheckCountdown = Tuning.TreasureStampCheckTicks;
        }
        else
        {
            if (--_stampCheckCountdown <= 0)
            {
                _stampCheckCountdown = Tuning.TreasureStampCheckTicks;
                var current = _datasetStamp();
                if (current.HasValue && current != _lastStamp)
                {
                    _lastStamp = current;
                    _db = _load();
                    _globalIdle        = false;
                    _globalIdleChecked = false;
                    _claimOkForBuild       = true;
                    _collectOkForBuild     = true;
                    _fingerprintOkForBuild = true;
                    ResetBattle();
                    var mapCount = 0;
                    foreach (var m in _db.Maps)
                        if (m.Tiles.Count > 0) mapCount++;
                    ModLogger.Event(LogVerb.Save, $"The treasure dataset was reloaded: {mapCount} map(s) carry addresses.");
                    Flight.Record("save", $"dataset reloaded maps={mapCount}");
                }
            }
        }

        if (!_globalIdleChecked)
        {
            CheckGlobalIdle();
            if (!_globalIdleChecked) { _fastHold.Publish(null); return; }
        }
        if (_globalIdle) { _fastHold.Publish(null); return; }

        if (Tuning.RetryDiagnostics)
        {
            string sd = $"inLive={inLive} phase={_phase}";
            if (sd != _lastStateDiag) { _lastStateDiag = sd; ModLogger.Debug(LogVerb.Trace, $"state {sd}"); }
        }

        if (!inLive)
        {
            _stableTicks = 0;
            _fastHold.Publish(null);
            return;
        }

        switch (_phase)
        {
            case Phase.Disarmed:      TickDisarmed(); break;
            case Phase.Arming:        TickArming();   break;
            case Phase.Armed:         TickArmed();    break;
        }

        _fastHold.Publish(_phase == Phase.Armed ? (_activeMap ?? _map) : null);
    }

    private void CheckGlobalIdle()
    {
        _globalIdleChecked = true;

        if (!_enabled)
        {
            _globalIdle = true;
            ModLogger.Event(LogVerb.Config, "Treasure marking is disabled in the mod configuration; enable it in the Reloaded launcher to mark treasure tiles.");
            return;
        }

        if (_db.Maps.Count == 0)
        {
            _globalIdle = true;
            return;
        }

        var live = _audit.ReadPeBuildKey();
        if (live is null)
        {
            _globalIdleChecked = false;
            return;
        }

        var bk = _db.BuildKey;
        string mismatchDetail = bk != null 
            ? $"dataset build {bk.TimeDateStamp:X}/{bk.SizeOfImage:X}, running build {live.Value.TimeDateStamp:X}/{live.Value.SizeOfImage:X}"
            : $"running build {live.Value.TimeDateStamp:X}/{live.Value.SizeOfImage:X} (no dataset key)";

        ModLogger.Event(LogVerb.Anchor, $"PE Header Diagnostic: {mismatchDetail}");

        bool matches = bk != null && BuildKeyMatches(
            (uint)bk.TimeDateStamp, (uint)bk.SizeOfImage,
            live.Value.TimeDateStamp, live.Value.SizeOfImage);

        // Always attempt signature resolution if we have a resolver available
        if (!matches || _resolver != null)
        {
            var res = _resolver?.Invoke(_db);
            if (res != null)
            {
                _addrs.Apply(res);
                _db = AnchorRemap.Remap(_db, res.RegionDeltas);
                _claimOkForBuild       = res.ClaimOk;
                _collectOkForBuild     = res.CollectOk;
                _fingerprintOkForBuild = res.FingerprintOk;

                var mapCount = 0;
                foreach (var m in _db.Maps) if (m.Tiles.Count > 0) mapCount++;
                string regions = string.Join(",", res.RegionDeltas.Select(kv => $"{kv.Key}={kv.Value:X}"));
                string derived = string.Join(",", res.DerivedRegions);
                ModLogger.EventWithTrace(LogVerb.Anchor,
                    $"Addresses resolved by signature scanning: {mapCount} map(s) remapped.",
                    $"anchor regions=[{regions}] derived=[{derived}] claim={_claimOkForBuild} " +
                    $"collect={_collectOkForBuild} fingerprint={_fingerprintOkForBuild} ({mismatchDetail})");
                Flight.Record("anchor", $"resolved regions=[{regions}] derived=[{derived}]");
                return;
            }
            else if (!matches)
            {
                _globalIdle = true;
                ModLogger.WarnWithTrace(LogVerb.Anchor,
                    "The game build does not match the treasure dataset and signature resolution failed.",
                    mismatchDetail);
                Flight.Record("anchor", "standdown " + mismatchDetail);
                Flight.RequestFlush("standdown");
                return;
            }
        }
    }

    private void TickDisarmed()
    {
        if (!_audit.TryReadMapId(out byte mapId)) { _stableTicks = 0; return; }

        if (mapId == _stableMapId)
            _stableTicks++;
        else
        {
            _stableMapId = mapId;
            _stableTicks = 1;
        }

        if (_stableTicks < Tuning.TreasureArmStableTicks) return;

        TreasureMap? found = null;
        foreach (var m in _db.Maps)
        {
            if (m.MapId == mapId) { found = m; break; }
        }

        if (found is null) return;

        if (found.Tiles.Count == 0)
        {
            if (!_naggedThisBattle)
            {
                _naggedThisBattle = true;
                ModLogger.WarnWithTrace(LogVerb.Treasure,
                    $"Map {mapId} {found.Name} hides {found.TileCount} treasure tile(s) whose addresses are not captured yet; they cannot be lit.",
                    "treasure capture tool: tools/treasure_flags.py session");
            }
            return;
        }

        _map         = found;
        _armAttempts = 0;
        _phase       = Phase.Arming;
    }

    private void TickArming()
    {
        var map = _map!;

        if (_fingerprintOkForBuild && !map.IsMapIdOnly && !_flapLoggedThisBattle && !_audit.FingerprintMatches(map))
        {
            _flapLoggedThisBattle = true;
            var d = _audit.FingerprintDiag(map);
            ModLogger.WarnWithTrace(LogVerb.Arm,
                $"Map {map.MapId} failed its terrain fingerprint check; arming on map id and quorum anyway (weather or terrain drift).",
                $"arm fingerprint readOk={d.ReadOk} fpVer={d.FpVer} got={d.Got:X} want={d.Expected:X}");
        }

        var (verdict, _) = _audit.AuditAddrs(map, Tuning.TreasureMinPlausibleAddrs);

        switch (verdict)
        {
            case ArmVerdict.Arm:
                _phase          = Phase.Armed;
                _revalidateTick = 0;
                SeedCollected(map);
                if (_claimDetection && _claimOkForBuild) InitClaimBaseline(map);
                RebuildActiveMap();
                var armMap = _activeMap ?? map;
                ModLogger.Event(LogVerb.Arm,
                    $"Map {map.MapId} {map.Name} is armed: {armMap.Tiles.Count} treasure tile(s) held lit" +
                    (_collected.Count > 0 ? $", {_collected.Count} already collected and hidden" : "") +
                    (map.IsMapIdOnly ? " (matched by map id only)" : "") + ".");
                Flight.Record("arm", $"armed map={map.MapId} tiles={armMap.Tiles.Count} collected={_collected.Count}");
                _holder.Hold(armMap);
                WriteMarkers(armMap);
                break;

            case ArmVerdict.Retry:
                _armAttempts++;
                if (_armAttempts >= Tuning.TreasureArmAttemptCap && !_capLoggedThisBattle)
                {
                    _capLoggedThisBattle = true;
                    ModLogger.Debug(LogVerb.Arm,
                        $"map {map.MapId} is waiting to arm; the flag bytes are not in their rest state (tiles off-screen?)");
                }
                break;
        }
    }

    private void TickArmed()
    {
        var map = _map!;

        bool mapOk = _audit.TryReadMapId(out byte currentMapId) && currentMapId == map.MapId;

        if (!mapOk)
        {
            _badMapTicks++;
            if (_badMapTicks >= Tuning.TreasureMapIdBadTicksToReset)
            {
                ModLogger.Event(LogVerb.Treasure, $"The map changed from {map.MapId} mid-battle (a chained battle); resetting for the new map.");
                ResetBattle();
            }
            return;
        }
        _badMapTicks = 0;

        if (_fingerprintOkForBuild && !map.IsMapIdOnly && !_flapLoggedThisBattle
                && ++_revalidateTick >= Tuning.TreasureRevalidateEveryNTicks)
        {
            _revalidateTick = 0;
            if (!_audit.FingerprintMatches(map))
            {
                _flapLoggedThisBattle = true;
                ModLogger.Warn(LogVerb.Arm,
                    $"Map {map.MapId} terrain drifted mid-battle; the marks are held through it (the fingerprint no longer matches; not a disarm).");
            }
        }

        if (_claimDetection && _claimOkForBuild)
        {
            bool grew   = DetectClaims(map);
            bool shrank = DetectRefunds(map);
            RefreshClaimCounts(map);
            if (grew || shrank)
            {
                ModLogger.EventWithTrace(LogVerb.Claim,
                    shrank
                        ? $"A battle reset refunded the claimed treasures; the tiles are lit again ({_claimed.Count} still claimed)."
                        : $"A treasure was claimed; {_claimed.Count} tile(s) on this map are now claimed and unlit.",
                    $"claim tiles=[{string.Join(", ", _claimed)}]");
                Flight.Record("claim", (shrank ? "refund" : "claim") + $" count={_claimed.Count}");
                RebuildActiveMap();
            }
            foreach (var tile in map.Tiles)
            {
                if (_claimed.Contains((tile.X, tile.Y))) _holder.Unlight(tile);
            }
        }

        var holdMap = _activeMap ?? map;
        var (wrote, foreign) = _holder.Hold(holdMap);
        WriteMarkers(holdMap);
        if (foreign > 0 && !_foreignLoggedThisBattle)
        {
            _foreignLoggedThisBattle = true;
            ModLogger.Warn(LogVerb.Treasure,
                $"Map {map.MapId} has {foreign} flag byte(s) away from their expected value (tiles off-screen?); skipping those and holding the rest.");
        }
        if (Tuning.RetryDiagnostics) LogHoldDiag(holdMap, wrote, foreign);
    }

    private void LogHoldDiag(TreasureMap map, int wrote, int foreign)
    {
        int rest = 0, held = 0, frgn = 0, unread = 0;
        var samples = new System.Collections.Generic.List<string>();
        foreach (var tile in map.Tiles)
        {
            foreach (var (addr, _) in tile.Addrs)
            {
                if (!_mem.Readable(addr, 1)) { unread++; continue; }
                switch (ClassifyAddr(_mem.U8(addr)))
                {
                    case AddrState.Resting: rest++; break;
                    case AddrState.Held:    held++; break;
                    case AddrState.Foreign: frgn++; break;
                }
            }
            if (tile.Addrs.Count > 0)
            {
                long a0 = tile.Addrs[0].Addr;
                string vv = _mem.Readable(a0, 1) ? _mem.U8(a0).ToString("X2") : "??";
                samples.Add($"({tile.X},{tile.Y}):{vv}");
            }
        }
        string diag = $"hold: tiles={map.Tiles.Count} wrote={wrote} rest={rest} held={held} " +
                      $"foreign={frgn} unread={unread} [{string.Join(" ", samples)}]";
        if (diag != _lastHoldDiag) { _lastHoldDiag = diag; ModLogger.Debug(LogVerb.Trace, diag); }
    }

    private void RebuildActiveMap()
    {
        if (_map is not { } fullMap) return;
        if (_claimed.Count == 0 && _collected.Count == 0) { _activeMap = fullMap; return; }

        var filteredTiles = new System.Collections.Generic.List<TreasureTile>(fullMap.Tiles.Count);
        foreach (var t in fullMap.Tiles)
        {
            var key = (t.X, t.Y);
            if (!_claimed.Contains(key) && !_collected.Contains(key)) filteredTiles.Add(t);
        }
        _activeMap = new TreasureMap
        {
            MapId     = fullMap.MapId,
            Name      = fullMap.Name,
            TileCount = fullMap.TileCount,
            FpVer     = fullMap.FpVer,
            FpLen     = fullMap.FpLen,
            FpHashHex = fullMap.FpHashHex,
            Tiles     = filteredTiles,
        };
    }

    private void SeedCollected(TreasureMap map)
    {
        _collected.Clear();
        if (!_collectOkForBuild) return;
        if (_collectedOracle is not { } oracle) return;
        foreach (var tile in map.Tiles)
            if (oracle(map.MapId, tile)) _collected.Add((tile.X, tile.Y));
    }

    private void InitClaimBaseline(TreasureMap map)
    {
        _lastCount.Clear();
        _armCount.Clear();
        _claimItem.Clear();
        foreach (var tile in map.Tiles)
        {
            TrackCount(tile.RareItemId);
            TrackCount(tile.CommonItemId);
            TrackArmCount(tile.RareItemId);
            TrackArmCount(tile.CommonItemId);
        }
    }

    private void TrackArmCount(int itemId)
    {
        if (itemId <= 0 || _armCount.ContainsKey(itemId)) return;
        int cur = _claims.ReadCount(itemId);
        if (cur >= 0) _armCount[itemId] = cur;
    }

    private void RefreshClaimCounts(TreasureMap map)
    {
        foreach (var tile in map.Tiles)
        {
            TrackCount(tile.RareItemId);
            TrackCount(tile.CommonItemId);
            TrackArmCount(tile.RareItemId);
            TrackArmCount(tile.CommonItemId);
        }
    }

    private bool DetectClaims(TreasureMap map)
    {
        var occupied = new HashSet<(int, int)>();
        _claims.CollectOccupied(occupied);

        bool grew = false;
        var occDiag = Tuning.ClaimDiagnostics ? new System.Collections.Generic.List<string>() : null;
        foreach (var tile in map.Tiles)
        {
            var key = (tile.X, tile.Y);
            if (!occupied.Contains(key)) continue;
            occDiag?.Add($"({tile.X},{tile.Y}) r{tile.RareItemId}={_claims.ReadCount(tile.RareItemId)} " +
                         $"c{tile.CommonItemId}={_claims.ReadCount(tile.CommonItemId)}");
            if (_claimed.Contains(key)) continue;

            bool rareRose = ItemCountRose(tile.RareItemId);
            bool commonRose = !rareRose && ItemCountRose(tile.CommonItemId);
            if (rareRose || commonRose)
            {
                _claimed.Add(key);
                _claimItem[key] = rareRose ? tile.RareItemId : tile.CommonItemId;
                grew = true;
            }
        }

        if (occDiag != null)
        {
            string diag = string.Join("  ", occDiag);
            if (diag != _lastClaimDiag)
            {
                _lastClaimDiag = diag;
                if (diag.Length > 0)
                    ModLogger.Debug(LogVerb.Claim, $"map {map.MapId} unit(s) on treasure tile(s): {diag}");
            }
        }
        return grew;
    }

    private bool DetectRefunds(TreasureMap map)
    {
        if (_claimed.Count == 0) return false;

        bool rareRefund = false;
        var  edgeDropItems = new HashSet<int>();
        foreach (var tile in map.Tiles)
        {
            var key = (tile.X, tile.Y);
            if (!_claimed.Contains(key)) continue;
            if (!_claimItem.TryGetValue(key, out int item)) continue;
            if (!_armCount.TryGetValue(item, out int baseCount)) continue;
            int cur = _claims.ReadCount(item);
            if (cur < 0 || cur > baseCount) continue;

            if (item == tile.RareItemId && tile.RareItemId != tile.CommonItemId) rareRefund = true;
            if (_lastCount.TryGetValue(item, out int prev) && prev > baseCount) edgeDropItems.Add(item);
        }

        if (!rareRefund && edgeDropItems.Count < 2) return false;

        _claimed.Clear();
        _claimItem.Clear();
        return true;
    }

    private bool ItemCountRose(int itemId)
    {
        if (itemId <= 0) return false;
        int cur = _claims.ReadCount(itemId);
        return cur >= 0 && _lastCount.TryGetValue(itemId, out int prev) && cur > prev;
    }

    private void TrackCount(int itemId)
    {
        if (itemId <= 0) return;
        int cur = _claims.ReadCount(itemId);
        if (cur >= 0) _lastCount[itemId] = cur;
    }

    private void WriteMarkers(TreasureMap map)
    {
        int wrote = _markers.Write(map);
        if (wrote > 0)
        {
            if (!_markersLoggedThisBattle)
            {
                _markersLoggedThisBattle = true;
                ModLogger.Event(LogVerb.Treasure, $"Enhanced markers are active: {wrote} marker slot(s) written for map {map.MapId} {map.Name}.");
            }
            return;
        }

        var (readable, basePtr, writable) = _markers.Resolve();
        string probe = $"readable={readable} base=0x{basePtr:X} writable={writable}";
        if (probe != _lastMarkerProbe)
        {
            _lastMarkerProbe = probe;
            ModLogger.WarnWithTrace(LogVerb.Treasure,
                "Enhanced markers could not be written; the marking utility pointer did not resolve.",
                $"marker ptr@0x{Offsets.EnhancedMarkingUtilityPtr:X} {probe}");
        }
    }
}
