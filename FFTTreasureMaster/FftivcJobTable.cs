using System;
using System.Runtime.CompilerServices;
using fftivc.utility.modloader.Interfaces.Tables;
using fftivc.utility.modloader.Interfaces.Tables.Models;
using Reloaded.Mod.Interfaces.Internal;

namespace FFTTreasureMaster;

/// <summary>
/// Live adapter over the FFTIVC modloader's IFFTOJobDataManager controller -- the production
/// IJobTable. Live-only and untested by design, exactly like LiveMemory over Mem: all logic
/// worth testing lives in TreasureHunterGrant behind the seam. Every member is exception-
/// proof; a missing or incompatible modloader surfaces as null / false, never as a throw
/// that could reach the engine.
/// </summary>
internal sealed class FftivcJobTable : IJobTable
{
    private const string OwnerModId = "prawl.fft.treasuremaster";

    private readonly IFFTOJobDataManager _mgr;

    private FftivcJobTable(IFFTOJobDataManager mgr) => _mgr = mgr;

    /// <summary>Null when the FFTIVC Mod Loader is absent or its interfaces assembly cannot
    /// be resolved. Never throws.</summary>
    public static IJobTable? TryCreate(IModLoaderV1 loader)
    {
        try 
        { 
            return Create(loader); 
        }
        catch (Exception ex) 
        { 
            try { ModLogger.Warn(LogVerb.Config, $"TryCreate error: {ex.GetType().Name} - {ex.Message}"); } catch { }
            return null; 
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static IJobTable? Create(IModLoaderV1 loader)
    {
        var weak = loader.GetController<IFFTOJobDataManager>();
        return weak != null && weak.TryGetTarget(out var mgr) ? new FftivcJobTable(mgr) : null;
    }

    public bool IsReady()
    {
        try { _mgr.GetJob(0); return true; }
        catch { return false; }
    }

    public ushort[]? GetInnates(int jobId)
    {
        try
        {
            var job = _mgr.GetJob(jobId);
            if (job == null) return null;
            var baseValues = new[]
            {
                job.InnateAbilityId1 ?? 0,
                job.InnateAbilityId2 ?? 0,
                job.InnateAbilityId3 ?? 0,
                job.InnateAbilityId4 ?? 0,
            };
            var audit = _mgr.ChangedProperties;
            return InnateOverlay.Apply(baseValues, slot =>
                audit.TryGetValue((jobId, InnateOverlay.SlotPropertyNames[slot]), out var entry)
                    ? entry.Difference?.NewValue
                    : null);
        }
        catch { return null; }
    }

    public bool TryApplyInnate(int jobId, int slotIndex, ushort abilityId)
    {
        try
        {
            var patch = new Job { Id = jobId };
            switch (slotIndex)
            {
                case 0: patch.InnateAbilityId1 = abilityId; break;
                case 1: patch.InnateAbilityId2 = abilityId; break;
                case 2: patch.InnateAbilityId3 = abilityId; break;
                case 3: patch.InnateAbilityId4 = abilityId; break;
                default: return false;
            }
            _mgr.ApplyTablePatch(OwnerModId, patch);
            return true;
        }
        catch { return false; }
    }
}
