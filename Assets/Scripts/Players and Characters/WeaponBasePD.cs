using UnityEngine;
using PurrNet.Prediction;

// Weapon model/hitbox base class (2026-09-28 through 2026-09-30 — the "Normalized" rework, since renamed to
// drop that suffix once it fully replaced the old system it was staged alongside; the old WeaponBasePD/
// WeaponMeleePD/WeaponAxePD/WeaponRangedPD/WeaponMeleePDOld classes and their own per-model-piece fields
// (yScaleFollowers/xzScaleFollowers/xScaleFollowers/hitboxTip/hitboxBackTip and all their scalesWith* flags)
// were deleted once every real weapon prefab had migrated off them — see WeaponSharedPD.cs's own class doc
// comment for where the still-needed shared infrastructure from the old WeaponBasePD.cs ended up). Every
// weapon model is organized into 4 authored buckets: Blade, Hilt, Tip (each an empty parent GameObject
// holding the actual mesh pieces as children — see the "KnightWeapon" reference prefab), and a Particle
// System bucket (organizational + doubles as the particle-span target, see particleSystemParent's own doc
// comment). WeaponRangedPD never uses the Blade/Hilt/Tip bucket system at all (see GetNonBladeHalfRange's own
// doc comment) but still inherits this base for the shared orbit/damage-roll/hit-feedback machinery.
// hitboxHilt/hitboxTip don't EXTEND the weapon's hitbox beyond EffectiveWeaponRange — they're already PART of
// that same total (e.g. a 10-length hitbox with hilt=1/tip=1 means blade=8, not a 12-length hitbox) — Blade's
// own length is 100% inferred as whatever's left after subtracting both.
//
// hitboxTarget is untouched — still a fully independent field/exception, its own geometry still entirely
// driven by WeaponManagerPD.SetupWeaponRange exactly as before. rangeScaleTarget (IWeaponPD's own interface
// member) is now privately backed by bladeParent instead of being its own separately-serialized field.
//
// rangeSizeScalingRatio (2026-09-29, replaces xRangeScaleRatio) — a fundamentally different mechanic now, not
// just a rename. The old version stretched Blade's own X thin as range grew (looked bad — a visibly deformed
// blade). This instead RESIZES Hilt/Tip (full XYZ) and Blade (XY only, its own Z is already the growing-length
// axis) uniformly by a single scaleMultiplier — see ComputeScaleMultiplier()'s own doc comment — while
// hitboxHilt/hitboxTip themselves scale by that SAME multiplier before being used anywhere in the position
// math, so the real hitbox's own total length never changes (no "arbitrary bonus range" from a bigger-looking
// weapon) — Blade's own growing length silently absorbs/gives back whatever Hilt/Tip's resize consumes.
public abstract class WeaponBasePD<TState> : PredictedIdentity<TState>, IWeaponPD
    where TState : struct, IPredictedData<TState>
{
    protected CharacterStatsPD _stats;
    public virtual bool IsMelee => true;

    public Transform hitboxTarget;

    // Explicit interface forwarding — same reasoning WeaponBasePD's own identical forwarding comment gives:
    // rangeScaleTarget needs to stay a real Transform-returning property for IWeaponPD consumers
    // (WeaponManagerPD.SetupWeaponRange, GetOuterReach's siblings, etc.), but is now privately derived from
    // bladeParent rather than its own directly-serialized public field (see class doc comment).
    Transform IWeaponPD.hitboxTarget => hitboxTarget;
    Transform IWeaponPD.rangeScaleTarget => bladeParent;
    private Transform rangeScaleTarget => bladeParent;

    [Header("Model Buckets")]
    [SerializeField] private Transform bladeParent;
    [SerializeField] private Transform hiltParent;
    [SerializeField] private Transform tipParent;

    // Flat, in local Z units at Size=1 — how much of the weapon's own already-fixed total hitbox length
    // (EffectiveWeaponRange) each end consumes; NOT extra reach added on top (see class doc comment). Scaled
    // live by ComputeScaleMultiplier() before use everywhere in this class now (2026-09-29) — a bigger Hilt/
    // Tip (via rangeSizeScalingRatio) eats proportionally more of the fixed total, Blade gives back the
    // difference, so the real hitbox length never moves.
    [SerializeField] protected float hitboxHilt;
    // Also the NominalOrbitRadius()/ScaleModel() shared baseline term keeping Pivot/Model/hitboxTarget
    // correctly aligned — see NominalOrbitRadius()'s own doc comment for the full derivation history.
    [SerializeField] protected float hitboxTip;

    // Particle System bucket — organizational (holds the actual ParticleSystem children, which still get
    // individually wired into weaponParticles[] below exactly as before) AND doubles as this weapon's own
    // particle-span target (2026-09-28, replacing the old particleRangeTarget) — spanning from either Blade's
    // own near edge or all the way back to the hilt's own near edge (see particlesExtendToHilt below) out to
    // Blade's far edge, same edge Tip's own centerpoint tracks. Z is exclusively span-driven (never touched by
    // rangeSizeScalingRatio, same reasoning as Blade's own Z) — only X/Y get the resize treatment.
    [SerializeField] private Transform particleSystemParent;
    // false (default): particle span starts at Blade's own near edge (hitboxHilt) — matches every weapon
    // except Bladeweaver. true: starts at the very back of the weapon (local Z 0, the hilt's own near edge)
    // instead — explicit per-weapon opt-in for weapons whose particle trail should visibly extend all the way
    // back to/past the hilt.
    [SerializeField] private bool particlesExtendToHilt;
    // false (default): particle span ends at Blade's own far edge (Tip's own centerpoint). true: extends all
    // the way to Tip's own OUTER edge instead — the true total-hitbox boundary every weapon's own hitboxTarget
    // shares (see ScaleModel's own particleFarEdge comment for the derivation). Independent opt-in from
    // particlesExtendToHilt above — most current weapons want both, but a future one might only want one end.
    [SerializeField] private bool particlesExtendToTip;

    // Authored per-weapon reference point for ComputeScaleMultiplier() — see its own doc comment. In the SAME
    // real-world units as EffectiveWeaponRange (NOT stat points — divide the stat panel's own display by 5
    // first, per the project's documented WeaponRange 0.2 scalar). A pure art/weapon-design choice, completely
    // independent of any character's own base WeaponRange stat (2026-09-29 — deliberately NOT derived from
    // _stats, so the same weapon prefab behaves identically regardless which class ends up wielding it).
    [SerializeField] private float rangeScalingBaseline = 2f;
    // How strongly Hilt/Tip/Blade's-XY actually resize as range grows past rangeScalingBaseline — 0 means "no
    // resizing at all" (scaleMultiplier pinned to 1), same "> 0f opts in" convention xRangeScaleRatio always
    // used. Expected to be a small fraction (well under 1) for most weapons — see ComputeScaleMultiplier's own
    // doc comment for why a naive 1.0 here would mean "+100% range = +100% size," usually far too aggressive.
    [SerializeField] public float rangeSizeScalingRatio = 0f;

    // Every visible mesh piece on this weapon — see IWeaponPD.SetRenderersEnabled's own doc comment for why
    // this toggles Renderer.enabled rather than SetActive. Optional: leave empty and this weapon simply
    // never hides, no error.
    [SerializeField] private Renderer[] _renderers;

    // Particle systems that should hide/show in lockstep with _renderers (e.g. on death) and freeze/resume
    // with PausePD rather than running on Unity's own real-time playback while the game is paused. See
    // WeaponBasePD's own identical field for the full reasoning — unchanged here.
    [SerializeField] private ParticleSystem[] weaponParticles;

    [SerializeField] private float minEmissionRate  = 0f;
    [SerializeField] private float maxEmissionRate  = 0f;
    [SerializeField] private float minEmissionRange = 0f;
    [SerializeField] private float maxEmissionRange = 0f;

    // Each bucket's own authored XYZ scale, captured once — scaleMultiplier (see ComputeScaleMultiplier) is a
    // MULTIPLIER applied on top of whatever each bucket was individually authored at, same "base value, scaled
    // live" convention WeaponBasePD's own _baseRangeScaleX used, just full Vector3 now instead of X-only
    // (2026-09-29 — see class doc comment for why: Hilt/Tip resize on every axis, Blade/Particle System only
    // X/Y, their own Z being independently driven by the growing-length/span math instead).
    private Vector3 _baseBladeScale    = Vector3.one;
    private Vector3 _baseHiltScale     = Vector3.one;
    private Vector3 _baseTipScale      = Vector3.one;
    private Vector3 _baseParticleScale = Vector3.one;
    private bool    _modelVisualsInitialized;

    [System.NonSerialized] public float designOrbit; // unused here — kept only so any stray external read compiles; NominalOrbitRadius uses hitboxTip directly.
    public float   RangeValue { get; set; }
    public Vector3 RootScale  { get; set; } = Vector3.one;
    public int     SlotIndex  { get; set; }
    public bool    IsOuterTrack { get; set; }

    public float EffectiveWeaponRange => _stats.EffectiveWeaponRange;
    public float EffectiveRangeMultiplier => _stats.weaponRangeScalingBonusPercent * _stats.weaponRangeScalingPenalty;

    [System.NonSerialized] public WeaponManagerPD _manager;

    public virtual void Init(CharacterStatsPD stats) => _stats = stats;

    // Shared by ScaleModel() and NominalOrbitRadius() (2026-09-29) — a single source of truth so the two
    // methods, which run at completely different cadences (ScaleModel only on an actual range/size change;
    // NominalOrbitRadius every single tick via LateSimulate), can never drift out of sync the way hitboxHilt/
    // hitboxTip themselves already nearly did once this session. Reads _stats.EffectiveWeaponRange directly
    // rather than requiring a range parameter, since that's exactly what ScaleModel's own range parameter
    // always already is (WeaponManagerPD.SetupWeaponRange's own call site), and NominalOrbitRadius has no
    // parameter to receive it through at all.
    //
    // rangeScalingBaseline is deliberately NOT "the character's starting WeaponRange" or any other stat-
    // derived value — see that field's own doc comment for why a pure per-weapon-authored constant was chosen
    // instead (mathematically the RATIO comes out identical either way, since EffectiveWeaponRange's own
    // conversion factor from raw stat points is fixed per-character, but the authored version keeps this a
    // pure art/weapon-design decision, independent of whichever class ends up using this weapon).
    //
    // Protected, not private — WeaponMeleePD's own real damage hitbox (meleeHalfExtents.x) also
    // widens in step with the visual model now, via this exact same multiplier, so a bigger-looking blade
    // genuinely hits wider too instead of just looking wider.
    protected float ComputeScaleMultiplier()
    {
        if (rangeSizeScalingRatio <= 0f || _stats == null) return 1f;
        return Mathf.Lerp(1f, _stats.EffectiveWeaponRange / Mathf.Max(0.001f, rangeScalingBaseline), rangeSizeScalingRatio);
    }

    // Extension point (2026-09-29) — an additional multiplier applied on top of ComputeScaleMultiplier(),
    // scoped to Tip specifically (not Hilt/Blade). 1f (no-op) here always — neither WeaponMeleePD nor
    // WeaponAxePD currently override this (Axe stopped using the Tip bucket at all once its own axehead
    // became a percentage of Blade's own length instead — see WeaponAxePD's own class doc comment), so it's
    // presently dead weight kept only as a hook for a future weapon that DOES want Tip to scale differently
    // from the rest of the model. Feeds into effectiveTip's own calculation in ScaleModel() below, so a
    // bigger-looking Tip would proportionally eat more of the fixed total hitbox length the same way
    // ComputeScaleMultiplier() itself already does — every downstream formula that reads effectiveTip
    // (bladeFarEdge, NominalOrbitRadius, Model's compensation, hitboxTarget's correction, Tip's own position)
    // would pick it up automatically, nothing else would need touching.
    protected virtual float GetTipExtraMultiplier() => 1f;

    // Extension point (2026-09-30) — the halfRange fallback NominalOrbitRadius() uses when this weapon has no
    // bladeParent at all (i.e. doesn't use the Blade/Hilt/Tip system — currently only WeaponRangedPD).
    // Default matches the old inline fallback exactly (dead code for Melee/Axe, whose bladeParent is always
    // non-null), so this is behavior-preserving for every existing Normalized weapon. WeaponRangedPD
    // overrides it to return an authored per-weapon muzzle offset instead of a hardcoded 0.25f.
    protected virtual float GetNonBladeHalfRange() => IsMelee ? RangeValue / 2f : 0.25f;

    public void SetRenderersEnabled(bool value)
    {
        if (_renderers != null)
            for (int i = 0; i < _renderers.Length; i++)
                if (_renderers[i] != null) _renderers[i].enabled = value;

        if (weaponParticles != null)
            for (int i = 0; i < weaponParticles.Length; i++)
            {
                if (weaponParticles[i] == null) continue;
                if (value) weaponParticles[i].Play();
                else weaponParticles[i].Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            }
    }

    protected void UpdateWeaponParticlePause()
    {
        if (weaponParticles == null) return;
        bool paused = PausePD.instance != null && PausePD.instance.IsPaused;
        for (int i = 0; i < weaponParticles.Length; i++)
        {
            var ps = weaponParticles[i];
            if (ps == null) continue;
            if (paused && ps.isPlaying) ps.Pause();
            else if (!paused && ps.isPaused) ps.Play();
        }
    }

    // No more reading from bladeParent's own initial transform state at all — hitboxHilt/hitboxTip are pure
    // authored constants now, nothing to snapshot from a prefab-authored rangeScaleTarget position/scale the
    // way the old system needed to. Still guarded to run once per real instance (not per pooled reuse) for
    // the SAME reason WeaponBasePD's own version is — capturing each bucket's OWN authored XYZ scale exactly
    // once, before ScaleModel() ever overwrites it.
    public void InitModelVisuals()
    {
        if (_modelVisualsInitialized) return;
        _modelVisualsInitialized = true;

        if (bladeParent          != null) _baseBladeScale    = bladeParent.localScale;
        if (hiltParent           != null) _baseHiltScale     = hiltParent.localScale;
        if (tipParent            != null) _baseTipScale      = tipParent.localScale;
        if (particleSystemParent != null) _baseParticleScale = particleSystemParent.localScale;
    }

    public void ScaleModel(float range)
    {
        bool emissionScales = weaponParticles != null && maxEmissionRange > minEmissionRange;
        if (!emissionScales && bladeParent == null && hiltParent == null && tipParent == null && particleSystemParent == null) return;

        if (emissionScales)
        {
            float rate = Mathf.Lerp(minEmissionRate, maxEmissionRate, Mathf.InverseLerp(minEmissionRange, maxEmissionRange, range));
            for (int i = 0; i < weaponParticles.Length; i++)
            {
                if (weaponParticles[i] == null) continue;
                var emission = weaponParticles[i].emission;
                emission.rateOverTime = rate;
            }
        }

        // Player Size decoupling (2026-09-29, made permanent 2026-09-30) — Size no longer scales any weapon's
        // model/hitbox geometry at all. WeaponManagerPD.RegisterWeapon()/LateUpdate() set transform.localScale
        // = weaponPrefabScale directly (no Size multiply — see that file's own RegisterWeapon comment), so
        // rootZ below is correct by construction, nothing to undo/reset here anymore. Size now ONLY affects
        // the clearance offset terms that already read _stats.bonusSizePercent directly (NominalOrbitRadius,
        // ApplyHeightLift, hitboxTarget's own corrected formula in WeaponManagerPD.SetupWeaponRange) — nothing
        // scales bigger/smaller just because the player picked up a Size augment. (This started as a
        // Normalized-system-only bandaid that reset transform.localScale every call to undo a *_currentSize
        // multiply WeaponManagerPD used to apply — removed at the source once every weapon type had migrated
        // off the old system; see git history around project_weapon_size_decoupling_bandaid for that period.)
        float rootZ = transform.localScale.z;

        // scaleMultiplier drives how big Hilt/Tip/Blade's-XY actually render (see ComputeScaleMultiplier's own
        // doc comment) — hitboxHilt/hitboxTip get scaled by this SAME factor before anything below uses them,
        // so a bigger-looking Hilt/Tip proportionally eats more of the fixed total hitbox length, and Blade's
        // own growing length absorbs the difference. The real hitbox (hitboxTarget, untouched) never moves.
        // tipExtraMultiplier (see GetTipExtraMultiplier's own doc comment) is Tip-only, on top of the shared
        // scaleMultiplier — 1f/no-op unless a subclass (WeaponAxePD) overrides it.
        float scaleMultiplier    = ComputeScaleMultiplier();
        float tipExtraMultiplier = GetTipExtraMultiplier();
        float effectiveHilt      = hitboxHilt * scaleMultiplier;
        float effectiveTip       = hitboxTip  * scaleMultiplier * tipExtraMultiplier;

        // hitboxHilt/hitboxTip (now effectiveHilt/effectiveTip) are TRUE WORLD-UNIT constants (same units as
        // EffectiveWeaponRange itself — meant to be universally comparable across every weapon regardless of
        // that weapon's own root scale), so they need the SAME /rootZ conversion range itself already gets
        // before being usable as LOCAL Transform values (2026-09-29 fix — previously only `range` was
        // divided). Blade's far edge = total live reach minus however much Tip eats into it (NOT reach beyond
        // it — see hitboxTip's own field comment). Blade's near edge is just effectiveHilt itself — hiltParent
        // no longer sits at a purely author-fixed position (2026-09-29 — see its own comment below for why
        // that changed once Hilt could resize).
        float bladeFarEdge  = (range - effectiveTip) / rootZ;
        float bladeNearEdge = effectiveHilt / rootZ;
        float bladeLength   = bladeFarEdge - bladeNearEdge;

        // Compensates for NominalOrbitRadius's own hitboxTip contribution to Pivot's offset (2026-09-29 —
        // see that method's own doc comment for why hitboxTip has to stay there for hitboxTarget's sake).
        // Model is purely cosmetic — Hitbox is Pivot's own sibling, never nested under Model — so this can't
        // touch the real hitbox at all. -effectiveTip/rootZ (uses the SCALED value now, matching Pivot's own
        // now-scaled contribution — see NominalOrbitRadius) exactly cancels Pivot's own hitboxTip term,
        // landing Hilt's own back edge — Model-relative Z=0 — at true-world weaponRadiusOffset + clearance,
        // the correct clearance boundary. Z only; X/Y preserved (Y in particular is separately owned by
        // ApplyVisualAdjustments's own height-lift logic, which reads back whatever Z is left here).
        if (visualHeightTarget != null)
        {
            var p = visualHeightTarget.localPosition;
            visualHeightTarget.localPosition = new Vector3(p.x, p.y, -effectiveTip / rootZ);
        }

        // hitboxTarget: full direct override now (2026-09-29 — was an additive correction on top of
        // WeaponManagerPD.SetupWeaponRange's own already-set value; that value is now stale/wrong regardless,
        // since it was computed using the size-INCLUDED rootZ a moment before this method reset
        // transform.localScale above, so reading it back and nudging it no longer makes sense — recomputed
        // from scratch instead). Same target as before: hitboxTarget's own near edge lands at exactly
        // weaponRadiusOffset + clearance (matching Model/Hilt above), its far edge at
        // weaponRadiusOffset + clearance + range (matching Tip's own outer edge) — derived directly against
        // Pivot's own formula (NominalOrbitRadius) rather than corrected after the fact. scale.z stays a plain
        // range/rootZ (hitboxTarget's own total length is never affected by any of this — see class doc
        // comment), only its position changes from the shared method's own formula.
        if (hitboxTarget != null)
        {
            var hs = hitboxTarget.localScale;
            hitboxTarget.localScale = new Vector3(hs.x, hs.y, range / rootZ);
            var hp = hitboxTarget.localPosition;
            hitboxTarget.localPosition = new Vector3(hp.x, hp.y, range * 0.5f / rootZ - effectiveTip);
        }

        // Blade: XY resize with scaleMultiplier, Z is exclusively the growing-length axis (never touched by
        // scaleMultiplier — that's what actually lets the model "blow up" without the hitbox gaining any
        // extra reach, per the class doc comment).
        if (bladeParent != null)
        {
            bladeParent.localScale = new Vector3(_baseBladeScale.x * scaleMultiplier, _baseBladeScale.y * scaleMultiplier, bladeLength);
            var p = bladeParent.localPosition;
            bladeParent.localPosition = new Vector3(p.x, p.y, (bladeNearEdge + bladeFarEdge) / 2f);
        }

        // Hilt: full XYZ resize now (2026-09-29, was X-only). Its own LOCAL Z POSITION is now computed too
        // (was "never touched, purely author-fixed" — that only worked while Hilt's own size never changed).
        // effectiveHilt/2/rootZ keeps Hilt's own back edge anchored at local Z=0 (the clearance boundary,
        // once Model's own compensation above is applied) regardless of how big Hilt currently renders, with
        // its front edge advancing to match — verified against the user's own worked example (0.35->0.7 hilt
        // budget, 0.175->0.35 position, i.e. exactly half the new budget either way).
        if (hiltParent != null)
        {
            hiltParent.localScale = _baseHiltScale * scaleMultiplier;
            var p = hiltParent.localPosition;
            hiltParent.localPosition = new Vector3(p.x, p.y, effectiveHilt / 2f / rootZ);
        }

        // Tip: full XYZ resize now (2026-09-29, was X-only). Position formula itself is unchanged — it
        // already automatically reflects the resize, since bladeFarEdge itself is derived from effectiveTip.
        // tipExtraMultiplier applied uniformly (full XYZ, not X/Z-only like the old OuterXZMultiplier stat
        // convention it replaces) — deliberately simpler now that it's a plain authored multiplier rather
        // than a stat, per explicit design.
        if (tipParent != null)
        {
            tipParent.localScale = _baseTipScale * scaleMultiplier * tipExtraMultiplier;
            var p = tipParent.localPosition;
            tipParent.localPosition = new Vector3(p.x, p.y, bladeFarEdge);
        }

        // Particle System bucket: X/Y resize with scaleMultiplier (2026-09-29, was untouched) — individual
        // particle systems inside can still opt out of inheriting this via their own native Scaling Mode
        // (Local vs Hierarchy), same mechanism Arrow Rain's own particle system already uses. Z stays
        // exclusively span-driven, same reasoning as Blade's own Z — resizing it here on top of the span
        // would just double-count/fight with that.
        if (particleSystemParent != null)
        {
            float particleNearEdge = particlesExtendToHilt ? 0f : bladeNearEdge;
            // bladeFarEdge + effectiveTip/rootZ reaches Tip's own OUTER edge, not just its centerpoint (which
            // is all bladeFarEdge alone represents) — derived from the same "Tip's own half-reach cancels the
            // effectiveTip subtracted out of bladeFarEdge" identity GetOuterReach's own doc comment already
            // relies on; this lands exactly on the same true-total-hitbox boundary hitboxTarget's own far edge
            // shares, not an approximation.
            float particleFarEdge = particlesExtendToTip ? bladeFarEdge + effectiveTip / rootZ : bladeFarEdge;
            particleSystemParent.localScale = new Vector3(
                _baseParticleScale.x * scaleMultiplier,
                _baseParticleScale.y * scaleMultiplier,
                particleFarEdge - particleNearEdge);
            var p = particleSystemParent.localPosition;
            particleSystemParent.localPosition = new Vector3(p.x, p.y, (particleNearEdge + particleFarEdge) / 2f);
        }
    }

    protected float RollDamage(float damage, out bool isHighroll) =>
        WeaponDamageMath.RollDamage(damage, _stats.LuckChance, _stats.LuckFloor, _stats.LuckCeiling, _stats.Highroll, out isHighroll);

    protected void NotifyHitFeedback(HitFeedbackData data) => _stats.NotifyHitFeedback(data);

    [SerializeField] protected Transform visualHeightTarget;

    private static readonly Quaternion ModelTipRotation = Quaternion.Euler(0f, 0f, -90f);

    protected void ApplyVisualAdjustments()
    {
        if (visualHeightTarget == null || _stats == null) return;
        Vector3 p = visualHeightTarget.localPosition;
        float localLift = _stats.IsOrbitFlipped ? 0f : WeaponVisualConstants.BaseMidHeightLift;
        visualHeightTarget.localPosition = new Vector3(p.x, localLift, p.z);
        visualHeightTarget.localRotation = _stats.IsOrbitFlipped ? ModelTipRotation : Quaternion.identity;
    }

    protected override void LateAwake()
    {
        base.LateAwake();
    }

    private static readonly Quaternion OrbitFlipTip = Quaternion.Euler(0f, 0f, -90f);

    // Protected, not private (2026-09-28, unlike WeaponBasePD's own private version) — WeaponMeleePD
    // calls this directly instead of duplicating the formula, since (unlike the original WeaponBasePD/
    // WeaponMeleePD split) there are no other legacy subclasses of this NEW base class whose behavior needs
    // protecting from a shared-method change.
    //
    // hitboxTip IS needed here (2026-09-29 — briefly removed, then restored) — not because it belongs
    // conceptually in "how far Hilt starts," but because WeaponManagerPD.SetupWeaponRange's own hitboxTarget
    // positioning (shared, untouched, also used by the original WeaponMeleePD/Rogue's weapon) computes
    // hitboxTarget's near edge at -clearance RELATIVE TO PIVOT — i.e. it assumes Pivot already sits far enough
    // from the player that reaching half a clearance-width backward from Pivot still clears the player's own
    // body. The visual model's own over-extension this causes is corrected by pulling visualHeightTarget
    // ("Model") back by this same amount in ScaleModel() — see its own comment — which cannot affect the real
    // hitbox at all, since Hitbox is Pivot's OWN sibling, never nested under Model.
    //
    // Uses the SAME scaled effectiveTip ScaleModel() computes (2026-09-29, via ComputeScaleMultiplier AND
    // GetTipExtraMultiplier — see each's own doc comment) — Pivot's offset has to track Hilt/Tip's live resize
    // too, or Model's compensation/hitboxTarget's correction (both computed against THIS method's own
    // hitboxTip contribution) would drift out of sync the moment either multiplier is nonzero/non-1.
    protected float NominalOrbitRadius()
    {
        float halfRange = bladeParent != null
            ? hitboxTip * ComputeScaleMultiplier() * GetTipExtraMultiplier() * RootScale.z
            : GetNonBladeHalfRange();
        float weaponRadiusOffset = IsOuterTrack ? _stats.WeaponRadius + _stats.EffectiveWeaponRange : _stats.WeaponRadius;
        return weaponRadiusOffset + _stats.bonusSizePercent * 0.5f + halfRange;
    }

    protected Vector3 OrbitalPosition(float baseAngle, Vector3 origin)
    {
        int   total = _manager != null ? Mathf.Max(1, IsOuterTrack ? _manager.OuterWeaponCount : _manager.WeaponCount) : 1;
        float rad   = (baseAngle + 360f / total * SlotIndex) * Mathf.Deg2Rad;
        float radius = NominalOrbitRadius();
        Vector3 flatOffset = new(Mathf.Cos(rad) * radius, 0f, Mathf.Sin(rad) * radius);

        if (_stats.IsOrbitFlipped)
        {
            Quaternion tip = Quaternion.Euler(0f, _stats.OrbitFacingYaw, 0f) * OrbitFlipTip;
            return origin + tip * flatOffset;
        }
        return origin + flatOffset;
    }

    protected Vector3 ApplyHeightLift(Vector3 position)
    {
        float lift = _stats.SpecialHeightOffset;
        if (_stats.IsOrbitFlipped) lift += WeaponVisualConstants.BaseMidHeightLift * _stats.bonusSizePercent;
        return position + Vector3.up * lift;
    }

    // No more separate tip-extra-reach term at all (2026-09-28, replacing GetHitboxTipExtraReach/
    // GetHitboxBackTipExtraReach entirely) — hitboxHilt/hitboxTip don't EXTEND the hitbox beyond
    // EffectiveWeaponRange, they're already part of it (e.g. a 10-length hitbox with hilt=1/tip=1 means
    // blade=8, not a 12-length hitbox). hitboxTarget's own scale/position (driven entirely by
    // WeaponManagerPD.SetupWeaponRange, untouched by this refactor) already represents the FULL
    // EffectiveWeaponRange total on its own, so the base formula below needs nothing added on top — it
    // already sums to WeaponRadius + clearance + EffectiveWeaponRange exactly, by construction (see
    // NominalOrbitRadius's own doc comment for the derivation that makes this hold, now including
    // rangeSizeScalingRatio's own live resize).
    public float GetOuterReach()
    {
        if (!IsMelee)
            return NominalOrbitRadius() + EffectiveWeaponRange;

        Transform hb = hitboxTarget != null ? hitboxTarget : transform;
        float hitboxLocalOffset = hitboxTarget != null ? hitboxTarget.localPosition.z * transform.lossyScale.z : 0f;
        return NominalOrbitRadius() + hitboxLocalOffset + hb.lossyScale.z * 0.5f;
    }
}
