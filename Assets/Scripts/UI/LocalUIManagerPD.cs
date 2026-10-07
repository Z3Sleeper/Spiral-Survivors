using System.Collections;
using System.Collections.Generic;
using PurrNet;
using PurrNet.Prediction;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

public class LocalUIManagerPD : MonoBehaviour
{
    public static LocalUIManagerPD instance;

    [SerializeField] private Slider healthBar;
    // The health bar Slider's own Fill Image — Slider itself has no color to tint, so this is a separate
    // reference to whichever child Image actually renders the fill, wired up in the Inspector.
    [SerializeField] private Image healthBarFillImage;
    // Extra "hey, you're invincible" heads-up — tints the health bar this color for as long as
    // CharacterStatsPD.UIIsInvincible reads true, reverting to whatever color the fill was authored with
    // (captured once in Awake()) the instant invincibility ends. Exposed so it can be tuned/reused for a
    // different tint without touching code.
    [SerializeField] private Color invincibleHealthBarColor = Color.white;
    [SerializeField] private Slider shieldBar;
    // Sits BEHIND shieldBar (2026-09-13, reinstated — was briefly replaced with a tint-swap on shieldBar
    // itself, but that hid recharge progress entirely whenever any shield was still up, e.g. sitting at half
    // shield gave no indication of how close the next full recharge was). Always shows recharge progress
    // (0->1 as UIShieldRechargeTimer counts down) regardless of current shield amount — see
    // UpdateHealthAndShields's own doc comment.
    [SerializeField] private Slider shieldRegenBar;
    [SerializeField] private TextMeshProUGUI healthText;
    [SerializeField] private TextMeshProUGUI healthRegenText;
    [SerializeField] private TextMeshProUGUI damageReductionText;
    [SerializeField] private Image dashChargeCooldownImage;
    [SerializeField] private TextMeshProUGUI dashChargesText;
    [SerializeField] private Image energyImage;
    [SerializeField] private TextMeshProUGUI energyText;
    // Held-to-show only (Tab), inactive by default (2026-08-21 — statsUIExpanded retired entirely; this
    // panel absorbed its stats instead of the two panels swapping while held). See SetStatsVisible.
    [SerializeField] private GameObject statsUI;
    // Code-driven (2026-08-21), same reasoning statsWordsTextExpanded used to have — Multishot/Ricochet are
    // gated behind _isRanged (melee has neither stat), so the label column has to vary per-class same as
    // the numbers column does. Built once in SetPlayer (a class's own _isRanged never changes mid-match),
    // not every frame like statsNumbersText — see BuildStatsWords.
    [SerializeField] private TextMeshProUGUI statsWordsText;
    [SerializeField] private TextMeshProUGUI statsNumbersText;
    // Visibility is a persisted Settings toggle (Settings.HideControls), not a fixed default — applied
    // once here at Awake (this is the live scene reference Settings.ApplyOnLoad's own static-context callback
    // can't reach, same "needs a live component reference" reasoning Settings.ApplySavedMixerVolumes already
    // has for the AudioMixer) and pushed live via SetControlsHidden whenever the Settings toggle changes
    // mid-session.
    [SerializeField] private GameObject controlsUI;
    // The restored Dash/Energy/Ability panel (2026-08-21) — visibility AND the position/size of
    // playerUIBackground/buffBar below are all driven together by the same persisted Settings toggle
    // (Settings.HideExpandedUI), same "live scene reference the static ApplyOnLoad callback can't reach"
    // reasoning controlsUI above already has. See SetExpandedUIHidden for the full apply logic and the exact
    // authored position/size values for both states.
    [SerializeField] private GameObject expandedPlayerUI;
    [SerializeField] private RectTransform playerUIBackground;
    [SerializeField] private RectTransform buffBar;
    [SerializeField] private Slider xpBar;
    [SerializeField] private TextMeshProUGUI xpText;
    [SerializeField] private TextMeshProUGUI timerText;
    [SerializeField] private TextMeshProUGUI bossTimerText;
    [SerializeField] private TextMeshProUGUI upgradePointsText;
    [SerializeField] private TextMeshProUGUI difficultyText;
    // Rogue's invisibility ability's screen tint — this manager is already per-local-client only, so a
    // caller just needs to gate on its own isOwner before calling this (see RogueVanishAbilityPD); no
    // further "is this really me" check needed here.
    [SerializeField] private GameObject invisibleTintOverlay;

    [Header("Ability1 UI")]
    [SerializeField] private Image ability1CDFill;
    [SerializeField] private GameObject ability1OnCooldownBG;
    [SerializeField] private Image ability1Icon;
    [SerializeField] private TextMeshProUGUI ability1CooldownText;
    private bool _ability1CooldownUIShown;
    private bool _ability1CooldownTextShown;
    // DurationUI is the parent panel (DurationBG + DurationBar) — hidden by default in the prefab. Shown
    // only while IAbilityPD.HasActiveWindow AND AbilityIsActive are both true (see UpdateAbility1's own
    // comment for why HasActiveWindow gates this permanently rather than just "not active right now"), and
    // DurationBar's fillAmount counts DOWN from 1 to 0 over ActiveTimeRemaining/ActiveTimeDuration — opposite
    // direction from the CDFill above, which counts UP as a cooldown drains (both still read as "how much is
    // left" at a glance, just for two different timers).
    [SerializeField] private GameObject ability1DurationUI;
    [SerializeField] private Image ability1DurationBar;
    private bool _ability1DurationUIShown;
    // Ability charge system (2026-10-02) — see IAbilityPD.CurrentCharges/MaxCharges's own doc comment.
    // ChargeUI is the parent container (count text + fill) — visible only while MaxCharges > 1; for a
    // 1-charge ability (the default) this stays hidden and unused, same old off-cooldown look as before.
    // ChargeCount is the live integer readout. ChargeFill shows the SAME CooldownRemaining/CooldownDuration
    // fill ratio ability1CDFill already uses, just under a different visibility condition (castable right
    // now AND not fully topped off) — see UpdateAbility1's own comment for the exact conditions.
    [SerializeField] private GameObject ability1ChargeUI;
    [SerializeField] private TextMeshProUGUI ability1ChargeCount;
    [SerializeField] private Image ability1ChargeFill;
    private bool _ability1ChargeUIShown;
    private int  _lastAbility1ChargeCount = -1;
    private bool _ability1ChargeFillShown;
    // Hover tooltip — AbilityTooltipTriggerPD (on this icon's own hover-catcher Image) relays
    // OnPointerEnter/Exit into SetAbilityTooltipVisible below, which owns showing/hiding the panel and
    // formatting the description text; the trigger itself holds no tooltip state.
    [SerializeField] private GameObject ability1TooltipPanel;
    [SerializeField] private TextMeshProUGUI ability1DescriptionText;

    // Same shape as Ability1 UI above — both slots are functionally identical now (cooldown-gated
    // IAbilityPD), so Ability2 gets the same fill/BG/text/duration treatment.
    [Header("Ability2 UI")]
    [SerializeField] private Image ability2CDFill;
    [SerializeField] private GameObject ability2OnCooldownBG;
    [SerializeField] private Image ability2Icon;
    [SerializeField] private TextMeshProUGUI ability2CooldownText;
    private bool _ability2CooldownUIShown;
    private bool _ability2CooldownTextShown;
    [SerializeField] private GameObject ability2DurationUI;
    [SerializeField] private Image ability2DurationBar;
    private bool _ability2DurationUIShown;
    // See ability1ChargeUI's own doc comment — identical shape for slot 2.
    [SerializeField] private GameObject ability2ChargeUI;
    [SerializeField] private TextMeshProUGUI ability2ChargeCount;
    [SerializeField] private Image ability2ChargeFill;
    private bool _ability2ChargeUIShown;
    private int  _lastAbility2ChargeCount = -1;
    private bool _ability2ChargeFillShown;
    // See ability1TooltipPanel/ability1DescriptionText's own doc comment — identical shape for slot 2.
    [SerializeField] private GameObject ability2TooltipPanel;
    [SerializeField] private TextMeshProUGUI ability2DescriptionText;

    // Parent panel hidden entirely while Stats.GainsDeathPreventions is false (the character has no way to
    // ever earn one, e.g. DeathPreventionShieldDRAugment not picked) — CDOverlay/Timer are a nested pair,
    // shown together only while eligible AND no save is currently banked (Stats.HasDeathPrevention false),
    // i.e. the grant timer is actively counting toward the next one. See LocalUIManagerPD.
    // UpdateDeathPreventionUI's own comment for the full show/hide logic.
    [Header("Death Prevention UI")]
    [SerializeField] private GameObject deathPreventionUI;
    [SerializeField] private GameObject deathPreventionCDOverlay;
    [SerializeField] private TextMeshProUGUI deathPreventionTimerText;
    private bool _deathPreventionUIShown;
    private bool _deathPreventionCooldownShown;

    [Header("Boss UI")]
    // Pooled per-boss health bar, one instance per currently-alive boss — supports any number spawned
    // simultaneously (see EnemySpawnerPD's activeBosses list). bossBarTemplate is a prefab asset (not an
    // in-scene object) so Instantiate() can freely clone it; bossBarContainer is the Horizontal Layout
    // Group that arranges however many are currently active.
    [SerializeField] private BossHealthBarUI bossBarTemplate;
    [SerializeField] private Transform bossBarContainer;
    private readonly Dictionary<PredictedObjectID, BossHealthBarUI> _activeBossBars = new();
    private readonly Stack<BossHealthBarUI> _bossBarPool = new();
    private readonly List<PredictedObjectID> _bossIdBuffer = new();
    private readonly List<PredictedObjectID> _staleBossIdBuffer = new();

    // Shared across every system that used to have its own separate overlay/timer (Level Up, Pause,
    // End Screen) — none of these are ever up at the same time as another (Win/Loss > Level Up > Pause,
    // see PausePD/AugmentManagerPD's own priority-ordering comments), so one tint + one timer text covers
    // all of them. Resolved every frame in UpdateTintOverlay()/UpdateMultiplayerTimer() rather than each
    // system independently toggling them — see those methods' own doc comments for why.
    [Header("Shared Overlay/Timer")]
    [SerializeField] private GameObject tintScreenOverlay;
    [SerializeField] private TextMeshProUGUI multiplayerTimerText;

    [Header("Level Up Panel")]
    [SerializeField] private GameObject levelUpScreen;
    [SerializeField] private AugmentCardUI cardPrefab;
    [SerializeField] private Transform cardContainer;
    private List<AugmentCardUI> _cardPool = new();
    // Reroll button (2026-09-30) — shown only while a real-augment round is presenting, the local player
    // hasn't picked yet, and they have at least one CharacterStatsPD.RerollTokens. Optional: leave
    // unassigned on any scene that doesn't have it wired yet, no error.
    [SerializeField] private GameObject rerollButton;
    // The button's own text component — shows the player's current CharacterStatsPD.RerollTokens count,
    // refreshed alongside the button's own visibility. Optional, same as rerollButton itself.
    [SerializeField] private TextMeshProUGUI rerollCountText;
    // Played once per successful click, at the camera/listener position — same "local UI cue, no positional/
    // multiplayer concern" reasoning as AugmentManagerPD's own levelUpSfx.
    [SerializeField] private AudioClip rerollSfx;
    // Very short anti-double-click guard (2026-09-30), not a real gameplay cooldown — RequestReroll()'s own
    // token-count/round checks already prevent anything unsafe; this purely stops a single fast double-click
    // from firing the request (and the SFX) twice. Real time, not scaled, same WaitForSecondsRealtime
    // reasoning pickLockoutDuration's own coroutine uses (the level-up screen has Time.timeScale at 0).
    [SerializeField] private float rerollClickLockoutDuration = 0.1f;
    private float _rerollClickLockoutUntil;
    private bool _rerollButtonShown;
    // -1 sentinel (never a real token count) so the very first refresh always writes, same convention
    // _lastDeathPreventionSecondsForText already uses.
    private int _lastRerollCount = -1;

    [Header("Augment List")]
    [SerializeField] private AugmentListUIPD augmentListUI;

    [Header("Settings")]
    [SerializeField] private GameObject settingsPanel;

    // Shared mid-cycle event description panel (2026-08-25) — every EventBasePD-derived event writes into
    // this SAME text box (EventBasePD's own UpdateView drives it via each event's GetEventDescription()
    // override), rather than each event needing its own bespoke UI. Inactive by default (see Awake below) —
    // ShowEventPanel/HideEventPanel are called generically from EventBasePD's own OnEventStart/Cleanup base
    // implementations, so no per-event script needs to know this panel exists at all.
    [Header("Event")]
    [SerializeField] private GameObject eventPanel;
    [SerializeField] private TextMeshProUGUI eventDescriptionText;
    // Outline border around the whole event panel (2026-09-30) — same white/[yellow or red] flash as
    // eventDescriptionText, driven off the exact same color EventBasePD.GetDescriptionColor computes, so an
    // ongoing event reads as more apparent even when a player isn't looking directly at the description text
    // itself. Optional: leave unassigned on any prefab that doesn't have the border image wired yet, no error.
    [SerializeField] private Image eventBorderImage;
    // Captured once in Awake() — GetDescriptionColor's own flash (Color.Lerp between Color.white/yellow/red,
    // all alpha=1 by default) would otherwise clobber whatever partial alpha the border Image was authored
    // with (confirmed live: an authored alpha of 20/255 was silently overridden back to full opacity). Every
    // color SetEventBorderColor receives has this alpha re-forced onto it before being applied, same
    // "preserve the authored alpha, only drive RGB" reasoning PlayerColorApplicator's own alpha field exists
    // for on a completely different component.
    private float _eventBorderOriginalAlpha = 1f;
    // Change-detection cache for SetEventDescription — same "only rebuild the TMP text when the value
    // actually changed" convention UpdateRunClock/OnPresentingTimerChanged etc. already use elsewhere in
    // this file. Reset to null on HideEventPanel so the NEXT event's first SetEventDescription call always
    // actually applies, even if it happens to match whatever text was showing when the panel last hid.
    private string _lastEventDescription;
    // Change-detection cache for SetEventDescriptionColor — same convention/reasoning as
    // _lastEventDescription above, just a Color instead of a string (hence the separate _has bool, since
    // Color has no natural "not set yet" sentinel the way a string has null).
    private Color _lastEventDescriptionColor;
    private bool  _hasLastEventDescriptionColor;
    // Same change-detection shape as _lastEventDescriptionColor above, for eventBorderImage.
    private Color _lastEventBorderColor;
    private bool  _hasLastEventBorderColor;

    [Header("Pause System")]
    [SerializeField] private GameObject pauseScreen;
    [SerializeField] private GameObject singlePlayerResumePanel;
    [SerializeField] private GameObject multiplayerPausePanel;
    [SerializeField] private TextMeshProUGUI pauseReadyCountText;
    [SerializeField] private TextMeshProUGUI pauseReadyButtonText;
    [SerializeField] private GameObject returnToLobbyButton;

    [Header("End Screen")]
    [SerializeField] private GameObject endScreenPanel;
    [SerializeField] private TextMeshProUGUI endScreenTitleText;
    [SerializeField] private TextMeshProUGUI endScreenReadyCountText;

    private readonly System.Text.StringBuilder _statsSB = new();
    private readonly System.Text.StringBuilder _statsWordsSB = new();

    private CharacterStatsPD _stats;
    private PlayerControllerPD _controller;
    private IAbilityPD _ability1;
    private IAbilityPD _ability2;
    private bool _isRanged;

    // Captured once, before anything can tint it — same "cache the true authored value before anything can
    // touch it" reasoning EnemyBasePD's own _originalColors uses.
    private Color _healthBarDefaultColor;
    private bool  _healthBarInvincibleTintShown;

    private void Awake()
    {
        instance = this;
        if (healthBarFillImage != null) _healthBarDefaultColor = healthBarFillImage.color;
        if (eventBorderImage != null) _eventBorderOriginalAlpha = eventBorderImage.color.a;
        levelUpScreen.SetActive(false);
        if (tintScreenOverlay != null) tintScreenOverlay.SetActive(false);
        statsUI.SetActive(false);
        if (controlsUI != null) controlsUI.SetActive(!Settings.HideControls);
        SetExpandedUIHidden(Settings.HideExpandedUI);
        pauseScreen.SetActive(false);
        if (settingsPanel != null) settingsPanel.SetActive(false);
        if (eventPanel != null) eventPanel.SetActive(false);
        if (endScreenPanel != null) endScreenPanel.SetActive(false);
        if (rerollButton != null) rerollButton.SetActive(false);
        // Ability charge system (2026-10-02) — forced hidden here regardless of whatever the Editor-authored
        // default happens to be, same "the change-detection guard below only fires on an actual change"
        // reasoning rerollButton's own fix above already established — without this, an Editor-default-active
        // ChargeUI would never get its initial SetActive(false) call, since _ability1ChargeUIShown/
        // _ability2ChargeUIShown both default to false in code and "false != false" never trips the guard.
        if (ability1ChargeUI != null) ability1ChargeUI.SetActive(false);
        if (ability1ChargeFill != null) ability1ChargeFill.gameObject.SetActive(false);
        if (ability2ChargeUI != null) ability2ChargeUI.SetActive(false);
        if (ability2ChargeFill != null) ability2ChargeFill.gameObject.SetActive(false);
        PausePD.OnPauseChanged += OnPauseStateChanged;
        PausePD.OnCountdownChanged += OnCountdownChanged;
        PausePD.OnReadyCountChanged += OnReadyCountChanged;
        PausePD.OnFallbackTimerChanged += SetFallbackTimer;
        AugmentManagerPD.OnPresentingTimerChanged += OnPresentingTimerChanged;
        GameManagerPD.OnGameEnded += OnGameEnded;
        GameManagerPD.OnEndReadyCountChanged += OnEndReadyCountChanged;
    }

    private void OnDestroy()
    {
        // Guarded, not unconditional (2026-09-30) — this object is NOT DontDestroyOnLoad, so a fresh instance
        // is created every time the Game scene loads and the old one is destroyed on the way back to the
        // lobby. If a newer instance's own Awake() already ran and reassigned `instance` before this OLDER
        // one's OnDestroy() fires (teardown/setup ordering isn't guaranteed to be strictly sequential), an
        // unconditional `instance = null` here would null out the NEW, still-live instance instead of just
        // cleaning up this dead one. Without this guard at all, `instance` was previously left dangling on a
        // destroyed object — `?.` on a destroyed UnityEngine.Object doesn't behave like a real null check (the
        // C# reference itself isn't null, just Unity's own "fake null"), so a caller like Settings.
        // OnControlsVisibilityChanged could silently misbehave against a dead reference instead of cleanly
        // no-oping.
        if (instance == this) instance = null;

        PausePD.OnPauseChanged -= OnPauseStateChanged;
        PausePD.OnCountdownChanged -= OnCountdownChanged;
        PausePD.OnReadyCountChanged -= OnReadyCountChanged;
        PausePD.OnFallbackTimerChanged -= SetFallbackTimer;
        AugmentManagerPD.OnPresentingTimerChanged -= OnPresentingTimerChanged;
        GameManagerPD.OnGameEnded -= OnGameEnded;
        GameManagerPD.OnEndReadyCountChanged -= OnEndReadyCountChanged;
    }

    private void OnGameEnded(bool hasWon)
    {
        if (endScreenPanel == null) return;
        if (endScreenTitleText != null) endScreenTitleText.text = hasWon ? "Victory!" : "Defeat";
        if (endScreenReadyCountText != null)
        {
            int total = NetworkManager.main != null ? NetworkManager.main.playerCount : 1;
            endScreenReadyCountText.text = $"0/{total} Ready";
        }
        endScreenPanel.SetActive(true);
    }

    private void OnEndReadyCountChanged(int ready, int total)
    {
        if (endScreenReadyCountText != null)
            endScreenReadyCountText.text = $"{ready}/{total} Ready";
    }

    public void OnEndScreenReadyClicked()
    {
        if (_controller == null) return;
        _controller.RequestEndScreenReady();
    }

    // Stores the latest value only — doesn't touch multiplayerTimerText directly. Three separate
    // PredictedIdentity UpdateView() calls (AugmentManagerPD, PausePD twice) can fire in any order within
    // the same tick, so writing straight to the shared text from each handler risked one clobbering
    // another's more-current state. UpdateMultiplayerTimer() resolves all three with an explicit priority
    // every frame instead, which is order-independent.
    private void OnPresentingTimerChanged(int seconds, bool visible)
    {
        _levelUpTimerSeconds = seconds;
        _levelUpTimerVisible = visible;
    }

    private void OnPauseStateChanged(bool isPaused)
    {
        bool isSinglePlayer = NetworkManager.main == null || NetworkManager.main.playerCount <= 1;
        if (isPaused) ShowPauseScreen(isSinglePlayer);
        else HidePauseScreen();
    }

    private void OnCountdownChanged(int seconds)
    {
        _pauseCountdownSeconds = seconds;
        _pauseCountdownVisible = seconds > 0;
    }

    private void OnReadyCountChanged(int ready, int total)
    {
        UpdatePauseReadyCount(ready, total);
    }

    // Renamed from SetStatsExpanded (2026-08-21) — used to swap between statsUI and statsUIExpanded while
    // held; now that statsUIExpanded is retired (its stats absorbed into statsUI directly), this is just a
    // plain show/hide. Called by PlayerControllerPD's own Tab press/release.
    public void SetStatsVisible(bool visible) => statsUI.SetActive(visible);

    // Renamed from SetControlsVisible(bool visible) (2026-09-03, alongside Settings.HideControls' own
    // rename) — matches SetExpandedUIHidden's identical hidden=true polarity below instead of the old
    // inverted visible=true one. Called by Settings.OnControlsVisibilityChanged whenever that toggle
    // changes mid-session, so the panel reacts immediately instead of only on the next scene load — see
    // controlsUI's own doc comment.
    public void SetControlsHidden(bool hidden)
    {
        if (controlsUI != null) controlsUI.SetActive(!hidden);
    }

    // Compact (no ExpandedPlayerUI) vs expanded (Dash/Energy/Ability panel showing) anchored positions/size
    // — author-given values, 2026-08-21. Z is always 0 for both states on both elements, so these only carry
    // X/Y (anchoredPosition) plus playerUIBackground's own height (sizeDelta.y) for the compact/expanded pair.
    // 2026-09-08: first pass bumped height/pos.y (both states) plus both buffBar pos.y to stop the buff
    // icons clipping against the background (190/0 compact, 380/95 expanded before that; buffBar 60/240) —
    // deliberately not shrinking the icons themselves. Second pass same day: buffBar now sits ABOVE
    // playerUIBackground instead (buffBar left untouched from the first pass), so playerUIBackground itself
    // shrunk back down (225/25 -> 150/-15 compact, 400/110 -> 320/70 expanded) to make room above it.
    private static readonly Vector2 CompactPlayerUIBackgroundPos = new(0f, -10f);
    private const float CompactPlayerUIBackgroundHeight = 160f;
    private static readonly Vector2 ExpandedPlayerUIBackgroundPos = new(0f, 70f);
    private const float ExpandedPlayerUIBackgroundHeight = 320f;
    private static readonly Vector2 CompactBuffBarPos = new(0f, 90f);
    private static readonly Vector2 ExpandedBuffBarPos = new(0f, 260f);

    // Called by Settings.OnHideExpandedUIChanged whenever that toggle changes mid-session (same reasoning as
    // SetControlsHidden above), and once from Awake() to apply whatever was already saved. hidden=true
    // means the Dash/Energy/Ability panel is OFF and playerUIBackground/buffBar sit at their compact
    // position/size — see Settings.HideExpandedUI's own doc comment for the naming (checked = hidden).
    public void SetExpandedUIHidden(bool hidden)
    {
        if (expandedPlayerUI != null) expandedPlayerUI.SetActive(!hidden);

        if (playerUIBackground != null)
        {
            playerUIBackground.anchoredPosition = hidden ? CompactPlayerUIBackgroundPos : ExpandedPlayerUIBackgroundPos;
            Vector2 size = playerUIBackground.sizeDelta;
            size.y = hidden ? CompactPlayerUIBackgroundHeight : ExpandedPlayerUIBackgroundHeight;
            playerUIBackground.sizeDelta = size;
        }

        if (buffBar != null)
            buffBar.anchoredPosition = hidden ? CompactBuffBarPos : ExpandedBuffBarPos;
    }

    public void SetAugmentListVisible(bool visible) => augmentListUI.SetVisible(visible);

    public void SetInvisibleTintVisible(bool visible)
    {
        if (invisibleTintOverlay != null)
            invisibleTintOverlay.SetActive(visible);
    }

    // Called by AbilityTooltipTriggerPD's OnPointerEnter/OnPointerExit (see its own doc comment) — the
    // small per-icon hover trigger just relays here rather than owning any tooltip state itself, same
    // "small trigger calls into the central HUD manager" convention SetInvisibleTintVisible above already
    // uses. Description text is only (re)formatted on show, not every frame — a hovered tooltip doesn't
    // need to track live-changing values tick-by-tick the way the rest of this HUD does.
    public void SetAbilityTooltipVisible(bool isAbility1, bool visible)
    {
        GameObject panel = isAbility1 ? ability1TooltipPanel : ability2TooltipPanel;
        if (panel == null) return;
        panel.SetActive(visible);
        if (!visible) return;

        TextMeshProUGUI text = isAbility1 ? ability1DescriptionText : ability2DescriptionText;
        IAbilityPD ability = isAbility1 ? _ability1 : _ability2;
        if (text != null && ability != null)
            text.text = AugmentDescriptionFormatter.Format(ability.Description, ability, _stats);
    }

    public void SetPlayer(CharacterStatsPD stats)
    {
        _stats = stats;
        _controller = stats.GetComponent<PlayerControllerPD>();
        // A character can carry one of each (Ability1 slot + Ability2 slot) on the same GameObject — both
        // implement IAbilityPD, so a plain GetComponent<IAbilityPD>() would resolve ambiguously between
        // them. GetComponents + Slot routes each instance to the right slot instead.
        foreach (var a in stats.GetComponents<IAbilityPD>())
        {
            switch (a.Slot)
            {
                case AbilitySlot.Ability1: _ability1 = a; break;
                case AbilitySlot.Ability2: _ability2 = a; break;
            }
        }
        // Icon is fixed for the whole match (this player's class doesn't change), so it's set once here
        // rather than re-applied every frame like the cooldown fill/text below.
        if (ability1Icon != null) ability1Icon.sprite = _ability1?.Icon;
        if (ability2Icon != null) ability2Icon.sprite = _ability2?.Icon;
        _isRanged = stats.GetComponent<WeaponManagerPD>().IsRanged;
        BuildStatsWords();
        healthBar.value = 1f;
        shieldBar.value = 1f;
        shieldRegenBar.value = 1f;
        dashChargeCooldownImage.fillAmount = 1f;
        energyImage.fillAmount = 1f;
    }

    public bool IsLevelingUp { get; private set; }

    public void ShowLevelUpScreen()
    {
        IsLevelingUp = true;
        levelUpScreen.SetActive(true);
    }

    public void HideLevelUpScreen()
    {
        IsLevelingUp = false;
        levelUpScreen.SetActive(false);
    }

    // How long every card stays non-interactable right after the level-up screen appears — two reasons:
    // (1) a player mashing an attack/dash/ability input right as they level up could otherwise land that
    // same click on a pick button the instant it appears, and (2) augment picks matter enough (unlike, say,
    // a cosmetic dialog) that a beat of forced delay before the FIRST click can land is worth it purely so
    // a player who doesn't read has a moment where clicking literally can't do anything yet, rather than
    // reflexively clicking the first option a fraction of a second after the panel opens.
    [SerializeField] private float pickLockoutDuration = 1f;
    private Coroutine _pickLockoutRoutine;

    // Barebones — every level-up is a stat-augment pick for now, no real augment pool yet.
    public void SetUpLevelUpScreen(List<LevelUpOption> options)
    {
        HideAllCards();
        for (int i = 0; i < options.Count; i++)
        {
            var card = GetCard(i);
            if (options[i].isAugment)
                card.AugmentSetup(options[i].augmentData, i, _stats);
            else
                card.StatAugmentSetup(options[i].statPair, i, _stats);
            // AugmentSetup/StatAugmentSetup both set this true internally — overridden back to false here,
            // lifted by LiftPickLockout below once pickLockoutDuration elapses.
            card.SetInteractive(false);
        }

        if (_pickLockoutRoutine != null) StopCoroutine(_pickLockoutRoutine);
        _pickLockoutRoutine = StartCoroutine(LiftPickLockout(options.Count));
    }

    // Real-time, not scaled — the level-up screen is up precisely because PausePD's "presenting" state has
    // Time.timeScale at 0 (see PausePD.IsPaused's own doc comment), so a scaled WaitForSeconds here would
    // simply never elapse and the lockout would never lift.
    private IEnumerator LiftPickLockout(int cardCount)
    {
        yield return new WaitForSecondsRealtime(pickLockoutDuration);
        for (int i = 0; i < cardCount && i < _cardPool.Count; i++)
            _cardPool[i].SetInteractive(true);
        _pickLockoutRoutine = null;
    }

    // Only buffers the request through the local player's own controller — Simulate() performs the
    // actual pick once this is drained into Input, same reasoning as the ready/pause buttons.
    public void RequestPick(int cardIndex, float luckMultiplierA = 1f, float luckMultiplierB = 1f)
    {
        if (_controller == null) return;
        _controller.RequestPick(cardIndex, luckMultiplierA, luckMultiplierB);
    }

    // Same forwarding shape as RequestPick above — the reroll button's own OnClick should call this.
    public void RequestReroll()
    {
        if (_controller == null) return;
        if (Time.unscaledTime < _rerollClickLockoutUntil) return;
        _rerollClickLockoutUntil = Time.unscaledTime + rerollClickLockoutDuration;

        if (rerollSfx != null && SFXManager.instance != null)
        {
            var listenerPos = Camera.main != null ? Camera.main.transform.position : Vector3.zero;
            SFXManager.instance.Play(rerollSfx, listenerPos);
        }

        _controller.RequestReroll();
    }

    private bool _levelUpScreenShown;
    private bool _levelUpBackgroundShown;
    private bool _cardsShown;

    // Latest values reported by each of the three timer sources that now share multiplayerTimerText —
    // resolved with an explicit priority in UpdateMultiplayerTimer() every frame, not written to directly
    // by the event handlers themselves (see OnPresentingTimerChanged's own doc comment).
    private int  _levelUpTimerSeconds;
    private bool _levelUpTimerVisible;
    private int  _pauseCountdownSeconds;
    private bool _pauseCountdownVisible;
    private int  _fallbackTimerSeconds;
    private bool _fallbackTimerVisible;
    private bool _multiplayerTimerShown;

    // Edge-detected purely to avoid a redundant SetActive call every frame.
    private bool _tintOverlayShown;

    private void UpdateLevelUpScreen()
    {
        if (AugmentManagerPD.instance == null || _controller == null || !_controller.owner.HasValue) return;

        bool presenting = AugmentManagerPD.instance.VerifiedIsPresenting;

        // Reflects the whole multiplayer presenting window (every player, not just this one) — otherwise
        // it drops the instant THIS player picks, even while teammates are still choosing and the game is
        // still effectively paused underneath. Only tracked as a bool here now — UpdateTintOverlay() is
        // what actually drives the shared tintScreenOverlay, reading this alongside pause/end-screen state.
        _levelUpBackgroundShown = presenting;

        // The level-up screen itself (overlay + timer + card container) now ALSO spans the whole
        // presenting window, same as the background flag above — it used to close the instant THIS player
        // picked (levelUpScreen.SetActive(false) hid its overlay/timer children along with the cards),
        // which read as "the level-up screen closed early" even though other players were still choosing.
        // Only the cards themselves hide early now (below) — the timer/overlay stay up for everyone until
        // the whole window actually ends.
        if (presenting && !_levelUpScreenShown)
        {
            ShowLevelUpScreen();
            _levelUpScreenShown = true;
        }
        else if (!presenting && _levelUpScreenShown)
        {
            HideLevelUpScreen();
            _levelUpScreenShown = false;
        }

        // Cards only need to be visible until THIS player has picked — no reason to keep showing your
        // own already-answered options while waiting on everyone else.
        bool shouldShowCards = presenting && !AugmentManagerPD.instance.HasPlayerPicked(_controller.owner.Value);

        if (shouldShowCards && !_cardsShown)
        {
            SetUpLevelUpScreen(AugmentManagerPD.instance.GetOptionsForPlayer(_controller.owner.Value));
            cardContainer.gameObject.SetActive(true);
            _cardsShown = true;
        }
        else if (!shouldShowCards && _cardsShown)
        {
            cardContainer.gameObject.SetActive(false);
            _cardsShown = false;
        }

        // Reroll button — same base condition as the cards above (presenting, hasn't picked yet — a reroll
        // is meaningless once already committed), further gated to a real-augment round and at least one
        // token. Re-evaluated every frame off live RerollTokens, so spending the last one (1 -> 0) hides it
        // on the very next refresh without needing its own separate edge trigger.
        if (rerollButton != null)
        {
            bool shouldShowReroll = shouldShowCards && AugmentManagerPD.instance.VerifiedIsAugmentRound && _stats.RerollTokens > 0;
            if (shouldShowReroll != _rerollButtonShown)
            {
                _rerollButtonShown = shouldShowReroll;
                rerollButton.SetActive(shouldShowReroll);
            }
        }

        // Reroll refresh (2026-09-30 fix) — AugmentManagerPD.RequestReroll() spends a token and regenerates
        // this player's option set in-place (_currentOptions), but SetUpLevelUpScreen only ever ran on the
        // cards' initial rising edge above, so the visible cards never actually repopulated after a reroll.
        // RerollTokens can only decrease mid-run via a reroll (shop spending only happens outside a run), so
        // a drop while cards are still shown is an unambiguous "regenerate the cards" signal.
        if (_cardsShown && _lastRerollCount >= 0 && _stats.RerollTokens < _lastRerollCount)
            SetUpLevelUpScreen(AugmentManagerPD.instance.GetOptionsForPlayer(_controller.owner.Value));

        if (rerollCountText != null && _stats.RerollTokens != _lastRerollCount)
        {
            _lastRerollCount = _stats.RerollTokens;
            rerollCountText.text = _lastRerollCount.ToString();
        }
    }

    public void HideAllCards()
    {
        foreach (var card in _cardPool)
            card.gameObject.SetActive(false);
    }

    public void ShowPauseScreen(bool isSinglePlayer)
    {
        if (pauseReadyButtonText != null) pauseReadyButtonText.text = "Ready Up";
        if (pauseReadyCountText != null) pauseReadyCountText.text = $"0/{NetworkManager.main.playerCount} Ready";
        singlePlayerResumePanel.SetActive(isSinglePlayer);
        multiplayerPausePanel.SetActive(!isSinglePlayer);
        if (returnToLobbyButton != null)
            returnToLobbyButton.SetActive(NetworkManager.main.isServer);
        pauseScreen.SetActive(true);
    }

    public void HidePauseScreen()
    {
        pauseScreen.SetActive(false);
    }

    public void UpdatePauseReadyCount(int ready, int total)
    {
        if (pauseReadyCountText != null)
            pauseReadyCountText.text = $"{ready}/{total} Ready";
    }

    private void UpdatePauseReadyButton()
    {
        SetText(pauseReadyButtonText, _controller.IsReady ? "Unready" : "Ready Up");
    }

    // Stores the latest value only — see OnPresentingTimerChanged's own doc comment for why this doesn't
    // touch multiplayerTimerText directly.
    public void SetFallbackTimer(int seconds, bool visible)
    {
        _fallbackTimerSeconds = seconds;
        _fallbackTimerVisible = visible;
    }

    public void OnPauseReadyClicked()
    {
        if (_controller == null) return;
        // Only buffers the request — Simulate() performs the actual toggle once this is drained into
        // Input, so the button label updates reactively in UpdatePauseReadyButton() instead of here.
        _controller.RequestReadyToggle();
    }

    public void OnSettingsClicked()
    {
        if (settingsPanel != null) settingsPanel.SetActive(true);
    }

    public void OnSettingsBackClicked()
    {
        if (settingsPanel != null) settingsPanel.SetActive(false);
    }

    // Read by PlayerControllerPD.UpdateInput() to make Escape close an open Settings panel instead of
    // reaching the networked pause/ready toggle underneath it — see that call site's own doc comment.
    public bool IsSettingsPanelOpen => settingsPanel != null && settingsPanel.activeSelf;

    // Called once from EventBasePD's own OnEventStart base implementation — see IEventPD.OnEventStart's own
    // doc comment for why this lives in the shared base rather than every concrete event calling it itself.
    public void ShowEventPanel()
    {
        if (eventPanel != null) eventPanel.SetActive(true);
    }

    // Called every UpdateView (render-frame) while an event is active — see EventBasePD.UpdateView's own doc
    // comment. Change-detection guarded (see _lastEventDescription's own doc comment) so a value that hasn't
    // actually changed since last frame doesn't rebuild the TMP text for nothing.
    public void SetEventDescription(string text)
    {
        if (eventDescriptionText == null) return;
        if (text == _lastEventDescription) return;
        _lastEventDescription = text;
        eventDescriptionText.text = text;
    }

    // Called every UpdateView (render-frame) while an event is active — see EventBasePD.UpdateView's own doc
    // comment (drives the "flash for the first few seconds" tell). Change-detection guarded same as
    // SetEventDescription above — the color changes virtually every frame during the actual flash (so this
    // guard rarely bites there), but still avoids redundant writes once it's settled on white.
    public void SetEventDescriptionColor(Color color)
    {
        if (eventDescriptionText == null) return;
        if (_hasLastEventDescriptionColor && color == _lastEventDescriptionColor) return;
        _hasLastEventDescriptionColor = true;
        _lastEventDescriptionColor = color;
        eventDescriptionText.color = color;
    }

    // Same shape/call site as SetEventDescriptionColor above — called with the exact same color
    // (EventBasePD.GetDescriptionColor's own white/[yellow or red] flash), so the border and the text always
    // flash in lockstep, never independently. eventBorderImage is optional (see its own field comment), so
    // this is a harmless no-op on any prefab that hasn't wired it up yet.
    public void SetEventBorderColor(Color color)
    {
        if (eventBorderImage == null) return;
        color.a = _eventBorderOriginalAlpha;
        if (_hasLastEventBorderColor && color == _lastEventBorderColor) return;
        _hasLastEventBorderColor = true;
        _lastEventBorderColor = color;
        eventBorderImage.color = color;
    }

    // Called once from EventBasePD's own Cleanup() base implementation, for EITHER outcome (success or
    // failure) — see IEventPD.Cleanup()'s own doc comment.
    public void HideEventPanel()
    {
        if (eventDescriptionText != null)
        {
            eventDescriptionText.text = string.Empty;
            eventDescriptionText.color = Color.white;
        }
        if (eventBorderImage != null) eventBorderImage.color = new Color(1f, 1f, 1f, _eventBorderOriginalAlpha);
        if (eventPanel != null) eventPanel.SetActive(false);
        _lastEventDescription = null;
        _hasLastEventDescriptionColor = false;
        _hasLastEventBorderColor = false;
    }

    public void OnResumeClicked()
    {
        if (_controller == null) return;
        _controller.RequestPausePressed();
    }

    public void OnReturnToLobbyClicked()
    {
        if (ReturnToLobbyManager.instance != null) ReturnToLobbyManager.instance.ReturnToLobby();
    }

    private AugmentCardUI GetCard(int index)
    {
        if (index < _cardPool.Count)
        {
            _cardPool[index].gameObject.SetActive(true);
            return _cardPool[index];
        }
        var card = Instantiate(cardPrefab, cardContainer);
        _cardPool.Add(card);
        return card;
    }

    private void Update()
    {
        if (_stats == null || _controller == null) return;
        UpdateHealthAndShields();
        UpdateHealthRegen();
        UpdateDamageReduction();
        UpdateDash();
        UpdateAbility1();
        UpdateAbility2();
        UpdateDeathPreventionUI();
        UpdateEnergy();
        UpdateStats();
        UpdateXP();
        UpdateUpgradePoints();
        UpdateDifficultyText();
        UpdateRunClock();
        UpdateBossUI();
        UpdateBossHealthBars();
        UpdatePauseReadyButton();
        UpdateLevelUpScreen();
        UpdateTintOverlay();
        UpdateMultiplayerTimer();
    }

    // Resolved fresh every frame from each system's already-current ground truth (pauseScreen/
    // endScreenPanel's own activeSelf, _levelUpBackgroundShown) rather than each system independently
    // toggling tintScreenOverlay from its own show/hide call site. Three separate PredictedIdentity
    // UpdateView() calls (AugmentManagerPD, PausePD, GameManagerPD) have no guaranteed order within the
    // same tick, so having each one flip a shared bool directly risked one clobbering another's more-
    // current state — reading the resolved state instead sidesteps that entirely.
    private void UpdateTintOverlay()
    {
        if (tintScreenOverlay == null) return;

        bool wantOverlay = (endScreenPanel != null && endScreenPanel.activeSelf)
                         || _levelUpBackgroundShown
                         || (pauseScreen != null && pauseScreen.activeSelf);

        if (wantOverlay == _tintOverlayShown) return;
        _tintOverlayShown = wantOverlay;
        tintScreenOverlay.SetActive(wantOverlay);
    }

    // Same "resolve every frame instead of writing from each handler" reasoning as UpdateTintOverlay()
    // above. Priority matches the established Win/Loss > Level Up > Pause ordering (PausePD.Simulate()/
    // AugmentManagerPD.Simulate() already enforce this at the source, so in practice at most one of these
    // three is ever visible at once — this priority just makes the UI itself robust to any transient
    // overlap too, rather than depending on it never happening).
    private void UpdateMultiplayerTimer()
    {
        if (multiplayerTimerText == null) return;

        bool visible;
        string text = null;
        if (_levelUpTimerVisible)
        {
            visible = true;
            text = $"Time Remaining: {_levelUpTimerSeconds}s";
        }
        else if (_pauseCountdownVisible)
        {
            visible = true;
            text = $"Pausing in {_pauseCountdownSeconds}...";
        }
        else if (_fallbackTimerVisible)
        {
            visible = true;
            text = $"Auto-resume in {_fallbackTimerSeconds}s";
        }
        else
        {
            visible = false;
        }

        if (visible != _multiplayerTimerShown)
        {
            _multiplayerTimerShown = visible;
            multiplayerTimerText.gameObject.SetActive(visible);
        }
        // Was a raw assignment with no redundancy guard at all (unlike every other text field in this
        // class, which goes through SetText) — rebuilt and rewrote every single frame this panel was
        // visible, even though the seconds count underneath only actually changes once per second.
        if (visible) SetText(multiplayerTimerText, text);
    }

    // Only writes when the value actually changed, so tiny prediction/rollback jitter doesn't
    // constantly rewrite the UI (which is what caused the "ticking up and down" flicker).
    private static void SetSlider(Slider slider, float value)
    {
        if (slider != null && !Mathf.Approximately(slider.value, value))
            slider.value = value;
    }

    private static void SetFill(Image image, float value)
    {
        if (image != null && !Mathf.Approximately(image.fillAmount, value))
            image.fillAmount = value;
    }

    private static void SetColor(Image image, Color value)
    {
        if (image != null && image.color != value)
            image.color = value;
    }

    private static void SetText(TextMeshProUGUI text, string value)
    {
        if (text != null && text.text != value)
            text.text = value;
    }

    private static string FormatStat(float value, int decimals = 0)
    {
        if (value >= 1_000_000_000_000f) return $"{value / 1_000_000_000_000f:F2}tril";
        if (value >= 1_000_000_000f)     return $"{value / 1_000_000_000f:F2}bil";
        if (value >= 1_000_000f)         return $"{value / 1_000_000f:F2}mil";
        if (value >= 1_000f)             return $"{value / 1_000f:F2}k";
        return decimals == 0 ? $"{Mathf.RoundToInt(value)}" : value.ToString($"F{decimals}");
    }

    // Last raw stat values actually behind a displayed string, one group per Update*() method below — the
    // SetText/SetSlider/SetFill/SetColor helpers above already skip the actual UI WRITE when the formatted
    // result is unchanged, but they can't stop the string/StringBuilder work that runs to PRODUCE that
    // result in the first place, since that has to happen before the comparison can even occur. With up to
    // several of these running unconditionally every frame regardless of whether the underlying stat moved
    // at all, that's a steady stream of avoidable GC allocation (confirmed via Profiler — LocalUIManagerPD
    // showing up as a GC-allocation contributor across multiple consecutive frames, same investigation that
    // found EnemyUIPD's equivalent per-enemy issue). Caching the SOURCE values and skipping the whole
    // formatting block when nothing changed removes the allocation, not just the write.
    private float _lastUIHealthForText = float.NaN;
    private float _lastMaxHealthForText = float.NaN;
    private float _lastUIShieldForText = float.NaN;
    private float _lastMaxShieldForText = float.NaN;
    private float _lastHealthRegenForText = float.NaN;
    private float _lastDamageReductionForText = float.NaN;
    private int   _lastDashChargesForText = -1;
    private int   _lastEnergyIntForText = -1;
    private int   _lastDeathPreventionSecondsForText = -1;
    private int   _lastXPLevelForText = -1;
    private float _lastXPCurrentForText = float.NaN;
    private float _lastXPToNextForText = float.NaN;
    private float _lastUpgradePointsForText = float.NaN;
    // Set once and never again — Difficulty is fixed for the whole match (GameManagerPD.Awake() reads it
    // once from GamePresets and never touches it again), so there's nothing to change-detect against like
    // every other *ForText field here. Stays false (retries every frame) until GameManagerPD.instance
    // actually exists, in case this manager's own Awake() happens to run first.
    private bool  _difficultyTextSet;
    private int   _lastRunClockMinutes = -1;
    private int   _lastRunClockSeconds = -1;
    private int   _lastBossMinutes = -1;
    private int   _lastBossSeconds = -1;
    // Cooldown countdown text genuinely changes almost every frame while active, so this can't eliminate
    // most rebuilds the way the others above do — but the display only shows 1 decimal (":F1"), so caching
    // the ROUNDED value still catches the frames where two different raw floats format to the identical
    // string (still a real, if smaller, win).
    private float _lastAbility1CooldownDisplay = float.NaN;
    private float _lastAbility2CooldownDisplay = float.NaN;

    // shieldRegenBar sits behind shieldBar and always shows recharge progress (0->1 as UIShieldRechargeTimer
    // counts down to 0) regardless of current shield amount — including while sitting at partial shield,
    // since CharacterStatsPD's own shield-regen gate (Simulate) ticks the recharge timer any time
    // shield < MaxShield, not just at 0. A brief tint-swap-on-shieldBar-itself version of this existed
    // 2026-09-13 but was reverted the same day: it only showed recharge progress once shield was FULLY
    // depleted, giving no feedback at all while sitting at partial shield. MaxShield <= 0 (no shield source
    // picked at all) forces 0 fill — without this, a character who never has any shield capacity would show
    // UIShieldRechargeTimer's own never-touched-since-spawn value as a permanently "fully recharged" bar
    // instead of an empty one.
    private void UpdateHealthAndShields()
    {
        SetSlider(healthBar, _stats.UIHealth / _stats.MaxHealth);
        SetSlider(shieldBar, _stats.MaxShield > 0f ? _stats.UIShield / _stats.MaxShield : 0f);

        // Extra "you're invincible" heads-up — see invincibleHealthBarColor's own doc comment.
        if (healthBarFillImage != null && _stats.UIIsInvincible != _healthBarInvincibleTintShown)
        {
            _healthBarInvincibleTintShown = _stats.UIIsInvincible;
            healthBarFillImage.color = _healthBarInvincibleTintShown ? invincibleHealthBarColor : _healthBarDefaultColor;
        }

        SetSlider(shieldRegenBar, _stats.MaxShield > 0f && _stats.shieldRechargeTime > 0f
            ? (_stats.shieldRechargeTime - _stats.UIShieldRechargeTimer) / _stats.shieldRechargeTime
            : 0f);

        if (_stats.UIHealth != _lastUIHealthForText || _stats.MaxHealth != _lastMaxHealthForText ||
            _stats.UIShield != _lastUIShieldForText || _stats.MaxShield != _lastMaxShieldForText)
        {
            _lastUIHealthForText = _stats.UIHealth;
            _lastMaxHealthForText = _stats.MaxHealth;
            _lastUIShieldForText = _stats.UIShield;
            _lastMaxShieldForText = _stats.MaxShield;
            SetText(healthText, $"<b><color=#FF00FF>{FormatStat(_stats.UIShield)}/{FormatStat(_stats.MaxShield)}</color> {FormatStat(_stats.UIHealth)}/{FormatStat(_stats.MaxHealth)}</b>");
        }
    }

    private void UpdateHealthRegen()
    {
        if (_stats.HealthRegen == _lastHealthRegenForText) return;
        _lastHealthRegenForText = _stats.HealthRegen;
        SetText(healthRegenText, $"<b>+{FormatStat(_stats.HealthRegen, 2)}/s</b>");
    }

    private void UpdateDamageReduction()
    {
        if (_stats.DamageReduction == _lastDamageReductionForText) return;
        _lastDamageReductionForText = _stats.DamageReduction;

        float percent = (1f - _stats.DamageReduction) * 100f;
        string sign = percent >= 0f ? "+" : "";
        SetText(damageReductionText, $"<b>{sign}{percent:F0}%\nDR</b>");
    }

    private void UpdateDash()
    {
        SetFill(dashChargeCooldownImage, _controller.currentState.currentCharges < _stats.dashCharges
            ? 1f - (_controller.currentState.chargeRegenTimer / _stats.DashCooldown)
            : 1f);

        if (_controller.currentState.currentCharges == _lastDashChargesForText) return;
        _lastDashChargesForText = _controller.currentState.currentCharges;
        SetText(dashChargesText, $"<b>{_controller.currentState.currentCharges}</b>");
    }

    // Characters without an ability yet (nothing assigned _ability1) just leave this whole panel however
    // it was left in the editor — every field access below is null-guarded via the early return.
    private void UpdateAbility1()
    {
        if (_ability1 == null) return;

        // CDFill now sits transparent on top of the ability icon and DEPLETES over the cooldown — full
        // (1) the instant the cooldown starts, draining to empty (0) as it becomes ready, the opposite of
        // the old "fills up as it becomes ready" direction. 0f fallback (not 1f) for a degenerate
        // zero-duration cooldown, since "no cooldown to deplete" should read as already-empty/ready.
        SetFill(ability1CDFill, _ability1.CooldownDuration > 0f
            ? _ability1.CooldownRemaining / _ability1.CooldownDuration
            : 0f);

        // Tied to CurrentCharges, NOT "!IsReady" — an ability can be unready for a reason that isn't a zero
        // charge count at all (e.g. BWBlinkAbilityPD, only castable while Blade Dance is active — IsReady
        // reads false the rest of the time even with a charge sitting banked). Using !IsReady here made the
        // countdown text show a permanent, misleading "0.0s" in exactly that case. Also excludes
        // AbilityIsActive — a buff ability (ExeBigAxeAbilityPD, RogueVanishAbilityPD, BWBlinkAbilityPD, etc.)
        // already sets cooldownTimer to its full value the INSTANT it's cast, then (with the default
        // cooldownStartsOnCast = false) holds it there unticked for the whole active window — so
        // CooldownRemaining reads > 0 throughout the buff too, not just once the real cooldown actually
        // starts counting down. Without this exclusion the text/fill would show a misleading stuck-at-full
        // countdown for the buff's own duration instead of staying hidden until the buff ends and the
        // cooldown genuinely begins ticking. Charge system (2026-10-02): CurrentCharges <= 0, not
        // CooldownRemaining > 0 — a multi-charge ability can have CooldownRemaining > 0 (regenerating toward
        // an EXTRA charge) while still fully castable off a banked charge, which must NOT show this
        // "unready" sweep — see ability1ChargeFill below for that in-between case instead.
        bool onCooldown = _ability1.CurrentCharges <= 0 && !_ability1.AbilityIsActive;
        // BG is shown more broadly — real cooldown, any OTHER not-ready reason, a hard lockout (a locking
        // ability elsewhere is active, or isSpecialMoving/stunned — see PlayerControllerPD.IsLockedOut's own
        // doc comment), or blocked by a Berserk-Surge-style forced Surge while this ability's own
        // ActiveSurgeBlock is true (2026-08-31, was IsActionAbility + dashing — dash no longer blocks any
        // ability at all, see PlayerControllerPD.Simulate()'s own ability-activation block for the current
        // rules this mirrors) — even though the ability itself may otherwise be fully ready. The countdown
        // TEXT and the CDFill overlay itself both stay tied to actual cooldown only (onCooldown above) — none
        // of the other reasons have a timer/depletion to show, so both would just read as stale/wrong if they
        // followed the BG here.
        bool showBG = !_ability1.IsReady || (_controller != null && _controller.IsLockedOut)
            || (_stats.IsForcedSurging && _ability1.ActiveSurgeBlock);
        if (showBG != _ability1CooldownUIShown)
        {
            _ability1CooldownUIShown = showBG;
            if (ability1OnCooldownBG != null) ability1OnCooldownBG.SetActive(showBG);
        }
        if (onCooldown != _ability1CooldownTextShown)
        {
            _ability1CooldownTextShown = onCooldown;
            if (ability1CooldownText != null) ability1CooldownText.gameObject.SetActive(onCooldown);
            // Hidden entirely once ready (not just drained to 0) so the icon reads clean/unobstructed —
            // same onCooldown edge as the text above, since both should always change together.
            if (ability1CDFill != null) ability1CDFill.gameObject.SetActive(onCooldown);
        }
        if (onCooldown)
        {
            float rounded = Mathf.Round(_ability1.CooldownRemaining * 10f) / 10f;
            if (rounded != _lastAbility1CooldownDisplay)
            {
                _lastAbility1CooldownDisplay = rounded;
                SetText(ability1CooldownText, $"{_ability1.CooldownRemaining:F1}s");
            }
        }

        // HasActiveWindow gates this PERMANENTLY (not just "isn't active right now") — an ability that never
        // has an active window at all (e.g. ExeAxeThrowAbilityPD) should never show this panel even for a
        // single frame, not just while AbilityIsActive happens to read false. See IAbilityPD.HasActiveWindow's
        // own doc comment.
        bool showDuration = _ability1.HasActiveWindow && _ability1.AbilityIsActive;
        if (showDuration != _ability1DurationUIShown)
        {
            _ability1DurationUIShown = showDuration;
            if (ability1DurationUI != null) ability1DurationUI.SetActive(showDuration);
        }
        if (showDuration)
            SetFill(ability1DurationBar, _ability1.ActiveTimeDuration > 0f
                ? _ability1.ActiveTimeRemaining / _ability1.ActiveTimeDuration
                : 0f);

        // Ability charge system (2026-10-02) — see IAbilityPD.CurrentCharges/MaxCharges's own doc comment.
        // ChargeUI container: visible only while MaxCharges > 1 — a 1-charge ability (the default) never
        // shows this at all, same old look as before. Live, not a one-time check, so a mid-run bonus-charge
        // augment flips this on immediately.
        int maxCharges1 = _ability1.MaxCharges;
        bool showChargeUI1 = maxCharges1 > 1;
        if (showChargeUI1 != _ability1ChargeUIShown)
        {
            _ability1ChargeUIShown = showChargeUI1;
            if (ability1ChargeUI != null) ability1ChargeUI.SetActive(showChargeUI1);
        }
        if (showChargeUI1 && _ability1.CurrentCharges != _lastAbility1ChargeCount)
        {
            _lastAbility1ChargeCount = _ability1.CurrentCharges;
            SetText(ability1ChargeCount, $"{_lastAbility1ChargeCount}");
        }
        // ChargeFill: castable right now (>=1 charge) AND not fully topped off (< max) — the "regenerating
        // an EXTRA charge in the background while still ready" case the main CDFill/onCooldown sweep above
        // deliberately excludes. Same fill-ratio formula/source (CooldownRemaining/CooldownDuration) as the
        // main CDFill, just a different element shown under a different condition.
        bool showChargeFill1 = _ability1.CurrentCharges >= 1 && _ability1.CurrentCharges < maxCharges1;
        if (showChargeFill1 != _ability1ChargeFillShown)
        {
            _ability1ChargeFillShown = showChargeFill1;
            if (ability1ChargeFill != null) ability1ChargeFill.gameObject.SetActive(showChargeFill1);
        }
        if (showChargeFill1)
            SetFill(ability1ChargeFill, _ability1.CooldownDuration > 0f
                ? _ability1.CooldownRemaining / _ability1.CooldownDuration
                : 0f);
    }

    // Identical shape to UpdateAbility1 above — both slots are functionally the same now (cooldown-gated
    // IAbilityPD), see that method's own comments for the reasoning.
    private void UpdateAbility2()
    {
        if (_ability2 == null) return;

        SetFill(ability2CDFill, _ability2.CooldownDuration > 0f
            ? _ability2.CooldownRemaining / _ability2.CooldownDuration
            : 0f);

        // See UpdateAbility1's own identical comments for why this is CurrentCharges (not CooldownRemaining/
        // !IsReady) AND excludes AbilityIsActive.
        bool onCooldown = _ability2.CurrentCharges <= 0 && !_ability2.AbilityIsActive;
        // See UpdateAbility1's own identical comment for the full BG-visibility reasoning.
        bool showBG = !_ability2.IsReady || (_controller != null && _controller.IsLockedOut)
            || (_stats.IsForcedSurging && _ability2.ActiveSurgeBlock);
        if (showBG != _ability2CooldownUIShown)
        {
            _ability2CooldownUIShown = showBG;
            if (ability2OnCooldownBG != null) ability2OnCooldownBG.SetActive(showBG);
        }
        if (onCooldown != _ability2CooldownTextShown)
        {
            _ability2CooldownTextShown = onCooldown;
            if (ability2CooldownText != null) ability2CooldownText.gameObject.SetActive(onCooldown);
            if (ability2CDFill != null) ability2CDFill.gameObject.SetActive(onCooldown);
        }
        if (onCooldown)
        {
            float rounded = Mathf.Round(_ability2.CooldownRemaining * 10f) / 10f;
            if (rounded != _lastAbility2CooldownDisplay)
            {
                _lastAbility2CooldownDisplay = rounded;
                SetText(ability2CooldownText, $"{_ability2.CooldownRemaining:F1}s");
            }
        }

        // See UpdateAbility1's own identical comment for why HasActiveWindow gates this permanently.
        bool showDuration = _ability2.HasActiveWindow && _ability2.AbilityIsActive;
        if (showDuration != _ability2DurationUIShown)
        {
            _ability2DurationUIShown = showDuration;
            if (ability2DurationUI != null) ability2DurationUI.SetActive(showDuration);
        }
        if (showDuration)
            SetFill(ability2DurationBar, _ability2.ActiveTimeDuration > 0f
                ? _ability2.ActiveTimeRemaining / _ability2.ActiveTimeDuration
                : 0f);

        // See UpdateAbility1's own identical comment for the full ChargeUI/ChargeCount/ChargeFill reasoning.
        int maxCharges2 = _ability2.MaxCharges;
        bool showChargeUI2 = maxCharges2 > 1;
        if (showChargeUI2 != _ability2ChargeUIShown)
        {
            _ability2ChargeUIShown = showChargeUI2;
            if (ability2ChargeUI != null) ability2ChargeUI.SetActive(showChargeUI2);
        }
        if (showChargeUI2 && _ability2.CurrentCharges != _lastAbility2ChargeCount)
        {
            _lastAbility2ChargeCount = _ability2.CurrentCharges;
            SetText(ability2ChargeCount, $"{_lastAbility2ChargeCount}");
        }
        bool showChargeFill2 = _ability2.CurrentCharges >= 1 && _ability2.CurrentCharges < maxCharges2;
        if (showChargeFill2 != _ability2ChargeFillShown)
        {
            _ability2ChargeFillShown = showChargeFill2;
            if (ability2ChargeFill != null) ability2ChargeFill.gameObject.SetActive(showChargeFill2);
        }
        if (showChargeFill2)
            SetFill(ability2ChargeFill, _ability2.CooldownDuration > 0f
                ? _ability2.CooldownRemaining / _ability2.CooldownDuration
                : 0f);
    }

    // Whole DeathPreventionUI panel hidden unless Stats.GainsDeathPreventions is true — a character who
    // never picked the augment (or whatever else grants it) has no business showing an empty/dormant
    // Death Prevention indicator at all. CDOverlay + Timer are a nested pair inside that panel, shown
    // together only while a save ISN'T currently banked (Stats.HasDeathPrevention false) — that's exactly
    // when CharacterStatsPD's own grant timer is actively ticking (see its Simulate()'s own comment) toward
    // the next one, so this is the "on cooldown" window from the player's perspective. Once the timer
    // completes and a Death Prevention is granted, both hide — the outer panel alone (still visible) is
    // enough to signal "ready".
    //
    // Delayed by deathPreventionInvincibilityDuration on top of that — the instant a save is spent, the
    // grant timer immediately re-arms to the full DeathPreventionTime (see CharacterStatsPD.Simulate()'s own
    // comment), which now always includes that duration as an unreducible flat floor (see
    // DeathPreventionTime's own doc comment). The FIRST deathPreventionInvincibilityDuration seconds of that
    // fresh countdown exactly line up with the post-save invincibility window itself, so showing the CD
    // overlay/timer immediately would visually nag the player while they're still actually protected. Kept
    // hidden — reads as still "ready" — until UIDeathPreventionTimer drops to baseDeathPreventionTime *
    // deathPreventionTimeMultiplier (i.e. DeathPreventionTime minus that flat floor), the same instant the
    // invincibility window itself would have ended.
    private void UpdateDeathPreventionUI()
    {
        if (deathPreventionUI == null) return;

        bool gains = _stats.GainsDeathPreventions;
        if (gains != _deathPreventionUIShown)
        {
            _deathPreventionUIShown = gains;
            deathPreventionUI.SetActive(gains);
        }
        if (!gains) return;

        bool onCooldown = !_stats.HasDeathPrevention
            && _stats.UIDeathPreventionTimer <= _stats.baseDeathPreventionTime * _stats.deathPreventionTimeMultiplier;
        if (onCooldown != _deathPreventionCooldownShown)
        {
            _deathPreventionCooldownShown = onCooldown;
            if (deathPreventionCDOverlay != null) deathPreventionCDOverlay.SetActive(onCooldown);
            if (deathPreventionTimerText != null) deathPreventionTimerText.gameObject.SetActive(onCooldown);
        }
        // Whole seconds, rounded UP (CeilToInt) so it never flashes "0" for a frame while still on
        // cooldown — same convention as PausePD.OnFallbackTimerChanged/AugmentManagerPD.
        // OnPresentingTimerChanged's own countdown displays.
        if (onCooldown)
        {
            int seconds = Mathf.CeilToInt(_stats.UIDeathPreventionTimer);
            if (seconds != _lastDeathPreventionSecondsForText)
            {
                _lastDeathPreventionSecondsForText = seconds;
                SetText(deathPreventionTimerText, $"{seconds}");
            }
        }
    }

    private void UpdateEnergy()
    {
        SetFill(energyImage, _stats.UIEnergy < _stats.MaxEnergy
            ? _stats.UIEnergy / _stats.MaxEnergy
            : 1f);
        SetColor(energyImage, _stats.UIIsSurging || _stats.UIEnergy < _stats.SurgeActivationThreshhold ? Color.red : Color.yellow);

        int energyInt = (int)_stats.UIEnergy;
        if (energyInt == _lastEnergyIntForText) return;
        _lastEnergyIntForText = energyInt;
        SetText(energyText, $"<b>{energyInt}</b>");
    }

    private void UpdateStats()
    {
        // This panel is only visible while held (e.g. Tab) — the full stat readout (15+ live reads plus a
        // StringBuilder rebuild) was running every single frame regardless, even while completely hidden.
        // Skipping it entirely while inactive is a much bigger win here than caching each individual stat
        // value, since the panel is closed the overwhelming majority of the time.
        if (statsUI == null || !statsUI.activeInHierarchy) return;

        _statsSB.Clear();
        // Current Strength only (2026-08-19) — folded back into the top of this panel from its own dedicated
        // statsDamageText box, which is gone; no more LuckFloor/LuckCeiling range display alongside it.
        // FormatStat (not :G4 like the rest of this panel) — Strength alone can scale into the thousands+
        // over a long run, and needs FormatStat's k/mil/bil/tril abbreviation the other stats here don't.
        _statsSB.AppendLine($" {FormatStat(_stats.Strength, 1)}");
        _statsSB.AppendLine($" {_stats.AttackSpeed:G4}");
        _statsSB.AppendLine($" {_stats.EnergyRegen:G4}");
        // No * 5f compensation anymore (2026-09-16) — WeaponRange itself is now the plain, uncompressed
        // player-facing number; that display hack existed only because WeaponRange used to carry a hidden
        // * 0.2f world-unit compression directly (see CharacterStatsPD.WeaponRange's own doc comment).
        _statsSB.AppendLine($" {_stats.WeaponRange:G4}");
        _statsSB.AppendLine($" {_stats.MoveSpeed:G4}");
        _statsSB.AppendLine($" {_stats.EffectiveDashHaste:G4}");
        _statsSB.AppendLine($" {Mathf.Min(_stats.CritChance * 100f, 100f):G4}%");
        _statsSB.AppendLine($" {_stats.CritDamage * 100f:G4}%");
        _statsSB.AppendLine($" {_stats.Luck:G4}");
        // Luck Variance/Highroll Bonus — absorbed from the now-retired statsUIExpanded panel (2026-08-21).
        _statsSB.AppendLine($" {(_stats.EffectiveLuckFloor - 1f) * 100f:+0.0;-0.0}% | {(_stats.LuckCeiling - 1f) * 100f:+0.0;-0.0}%");
        _statsSB.AppendLine($" {Mathf.Min(_stats.HighrollChance * 100f, 100f):0.##}%");
        _statsSB.AppendLine($" {_stats.HighrollBonus * 100f:+0.0;-0.0}%");
        // Same raw-number convention as EffectiveDashHaste above (no % suffix) — both are the same
        // baseCooldown / (1 + haste/100) shaped stat, just for abilities instead of dash.
        _statsSB.AppendLine($" {_stats.EffectiveAbilityHaste:G4}");
        // 1f-baseline-as-percent convention (default multiplier 1f reads as 100%).
        _statsSB.AppendLine($" {_stats.OnHitPower * 100f:G4}%");
        // Multishot/Ricochet — absorbed from the now-retired statsUIExpanded panel, moved to the tail end.
        // Gated behind _isRanged again (melee has neither stat) — see BuildStatsWords for the matching
        // label-side gate; the two must stay in lockstep or the numbers column desyncs from its own labels.
        if (_isRanged)
        {
            _statsSB.AppendLine($" {_stats.Multishot}");
            _statsSB.AppendLine($" {_stats.Ricochet}");
        }
        SetText(statsNumbersText, _statsSB.ToString());
    }

    // Builds statsWordsText's own label column — called once per SetPlayer (a class's own _isRanged never
    // changes mid-match, so this never needs to re-run per frame the way UpdateStats' numbers column does).
    // Order MUST match UpdateStats' own _statsSB.AppendLine sequence exactly, line for line — the two
    // StringBuilders render side-by-side as separate TMP columns with no shared indexing to keep them
    // aligned automatically.
    private void BuildStatsWords()
    {
        if (statsWordsText == null) return;

        _statsWordsSB.Clear();
        _statsWordsSB.AppendLine("Strength:");
        _statsWordsSB.AppendLine("Attack Speed:");
        _statsWordsSB.AppendLine("Energy Regen:");
        _statsWordsSB.AppendLine("Range:");
        _statsWordsSB.AppendLine("Move Speed:");
        _statsWordsSB.AppendLine("Dash Haste:");
        _statsWordsSB.AppendLine("Crit Chance:");
        _statsWordsSB.AppendLine("Crit Damage:");
        _statsWordsSB.AppendLine("Luck:");
        _statsWordsSB.AppendLine("Luck Variance:");
        _statsWordsSB.AppendLine("Highroll Chance:");
        _statsWordsSB.AppendLine("Highroll Bonus:");
        _statsWordsSB.AppendLine("Ability Haste:");
        _statsWordsSB.AppendLine("On-Hit Power:");
        if (_isRanged)
        {
            _statsWordsSB.AppendLine("Multishot:");
            _statsWordsSB.AppendLine("Ricochet:");
        }
        SetText(statsWordsText, _statsWordsSB.ToString());
    }

    private void UpdateXP()
    {
        if (GameManagerPD.instance == null) return;
        SetSlider(xpBar, GameManagerPD.instance.CurrentXP / GameManagerPD.instance.XPToNextLevel);

        int level = GameManagerPD.instance.Level;
        float current = GameManagerPD.instance.CurrentXP;
        float toNext = GameManagerPD.instance.XPToNextLevel;
        if (level == _lastXPLevelForText && current == _lastXPCurrentForText && toNext == _lastXPToNextForText) return;
        _lastXPLevelForText = level;
        _lastXPCurrentForText = current;
        _lastXPToNextForText = toNext;
        SetText(xpText, $"<b>Level {level} | {FormatStat(current, 1)}/{FormatStat(toNext, 1)} XP</b>");
    }

    private void UpdateUpgradePoints()
    {
        if (upgradePointsText == null) return;
        float points = LocalUpgradePointsTracker.instance.TotalEarnedThisSession;
        if (points == _lastUpgradePointsForText) return;
        _lastUpgradePointsForText = points;
        SetText(upgradePointsText, $"<b>{FormatStat(points)} pts</b>");
    }

    // Same "Threat Level {X}" text CharacterSelectUI.RefreshDifficultyDisplay already shows in the lobby —
    // set once and latched via _difficultyTextSet since Difficulty never changes mid-match, unlike every
    // other text updater here. Gated on GameManagerPD.GameStarted specifically, not just instance existing
    // (2026-08-21 fix) — GameManagerPD.Difficulty isn't actually resolved/replicated from the server until
    // the same tick GameStarted itself flips true (see GameManagerPD.Simulate()'s own difficulty-capture),
    // and this HUD can be up well before that (loading screen clears close to, but not strictly after, that
    // moment) — without this a fast client could latch "Threat Level 1" (the State default) permanently.
    private void UpdateDifficultyText()
    {
        if (difficultyText == null || _difficultyTextSet || GameManagerPD.instance == null || !GameManagerPD.instance.GameStarted) return;
        _difficultyTextSet = true;
        SetText(difficultyText, $"Threat Level {GameManagerPD.instance.Difficulty}");
    }

    private void UpdateRunClock()
    {
        if (GameManagerPD.instance == null) return;
        float runtime = GameManagerPD.instance.ElapsedRunTime;
        int minutes = (int)(runtime / 60f);
        int seconds = (int)(runtime % 60f);
        // The underlying value only genuinely changes once per real second — rebuilding the string on every
        // one of the ~60 frames in between was pure waste.
        if (minutes == _lastRunClockMinutes && seconds == _lastRunClockSeconds) return;
        _lastRunClockMinutes = minutes;
        _lastRunClockSeconds = seconds;
        SetText(timerText, $"<b>{minutes:00}:{seconds:00}</b>");
    }

    // TODO: enrage timer bar not implemented yet — deferred until the enrage mechanic itself exists.
    private void UpdateBossUI()
    {
        if (EnemySpawnerPD.instance != null && bossTimerText != null)
        {
            float remaining = Mathf.Max(0f, EnemySpawnerPD.instance.CycleTimer);
            int minutes = (int)(remaining / 60f);
            int seconds = (int)(remaining % 60f);
            bossTimerText.gameObject.SetActive(true);
            // Same "only really changes once a second" reasoning as UpdateRunClock above.
            if (minutes != _lastBossMinutes || seconds != _lastBossSeconds)
            {
                _lastBossMinutes = minutes;
                _lastBossSeconds = seconds;
                SetText(bossTimerText, $"Next Boss:\n{minutes:00}:{seconds:00}");
            }
            return;
        }

        bossTimerText.gameObject.SetActive(false);
    }

    // Pools one BossHealthBarUI per currently-alive boss (see EnemySpawnerPD.activeBosses) — supports any
    // number spawned simultaneously. Never holds an IEnemyPD reference across frames: enemies are pooled
    // and reused for new spawns, so a cached reference from an earlier frame could silently end up
    // pointing at a completely different enemy later. Resolves fresh from the id every call instead.
    private void UpdateBossHealthBars()
    {
        if (EnemySpawnerPD.instance == null || bossBarTemplate == null || bossBarContainer == null) return;

        EnemySpawnerPD.instance.GetVerifiedActiveBosses(_bossIdBuffer);

        // Release bars for bosses that are no longer active.
        _staleBossIdBuffer.Clear();
        foreach (var id in _activeBossBars.Keys)
            if (!_bossIdBuffer.Contains(id)) _staleBossIdBuffer.Add(id);

        for (int i = 0; i < _staleBossIdBuffer.Count; i++)
        {
            var id = _staleBossIdBuffer[i];
            var staleBar = _activeBossBars[id];
            _activeBossBars.Remove(id);
            staleBar.gameObject.SetActive(false);
            _bossBarPool.Push(staleBar);
        }

        // Acquire bars for newly active bosses, and refresh every currently active one's health.
        for (int i = 0; i < _bossIdBuffer.Count; i++)
        {
            var id = _bossIdBuffer[i];
            var enemy = EnemySpawnerPD.instance.ResolveBoss(id);
            if (enemy == null) continue;

            if (!_activeBossBars.TryGetValue(id, out var bar))
            {
                bar = _bossBarPool.Count > 0 ? _bossBarPool.Pop() : Instantiate(bossBarTemplate, bossBarContainer);
                bar.gameObject.SetActive(true);
                bar.SetName(enemy.EnemyName);
                _activeBossBars[id] = bar;
            }

            bar.SetHealth(enemy.CurrentHealth, enemy.MaxHealth);
            bar.SetEnrage(enemy.EnrageProgress);
        }
    }
}
