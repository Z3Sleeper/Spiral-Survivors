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
    [SerializeField] private Slider shieldBar;
    [SerializeField] private Slider shieldRegenBar;
    [SerializeField] private TextMeshProUGUI healthText;
    [SerializeField] private TextMeshProUGUI healthRegenText;
    [SerializeField] private TextMeshProUGUI damageReductionText;
    [SerializeField] private Image dashChargeCooldownImage;
    [SerializeField] private TextMeshProUGUI dashChargesText;
    [SerializeField] private Image energyImage;
    [SerializeField] private TextMeshProUGUI energyText;
    [SerializeField] private GameObject statsUI;
    [SerializeField] private TextMeshProUGUI statsDamageText;
    [SerializeField] private TextMeshProUGUI statsNumbersText;
    [SerializeField] private GameObject statsUIExpanded;
    [SerializeField] private TextMeshProUGUI statsWordsTextExpanded;
    [SerializeField] private TextMeshProUGUI statsNumbersTextExpanded;
    [SerializeField] private Slider xpBar;
    [SerializeField] private TextMeshProUGUI xpText;
    [SerializeField] private TextMeshProUGUI timerText;
    [SerializeField] private TextMeshProUGUI bossTimerText;
    [SerializeField] private TextMeshProUGUI upgradePointsText;
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

    [Header("Augment List")]
    [SerializeField] private GameObject augmentListPanel;

    [Header("Settings")]
    [SerializeField] private GameObject settingsPanel;

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
    private readonly System.Text.StringBuilder _statsExpandedSB = new();

    private CharacterStatsPD _stats;
    private PlayerControllerPD _controller;
    private IAbilityPD _ability1;
    private IAbilityPD _ability2;
    private bool _isRanged;

    private void Awake()
    {
        instance = this;
        levelUpScreen.SetActive(false);
        if (tintScreenOverlay != null) tintScreenOverlay.SetActive(false);
        statsUI.SetActive(true);
        statsUIExpanded.SetActive(false);
        pauseScreen.SetActive(false);
        if (settingsPanel != null) settingsPanel.SetActive(false);
        if (endScreenPanel != null) endScreenPanel.SetActive(false);
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

    public void SetStatsExpanded(bool expanded)
    {
        statsUI.SetActive(!expanded);
        statsUIExpanded.SetActive(expanded);
    }

    public void SetAugmentListVisible(bool visible)
    {
        if (augmentListPanel != null)
            augmentListPanel.SetActive(visible);
    }

    public void SetInvisibleTintVisible(bool visible)
    {
        if (invisibleTintOverlay != null)
            invisibleTintOverlay.SetActive(visible);
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
        statsWordsTextExpanded.text = _isRanged
            ? "Dash Cooldown:\nLuck Variance:\nHighroll Bonus:\nRicochet:\nMultishot:"
            : "Dash Cooldown:\nLuck Variance:\nHighroll Bonus:";
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

    // Barebones — every level-up is a stat-augment pick for now, no real augment pool yet.
    public void SetUpLevelUpScreen(List<LevelUpOption> options)
    {
        HideAllCards();
        for (int i = 0; i < options.Count; i++)
        {
            var card = GetCard(i);
            if (options[i].isAugment)
                card.AugmentSetup(options[i].augmentData, i);
            else
                card.StatAugmentSetup(options[i].statPair, i);
        }
    }

    // Only buffers the request through the local player's own controller — Simulate() performs the
    // actual pick once this is drained into Input, same reasoning as the ready/pause buttons.
    public void RequestPick(int cardIndex)
    {
        if (_controller == null) return;
        _controller.RequestPick(cardIndex);
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
        UpdateEnergy();
        UpdateStats();
        UpdateStatsExpanded();
        UpdateXP();
        UpdateUpgradePoints();
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
        if (visible) multiplayerTimerText.text = text;
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

    private void UpdateHealthAndShields()
    {
        SetSlider(healthBar, _stats.UIHealth / _stats.MaxHealth);
        SetSlider(shieldBar, _stats.MaxShield > 0f ? _stats.UIShield / _stats.MaxShield : 0f);
        SetSlider(shieldRegenBar, (_stats.shieldRechargeTime - _stats.UIShieldRechargeTimer) / _stats.shieldRechargeTime);
        SetText(healthText, $"<b><color=#FF00FF>{FormatStat(_stats.UIShield)}/{FormatStat(_stats.MaxShield)}</color> {FormatStat(_stats.UIHealth)}/{FormatStat(_stats.MaxHealth)}</b>");
    }

    private void UpdateHealthRegen()
    {
        SetText(healthRegenText, $"<b>+{FormatStat(_stats.HealthRegen, 2)}/s</b>");
    }

    private void UpdateDamageReduction()
    {
        float percent = (1f - _stats.DamageReduction) * 100f;
        string sign = percent >= 0f ? "+" : "";
        SetText(damageReductionText, $"<b>{sign}{percent:F0}%\nDR</b>");
    }

    private void UpdateDash()
    {
        SetFill(dashChargeCooldownImage, _controller.currentState.currentCharges < _stats.dashCharges
            ? 1f - (_controller.currentState.chargeRegenTimer / _stats.DashCooldown)
            : 1f);
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

        // Tied to CooldownRemaining directly, NOT "!IsReady" — an ability can be unready for a reason that
        // isn't a running cooldown at all (e.g. BWBlinkAbilityPD, only castable while Blade Dance is active
        // — IsReady reads false the rest of the time with CooldownRemaining already sitting at 0). Using
        // !IsReady here made the countdown text show a permanent, misleading "0.0s" in exactly that case.
        // Also excludes AbilityIsActive — a buff ability (ExeBigAxeAbilityPD, RogueVanishAbilityPD,
        // BWBlinkAbilityPD, etc.) already sets cooldownTimer to its full value the INSTANT it's cast, then
        // (with the default cooldownStartsOnCast = false) holds it there unticked for the whole active
        // window — so CooldownRemaining reads > 0 throughout the buff too, not just once the real cooldown
        // actually starts counting down. Without this exclusion the text/fill would show a misleading
        // stuck-at-full countdown for the buff's own duration instead of staying hidden until the buff ends
        // and the cooldown genuinely begins ticking.
        bool onCooldown = _ability1.CooldownRemaining > 0f && !_ability1.AbilityIsActive;
        // BG is shown more broadly — real cooldown, any OTHER not-ready reason, or merely locked out
        // (dashing, or a locking ability elsewhere is active — see PlayerControllerPD.IsLockedOut's own doc
        // comment) — even though the ability itself may otherwise be fully ready. The countdown TEXT and
        // the CDFill overlay itself both stay tied to actual cooldown only (onCooldown above) — neither a
        // lockout nor a non-cooldown IsReady gate has a timer/depletion to show, so both would just read as
        // stale/wrong if they followed the BG here.
        bool showBG = !_ability1.IsReady || (_controller != null && _controller.IsLockedOut);
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
        if (onCooldown) SetText(ability1CooldownText, $"{_ability1.CooldownRemaining:F1}s");

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
    }

    // Identical shape to UpdateAbility1 above — both slots are functionally the same now (cooldown-gated
    // IAbilityPD), see that method's own comments for the reasoning.
    private void UpdateAbility2()
    {
        if (_ability2 == null) return;

        SetFill(ability2CDFill, _ability2.CooldownDuration > 0f
            ? _ability2.CooldownRemaining / _ability2.CooldownDuration
            : 0f);

        // See UpdateAbility1's own identical comments for why this is CooldownRemaining (not !IsReady) AND
        // excludes AbilityIsActive (not just "> 0f").
        bool onCooldown = _ability2.CooldownRemaining > 0f && !_ability2.AbilityIsActive;
        bool showBG = !_ability2.IsReady || (_controller != null && _controller.IsLockedOut);
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
        if (onCooldown) SetText(ability2CooldownText, $"{_ability2.CooldownRemaining:F1}s");

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
    }

    private void UpdateEnergy()
    {
        SetFill(energyImage, _stats.UIEnergy < _stats.MaxEnergy
            ? _stats.UIEnergy / _stats.MaxEnergy
            : 1f);
        SetColor(energyImage, _stats.UIIsSurging || _stats.UIEnergy < _stats.SurgeActivationThreshhold ? Color.red : Color.yellow);
        SetText(energyText, $"<b>{(int)_stats.UIEnergy}</b>");
    }

    private void UpdateStats()
    {
        SetText(statsDamageText, $" {FormatStat(_stats.Strength, 1)} ({FormatStat(_stats.Strength * _stats.EffectiveLuckFloor, 1)} - {FormatStat(_stats.Strength * _stats.LuckCeiling, 1)})");

        _statsSB.Clear();
        _statsSB.AppendLine($" {_stats.AttackSpeed:G4}");
        _statsSB.AppendLine($" {_stats.EnergyRegen:G4}");
        _statsSB.AppendLine($" {_stats.WeaponRange * 5f:G4}");
        _statsSB.AppendLine($" {_stats.MoveSpeed:G4}");
        _statsSB.AppendLine($" {_stats.EffectiveDashHaste:G4}");
        _statsSB.AppendLine($" {Mathf.Min(_stats.CritChance * 100f, 100f):G4}%");
        _statsSB.AppendLine($" {_stats.CritDamage * 100f:G4}%");
        _statsSB.AppendLine($" {_stats.Luck:G4}");
        _statsSB.AppendLine($" {Mathf.Min(_stats.HighrollChance * 100f, 100f):0.##}%");
        _statsSB.AppendLine($" {_stats.OnHitPower * 100f:G4}%");
        // Same raw-number convention as EffectiveDashHaste above (no % suffix) — both are the same
        // baseCooldown / (1 + haste/100) shaped stat, just for abilities instead of dash.
        _statsSB.AppendLine($" {_stats.EffectiveAbilityHaste:G4}");
        // Same 1f-baseline-as-percent convention as OnHitPower above (default multiplier 1f reads as 100%).
        _statsSB.AppendLine($" {_stats.AbilityPower * 100f:G4}%");
        SetText(statsNumbersText, _statsSB.ToString());
    }

    private void UpdateStatsExpanded()
    {
        _statsExpandedSB.Clear();
        _statsExpandedSB.AppendLine($" {_stats.DashCooldown:F1}s");
        _statsExpandedSB.AppendLine($" {(_stats.EffectiveLuckFloor - 1f) * 100f:+0.0;-0.0}% | {(_stats.LuckCeiling - 1f) * 100f:+0.0;-0.0}%");
        _statsExpandedSB.AppendLine($" {_stats.HighrollBonus * 100f:+0.0;-0.0}%");
        if (_isRanged)
        {
            _statsExpandedSB.AppendLine($" {_stats.Ricochet}");
            _statsExpandedSB.AppendLine($" {_stats.Multishot}");
        }
        SetText(statsNumbersTextExpanded, _statsExpandedSB.ToString());
    }

    private void UpdateXP()
    {
        if (GameManagerPD.instance == null) return;
        SetSlider(xpBar, GameManagerPD.instance.CurrentXP / GameManagerPD.instance.XPToNextLevel);
        SetText(xpText, $"<b>Level {GameManagerPD.instance.Level} | {FormatStat(GameManagerPD.instance.CurrentXP, 1)}/{FormatStat(GameManagerPD.instance.XPToNextLevel, 1)} XP</b>");
    }

    private void UpdateUpgradePoints()
    {
        if (upgradePointsText == null) return;
        SetText(upgradePointsText, $"<b>{FormatStat(LocalUpgradePointsTracker.instance.TotalEarnedThisSession)} pts</b>");
    }

    private void UpdateRunClock()
    {
        if (GameManagerPD.instance == null) return;
        float runtime = GameManagerPD.instance.ElapsedRunTime;
        int minutes = (int)(runtime / 60f);
        int seconds = (int)(runtime % 60f);
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
            SetText(bossTimerText, $"Next Boss:\n{minutes:00}:{seconds:00}");
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
