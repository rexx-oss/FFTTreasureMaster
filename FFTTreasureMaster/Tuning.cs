namespace FFTTreasureMaster;

/// <summary>
/// Treasure Master tuning knobs.
/// </summary>
internal static class Tuning
{
    public const bool TreasureEnabled = true;
    public const int TreasureArmStableTicks = 30;
    public const int TreasureRevalidateEveryNTicks = 30;
    public const int TreasureArmAttemptCap = 60;
    public const int TreasureMinPlausibleAddrs = 4;
    public const int TreasureMapIdBadTicksToReset = 3;
    public const int TreasureStampCheckTicks = 30;
    public const int TreasureFastHoldMs = 8;
    public const bool EnhancedMarkersEnabled = false;
    public const bool ClaimDetectionEnabled = true;

    // Set to true so battle-state and memory sentinels are logged
    public static readonly bool RetryDiagnostics = true;

    public static readonly bool ClaimDiagnostics = false;
    public static readonly bool CollectDetectionEnabled = true;
    public const bool AllUnitsTreasureHunterEnabled = false;

    public static readonly int[] TreasureHunterGrantJobIds =
    {
        1, 2, 3, 13, 22, 25, 26, 30, 50, 72,
        74, 75, 76, 77, 78, 79, 80, 81, 82, 83,
        84, 85, 86, 87, 88, 89, 90, 91, 92, 93,
    };

    public const int GrantRetryIntervalMs = 250;
    public const int GrantRetryCapMs = 30000;
    public const int GrantReassertDelayMs = 2000;
    public const int GrantWatchdogDelayMs = 5000;

    public const int AnchorScanChunkBytes = 0x40000;
    public const int AnchorScanMaxSectionBytes = 0x2000000;
    public const int AnchorScanMaxTotalBytes = 0x4000000;
    public const int AnchorScanHitCap = 8;
    public const int AnchorMaxDeltaBytes = 0x4000000;
}
