using System.Reflection;
using System.Text.RegularExpressions;
using UnityEngine;

// Resolves {TokenName} placeholders inside an AugmentData.description against the effect prefab's own
// public fields/properties/methods (and, if not found there, the given CharacterStatsPD's public
// fields/properties) via reflection — lets an augment script expose a value for display just by making a
// member public and referencing its exact name in the Inspector text, with no per-token plumbing needed
// anywhere else. The description text itself stays fully Inspector-editable, exactly like today; only the
// NUMBERS come from the script.
//
// Three kinds of member are checked, in order, for each token name found on the effect script:
// 1. A public FIELD — the augment's own raw tunable (e.g. onHitBonusHealthPercent). Substituted as-is.
// 2. A public PROPERTY — for values the script wants to compute but that never depend on which specific
//    character is being shown (rare for augments, since almost everything here scales off Stats).
// 3. A public METHOD taking exactly one CharacterStatsPD parameter — for values that DO need to be
//    computed against a specific character (e.g. "50% of THIS player's current Bonus Health"), which a
//    plain field/property can't express since properties/fields can't take arguments. Always invoked (with
//    `stats`, which may itself be null in a lobby-only display context) — the method itself is expected to
//    handle a null/no-context case gracefully, same convention ShowLiveValues below exists for.
//
// A field/property/method result whose token name ends in "Percent" is automatically shown *100 (matches
// this whole codebase's own "Percent fields store the fraction" convention, e.g. UpgradePanel.
// GetDescription's identical *100 handling) — everything else substitutes as-is, floats rounded to at most
// 2 decimal places. A token that can't be resolved anywhere is left as the literal "{TokenName}" text
// rather than silently vanishing, so a typo'd or renamed field is obvious to spot instead of quietly
// producing blank/missing text.
public static class AugmentDescriptionFormatter
{
    // True only while actually in a live match — GameManagerPD is scene-scoped to the Game scene (no
    // DontDestroyOnLoad), so this is false in the lobby/Character Select/Encyclopedia automatically, same
    // "check if a scene-scoped singleton's instance is null" convention AugmentEncyclopediaUI's own doc
    // comment already relies on for LocalUIManagerPD. Public so an individual augment's own computed
    // "live value" method can gate itself the same way (see class doc comment's own convention) — a
    // computed value should never show while merely browsing the Encyclopedia/Character Select, even if a
    // caller happened to pass a non-null CharacterStatsPD there for some other reason.
    public static bool ShowLiveValues => GameManagerPD.instance != null;

    // Adaptive precision for a Live{X}Ratio method's own computed preview number — a flat "round to nearest
    // int" (what every Live*Ratio method used before this existed) silently shows "0" for any genuinely
    // small-but-nonzero value (e.g. 10% of a base Health Regen under 1), which reads as broken rather than
    // small. C#'s own "G2" format specifier fixes the precision problem but flips to scientific notation
    // ("5.2E+02") past a few digits, which is even worse for a damage/heal number that routinely reaches
    // the hundreds — so this hand-rolls the "enough significant precision, never scientific notation" shape
    // instead: whole numbers once the value is large enough that a decimal wouldn't add useful information,
    // progressively more decimal places as the value shrinks toward (and below) 1, so a small value like
    // 0.3 still reads as "0.3" instead of "0". Every Live*Ratio method across the augment scripts should
    // call this instead of formatting its own float directly.
    public static string FormatLiveNumber(float value)
    {
        float abs = Mathf.Abs(value);
        if (abs >= 10f) return value.ToString("0");
        if (abs >= 1f) return value.ToString("0.#");
        return value.ToString("0.##");
    }

    // Convenience wrapper for FormatLiveNumber when the source value is already a fraction (e.g. an ability
    // buff amount like moveSpeedTotalPercent) that needs to display as a whole percent — same "this
    // codebase's percent fields store the fraction" convention FormatValue's own *100-on-Percent-suffix
    // handling already follows for plain reflected fields, just usable directly from inside a Live*Ratio
    // method's own string interpolation (a method's return value is a pre-built string by the time
    // FormatValue ever sees it, so the automatic *100 there never applies to it).
    public static string FormatLivePercent(float value) => FormatLiveNumber(value * 100f);

    private static readonly Regex TokenPattern = new(@"\{(\w+)\}", RegexOptions.Compiled);

    public static string Format(AugmentData data, CharacterStatsPD stats)
    {
        if (data == null) return string.Empty;
        object effect = data.effectPrefab != null ? data.effectPrefab.GetComponent<IAugmentEffectDescribable>() : null;
        return Format(data.description, effect, stats);
    }

    // Generic entry point — same reflection-token resolution as the AugmentData overload above, but for any
    // description string + "effect" object pair, not just an AugmentData/effectPrefab split. Abilities
    // (IAbilityPD) use this directly: unlike an augment, there's no separate data-asset-plus-spawned-prefab
    // indirection — the ability component itself both owns its own Description string AND is the object
    // carrying whatever public members a token might reference, so it's passed as both description source
    // and effect object by the caller (see AugmentCardUI.AbilitySetup).
    public static string Format(string description, object effect, CharacterStatsPD stats)
    {
        if (string.IsNullOrEmpty(description)) return string.Empty;

        // No {tokens} at all — the overwhelmingly common case (a flat/static description) — skip reflection
        // entirely and return the text completely unmodified, not just functionally a no-op.
        if (!TokenPattern.IsMatch(description)) return description;

        return TokenPattern.Replace(description, match =>
        {
            string name = match.Groups[1].Value;
            if (TryResolveToken(effect, name, stats, out string fromEffect)) return fromEffect;
            if (stats != null && TryResolveToken(stats, name, stats, out string fromStats)) return fromStats;
            return match.Value;
        });
    }

    private const BindingFlags PublicInstance = BindingFlags.Public | BindingFlags.Instance;

    private static bool TryResolveToken(object source, string name, CharacterStatsPD stats, out string formatted)
    {
        formatted = null;
        if (source == null) return false;

        var type = source.GetType();

        var field = type.GetField(name, PublicInstance);
        if (field != null) { formatted = FormatValue(name, field.GetValue(source)); return true; }

        var prop = type.GetProperty(name, PublicInstance);
        if (prop != null) { formatted = FormatValue(name, prop.GetValue(source)); return true; }

        var method = type.GetMethod(name, PublicInstance, null, new[] { typeof(CharacterStatsPD) }, null);
        if (method != null) { formatted = FormatValue(name, method.Invoke(source, new object[] { stats })); return true; }

        return false;
    }

    private static string FormatValue(string tokenName, object value)
    {
        if (value is float f)
        {
            if (tokenName.EndsWith("Percent")) f *= 100f;
            return f.ToString("0.##");
        }
        if (value is double d) return d.ToString("0.##");
        return value?.ToString() ?? string.Empty;
    }
}
