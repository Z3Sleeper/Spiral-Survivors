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
    [SerializeField] private Slider shieldRegenBar;
    [SerializeField] private TextMeshProUGUI healthText;
    [SerializeField] private TextMeshProUGUI healthRegenText;
    [SerializeField] private TextMeshProUGUI damageReductionText;
    [SerializeField] private Image dashChargeCooldownImage;
    [SerializeField] private TextMeshProUGUI dashChargesText;
    [SerializeField] private Image energyImage;
    [SerializeField] private TextMeshProUGUI energyText;
    [SerializeField] private GameObject statsUI;
    [SerializeField] private TextMeshProUGUI statsNumbersText;
    [SerializeField] private GameObject statsUIExpanded;
    [SerializeField] private TextMeshProUGUI statsWordsTextExpanded;
    [SerializeField] private TextMeshProUGUI statsNumbersTextExpanded;
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

    [Header("Augment List")]
    [SerializeField] private AugmentListUIPD augmentListUI;

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

    // Captured once, before anything can tint it — same "cache the true authored value before anything can
    // touch it" reasoning EnemyBasePD's own _originalColors uses.
    private Color _healthBarDefaultColor;
    private bool  _healthBarInvincibleTintShown;

    private void Awake()
    {
        instance = this;
        if (healthBarFillImage != null) _healthBarDefaultColor = healthBarFillImage.color;
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

    // Read by PlayerControllerPD.UpdateInput() to make Escape close an open Settings panel instead of
    // reaching the networked pause/ready toggle underneath it — see that call site's own doc comment.
    public bool IsSettingsPanelOpen => settingsPanel != null && settingsPanel.activeSelf;

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
        UpdateStatsExpanded();
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
        SetSlider(shieldRegenBar, (_stats.shieldRechargeTime - _stats.UIShieldRechargeTimer) / _stats.shieldRechargeTime);

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
        // BG is shown more broadly — real cooldown, any OTHER not-ready reason, a hard lockout (a locking
        // ability elsewhere is active, or isSpecialMoving/stunned — see PlayerControllerPD.IsLockedOut's own
        // doc comment), or blocked by actively dashing OR a Berserk-Surge-style forced Surge (only for an
        // action ability — see IAbilityPD.IsActionAbility's own doc comment, same condition
        // PlayerControllerPD.Simulate() itself gates Activate() on) — even though the ability itself may
        // otherwise be fully ready. The countdown TEXT and the CDFill overlay itself both stay tied to actual
        // cooldown only (onCooldown above) — none of the other reasons have a timer/depletion to show, so
        // both would just read as stale/wrong if they followed the BG here.
        bool showBG = !_ability1.IsReady || (_controller != null && _controller.IsLockedOut)
            || (((_controller != null && _controller.IsDashing) || _stats.IsForcedSurging) && _ability1.IsActionAbility);
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
        // See UpdateAbility1's own identical comment for the full BG-visibility reasoning.
        bool showBG = !_ability2.IsReady || (_controller != null && _controller.IsLockedOut)
            || (((_controller != null && _controller.IsDashing) || _stats.IsForcedSurging) && _ability2.IsActionAbility);
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
        // See UpdateStats' own comment — same "only visible while held" panel, same skip-entirely-while-
        // hidden win.
        if (statsUIExpanded == null || !statsUIExpanded.activeInHierarchy) return;

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
