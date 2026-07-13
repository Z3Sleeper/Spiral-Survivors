using System.Collections.Generic;
using PurrNet;
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
    [SerializeField] private GameObject bossUIContainer;
    [SerializeField] private Slider bossHealthBar;
    [SerializeField] private Slider bossEnrageBar;
    [SerializeField] private TextMeshProUGUI bossHealthText;
    [SerializeField] private TextMeshProUGUI bossNameText;

    [Header("Level Up Panel")]
    [SerializeField] private GameObject levelUpScreen;
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

    private readonly System.Text.StringBuilder _statsSB = new();
    private readonly System.Text.StringBuilder _statsExpandedSB = new();

    private CharacterStatsPD _stats;
    private PlayerControllerPD _controller;
    private bool _isRanged;

    private void Awake()
    {
        instance = this;
        levelUpScreen.SetActive(false);
        pauseCountdownText.gameObject.SetActive(false);
        bossUIContainer.SetActive(false);
        statsUI.SetActive(true);
        statsUIExpanded.SetActive(false);
        pauseScreen.SetActive(false);
        if (settingsPanel != null) settingsPanel.SetActive(false);
        PausePD.OnPauseChanged += OnPauseStateChanged;
        PausePD.OnCountdownChanged += OnCountdownChanged;
        PausePD.OnReadyCountChanged += OnReadyCountChanged;
    }

    private void OnDestroy()
    {
        PausePD.OnPauseChanged -= OnPauseStateChanged;
        PausePD.OnCountdownChanged -= OnCountdownChanged;
        PausePD.OnReadyCountChanged -= OnReadyCountChanged;
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
    public void SetUpStatAugmentScreen(List<StatAugmentPair> options)
    {
        HideAllCards();
        for (int i = 0; i < options.Count; i++)
            GetCard(i).StatAugmentSetup(options[i], i);
    }

    // Only buffers the request through the local player's own controller — Simulate() performs the
    // actual pick once this is drained into Input, same reasoning as the ready/pause buttons.
    public void RequestPick(int cardIndex)
    {
        if (_controller == null) return;
        _controller.RequestPick(cardIndex);
    }

    private bool _levelUpScreenShown;

    private void UpdateLevelUpScreen()
    {
        if (AugmentManagerPD.instance == null || _controller == null || !_controller.owner.HasValue) return;

        bool shouldShow = AugmentManagerPD.instance.VerifiedIsPresenting
            && !AugmentManagerPD.instance.HasPlayerPicked(_controller.owner.Value);

        if (shouldShow && !_levelUpScreenShown)
        {
            SetUpStatAugmentScreen(AugmentManagerPD.instance.GetOptionsForPlayer(_controller.owner.Value));
            ShowLevelUpScreen();
            _levelUpScreenShown = true;
        }
        else if (!shouldShow && _levelUpScreenShown)
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
        if (upgradePointsText == null || GameManagerPD.instance == null) return;
        SetText(upgradePointsText, $"<b>{FormatStat(GameManagerPD.instance.UpgradePoints)} pts</b>");
    }

    private void UpdateRunClock()
    {
        float runtime = Time.timeSinceLevelLoad;
        int minutes = (int)(runtime / 60f);
        int seconds = (int)(runtime % 60f);
        SetText(timerText, $"<b>{minutes:00}:{seconds:00}</b>");
    }

    // TODO: bosses haven't been ported to the PD system yet — no EnemySpawnerPD/BossBasePD equivalent
    // exists. Left as a no-op (keeps the boss UI hidden) rather than wired to the old (non-PD)
    // EnemySpawner/BossBase, which are being moved out of Assets/.
    private void UpdateBossUI()
    {
        bossUIContainer.SetActive(false);
        bossTimerText.gameObject.SetActive(false);
    }
}
