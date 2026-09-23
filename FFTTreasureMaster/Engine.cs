using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace FFTTreasureMaster;

/// <summary>
/// The Treasure Master runtime.
/// </summary>
internal sealed class Engine
{
    private const int PollMs = 33;

    private readonly TreasureMaster _treasure;
    private readonly BattleState _battle = new();
    private readonly AddrMap _addrs = new();
    private readonly bool _flightLogging;
    private CancellationTokenSource? _cts;
    private string? _lastDispKey;

    public Engine(string modDir, bool? enabled = null, bool? claimDetection = null, bool flightLogging = false)
    {
        _flightLogging = flightLogging;
        var treasureJson = Path.Combine(modDir, "treasure.json");
        var liveMem = new LiveMemory();
        var resolver = new AnchorResolver(liveMem);
        _treasure = new TreasureMaster(
            load:           () => TreasureDb.Load(modDir),
            datasetStamp:   () => { try { return File.GetLastWriteTimeUtc(treasureJson); }
                                    catch { return null; } },
            mem:            liveMem,
            enabled:        enabled,
            claimDetection: claimDetection,
            collectDetection: claimDetection,
            resolver:       resolver.TryResolve,
            addrs:          _addrs);
        _treasure.StartFastHold();
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        Task.Run(async () =>
        {
            ModLogger.Event(LogVerb.Startup, "The runtime loop has started; battles are being watched.");
            while (!token.IsCancellationRequested)
            {
                try { Tick(); }
                catch (Exception ex) { ModLogger.Error(LogVerb.Engine, "The engine tick failed: " + ex.Message); }
                try { await Task.Delay(PollMs, token); } catch { }
            }
        }, token);
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        _cts = null;
    }

    private void Tick()
    {
        uint slot0      = Mem.U32(_addrs.Slot0);
        uint slot9      = Mem.U32(_addrs.Slot9);
        int  battleMode = Mem.U8(_addrs.BattleMode);
        bool paused     = Mem.U8(_addrs.PauseFlag) == 1;
        int  eventId    = Mem.U16(_addrs.EventId);
        var  now        = DateTime.Now;

        BattleEdge edge = _battle.Step(slot0, slot9, battleMode, paused, eventId, now);
        if (edge == BattleEdge.Entered)
        {
            ModLogger.NoteBattleEdge();
            if (_flightLogging)
            {
                Flight.FlushBattleStart();
                Flight.Record("battle", $"enter slot0={slot0:X} slot9={slot9:X} mode={battleMode}");
            }
            ModLogger.EventWithTrace(LogVerb.BattleStart, "Battle started.",
                $"battle-start sentinels (slot0={slot0:X} slot9={slot9:X} mode={battleMode} event={eventId} paused={paused})");
        }
        if (edge == BattleEdge.Entered || edge == BattleEdge.Exited)
        {
            if (edge == BattleEdge.Exited)
            {
                ModLogger.EventWithTrace(LogVerb.BattleEnd, "Battle ended.",
                    $"battle-end sentinels (slot0={slot0:X} slot9={slot9:X} mode={battleMode} event={eventId} paused={paused})");
                if (_flightLogging)
                {
                    Flight.Record("battle", $"exit slot0={slot0:X} slot9={slot9:X} mode={battleMode}");
                    Flight.FlushBattleEnd();
                }
                ModLogger.NoteBattleEdge();
            }
            _treasure.ResetBattle();
        }

        bool battleDisplayed = BattleState.BattleDisplayed(slot9, battleMode);

        if (Tuning.RetryDiagnostics)
        {
            string key = $"{battleDisplayed}|{slot0:X}|{slot9:X}";
            if (key != _lastDispKey)
            {
                _lastDispKey = key;
                ModLogger.Debug(LogVerb.Trace, $"engine displayed={battleDisplayed} slot0={slot0:X} slot9={slot9:X} " +
                                               $"mode={battleMode} event={eventId} paused={paused}");
            }
        }

        _treasure.Tick(now, battleDisplayed);
        if (_flightLogging) Flight.DrainPending();
    }
}
