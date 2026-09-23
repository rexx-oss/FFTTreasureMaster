using System.ComponentModel;

namespace FFTTreasureMaster.Configuration;

/// <summary>
/// Mod configuration. Exposed to the Reloaded-II launcher via Configurator so the
/// player can toggle settings without editing files.
/// </summary>
public class Config : Configurable<Config>
{
    [DisplayName("Enable Treasure Master")]
    [Description("Highlight the battle tiles that hide Move-Find treasure (a yellow-fill mark is " +
                 "held on each treasure tile's render flag so it stays lit on the battlefield). " +
                 "Turn this off to disable the mod without uninstalling it. Default: on.")]
    [DefaultValue(true)]
    public bool Enabled { get; set; } = true;

    [DisplayName("Hide tiles after their treasure is claimed")]
    [Description("When a treasure is claimed from a tile -- a Chemist, or any unit with the " +
                 "Treasure Hunter movement ability, picks up the hidden item -- that tile stops " +
                 "being highlighted for the rest of the battle. Also hides any tile whose Move-Find " +
                 "treasure you already collected and won in a previous battle (it does not respawn). " +
                 "Turn this off to keep every treasure tile lit. Default: on.")]
    [DefaultValue(true)]
    public bool HideClaimedTiles { get; set; } = true;

    [DisplayName("All units gain Treasure Hunter")]
    [Description("Give every unit of the standard player jobs the Treasure Hunter movement " +
                 "ability for free, so anyone can pick up Move-Find treasure just by stepping on " +
                 "the tile (no more dragging a Chemist everywhere). Fair warning: enemy humans " +
                 "with those same jobs get it too, and a treasure an enemy grabs is gone for good. " +
                 "Requires the separately installed FFT: The Ivalice Chronicles Mod Loader (by " +
                 "Nenkai); without it this setting quietly does nothing. Changes apply the next " +
                 "time the game is launched. Default: off.")]
    [DefaultValue(false)]
    public bool AllUnitsTreasureHunter { get; set; } = false;

    [DisplayName("Enable Flight Logging")]
    [Description("Save flight_*.jsonl battle telemetry logs to disk when exiting a battle. " +
                 "Leave this off unless diagnosing battle-state or memory issues. Default: off.")]
    [DefaultValue(false)]
    public bool EnableFlightLogging { get; set; } = false;
}
