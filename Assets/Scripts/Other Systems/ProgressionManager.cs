using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using UnityEngine;

public static class ProgressionManager
{
    private const int    CurrentSaveVersion = 7;
    private const string SaveFileName       = "progression.json";

    private const int   BaseFee       = 50;
    private const float FeeMultiplier = 1.1f;
    private const long  PointsCap     = 1_000_000_000_000_000_000L;

    // ── Pricing design principles (deliberate, not accidental — check against these before assuming a typo) ──
    // 1. Every cost array below is a pure arithmetic progression: cost(tier) = start + step*tier, with a
    //    CONSTANT step within that stat (no per-stat compounding/curving). Verified true for every single
    //    array as of 2026-08-07, including the 10-entry LevelUpStats/XPMultiplier tables. The exponential
    //    "getting more expensive over time" feel comes entirely from the separate, global feePart = BaseFee *
    //    FeeMultiplier^boughtUpgrades surcharge added to every purchase regardless of which stat/tier (see
    //    GetBaseStatUpgradeCost) — NOT from these tables. If asked to normalize/audit these for typos later,
    //    check for constant step-per-array first; a broken step is the tell.
    //    EXCEPTION (2026-08-21, retuned same day from x2.5 down to a x1.5-based growing-exponent curve):
    //    every stat with a Threat-Level-gated tier is geometric, not arithmetic, over its gated portion.
    //    "L" below means Threat-Level COUNT within the gated portion, 1-based (the first gated pair/tier is
    //    L=1 regardless of its real UnlockDifficulty number — TL0 and TL1 both collapse to L=1, since neither
    //    has a unique modifier of its own). Two shapes, depending on how many tiers that stat unlocks per
    //    Threat Level:
    //    - TWO tiers per level (LevelUpStatsCosts/TotalStrengthPercentCosts/StrengthPerLevelCosts, pairing
    //      mirrors their own UnlockDifficulty's {2,2,3,3,4,4,5,5,6,6} shape): pair 1 = (seed, seed*1.5).
    //      Within any pair, the second tier is always its own pair's first tier * 1.5. Crossing into pair L
    //      (L=2,3,4,5): that pair's first tier = previous pair's SECOND tier * 1.5^(L-1) — the crossing
    //      exponent grows every time a new Threat Level is crossed, it does not stay constant.
    //    - ONE tier per level (MoveSpeedPercentCosts, and Strength's own gated tail — see below): tier 1 =
    //      seed; tier L (L=2,3,4,5) = tier(L-1) * 1.5^(L-1) — same growing-exponent crossing rule, applied to
    //      every single tier since there's no "pair" to hold a within-level step.
    //    1.5 isn't a power of 2, so a long enough chain eventually can't stay an exact integer — rounded
    //    (half up) only where unavoidable; every other entry is exact. Extend either shape's exact pattern
    //    (same growing exponent, L+1 next) if any of these arrays ever needs more tiers.
    //    Strength is the one MIXED case: tiers 0-2 are ungated (UnlockDifficulty all 0) and keep the plain
    //    arithmetic +250 step rule 1 describes. The geometric exception only applies from tier 3 onward
    //    (first Threat-Level-gated tier, L=1) — seeded off tier 2's own ungated value, but treated as ALREADY
    //    one exponent step up from it (500 * 1.5^1 = 750), not equal to it — unlike MoveSpeedPercentCosts'
    //    own L=1 (which has no preceding ungated tier to differentiate itself from, so starts unscaled at the
    //    seed directly). Never derive Strength's gated tiers from tier 0 directly — it's 0, and 0 times
    //    anything stays 0.
    // 2. Step is ~50% of that stat's own starting price almost everywhere (WeaponRange 500→750, MaxHealth/
    //    AttackSpeed/MoveSpeed/LevelUpStats/XPMultiplier/LevelUpOptions/StrengthPerLevel/TotalStrengthPercent
    //    all exactly start/2). ShieldRechargeTime (500 step on a 1500 start, ~33%) is the one deliberate
    //    outlier to that ratio — still a constant step per rule 1, just not exactly half.
    // 3. Stats are grouped into rough price brackets by perceived power, each roughly an order of magnitude
    //    above the last: ~250-750 (core stats), ~1000-3000 (high-impact), ~4000-44000 (systemic/meta,
    //    ungated portion) — every Threat-Level-gated tier on top of that scales geometrically per rule 1's
    //    exception instead of continuing a flat bracket, so those run well past their base bracket's own
    //    ceiling by design (e.g. TotalStrengthPercentCosts' own gated tail tops out at 4378939 — the growing
    //    crossing exponent means these climb much further than the flat brackets below ever suggest).
    // 4. Tier COUNT (separate from price) also follows a rule: "core stats" get 4 tiers on purpose — 4
    //    unupgraded stat-augment levels, i.e. 2 "upgraded" levels, since LevelUpStats effectively doubles
    //    the per-level value from the stat augment database at max rank. MaxHealth is the one deliberate
    //    exception at 5 tiers (base 50 + 5 points of +10 lands exactly on 100), and MoveSpeed is
    //    deliberately capped at 2 tiers (far higher per-point impact in this game than other core stats, so
    //    it gets less total room to buy).
    // 5. Every cost array has a same-length "UnlockDifficulty" sibling directly below it — see
    //    GetTierRequiredDifficulty/CanUpgradeBaseStat for how a nonzero entry gates that tier behind
    //    ProgressionManager.HighestUnlockedDifficulty, and UnlockNextDifficulty for the "More X Shop Tiers"
    //    announcement a newly-crossed threshold queues. All zero right now on every stat (ungated) — this is
    //    a hand-tuned list the user edits directly, so deliberately no further inline commentary here.
    private static readonly int[] StrengthCosts =
        { 0, 250, 500, 750, 1125, 2531, 8543, 43249 };
    private static readonly int[] StrengthUnlockDifficulty =
        { 0, 0, 0, 2, 3, 4, 5, 6 };
    private static readonly int[] DashChargesCosts =
        { 0, 250, 500, 750 };
    private static readonly int[] DashChargesUnlockDifficulty =
        { 0, 0, 0, 0 };
    private static readonly int[] LuckCosts =
        { 0, 250, 500, 750 };
    private static readonly int[] LuckUnlockDifficulty =
        { 0, 0, 0, 0 };
    private static readonly int[] EnergyRegenCosts =
        { 0, 250, 500 };
    private static readonly int[] EnergyRegenUnlockDifficulty =
        { 0, 0, 0 };
    private static readonly int[] DashHasteCosts =
        { 0, 250, 500, 750 };
    private static readonly int[] DashHasteUnlockDifficulty =
        { 0, 0, 0, 0 };
    private static readonly int[] AbilityHasteCosts =
        { 0, 250, 500, 750 };
    private static readonly int[] AbilityHasteUnlockDifficulty =
        { 0, 0, 0, 0 };
    private static readonly int[] CritChanceCosts =
        { 0, 250, 500, 750 };
    private static readonly int[] CritChanceUnlockDifficulty =
        { 0, 0, 0, 0 };
    private static readonly int[] HealthRegenCosts =
        { 0, 500, 1000, 1500 };
    private static readonly int[] HealthRegenUnlockDifficulty =
        { 0, 0, 0, 0 };
    private static readonly int[] MaxShieldCosts =
        { 0, 1000, 2000 };
    private static readonly int[] MaxShieldUnlockDifficulty =
        { 0, 0, 0 };
    private static readonly int[] WeaponRangeCosts =
        { 500, 750, 1000, 1250 };
    private static readonly int[] WeaponRangeUnlockDifficulty =
        { 0, 0, 0, 0 };
    private static readonly int[] MaxHealthCosts =
        { 500, 750, 1000, 1250, 1500 };
    private static readonly int[] MaxHealthUnlockDifficulty =
        { 0, 0, 0, 0, 0 };
    private static readonly int[] RevivePowerCosts =
        { 500, 750, 1000, 1250, 1500 };
    private static readonly int[] RevivePowerUnlockDifficulty =
        { 0, 0, 0, 0, 0 };
    private static readonly int[] MaxEnergyCosts =
        { 500, 750, 1000, 1250, 1500 };
    private static readonly int[] MaxEnergyUnlockDifficulty =
        { 0, 0, 0, 0, 0 };
    private static readonly int[] CollectionRadiusCosts =
        { 500, 750, 1000, 1250, 1500 };
    private static readonly int[] CollectionRadiusUnlockDifficulty =
        { 0, 0, 0, 0, 0 };
    private static readonly int[] ReviveTimePenaltyCosts =
        { 1000, 1500 };
    private static readonly int[] ReviveTimePenaltyUnlockDifficulty =
        { 0, 0 };
    private static readonly int[] AttackSpeedCosts =
        { 1000, 1500, 2000, 2500 };
    private static readonly int[] AttackSpeedUnlockDifficulty =
        { 0, 0, 0, 0 };
    private static readonly int[] ShieldRechargeTimeCosts =
        { 1500, 2000 };
    private static readonly int[] ShieldRechargeTimeUnlockDifficulty =
        { 0, 0 };
    private static readonly int[] MoveSpeedCosts =
        { 2000, 3000 };
    private static readonly int[] MoveSpeedUnlockDifficulty =
        { 0, 0 };
    private static readonly int[] DeathPreventionCosts =
        { 3000 };
    private static readonly int[] DeathPreventionUnlockDifficulty =
        { 0 };
    private static readonly int[] LevelUpStatsCosts =
        { 4000, 6000, 9000, 13500, 30375, 45563, 153773, 230660, 1167717, 1751576 };
    private static readonly int[] LevelUpStatsUnlockDifficulty =
        { 2, 2, 3, 3, 4, 4, 5, 5, 6, 6 };
    private static readonly int[] LevelUpOptionsCosts =
        { 10000, 15000 };
    private static readonly int[] LevelUpOptionsUnlockDifficulty =
        { 0, 0 };
    private static readonly int[] TotalStrengthPercentCosts =
        { 10000, 15000, 22500, 33750, 75938, 113906, 384434, 576650, 2919293, 4378939 };
    private static readonly int[] TotalStrengthPercentUnlockDifficulty =
        { 2, 2, 3, 3, 4, 4, 5, 5, 6, 6 };
    private static readonly int[] MoveSpeedPercentCosts =
        { 5000, 7500, 16875, 56953, 288325 };
    private static readonly int[] MoveSpeedPercentUnlockDifficulty =
        { 2, 3, 4, 5, 6 };
    private static readonly int[] StrengthPerLevelCosts =
        { 5000, 7500, 11250, 16875, 37969, 56953, 192217, 288325, 1459646, 2189469 };
    private static readonly int[] StrengthPerLevelUnlockDifficulty =
        { 2, 2, 3, 3, 4, 4, 5, 5, 6, 6 };

    private static string SavePath => Path.Combine(Application.persistentDataPath, SaveFileName);

    private class SaveData
    {
        public int                     saveVersion    = CurrentSaveVersion;
        public long                    totalPoints    = 0;
        public long                    spentPoints    = 0;
        public int                     boughtUpgrades = 0;
        public Dictionary<string, int> statTiers      = new();
        // 1 = only difficulty 1 unlocked (the starting state) — new field, no save-version bump needed
        // (Newtonsoft deserializes a missing field to this C# default automatically, so an old save file
        // loads exactly as if the player had never cleared anything, which is correct).
        public int                     highestUnlockedDifficulty = 1;
        // Scaffolding for the unlockable-augment-sets system (see AugmentData.unlockTag) — same
        // no-save-version-bump reasoning as highestUnlockedDifficulty above (missing field deserializes to
        // an empty set). Nothing currently ever adds to this — no unlock TRIGGER is wired up yet, this is
        // purely the storage half.
        public HashSet<string>         unlockedAugmentTags = new();
    }

    private static SaveData _data;

    private static SaveData Data
    {
        get
        {
            if (_data == null) Load();
            return _data;
        }
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void Initialize()
    {
        CleanLegacyPlayerPrefs();
        Load();
        if (_data.saveVersion != CurrentSaveVersion)
        {
            long savedPoints = _data.totalPoints;
            var savedTiers  = _data.statTiers;
            // Both added AFTER this migration block was originally written, and never backfilled into the
            // rebuild below — meaning any save-version bump (e.g. a cost-table retune, unrelated to either
            // of these) silently reset a player's unlocked Threat Level (and would have done the same to any
            // unlocked augment tags, once that system actually grants any) back to the C# default. Carried
            // forward explicitly now, same as totalPoints already was.
            int savedHighestUnlockedDifficulty = _data.highestUnlockedDifficulty;
            var savedUnlockedAugmentTags = _data.unlockedAugmentTags;
            _data = new SaveData
            {
                totalPoints = savedPoints,
                highestUnlockedDifficulty = savedHighestUnlockedDifficulty,
                unlockedAugmentTags = savedUnlockedAugmentTags,
            };

            foreach (StatType stat in System.Enum.GetValues(typeof(StatType)))
            {
                string key = stat.ToString();
                if (!savedTiers.TryGetValue(key, out int savedTier) || savedTier <= 0) continue;

                int[] costs      = GetStatCosts(stat);
                int   targetTier = Mathf.Min(savedTier, costs.Length);

                for (int t = 0; t < targetTier; t++)
                {
                    int  feePart = Mathf.RoundToInt(BaseFee * Mathf.Pow(FeeMultiplier, _data.boughtUpgrades));
                    long cost    = costs[t] + feePart;
                    if (_data.totalPoints - _data.spentPoints < cost) break;
                    _data.statTiers[key] = t + 1;
                    _data.boughtUpgrades++;
                    _data.spentPoints   += cost;
                }
            }

            Save();
        }
    }

    private static void CleanLegacyPlayerPrefs()
    {
        // These key strings are a hardcoded snapshot of an OLD PlayerPrefs-based save system's own key
        // names, used only to detect and delete its leftovers — NOT generated from StatType.ToString(), so
        // they intentionally still say "WeaponDamage" and must NOT be renamed to match StatType.Strength.
        string[] legacyKeys =
        {
            "TotalPoints", "SpentPoints", "BoughtUpgrades", "SaveVersion", "LevelUpTier",
            "BaseStatTier_WeaponDamage", "BaseStatTier_MaxHealth",    "BaseStatTier_MoveSpeed",
            "BaseStatTier_AttackSpeed",  "BaseStatTier_HealthRegen",  "BaseStatTier_EnergyRegen",
            "BaseStatTier_DashHaste",    "BaseStatTier_CritChance",   "BaseStatTier_WeaponRange",
            "BaseStatTier_CollectionRadius", "BaseStatTier_MaxEnergy", "BaseStatTier_DashCharges",
            "BaseStatTier_RevivePower",  "BaseStatTier_ReviveTimePenalty", "BaseStatTier_XPMultiplier",
            "BaseStatTier_LevelUpOptions", "BaseStatTier_LevelUpStats", "BaseStatTier_Pierce"
        };
        bool anyFound = false;
        foreach (var key in legacyKeys)
            if (PlayerPrefs.HasKey(key)) { anyFound = true; break; }
        if (!anyFound) return;
        foreach (var key in legacyKeys)
            PlayerPrefs.DeleteKey(key);
        PlayerPrefs.Save();
    }

    private static void Load()
    {
        try
        {
            if (File.Exists(SavePath))
            {
                _data = JsonConvert.DeserializeObject<SaveData>(File.ReadAllText(SavePath));
                if (_data != null) return;
            }
        }
        catch { }
        _data = new SaveData();
    }

    private static void Save()
    {
        try { File.WriteAllText(SavePath, JsonConvert.SerializeObject(_data, Formatting.Indented)); }
        catch (System.Exception e) { Debug.LogError($"[ProgressionManager] Failed to save: {e.Message}"); }
    }

    private static int[] GetStatCosts(StatType statType) => statType switch
    {
        StatType.MaxHealth         => MaxHealthCosts,
        StatType.Strength          => StrengthCosts,
        StatType.MoveSpeed         => MoveSpeedCosts,
        StatType.AttackSpeed       => AttackSpeedCosts,
        StatType.HealthRegen       => HealthRegenCosts,
        StatType.EnergyRegen       => EnergyRegenCosts,
        StatType.DashHaste         => DashHasteCosts,
        StatType.CritChance        => CritChanceCosts,
        StatType.WeaponRange       => WeaponRangeCosts,
        StatType.CollectionRadius  => CollectionRadiusCosts,
        StatType.MaxEnergy         => MaxEnergyCosts,
        StatType.DashCharges       => DashChargesCosts,
        StatType.RevivePower       => RevivePowerCosts,
        StatType.ReviveTimePenalty => ReviveTimePenaltyCosts,
        StatType.LevelUpOptions    => LevelUpOptionsCosts,
        StatType.LevelUpStats       => LevelUpStatsCosts,
        StatType.TotalStrengthPercent => TotalStrengthPercentCosts,
        StatType.Luck                => LuckCosts,
        StatType.StrengthPerLevel    => StrengthPerLevelCosts,
        StatType.MaxShield           => MaxShieldCosts,
        StatType.ShieldRechargeTime  => ShieldRechargeTimeCosts,
        StatType.AbilityHaste        => AbilityHasteCosts,
        StatType.DeathPrevention     => DeathPreventionCosts,
        StatType.MoveSpeedPercent    => MoveSpeedPercentCosts,
        _                           => System.Array.Empty<int>()
    };

    // Mirrors GetStatCosts above exactly, one UnlockDifficulty array per cost array — see the "Pricing
    // design principles" header comment's own point 5 for the full reasoning.
    private static int[] GetStatUnlockDifficulty(StatType statType) => statType switch
    {
        StatType.MaxHealth         => MaxHealthUnlockDifficulty,
        StatType.Strength          => StrengthUnlockDifficulty,
        StatType.MoveSpeed         => MoveSpeedUnlockDifficulty,
        StatType.AttackSpeed       => AttackSpeedUnlockDifficulty,
        StatType.HealthRegen       => HealthRegenUnlockDifficulty,
        StatType.EnergyRegen       => EnergyRegenUnlockDifficulty,
        StatType.DashHaste         => DashHasteUnlockDifficulty,
        StatType.CritChance        => CritChanceUnlockDifficulty,
        StatType.WeaponRange       => WeaponRangeUnlockDifficulty,
        StatType.CollectionRadius  => CollectionRadiusUnlockDifficulty,
        StatType.MaxEnergy         => MaxEnergyUnlockDifficulty,
        StatType.DashCharges       => DashChargesUnlockDifficulty,
        StatType.RevivePower       => RevivePowerUnlockDifficulty,
        StatType.ReviveTimePenalty => ReviveTimePenaltyUnlockDifficulty,
        StatType.LevelUpOptions    => LevelUpOptionsUnlockDifficulty,
        StatType.LevelUpStats       => LevelUpStatsUnlockDifficulty,
        StatType.TotalStrengthPercent => TotalStrengthPercentUnlockDifficulty,
        StatType.Luck                => LuckUnlockDifficulty,
        StatType.StrengthPerLevel    => StrengthPerLevelUnlockDifficulty,
        StatType.MaxShield           => MaxShieldUnlockDifficulty,
        StatType.ShieldRechargeTime  => ShieldRechargeTimeUnlockDifficulty,
        StatType.AbilityHaste        => AbilityHasteUnlockDifficulty,
        StatType.DeathPrevention     => DeathPreventionUnlockDifficulty,
        StatType.MoveSpeedPercent    => MoveSpeedPercentUnlockDifficulty,
        _                           => System.Array.Empty<int>()
    };

    public static int GetMaxBaseTier(StatType statType) => GetStatCosts(statType).Length;

    // tier is 0-based, same convention GetBaseStatTier/GetStatCosts already use (tier 0 = the FIRST
    // purchasable tier). 0 = no Threat Level requirement.
    public static int GetTierRequiredDifficulty(StatType statType, int tier)
    {
        int[] reqs = GetStatUnlockDifficulty(statType);
        return tier >= 0 && tier < reqs.Length ? reqs[tier] : 0;
    }

    public static long TotalPoints     => Data.totalPoints;
    public static long SpentPoints     => Data.spentPoints;
    public static long AvailablePoints => Data.totalPoints - Data.spentPoints;
    public static int  BoughtUpgrades  => Data.boughtUpgrades;

    public static bool IsShopUnlocked => TotalPoints >= 50;

    public static int HighestUnlockedDifficulty => Data.highestUnlockedDifficulty;

    // Called from GameManagerPD.UpdateView() off a verified win transition (see that call site's own
    // comment for why it has to be verified-state, not raw Simulate() — a predicted-context call here would
    // re-fire this save-file write on every resimulated replay of the winning tick, not just once). Max, not
    // a raw overwrite — clearing an EASIER difficulty than the player's current frontier (replaying an
    // already-cleared one) must never regress an already-unlocked higher difficulty back down. Clamped to
    // GamePresets.MaxDifficulty so clearing the final difficulty doesn't unlock a nonexistent one past it.
    // Returns the newly-unlocked difficulty number, or 0 if this win didn't actually unlock anything new
    // (already-cleared difficulty replayed) — GameManagerPD uses this to decide whether to show the
    // "Threat Level X Unlocked" message at all. Difficulty 1 itself can never be the return value here since
    // the minimum input is 1 (making the minimum possible "next" 2) — it starts unlocked, never announced.
    public static int UnlockNextDifficulty(int clearedDifficulty)
    {
        int next = Mathf.Min(clearedDifficulty + 1, GamePresets.MaxDifficulty);
        if (next <= Data.highestUnlockedDifficulty) return 0;

        int previous = Data.highestUnlockedDifficulty;

        // Snapshot BEFORE mutating highestUnlockedDifficulty — same "before/after set comparison" shape
        // CommitPoints uses, needed here to tell apart two different cases below: a stat whose OWN first
        // tier is what just crossed (the stat itself is newly becoming available — "Upgrade Shop Item",
        // same phrasing/dedup bucket CommitPoints already uses for a points-triggered first unlock) versus a
        // stat that was ALREADY fully unlocked gaining a later tier ("More Shop Tiers").
        var prevUnlocked = new HashSet<StatType>();
        foreach (StatType stat in System.Enum.GetValues(typeof(StatType)))
            if (IsStatUnlocked(stat)) prevUnlocked.Add(stat);

        Data.highestUnlockedDifficulty = next;
        Save();

        // A stat can only ever gain ONE tier per single difficulty unlock in the default mapping, but this
        // loop doesn't assume that — it reacts once per stat that gained ANY newly-crossed tier, regardless
        // of how many.
        foreach (StatType stat in System.Enum.GetValues(typeof(StatType)))
        {
            int[] reqs = GetStatUnlockDifficulty(stat);
            if (reqs.Length == 0) continue;

            bool gainedATier = false;
            foreach (int req in reqs)
                if (req > previous && req <= next) { gainedATier = true; break; }
            if (!gainedATier) continue;

            if (!prevUnlocked.Contains(stat) && IsStatUnlocked(stat))
                TryAnnounceUnlock(stat, GetStatDisplayName(stat) + " Upgrade Shop Item");
            else if (prevUnlocked.Contains(stat))
                TryAnnounceUnlock(stat, $"More {GetStatDisplayName(stat)} Upgrade Tiers");
            // else: gained a tier but still not unlocked even now (points not met yet) — no message, per
            // IsStatUnlocked's own "needs both" requirement.
        }

        return next;
    }

    // See AugmentData.unlockTag's own doc comment and SaveData.unlockedAugmentTags above. Called from
    // AugmentUnlockTagDatabase.GrantQualifyingUnlocks (see its own doc comment) once per player spawn.
    public static IEnumerable<string> UnlockedAugmentTags => Data.unlockedAugmentTags;

    public static bool IsAugmentTagUnlocked(string tag) =>
        !string.IsNullOrEmpty(tag) && Data.unlockedAugmentTags.Contains(tag);

    // Idempotent — re-unlocking an already-unlocked tag is a harmless no-op, same "safe to call blindly"
    // convention as most of this file's other grant-style methods. Unlike TryAnnounceUnlock/_announcedUnlocks
    // above, a tag doesn't need a separate per-lobby-visit dedup set — Data.unlockedAugmentTags.Add's own
    // true/false result IS the "genuinely new" signal, permanently (a tag only ever transitions
    // locked->unlocked once in a save file's lifetime, unlike a stat's many separate tier thresholds), so
    // unlockMessage only ever queues on the one real occurrence. Leave unlockMessage null/empty for a silent
    // unlock (no popup).
    public static void UnlockAugmentTag(string tag, string unlockMessage = null)
    {
        if (string.IsNullOrEmpty(tag) || !Data.unlockedAugmentTags.Add(tag)) return;
        Save();
        if (!string.IsNullOrEmpty(unlockMessage)) UnlockManager.Queue(unlockMessage);
    }

    // Points AND (where the stat's own first tier carries a Threat Level requirement) difficulty both have
    // to be satisfied — see GetTierRequiredDifficulty's own doc comment. Tier 0's requirement specifically
    // (not any later tier) gates the STAT ITSELF becoming visible/purchasable at all; a later tier being
    // gated only affects CanUpgradeBaseStat for that specific tier, not this.
    public static bool IsStatUnlocked(StatType statType)
    {
        if (statType == StatType.Strength) return true;
        int[] costs = GetStatCosts(statType);
        return costs.Length > 0
            && TotalPoints >= costs[0] * 10 + BaseFee
            && HighestUnlockedDifficulty >= GetTierRequiredDifficulty(statType, 0);
    }

    public static int GetBaseStatTier(StatType statType) =>
        Data.statTiers.TryGetValue(statType.ToString(), out int tier) ? tier : 0;

    public static float GetBaseStatValuePerTier(StatType statType) => statType switch
    {
        StatType.Strength          => 5f,
        StatType.MaxHealth         => 10f,
        StatType.MoveSpeed         => 1f,
        StatType.AttackSpeed       => 10f,
        StatType.HealthRegen       => 0.2f,
        StatType.EnergyRegen       => 2f,
        StatType.DashHaste         => 10f,
        StatType.CritChance        => 5f,
        StatType.WeaponRange       => 1f,
        StatType.CollectionRadius  => 1f,
        StatType.MaxEnergy         => 10f,
        StatType.DashCharges       => 1f,
        StatType.RevivePower       => 0.1f,
        StatType.ReviveTimePenalty => -25f,
        StatType.LevelUpOptions    => 1f,
        StatType.LevelUpStats       => 10f,
        StatType.TotalStrengthPercent => 0.1f,
        StatType.MoveSpeedPercent    => 0.1f,
        StatType.Luck                => 10f,
        StatType.StrengthPerLevel    => 1f,
        StatType.MaxShield           => 0.05f,
        StatType.ShieldRechargeTime  => -0.5f,
        StatType.AbilityHaste        => 5f,
        // Not a scaling amount — just a nonzero flag so GetBaseStatBonus reads > 0 once bought (tier 1 of 1)
        // and 0 otherwise. See CharacterStatsPD.ApplyProgressionBonuses' own DeathPrevention handling.
        StatType.DeathPrevention     => 1f,
        _                           => 0f
    };

    public static float GetBaseStatBonus(StatType statType) =>
        Mathf.Min(GetBaseStatTier(statType), GetMaxBaseTier(statType)) * GetBaseStatValuePerTier(statType);

    public static int GetBaseStatUpgradeCost(StatType statType)
    {
        int[] costs = GetStatCosts(statType);
        int   tier  = GetBaseStatTier(statType);
        if (tier >= costs.Length) return int.MaxValue;
        int feePart = Mathf.RoundToInt(BaseFee * Mathf.Pow(FeeMultiplier, BoughtUpgrades));
        return costs[tier] + feePart;
    }

    public static bool CanUpgradeBaseStat(StatType statType)
    {
        int tier = GetBaseStatTier(statType);
        return tier < GetMaxBaseTier(statType)
            && AvailablePoints >= GetBaseStatUpgradeCost(statType)
            && HighestUnlockedDifficulty >= GetTierRequiredDifficulty(statType, tier);
    }

    // The specific "you could afford this right now, but the tier itself isn't unlocked yet" state —
    // distinct from CanUpgradeBaseStat's own false (which doesn't say WHY), for UpgradePanel to show a
    // dedicated "Locked" state instead of just a greyed-out price.
    public static bool IsTierAffordableButDifficultyLocked(StatType statType)
    {
        int tier = GetBaseStatTier(statType);
        if (tier >= GetMaxBaseTier(statType)) return false;
        return AvailablePoints >= GetBaseStatUpgradeCost(statType)
            && HighestUnlockedDifficulty < GetTierRequiredDifficulty(statType, tier);
    }

    // Used to flag the "Upgrade Shop" button itself (not the individual stat buttons inside it) whenever
    // there's anything worth buying — safe to run over every StatType since GetStatCosts' default case
    // (empty array) makes CanUpgradeBaseStat/IsStatUnlocked naturally false for anything outside the shop.
    public static bool AnyBaseStatAffordable()
    {
        foreach (StatType stat in System.Enum.GetValues(typeof(StatType)))
            if (IsStatUnlocked(stat) && CanUpgradeBaseStat(stat))
                return true;
        return false;
    }

    public static bool TryUpgradeBaseStat(StatType statType)
    {
        if (!CanUpgradeBaseStat(statType)) return false;
        int cost = GetBaseStatUpgradeCost(statType);
        Data.statTiers[statType.ToString()] = GetBaseStatTier(statType) + 1;
        Data.boughtUpgrades++;
        Data.spentPoints += cost;
        Save();
        return true;
    }

    public static float GetStatMultiplier(StatType statType) =>
        1f + GetBaseStatTier(StatType.LevelUpStats) * 0.10f;

    // Simulates the exact same loop UpgradePanel.OnBuyAllClicked() actually runs (while
    // CanUpgradeBaseStat -> TryUpgradeBaseStat), without mutating anything — so the previewed price always
    // matches what a real "Buy All" click would spend, not the full remaining-tier total regardless of
    // affordability. Stops at whichever constraint bites first: running out of points, OR hitting a
    // Threat-Level-gated tier the player hasn't unlocked yet (2026-08-21 — this used to just sum every
    // remaining tier's cost unconditionally, which read as misleadingly high/wrong once gated tiers existed,
    // but was already slightly wrong even before that for the exact same reason on the AFFORDABILITY side).
    public static long GetBuyAllCost(StatType statType)
    {
        int[] costs     = GetStatCosts(statType);
        int   tier      = GetBaseStatTier(statType);
        long  total     = 0;
        long  remaining = AvailablePoints;
        int   simBought = BoughtUpgrades;
        int   difficulty = HighestUnlockedDifficulty;

        for (int t = tier; t < costs.Length; t++)
        {
            if (difficulty < GetTierRequiredDifficulty(statType, t)) break;
            int  feePart = Mathf.RoundToInt(BaseFee * Mathf.Pow(FeeMultiplier, simBought));
            long cost    = costs[t] + feePart;
            if (remaining < cost) break;
            total     += cost;
            remaining -= cost;
            simBought++;
        }
        return total;
    }

    public static long GetRefundAmount(StatType statType)
    {
        int tier = GetBaseStatTier(statType);
        if (tier == 0) return 0;
        int[] costs = GetStatCosts(statType);
        long refund = 0;
        int bought = BoughtUpgrades;
        for (int t = tier - 1; t >= 0; t--)
        {
            int feePart = Mathf.RoundToInt(BaseFee * Mathf.Pow(FeeMultiplier, Mathf.Max(0, bought - 1)));
            refund += costs[t] + feePart;
            bought--;
        }
        return refund;
    }

    public static void ResetBaseStat(StatType statType)
    {
        int tier = GetBaseStatTier(statType);
        if (tier == 0) return;
        int[] costs  = GetStatCosts(statType);
        long  refund = 0;
        int   bought = BoughtUpgrades;
        for (int t = tier - 1; t >= 0; t--)
        {
            int feePart = Mathf.RoundToInt(BaseFee * Mathf.Pow(FeeMultiplier, Mathf.Max(0, bought - 1)));
            refund += costs[t] + feePart;
            bought--;
        }
        Data.statTiers.Remove(statType.ToString());
        Data.boughtUpgrades = Mathf.Max(0, Data.boughtUpgrades - tier);
        Data.spentPoints    = System.Math.Max(0L, Data.spentPoints - refund);
        Save();
    }

    // Tie-break order for AutoBuy below, ONLY consulted when two+ stats are tied on both of its real sort
    // keys (lowest tier, then highest cost) — earlier in this list wins. Deliberately a separate array
    // instead of leaning on StatType's own declaration/numeric order (which is what a plain
    // Enum.GetValues(typeof(StatType)) iteration would implicitly do): StatType's explicit int values are
    // serialized directly into StatAugmentDatabase.asset (`statType: N`, Unity's default enum
    // serialization), so renumbering the enum to reshuffle THIS priority would silently repoint every
    // existing level-up stat-pair entry at the wrong stat. Freely reorder this array instead — it has zero
    // effect on anything StatType's own numeric identity touches. Currently mirrors StatType's own
    // declaration order (today's tie-break behavior, unchanged) — reorder to taste.
    private static readonly StatType[] AutoBuyPriority =
    {
        StatType.DashCharges,
        StatType.DeathPrevention,
        StatType.StrengthPerLevel,
        StatType.TotalStrengthPercent,
        StatType.MoveSpeedPercent,
        StatType.LevelUpStats,
        StatType.LevelUpOptions,
        StatType.HealthRegen,
        StatType.MaxShield,
        StatType.ShieldRechargeTime,
        StatType.MoveSpeed,
        StatType.Strength,
        StatType.AbilityHaste,
        StatType.Luck,
        StatType.CritChance,
        StatType.AttackSpeed,
        StatType.WeaponRange,
        StatType.MaxHealth,
        StatType.CollectionRadius,
        StatType.DashHaste,
        StatType.EnergyRegen,
        StatType.MaxEnergy,
        StatType.RevivePower,
        StatType.ReviveTimePenalty,
    };

    // Repeatedly buys whatever's currently affordable, one purchase at a time: among affordable+unlocked
    // stats, prioritizes the LOWEST current tier first (spreads investment broadly instead of dumping
    // everything into whichever stat happens to be cheapest), and among stats tied for that lowest tier,
    // the MOST expensive one (so a cheaper affordable purchase in that same tier group isn't left on the
    // table just because a pricier one in the group got bought first and ate the budget). Ties on BOTH of
    // those break by AutoBuyPriority's own order (see its own doc comment) — the strict `<`/`>` comparisons
    // below mean whichever candidate is iterated first never gets replaced by an exact tie, so iterating
    // AutoBuyPriority in order is what makes "first" mean "earlier in that list" rather than "whatever
    // Enum.GetValues happened to return first." Re-evaluates from scratch after every single purchase, not
    // just once up front — GetBaseStatUpgradeCost's feePart scales off BoughtUpgrades, which increases with
    // every purchase, so which stat is "most expensive" (and even which stats remain affordable at all) can
    // genuinely change mid-run. Returns how many purchases were made, purely informational for a caller
    // that wants to report/log it.
    public static int AutoBuy()
    {
        int purchased = 0;
        while (true)
        {
            StatType? best     = null;
            int       bestTier = int.MaxValue;
            long      bestCost = -1;

            foreach (StatType stat in AutoBuyPriority)
            {
                if (!IsStatUnlocked(stat) || !CanUpgradeBaseStat(stat)) continue;

                int  tier = GetBaseStatTier(stat);
                long cost = GetBaseStatUpgradeCost(stat);
                if (tier > bestTier) continue;
                if (tier < bestTier || cost > bestCost)
                {
                    best     = stat;
                    bestTier = tier;
                    bestCost = cost;
                }
            }

            if (best == null) break;
            TryUpgradeBaseStat(best.Value);
            purchased++;
        }
        return purchased;
    }

    // Runtime-only (never persisted to the save file) — max one unlock message per stat PER LOBBY VISIT, not
    // per app lifetime. Reset by LobbySceneController.Start() every time the Lobby scene loads (fresh
    // launch, or returning from a match), so a genuinely NEW unlock (e.g. a later match clears another
    // Threat Level that opens up yet another tier for a stat already announced once) still gets its own
    // message on the NEXT lobby return — this only collapses multiple unlock-worthy events for the SAME
    // stat happening within the SAME trip out to a match and back (e.g. points and a Threat Level tier both
    // being satisfied by the same win) into one message instead of two.
    private static readonly HashSet<StatType> _announcedUnlocks = new();

    public static void ResetAnnouncedUnlocks() => _announcedUnlocks.Clear();

    // HashSet.Add returns false if already present, so this is naturally "announce once per lobby visit,
    // silently no-op every time after until the next ResetAnnouncedUnlocks()" in one call.
    private static void TryAnnounceUnlock(StatType stat, string message)
    {
        if (_announcedUnlocks.Add(stat)) UnlockManager.Queue(message);
    }

    public static void CommitPoints(long upgradePoints)
    {
        bool shopWasUnlocked = IsShopUnlocked;
        var prevUnlocked = new HashSet<StatType>();
        foreach (StatType stat in System.Enum.GetValues(typeof(StatType)))
            if (IsStatUnlocked(stat)) prevUnlocked.Add(stat);

        Data.totalPoints = System.Math.Min(Data.totalPoints + upgradePoints, PointsCap);
        Save();

        if (!shopWasUnlocked && IsShopUnlocked)
            UnlockManager.Queue("Upgrade Shop");

        // IsStatUnlocked already requires BOTH points and (where applicable) Threat Level — see its own doc
        // comment — so a stat whose first tier is difficulty-gated correctly waits for whichever of the two
        // requirements it's still missing, even if points alone were satisfied first.
        foreach (StatType stat in System.Enum.GetValues(typeof(StatType)))
            if (!prevUnlocked.Contains(stat) && IsStatUnlocked(stat))
                TryAnnounceUnlock(stat, GetStatDisplayName(stat) + " Upgrade Shop Item");
    }

    private static string GetStatDisplayName(StatType stat) => stat switch
    {
        StatType.MaxHealth         => "Max Health",
        StatType.Strength          => "Strength",
        StatType.MoveSpeed         => "Move Speed",
        StatType.AttackSpeed       => "Attack Speed",
        StatType.HealthRegen       => "Health Regen",
        StatType.EnergyRegen       => "Energy Regen",
        StatType.DashHaste         => "Dash Haste",
        StatType.CritChance        => "Crit Chance",
        StatType.WeaponRange       => "Range",
        StatType.CollectionRadius  => "Collection Radius",
        StatType.MaxEnergy         => "Max Energy",
        StatType.DashCharges       => "Dash Charges",
        StatType.RevivePower       => "Revive Power",
        StatType.ReviveTimePenalty => "Revive Time Penalty",
        StatType.LevelUpOptions    => "Level Up Options",
        StatType.LevelUpStats       => "Level Up Stats",
        StatType.TotalStrengthPercent => "Total Strength %",
        StatType.MoveSpeedPercent    => "Move Speed Percent",
        StatType.Luck                => "Luck",
        StatType.StrengthPerLevel    => "Strength Per Level",
        StatType.MaxShield           => "Max Shield",
        StatType.ShieldRechargeTime  => "Shield Recharge Time",
        StatType.AbilityHaste        => "Ability Haste",
        StatType.DeathPrevention     => "Death Prevention",
        _                           => stat.ToString()
    };

    public static string FormatPoints(long value) => value switch
    {
        >= 1_000_000_000_000_000_000L => $"{value / 1_000_000_000_000_000_000f:F2}quint",
        >= 1_000_000_000_000_000L     => $"{value / 1_000_000_000_000_000f:F2}quad",
        >= 1_000_000_000_000L         => $"{value / 1_000_000_000_000f:F2}tril",
        >= 1_000_000_000L             => $"{value / 1_000_000_000f:F2}bil",
        >= 1_000_000L                 => $"{value / 1_000_000f:F2}mil",
        >= 1_000L                     => $"{value / 1_000f:F2}k",
        _                             => value.ToString()
    };

    public static void SpendPoints(int amount)
    {
        if (amount > AvailablePoints) return;
        Data.spentPoints += amount;
        Save();
    }

    public static void RefundPoints()
    {
        Data.spentPoints    = 0;
        Data.boughtUpgrades = 0;
        Data.statTiers.Clear();
        Save();
    }

    public static void HardReset()
    {
        _data = new SaveData();
        Save();
    }
}
