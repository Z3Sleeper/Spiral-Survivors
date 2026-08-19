using UnityEngine;
using UnityEngine.UI;

public class PlayerUIWorldSpace : MonoBehaviour
{
    // The "WorldspaceUI" Canvas — this script sits on a sibling/parent "UI Wrapper" GameObject, not the
    // Canvas itself, so this can't be a GetComponent<Canvas>() shortcut. Its worldCamera is assigned at
    // runtime in Start() (see WorldSpaceUICameraPD's own doc comment for why this can't be a static
    // Inspector reference on the prefab — the camera lives in the scene, not the prefab asset).
    [SerializeField] private Canvas worldspaceCanvas;
    [SerializeField] private Slider healthBar;
    // The health bar Slider's own Fill Image — Slider itself has no color to tint, so this is a separate
    // reference to whichever child Image actually renders the fill, wired up in the Inspector.
    [SerializeField] private Image healthBarFillImage;
    // Extra "hey, you're invincible" heads-up (visible to everyone, not just the owner — allies should see
    // it too) — tints the health bar this color for as long as CharacterStatsPD.UIIsInvincible reads true,
    // reverting to whatever color the fill was authored with (captured once in Start()) the instant
    // invincibility ends. Exposed so it can be tuned/reused for a different tint without touching code.
    [SerializeField] private Color invincibleHealthBarColor = Color.white;
    [SerializeField] private Slider shieldBar;
    [SerializeField] private GameObject dashUI;
    [SerializeField] private Image dashChargeCooldownImage;
    [SerializeField] private GameObject[] dashNotches;
    [SerializeField] private GameObject energyUI;
    [SerializeField] private Image energyImage;

    [Header("Ability1 UI")]
    [SerializeField] private Image ability1CDFill;
    [SerializeField] private GameObject ability1OnCooldownBG;
    [SerializeField] private Image ability1Icon;
    private bool _ability1CooldownUIShown;
    // Separate edge-detect from _ability1CooldownUIShown above — that one tracks the lockout-inclusive BG
    // visibility, but CDFill (transparent, sitting on top of the icon) should only show for actual
    // cooldown, not a lockout with nothing to deplete — see UpdateAbility1's own comment.
    private bool _ability1CDFillShown;
    // DurationUI (DurationBG + DurationBar) — hidden by default in the prefab, shown only while
    // IAbilityPD.HasActiveWindow AND AbilityIsActive are both true (permanently never for an ability with no
    // active window at all — see IAbilityPD.HasActiveWindow's own doc comment), DurationBar's fillAmount
    // counting DOWN from 1 to 0 over ActiveTimeRemaining/ActiveTimeDuration. Same shape as
    // LocalUIManagerPD's own identical duration-bar treatment.
    [SerializeField] private GameObject ability1DurationUI;
    [SerializeField] private Image ability1DurationBar;
    private bool _ability1DurationUIShown;

    // Same shape as Ability1 UI above — both slots are functionally identical now (cooldown-gated
    // IAbilityPD). No cooldown text here either, matching this world-space UI's existing textless design.
    [Header("Ability2 UI")]
    [SerializeField] private Image ability2CDFill;
    [SerializeField] private GameObject ability2OnCooldownBG;
    [SerializeField] private Image ability2Icon;
    private bool _ability2CooldownUIShown;
    private bool _ability2CDFillShown;
    [SerializeField] private GameObject ability2DurationUI;
    [SerializeField] private Image ability2DurationBar;
    private bool _ability2DurationUIShown;

    private CharacterStatsPD _stats;
    private PlayerControllerPD _controller;
    private IAbilityPD _ability1;
    private IAbilityPD _ability2;
    private Camera _camera;
    // Captured once, before anything can tint it — same "cache the true authored value before anything can
    // touch it" reasoning EnemyBasePD's own _originalColors uses.
    private Color _healthBarDefaultColor;
    private bool  _healthBarInvincibleTintShown;

    private void Start()
    {
        _stats = GetComponentInParent<CharacterStatsPD>();
        _controller = GetComponentInParent<PlayerControllerPD>();
        // A character can carry one of each (Ability1 slot + Ability2 slot) on the same GameObject — both
        // implement IAbilityPD, so a plain GetComponentInParent<IAbilityPD>() would resolve ambiguously
        // between them. GetComponentsInParent + Slot routes each instance to the right slot instead.
        foreach (var a in GetComponentsInParent<IAbilityPD>())
        {
            switch (a.Slot)
            {
                case AbilitySlot.Ability1: _ability1 = a; break;
                case AbilitySlot.Ability2: _ability2 = a; break;
            }
        }
        _camera = Camera.main;

        if (healthBarFillImage != null) _healthBarDefaultColor = healthBarFillImage.color;

        // See worldspaceCanvas/WorldSpaceUICameraPD's own doc comments for why this has to be assigned here
        // rather than in the Inspector.
        if (worldspaceCanvas != null) worldspaceCanvas.worldCamera = WorldSpaceUICameraPD.instance;

        if (dashUI != null) dashUI.SetActive(true);
        if (energyUI != null) energyUI.SetActive(true);
        // Icon is fixed for the whole match (this character's class doesn't change), so it's set once here
        // rather than every frame like the fill/text in UpdateAbility1()/UpdateAbility2().
        if (ability1Icon != null && _ability1 != null) ability1Icon.sprite = _ability1.Icon;
        if (ability2Icon != null && _ability2 != null) ability2Icon.sprite = _ability2.Icon;
    }

    private void Update()
    {
        if (_stats == null) return;

        if (healthBar != null)
        {
            float value = _stats.MaxHealth > 0f ? _stats.UIHealth / _stats.MaxHealth : 0f;
            if (!Mathf.Approximately(healthBar.value, value)) healthBar.value = value;
        }
        // Extra "you're invincible" heads-up — see invincibleHealthBarColor's own doc comment.
        if (healthBarFillImage != null && _stats.UIIsInvincible != _healthBarInvincibleTintShown)
        {
            _healthBarInvincibleTintShown = _stats.UIIsInvincible;
            healthBarFillImage.color = _healthBarInvincibleTintShown ? invincibleHealthBarColor : _healthBarDefaultColor;
        }
        if (shieldBar != null)
        {
            float value = _stats.MaxShield > 0f ? _stats.UIShield / _stats.MaxShield : 0f;
            if (!Mathf.Approximately(shieldBar.value, value)) shieldBar.value = value;
        }

        UpdateAbility1();
        UpdateAbility2();

        if (_controller == null) return;

        UpdateDash();
        UpdateEnergy();
    }

    // Visible to everyone (no isOwner gate), same as the health/shield bars above — teammates should be
    // able to see when someone's ability is up, not just the owner.
    private void UpdateAbility1()
    {
        if (_ability1 == null) return;

        // CDFill now sits transparent on top of the ability icon and DEPLETES over the cooldown — full (1)
        // the instant the cooldown starts, draining to empty (0) as it becomes ready, the opposite of the
        // old "fills up as it becomes ready" direction. 0f fallback (not 1f) for a degenerate zero-duration
        // cooldown, since "no cooldown to deplete" should read as already-empty/ready.
        // Tied to CooldownRemaining directly, NOT "!IsReady" — an ability can be unready for a reason that
        // isn't a running cooldown at all (e.g. BWBlinkAbilityPD, only castable while Blade Dance is active
        // — IsReady reads false the rest of the time with CooldownRemaining already sitting at 0). Using
        // !IsReady here made this fill show a permanent, misleading "on cooldown" state in exactly that case.
        // Also excludes AbilityIsActive — a buff ability sets cooldownTimer to its full value the INSTANT
        // it's cast, then (with the default cooldownStartsOnCast = false) holds it there unticked for the
        // whole active window, so CooldownRemaining reads > 0 throughout the buff too — this keeps the fill
        // hidden until the buff actually ends and the real cooldown starts counting down.
        bool onActualCooldown = _ability1.CooldownRemaining > 0f && !_ability1.AbilityIsActive;
        if (ability1CDFill != null)
        {
            float fill = _ability1.CooldownDuration > 0f
                ? _ability1.CooldownRemaining / _ability1.CooldownDuration
                : 0f;
            if (!Mathf.Approximately(ability1CDFill.fillAmount, fill)) ability1CDFill.fillAmount = fill;

            // Hidden entirely once ready (not just drained to 0) so the icon reads clean/unobstructed, and
            // tied to actual cooldown only, NOT the lockout/not-ready-inclusive bool below — neither has
            // anything to deplete, so showing this overlay for either would just be stale/wrong.
            if (onActualCooldown != _ability1CDFillShown)
            {
                _ability1CDFillShown = onActualCooldown;
                ability1CDFill.gameObject.SetActive(onActualCooldown);
            }
        }

        // Shown more broadly than the fill above — any not-ready reason (real cooldown OR e.g.
        // BWBlinkAbilityPD's Blade-Dance gate), a hard lockout (a locking ability elsewhere is active, or
        // isSpecialMoving/stunned — see PlayerControllerPD.IsLockedOut's own doc comment), or blocked by
        // actively dashing OR a Berserk-Surge-style forced Surge (only for an action ability — see
        // IAbilityPD.IsActionAbility's own doc comment, same condition PlayerControllerPD.Simulate() itself
        // gates Activate() on) — even though the ability itself may otherwise be fully ready. _controller can
        // still be null here (this runs before Update()'s own _controller == null check further down).
        bool onCooldown = !_ability1.IsReady || (_controller != null && _controller.IsLockedOut)
            || (((_controller != null && _controller.IsDashing) || _stats.IsForcedSurging) && _ability1.IsActionAbility);
        if (onCooldown != _ability1CooldownUIShown)
        {
            _ability1CooldownUIShown = onCooldown;
            if (ability1OnCooldownBG != null) ability1OnCooldownBG.SetActive(onCooldown);
        }

        // See ability1DurationUI's own doc comment — HasActiveWindow gates this permanently, not just
        // "isn't active right now".
        bool showDuration = _ability1.HasActiveWindow && _ability1.AbilityIsActive;
        if (showDuration != _ability1DurationUIShown)
        {
            _ability1DurationUIShown = showDuration;
            if (ability1DurationUI != null) ability1DurationUI.SetActive(showDuration);
        }
        if (showDuration && ability1DurationBar != null)
        {
            float fill = _ability1.ActiveTimeDuration > 0f
                ? _ability1.ActiveTimeRemaining / _ability1.ActiveTimeDuration
                : 0f;
            if (!Mathf.Approximately(ability1DurationBar.fillAmount, fill)) ability1DurationBar.fillAmount = fill;
        }
    }

    // Identical shape to UpdateAbility1 above — both slots are functionally the same now.
    private void UpdateAbility2()
    {
        if (_ability2 == null) return;

        // See UpdateAbility1's own identical comments for why this is CooldownRemaining (not !IsReady) AND
        // excludes AbilityIsActive (not just "> 0f").
        bool onActualCooldown = _ability2.CooldownRemaining > 0f && !_ability2.AbilityIsActive;
        if (ability2CDFill != null)
        {
            float fill = _ability2.CooldownDuration > 0f
                ? _ability2.CooldownRemaining / _ability2.CooldownDuration
                : 0f;
            if (!Mathf.Approximately(ability2CDFill.fillAmount, fill)) ability2CDFill.fillAmount = fill;

            if (onActualCooldown != _ability2CDFillShown)
            {
                _ability2CDFillShown = onActualCooldown;
                ability2CDFill.gameObject.SetActive(onActualCooldown);
            }
        }

        // See UpdateAbility1's own identical comment for the full visibility reasoning.
        bool onCooldown = !_ability2.IsReady || (_controller != null && _controller.IsLockedOut)
            || (((_controller != null && _controller.IsDashing) || _stats.IsForcedSurging) && _ability2.IsActionAbility);
        if (onCooldown != _ability2CooldownUIShown)
        {
            _ability2CooldownUIShown = onCooldown;
            if (ability2OnCooldownBG != null) ability2OnCooldownBG.SetActive(onCooldown);
        }

        // See ability1DurationUI's own doc comment — HasActiveWindow gates this permanently, not just
        // "isn't active right now".
        bool showDuration = _ability2.HasActiveWindow && _ability2.AbilityIsActive;
        if (showDuration != _ability2DurationUIShown)
        {
            _ability2DurationUIShown = showDuration;
            if (ability2DurationUI != null) ability2DurationUI.SetActive(showDuration);
        }
        if (showDuration && ability2DurationBar != null)
        {
            float fill = _ability2.ActiveTimeDuration > 0f
                ? _ability2.ActiveTimeRemaining / _ability2.ActiveTimeDuration
                : 0f;
            if (!Mathf.Approximately(ability2DurationBar.fillAmount, fill)) ability2DurationBar.fillAmount = fill;
        }
    }

    private void UpdateDash()
    {
        if (dashChargeCooldownImage != null)
        {
            float fill = _controller.currentState.currentCharges < _stats.dashCharges
                ? 1f - (_controller.currentState.chargeRegenTimer / _stats.DashCooldown)
                : 1f;
            if (!Mathf.Approximately(dashChargeCooldownImage.fillAmount, fill)) dashChargeCooldownImage.fillAmount = fill;
        }
        for (int i = 0; i < dashNotches.Length; i++)
        {
            bool active = i < _controller.currentState.currentCharges;
            if (dashNotches[i].activeSelf != active) dashNotches[i].SetActive(active);
        }
    }

    private void UpdateEnergy()
    {
        if (energyImage == null) return;
        float fill = _stats.MaxEnergy > 0f ? _stats.UIEnergy / _stats.MaxEnergy : 1f;
        if (!Mathf.Approximately(energyImage.fillAmount, fill)) energyImage.fillAmount = fill;

        Color color = _stats.UIIsSurging || _stats.UIEnergy < _stats.SurgeActivationThreshhold
            ? Color.red
            : Color.yellow;
        if (energyImage.color != color) energyImage.color = color;
    }

    private void LateUpdate()
    {
        if (_camera == null)
            _camera = Camera.main;
        if (_camera == null) return;
        transform.rotation = _camera.transform.rotation;
    }
}
