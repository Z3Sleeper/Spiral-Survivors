using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using UnityEngine;

public static class ProgressionManager
{
    private const int    CurrentSaveVersion = 11;
    private const string SaveFileName       = "progression.json";

    private const long  PointsCap     = 1_000_000_000_000_000_000L;

    // ── Pricing design principles (deliberate, not accidental — check against these before assuming a typo) ──
    // 1. TWO regimes, split at UnlockDifficulty 4 (2026-08-29 — TL1/TL2 are treated as "tutorial" Threat
    //    Levels; TL3 is where cost scaling genuinely starts, so the compounding formula stays for TL0-3 but
    //    is deliberately NOT allowed to keep compounding past it):
    //    - UnlockDifficulty 0-3 (effUD, below): cost[i] = cost[i-1] + base * effUD(i) — the original
    //      cumulative formula, unchanged from its own doc comment history. effUD(i) treats an
    //      UnlockDifficulty of 0 OR 1 as 1 (both mean "no extra Threat-Level cost multiplier yet").
    //    - UnlockDifficulty 4+: a CONSTANT step per Threat Level crossed, not the cumulative formula above.
    //      step = base * 4 (computed once per stat, using the first TL4+ tier's own UD number — 4 — not
    //      recomputed with a growing UD each time). Every tier sharing the SAME UnlockDifficulty gets the
    //      SAME price (no increase between, say, LevelUpStatsCosts' own two TL4 tiers) — price only changes
    //      when crossing into a NEW UnlockDifficulty group, by exactly +step each time: TL4 group =
    //      anchor + 1*step, TL5 group = anchor + 2*step, TL6 group = anchor + 3*step, where "anchor" is
    //      whatever the last UnlockDifficulty-3-or-below tier already priced at (untouched, see above).
    //      This was a deliberate fix for a real compounding problem: the original cumulative formula
    //      (cost[i] = cost[i-1] + base*UD(i), chained all the way through) made total cost grow
    //      QUADRATICALLY with tier depth — each tier's own increment itself scaled with UD, so a long chain
    //      of increasing UDs compounded on top of an ever-growing base — while points-gain-per-Threat-Level
    //      is only LINEAR (GameManagerPD.DifficultyPointMultiplier = Difficulty). The mismatch was invisible
    //      at TL0-3 (few tiers had compounded yet) but blew TL4-6 totals well past their "~2x that Threat
    //      Level's own win reward" target, LevelUpStatsCosts worst of all (10 tiers spanning TL2-6, the most
    //      compounding of any stat). A pure "no reference to any other tier, cost[i] = base*UD(i) directly"
    //      version was tried and rejected first — it let price DROP below an earlier tier whenever
    //      base*UD(i) happened to undercut it, which is never acceptable (explicit design: price must never
    //      drop tier-to-tier). A max(cost[i-1], base*UD(i)) version was tried next and also rejected — it
    //      never drops, but plateaus (flat, no growth at all) for however many tiers it takes base*UD(i) to
    //      catch back up to the anchor, which for some stats (LevelUpStatsCosts) meant 6 tiers flat in a
    //      row. The constant-step version is the one that stuck: guaranteed monotonic, genuinely linear
    //      (not quadratic, not flat) growth every single Threat Level crossed, with no dependence on how
    //      many tiers happen to share a given Threat Level.
    //    Each array's own entry is the final purchase price either way — no separate global fee/surcharge
    //    layered on top (see GetBaseStatUpgradeCost).
    //    1a. The "50-floor" shape: some stats' cost[0] is a flat 50 (so the very first tier never costs
    //        literally 0), with the REAL seed/base living at cost[1] instead, and the cumulative formula
    //        above only applying from cost[2] onward — StrengthCosts/DashChargesCosts/LuckCosts/
    //        EnergyRegenCosts/DashHasteCosts/AbilityHasteCosts/CritChanceCosts/HealthRegenCosts/
    //        MaxShieldCosts all follow this shape. Every other stat's cost[0] IS its own base directly
    //        (formula applies from cost[1] on), no separate floor entry.
    // 2. Tier COUNT (separate from price) still follows its own rule: "core stats" get 4 tiers on purpose —
    //    4 unupgraded stat-augment levels, i.e. 2 "upgraded" levels, since LevelUpStats effectively doubles
    //    the per-level value from the stat augment database at max rank. MaxHealth is the one deliberate
    //    exception at 5 tiers (base 50 + 5 points of +10 lands exactly on 100), and MoveSpeed is
    //    deliberately capped at 2 tiers (far higher per-point impact in this game than other core stats, so
    //    it gets less total room to buy).
    // 3. Every cost array has a same-length "UnlockDifficulty" sibling directly below it — see
    //    GetTierRequiredDifficulty/CanUpgradeBaseStat for how a nonzero entry gates that tier behind
    //    ProgressionManager.HighestUnlockedDifficulty, and UnlockNextDifficulty for the "More X Shop Tiers"
    //    announcement a newly-crossed threshold queues. This is a hand-tuned list the user edits directly, so
    //    deliberately no further inline commentary here.
    //    3a. Threat Level gating pulled back project-wide (2026-09-30, explicit "went overboard restricting
    //        things to Threat Levels" correction) — every array now only gates tier 0 (if it already did;
    //        several stats were never gated at all), every later tier's own requirement zeroed out. Cost
    //        alone gates progression past that point — a tier expensive enough to need real playtime to
    //        afford doesn't also need a separate Threat Level clear on top. (The "77" permanent-soft-disable
    //        sentinel pattern this same pass exempted MoveSpeedPercent/TotalStrengthPercent from is still a
    //        valid convention for some FUTURE stat that needs parking — see project_totalstrengthpercent_removal_planned
    //        memory — but both of those two specific stats were scrapped entirely, same day, once the
    //        CharacterStatsPD-side bug investigation that found them showed they'd never actually be used.)
    private static readonly int[] StrengthCosts =
        { 50, 250, 500, 1000, 1500, 3000, 4500, 6000 };
    private static readonly int[] StrengthUnlockDifficulty =
        { 0, 0, 0, 0, 0, 0, 0, 0 };
    private static readonly int[] DashChargesCosts =
        { 50, 500, 2000, 3500 };
    private static readonly int[] DashChargesUnlockDifficulty =
        { 0, 0, 0, 0 };
    private static readonly int[] LuckCosts =
        { 50, 500, 1500, 2500 };
    private static readonly int[] LuckUnlockDifficulty =
        { 0, 0, 0, 0 };
    // 4 tiers (2026-08-23, was 3) — matches +1.25%/tier * 4 = +5% total from the shop (retuned 2026-08-24
    // from +2.5%/tier alongside baseEnergyRegen's own 10% -> 5% nerf), landing exactly on the intended 5%
    // base -> 10% fully-upgraded target (see GetBaseStatValuePerTier's own EnergyRegen entry). Same
    // {0, 250, 500, 750} cost curve as DashHaste/AbilityHaste/CritChance's own 4-tier arrays.
    private static readonly int[] EnergyRegenCosts =
        { 50, 500, 1500, 2500 };
    private static readonly int[] EnergyRegenUnlockDifficulty =
        { 0, 0, 0, 0 };
    private static readonly int[] DashHasteCosts =
        { 50, 500, 1500, 2500 };
    private static readonly int[] DashHasteUnlockDifficulty =
        { 0, 0, 0, 0 };
    private static readonly int[] AbilityHasteCosts =
        { 50, 500, 1500, 2500 };
    private static readonly int[] AbilityHasteUnlockDifficulty =
        { 0, 0, 0, 0 };
    private static readonly int[] CritChanceCosts =
        { 50, 500, 1500, 2500 };
    private static readonly int[] CritChanceUnlockDifficulty =
        { 0, 0, 0, 0 };
    private static readonly int[] HealthRegenCosts =
        { 50, 500, 1500, 2500 };
    private static readonly int[] HealthRegenUnlockDifficulty =
        { 0, 0, 0, 0 };
    private static readonly int[] MaxShieldCosts =
        { 50, 1000, 2000 };
    private static readonly int[] MaxShieldUnlockDifficulty =
        { 0, 0, 0 };
    private static readonly int[] WeaponRangeCosts =
        { 1000, 4000, 8000, 12000 };
    private static readonly int[] WeaponRangeUnlockDifficulty =
        { 2, 0, 0, 0 };
    private static readonly int[] MaxHealthCosts =
        { 500, 1500, 3000, 5000, 7000 };
    private static readonly int[] MaxHealthUnlockDifficulty =
        { 0, 0, 0, 0, 0 };
    private static readonly int[] RevivePowerCosts =
        { 500, 1500, 3000, 5000, 7000 };
    private static readonly int[] RevivePowerUnlockDifficulty =
        { 0, 0, 0, 0, 0 };
    private static readonly int[] MaxEnergyCosts =
        { 500, 1500, 3000, 5000, 7000 };
    private static readonly int[] MaxEnergyUnlockDifficulty =
        { 0, 0, 0, 0, 0 };
    private static readonly int[] CollectionRadiusCosts =
        { 500, 1500, 3000, 5000, 7000 };
    private static readonly int[] CollectionRadiusUnlockDifficulty =
        { 0, 0, 0, 0, 0 };
    private static readonly int[] ReviveTimePenaltyCosts =
        { 1000, 2000 };
    private static readonly int[] ReviveTimePenaltyUnlockDifficulty =
        { 0, 0 };
    private static readonly int[] AttackSpeedCosts =
        { 2000, 8000, 16000, 24000 };
    private static readonly int[] AttackSpeedUnlockDifficulty =
        { 2, 0, 0, 0 };
    private static readonly int[] ShieldRechargeTimeCosts =
        { 3000, 15000 };
    private static readonly int[] ShieldRechargeTimeUnlockDifficulty =
        { 2, 0 };
    private static readonly int[] MoveSpeedCosts =
        { 2500, 10000, 20000, 30000 };
    private static readonly int[] MoveSpeedUnlockDifficulty =
        { 2, 0, 0, 0 };
    private static readonly int[] DeathPreventionCosts =
        { 3000 };
    private static readonly int[] DeathPreventionUnlockDifficulty =
        { 3 };
    // Re-derived 2026-08-29 from the new unified formula (rule 1 above) off the same 2000 seed — every
    // chain below is now a flat "cost[i-1] + base * effUD(i)" walk, no more geometric crossing exponent.
    private static readonly int[] LevelUpStatsCosts =
        { 3000, 18000, 39000, 51000, 63000 };
    private static readonly int[] LevelUpStatsUnlockDifficulty =
        { 2, 3, 4, 5, 6 };
    private static readonly int[] LevelUpOptionsCosts =
        { 8000, 40000 };
    private static readonly int[] LevelUpOptionsUnlockDifficulty =
        { 3, 0 };
    private static readonly int[] StrengthPerLevelCosts =
        { 3000, 12000, 24000, 36000, 48000 };
    private static readonly int[] StrengthPerLevelUnlockDifficulty =
        { 2, 3, 4, 5, 6 };
    private static readonly int[] RerollTokensCosts =
        { 500, 2000, 4000, 6000, 8000 };
    private static readonly int[] RerollTokensUnlockDifficulty =
        { 2, 3, 4, 5, 6 };
    private static readonly int[] SurgeMoveSpeedPenaltyReductionCosts =
        { 9000, 12000, 15000, 18000, 21000};
    private static readonly int[] SurgeMoveSpeedPenaltyReductionUnlockDifficulty =
        { 5, 0, 0, 0, 0 };
    private static readonly int[] RerollTokenLevelsPerGrantCosts =
        { 20000 };
    private static readonly int[] RerollTokenLevelsPerGrantUnlockDifficulty =
        { 6 };

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
                    long cost = costs[t];
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
        StatType.Luck                => LuckCosts,
        StatType.StrengthPerLevel    => StrengthPerLevelCosts,
        StatType.MaxShield           => MaxShieldCosts,
        StatType.ShieldRechargeTime  => ShieldRechargeTimeCosts,
        StatType.AbilityHaste        => AbilityHasteCosts,
        StatType.DeathPrevention     => DeathPreventionCosts,
        StatType.RerollTokens        => RerollTokensCosts,
        StatType.SurgeMoveSpeedPenaltyReduction => SurgeMoveSpeedPenaltyReductionCosts,
        StatType.RerollTokenLevelsPerGrant => RerollTokenLevelsPerGrantCosts,
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
        StatType.Luck                => LuckUnlockDifficulty,
        StatType.StrengthPerLevel    => StrengthPerLevelUnlockDifficulty,
        StatType.MaxShield           => MaxShieldUnlockDifficulty,
        StatType.ShieldRechargeTime  => ShieldRechargeTimeUnlockDifficulty,
        StatType.AbilityHaste        => AbilityHasteUnlockDifficulty,
        StatType.DeathPrevention     => DeathPreventionUnlockDifficulty,
        StatType.RerollTokens        => RerollTokensUnlockDifficulty,
        StatType.SurgeMoveSpeedPenaltyReduction => SurgeMoveSpeedPenaltyReductionUnlockDifficulty,
        StatType.RerollTokenLevelsPerGrant => RerollTokenLevelsPerGrantUnlockDifficulty,
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
            && TotalPoints >= costs[0] * 10
            && HighestUnlockedDifficulty >= GetTierRequiredDifficulty(statType, 0);
    }

    public static int GetBaseStatTier(StatType statType) =>
        Data.statTiers.TryGetValue(statType.ToString(), out int tier) ? tier : 0;

    public static float GetBaseStatValuePerTier(StatType statType) => statType switch
    {
        StatType.Strength          => 5f,
        StatType.MaxHealth         => 10f,
        StatType.MoveSpeed         => 0.5f,
        StatType.AttackSpeed       => 5f,
        StatType.HealthRegen       => 0.25f,
        // Fraction of MaxEnergy per second, not flat units (2026-08-23 rework — see
        // CharacterStatsPD.baseEnergyRegen's own doc comment). 0.0125 = +1.25% per tier (nerfed 2026-08-24
        // from 2.5%, alongside baseEnergyRegen's own 10% -> 5% nerf), 4 tiers max (EnergyRegenCosts) = +5%
        // total, landing on 5% base -> 10% fully-upgraded target.
        StatType.EnergyRegen       => 0.0125f,
        StatType.DashHaste         => 10f,
        StatType.CritChance        => 5f,
        StatType.WeaponRange       => 1f,
        StatType.CollectionRadius  => 1f,
        StatType.MaxEnergy         => 10f,
        StatType.DashCharges       => 1f,
        StatType.RevivePower       => 0.1f,
        StatType.ReviveTimePenalty => -25f,
        StatType.LevelUpOptions    => 1f,
        StatType.LevelUpStats       => 20f,
        StatType.Luck                => 5f,
        StatType.StrengthPerLevel    => 1f,
        StatType.MaxShield           => 0.05f,
        StatType.ShieldRechargeTime  => -0.5f,
        StatType.AbilityHaste        => 5f,
        // Not a scaling amount — just a nonzero flag so GetBaseStatBonus reads > 0 once bought (tier 1 of 1)
        // and 0 otherwise. See CharacterStatsPD.ApplyProgressionBonuses' own DeathPrevention handling.
        StatType.DeathPrevention     => 1f,
        // +1 CharacterStatsPD.RerollTokens per tier — see CharacterStatsPD.ApplyProgressionBonuses' own
        // rerollTokensBonus handling.
        StatType.RerollTokens        => 1f,
        // +5% move speed retained during Surge per tier — see CharacterStatsPD.bonusSurgeMoveSpeedPenaltyFlat's
        // own doc comment.
        StatType.SurgeMoveSpeedPenaltyReduction => 0.05f,
        // Not actually read for this stat's real effect — RerollTokenLevelsPerGrant is a single-tier
        // unlock flag (2026-10-06, simplified from an earlier 5-tier 5/4/3/2/1 override table), not a flat
        // per-tier addition, so CharacterStatsPD just writes the fixed "every 3 levels" constant directly
        // once GetBaseStatTier > 0 — see rerollTokenLevelsPerGrant's own doc comment there. -1f here is just
        // a sane non-broken value for GetBaseStatBonus's own generic formula, in case anything ever calls it
        // generically for this stat.
        StatType.RerollTokenLevelsPerGrant => -1f,
        _                           => 0f
    };

    public static float GetBaseStatBonus(StatType statType) =>
        Mathf.Min(GetBaseStatTier(statType), GetMaxBaseTier(statType)) * GetBaseStatValuePerTier(statType);

    public static int GetBaseStatUpgradeCost(StatType statType)
    {
        int[] costs = GetStatCosts(statType);
        int   tier  = GetBaseStatTier(statType);
        if (tier >= costs.Length) return int.MaxValue;
        return costs[tier];
    }

    public static bool CanUpgradeBaseStat(StatType statType)
    {
        int tier = GetBaseStatTier(statType);
        return tier < GetMaxBaseTier(statType)
            && AvailablePoints >= GetBaseStatUpgradeCost(statType)
            && HighestUnlockedDifficulty >= GetTierRequiredDifficulty(statType, tier);
    }

    // Whether this stat's NEXT tier is gated behind a Threat Level the player hasn't unlocked yet —
    // distinct from CanUpgradeBaseStat's own false (which doesn't say WHY), for UpgradePanel to show a
    // dedicated "Locked" state instead of just a greyed-out price. Deliberately independent of affordability
    // (2026-08-23, was: only true if ALSO currently affordable) — "locked" is meant to take priority over
    // "can't afford" as the shown reason regardless of whether the player happens to have enough points,
    // not just when both happen to be true at once.
    public static bool IsTierDifficultyLocked(StatType statType)
    {
        int tier = GetBaseStatTier(statType);
        if (tier >= GetMaxBaseTier(statType)) return false;
        return HighestUnlockedDifficulty < GetTierRequiredDifficulty(statType, tier);
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
        int   difficulty = HighestUnlockedDifficulty;

        for (int t = tier; t < costs.Length; t++)
        {
            if (difficulty < GetTierRequiredDifficulty(statType, t)) break;
            long cost = costs[t];
            if (remaining < cost) break;
            total     += cost;
            remaining -= cost;
        }
        return total;
    }

    public static long GetRefundAmount(StatType statType)
    {
        int tier = GetBaseStatTier(statType);
        if (tier == 0) return 0;
        int[] costs = GetStatCosts(statType);
        long refund = 0;
        for (int t = tier - 1; t >= 0; t--)
            refund += costs[t];
        return refund;
    }

    public static void ResetBaseStat(StatType statType)
    {
        int tier = GetBaseStatTier(statType);
        if (tier == 0) return;
        int[] costs  = GetStatCosts(statType);
        long  refund = 0;
        for (int t = tier - 1; t >= 0; t--)
            refund += costs[t];
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
    //
    // Deliberately excludes MoveSpeed/StrengthPerLevel/LevelUpStats (2026-10-14) — they live ONLY in
    // AutoBuyAlwaysPriority now, not here too. Keeping them in both would be dead duplication, not a real
    // fallback: AutoBuy checks AutoBuyAlwaysPriority first every single iteration, using the exact same
    // CanUpgradeBaseStat affordability check this list's own search uses, so by the time this list is even
    // consulted, all three have already failed that identical check moments earlier — they could never
    // actually be selected from here.
    private static readonly StatType[] AutoBuyPriority =
    {
        StatType.DashCharges,
        StatType.DeathPrevention,
        StatType.HealthRegen,
        StatType.MaxShield,
        StatType.ShieldRechargeTime,
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
        StatType.RerollTokens,
        StatType.SurgeMoveSpeedPenaltyReduction,
        StatType.LevelUpOptions,
        StatType.RerollTokenLevelsPerGrant,
        StatType.RevivePower,
        StatType.ReviveTimePenalty,
    };

    // Always bought first, ahead of the normal cheapest-first competition below, the instant any of these
    // are affordable+unlocked (2026-10-14, explicit request) — Strength Per Level, Move Speed, and Level Up
    // Stats are disproportionately strong purchases relative to their cost compared to every other stat, so
    // AutoBuy steers a new/unknowledgeable/lazy player onto them instead of letting them lose out to
    // whatever's merely cheapest this instant. Deliberately NOT also listed in AutoBuyPriority below — see
    // that list's own doc comment for why duplicating them there would be dead weight, never actually
    // reachable from it.
    private static readonly StatType[] AutoBuyAlwaysPriority =
    {
        StatType.StrengthPerLevel,
        StatType.LevelUpStats,
        StatType.MoveSpeed,
    };

    // Repeatedly buys whatever's currently affordable, one purchase at a time: among affordable+unlocked
    // stats, prioritizes the CHEAPEST current tier first (2026-08-20 — used to prioritize lowest tier first,
    // most-expensive-tied-tier second; flipped because the intended "buy these first" stats, being cheap,
    // kept losing out to pricier stats at the same low tier and ending up under-bought), and among stats
    // tied on cost, the LOWEST tier (so a stat genuinely behind on investment isn't skipped just because
    // something else happens to cost the exact same right now). Ties on BOTH of those break by whichever
    // candidate list's own order (see AutoBuyPriority/AutoBuyAlwaysPriority's own doc comments) — the strict
    // `<`/`>` comparisons below mean whichever candidate is iterated first never gets replaced by an exact
    // tie, so iterating a list in order is what makes "first" mean "earlier in that list" rather than
    // "whatever Enum.GetValues happened to return first." Re-evaluates from scratch after every single
    // purchase, not just once up front — buying a stat's current tier advances it to its NEXT tier, whose
    // own cost in that stat's costs[] array is typically different, so which stat is cheapest (and even
    // which stats remain affordable at all) can genuinely change mid-run. Checks AutoBuyAlwaysPriority
    // first every iteration, falling through to the full AutoBuyPriority list only once none of those three
    // are currently purchasable — see AutoBuyAlwaysPriority's own doc comment for why. Returns how many
    // purchases were made, purely informational for a caller that wants to report/log it.
    public static int AutoBuy()
    {
        int purchased = 0;
        while (true)
        {
            StatType? best = FindCheapestAffordable(AutoBuyAlwaysPriority) ?? FindCheapestAffordable(AutoBuyPriority);
            if (best == null) break;
            TryUpgradeBaseStat(best.Value);
            purchased++;
        }
        return purchased;
    }

    // Shared search body for AutoBuy's two passes (see its own doc comment) — same cheapest-cost-then-
    // lowest-tier-then-list-order selection either way, just scoped to whichever candidate list is passed in.
    private static StatType? FindCheapestAffordable(StatType[] candidates)
    {
        StatType? best     = null;
        long      bestCost = long.MaxValue;
        int       bestTier = int.MaxValue;

        foreach (StatType stat in candidates)
        {
            if (!IsStatUnlocked(stat) || !CanUpgradeBaseStat(stat)) continue;

            long cost = GetBaseStatUpgradeCost(stat);
            int  tier = GetBaseStatTier(stat);
            if (cost > bestCost) continue;
            if (cost < bestCost || tier < bestTier)
            {
                best     = stat;
                bestCost = cost;
                bestTier = tier;
            }
        }
        return best;
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
        StatType.Luck                => "Luck",
        StatType.StrengthPerLevel    => "Strength Per Level",
        StatType.MaxShield           => "Max Shield",
        StatType.ShieldRechargeTime  => "Shield Recharge Time",
        StatType.AbilityHaste        => "Ability Haste",
        StatType.DeathPrevention     => "Death Prevention",
        // Player-facing name is "Starting Reroll Tokens" (2026-10-01, was "Reroll Tokens" — renamed to
        // disambiguate from the newer "Level Up Reroll Tokens" item) — see CharacterStatsPD.RerollTokens's
        // own doc comment.
        StatType.RerollTokens        => "Starting Reroll Tokens",
        StatType.SurgeMoveSpeedPenaltyReduction => "Surge Speed Penalty",
        StatType.RerollTokenLevelsPerGrant => "Level Up Reroll Tokens",
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
