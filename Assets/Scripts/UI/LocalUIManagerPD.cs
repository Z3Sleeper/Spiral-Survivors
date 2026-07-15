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
    [SerializeField] private TextMeshProUGUI statsNumbersText;
    [SerializeField] private GameObject statsUIExpanded;
    [SerializeField] private TextMeshProUGUI statsWordsTextExpanded;
    [SerializeField] private TextMeshProUGUI statsNumbersTextExpanded;
    [SerializeField] private Slider spBar;
    [SerializeField] private TextMeshProUGUI spText;
    [SerializeField] private Slider xpBar;
    [SerializeField] private TextMeshProUGUI xpText;
    [SerializeField] private TextMeshProUGUI timerText;
    [SerializeField] private TextMeshProUGUI bossTimerText;
    [SerializeField] private TextMeshProUGUI upgradePointsText;

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

    [Header("Level Up Panel")]
    [SerializeField] private GameObject levelUpScreen;
    // Separate from levelUpScreen so the paused-feeling tint can stay up for the whole multiplayer
    // presenting window, independent of whether THIS player specifically has already picked.
    [SerializeField] private GameObject levelUpBackground;
    [SerializeField] private TextMeshProUGUI levelUpTimerText;
    [SerializeField] private AugmentCardUI cardPrefab;
    [SerializeField] private Transform cardContainer;
    private List<AugmentCardUI> _cardPool = new();

    [Header("Augment List")]
    [SerializeField] private GameObject augmentListPanel;

    [Header("Settings")]
    [SerializeField] private GameObject settingsPanel;

    [Header("Pause System")]
    [SerializeField] private TextMeshProUGUI pauseCountdownText;
    [SerializeField] private GameObject pauseScreen;
    [SerializeField] private GameObject singlePlayerResumePanel;
    [SerializeField] private GameObject multiplayerPausePanel;
    [SerializeField] private TextMeshProUGUI pauseReadyCountText;
    [SerializeField] private TextMeshProUGUI pauseFallbackTimerText;
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
    private bool _isRanged;

    private void Awake()
    {
        instance = this;
        levelUpScreen.SetActive(false);
        levelUpBackground.SetActive(false);
        pauseCountdownText.gameObject.SetActive(false);
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

    private void OnPresentingTimerChanged(int seconds)
    {
        if (levelUpTimerText == null) return;
        levelUpTimerText.text = $"Time Remaining: {seconds}s";
    }

    private void OnPauseStateChanged(bool isPaused)
    {
        bool isSinglePlayer = NetworkManager.main == null || NetworkManager.main.playerCount <= 1;
        if (isPaused) ShowPauseScreen(isSinglePlayer);
        else HidePauseScreen();
    }

    private void OnCountdownChanged(int seconds)
    {
        if (seconds > 0) ShowPauseCountdown(seconds);
        else UpdatePauseCountdown(0);
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

    public void SetPlayer(CharacterStatsPD stats)
    {
        _stats = stats;
        _controller = stats.GetComponent<PlayerControllerPD>();
        _isRanged = stats.GetComponent<WeaponManagerPD>().IsRanged;
        statsWordsTextExpanded.text = _isRanged
            ? "Damage %:\nAttack Speed %:\nEnergy Regen %:\nRange %:\nMove Speed %:\nDash Cooldown:\nRicochet:\nMultishot:"
            : "Damage %:\nAttack Speed %:\nEnergy Regen %:\nRange %:\nMove Speed %:\nDash Cooldown:";
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

    private void UpdateLevelUpScreen()
    {
        if (AugmentManagerPD.instance == null || _controller == null || !_controller.owner.HasValue) return;

        bool presenting = AugmentManagerPD.instance.VerifiedIsPresenting;

        // Background reflects the whole multiplayer presenting window (every player, not just this
        // one) — otherwise it drops the instant THIS player picks, even while teammates are still
        // choosing and the game is still effectively paused underneath.
        if (presenting && !_levelUpBackgroundShown)
        {
            levelUpBackground.SetActive(true);
            _levelUpBackgroundShown = true;
        }
        else if (!presenting && _levelUpBackgroundShown)
        {
            levelUpBackground.SetActive(false);
            _levelUpBackgroundShown = false;
        }

        // Cards only need to be visible until THIS player has picked — no reason to keep showing your
        // own already-answered options while waiting on everyone else.
        bool shouldShowCards = presenting && !AugmentManagerPD.instance.HasPlayerPicked(_controller.owner.Value);

        if (shouldShowCards && !_levelUpScreenShown)
        {
            SetUpLevelUpScreen(AugmentManagerPD.instance.GetOptionsForPlayer(_controller.owner.Value));
            ShowLevelUpScreen();
            _levelUpScreenShown = true;
        }
        else if (!shouldShowCards && _levelUpScreenShown)
        {
            HideLevelUpScreen();
            _levelUpScreenShown = false;
        }
    }

    public void HideAllCards()
    {
        foreach (var card in _cardPool)
            card.gameObject.SetActive(false);
    }

    public void ShowPauseCountdown(int seconds)
    {
        pauseCountdownText.gameObject.SetActive(true);
        pauseCountdownText.text = $"Pausing in {seconds}...";
    }

    public void UpdatePauseCountdown(int seconds)
    {
        if (seconds <= 0) { pauseCountdownText.gameObject.SetActive(false); return; }
        pauseCountdownText.text = $"Pausing in {seconds}...";
    }

    public void ShowPauseScreen(bool isSinglePlayer)
    {
        pauseCountdownText.gameObject.SetActive(false);
        if (pauseReadyButtonText != null) pauseReadyButtonText.text = "Ready Up";
        if (pauseReadyCountText != null) pauseReadyCountText.text = $"0/{NetworkManager.main.playerCount} Ready";
        if (pauseFallbackTimerText != null) pauseFallbackTimerText.gameObject.SetActive(false);
        singlePlayerResumePanel.SetActive(isSinglePlayer);
        multiplayerPausePanel.SetActive(!isSinglePlayer);
        if (returnToLobbyButton != null)
            returnToLobbyButton.SetActive(NetworkManager.main.isServer);
        pauseScreen.SetActive(true);
    }

    public void HidePauseScreen()
    {
        pauseScreen.SetActive(false);
        pauseCountdownText.gameObject.SetActive(false);
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

    public void SetFallbackTimer(int seconds, bool visible)
    {
        if (pauseFallbackTimerText == null) return;
        pauseFallbackTimerText.gameObject.SetActive(visible);
        if (visible) pauseFallbackTimerText.text = $"Auto-resume in {seconds}s";
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
        UpdateEnergy();
        UpdateStats();
        UpdateStatsExpanded();
        UpdateSP();
        UpdateXP();
        UpdateUpgradePoints();
        UpdateRunClock();
        UpdateBossUI();
        UpdateBossHealthBars();
        UpdatePauseReadyButton();
        UpdateLevelUpScreen();
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
        SetText(damageReductionText, $"<b>{sign}{percent:F0}% DR</b>");
    }

    private void UpdateDash()
    {
        SetFill(dashChargeCooldownImage, _controller.currentState.currentCharges < _stats.dashCharges
            ? 1f - (_controller.currentState.chargeRegenTimer / _stats.DashCooldown)
            : 1f);
        SetText(dashChargesText, $"<b>{_controller.currentState.currentCharges}</b>");
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
        _statsSB.Clear();
        _statsSB.AppendLine($" {_stats.WeaponDamage:G4}");
        _statsSB.AppendLine($" {_stats.AttackSpeed:G4}");
        _statsSB.AppendLine($" {_stats.EnergyRegen:G4}");
        _statsSB.AppendLine($" {_stats.WeaponRange * 5f:G4}");
        _statsSB.AppendLine($" {_stats.MoveSpeed:G4}");
        _statsSB.AppendLine($" {_stats.dashHaste:G4}");
        _statsSB.AppendLine($" {Mathf.Min(_stats.CritChance * 100f, 100f):G4}%");
        _statsSB.AppendLine($" {_stats.CritDamage * 100f:G4}%");
        _statsSB.AppendLine($" {_stats.Luck:G4}");
        _statsSB.AppendLine($" {_stats.OnHitPower * 100f:G4}%");
        SetText(statsNumbersText, _statsSB.ToString());
    }

    private void UpdateStatsExpanded()
    {
        _statsExpandedSB.Clear();
        _statsExpandedSB.AppendLine($" {_stats.bonusWeaponDamagePercent * _stats.bonusWeaponDamagePenalty * 100f - 100f:+0.0;-0.0}%");
        _statsExpandedSB.AppendLine($" {_stats.attackSpeedTotalPercent * _stats.attackSpeedPenalty * 100f - 100f:+0.0;-0.0}%");
        _statsExpandedSB.AppendLine($" {_stats.bonusEnergyRegenPercent * _stats.energyRegenPenalty * 100f - 100f:+0.0;-0.0}%");
        _statsExpandedSB.AppendLine($" {_stats.bonusWeaponRangePercent * _stats.weaponRangePenalty * 100f - 100f:+0.0;-0.0}%");
        _statsExpandedSB.AppendLine($" {_stats.bonusMoveSpeedPercent * _stats.moveSpeedPenalty * 100f - 100f:+0.0;-0.0}%");
        _statsExpandedSB.AppendLine($" {_stats.DashCooldown:F1}s");
        if (_isRanged)
        {
            _statsExpandedSB.AppendLine($" {_stats.Ricochet}");
            _statsExpandedSB.AppendLine($" {_stats.Multishot}");
        }
        SetText(statsNumbersTextExpanded, _statsExpandedSB.ToString());
    }

    private void UpdateSP()
    {
        SetSlider(spBar, _stats.UISP / _stats.MaxSP);
        SetText(spText, _stats.UISP >= _stats.MaxSP ? "<b>BURST</b>" : $"<b>{(int)_stats.UISP}/{(int)_stats.MaxSP}</b>");
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
        }
    }
}
