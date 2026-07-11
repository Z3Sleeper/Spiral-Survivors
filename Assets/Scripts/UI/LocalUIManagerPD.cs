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
    [SerializeField] private TextMeshProUGUI sessionPointsText;

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
    private PDCharacterController _controller;
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
        _controller = stats.GetComponent<PDCharacterController>();
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

    public void SetUpLevelUpScreen(List<LevelUpOption> options, PlayerID playerID)
    {
        levelUpTimerText.text = NetworkManager.main.playerCount > 1 ? "Time Remaining: 30s" : "";
        HideAllCards();
        for (int i = 0; i < options.Count; i++)
        {
            var card = GetCard(i);
            if (options[i].type == LevelUpOption.OptionType.Augment)
                card.AugmentSetup(options[i].augmentData, i, playerID);
            else
                card.StatAugmentSetup(options[i].statPair, i, playerID);
        }
        GameManager.instance.ReadyToLevelRpc(playerID);
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

    public void UpdateTimer(int secondsRemaining)
    {
        if (levelUpTimerText != null)
            levelUpTimerText.text = NetworkManager.main.playerCount >= 2
                ? "Time Remaining: " + secondsRemaining + "s"
                : "";
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

    public void SetFallbackTimer(int seconds, bool visible)
    {
        if (pauseFallbackTimerText == null) return;
        pauseFallbackTimerText.gameObject.SetActive(visible);
        if (visible) pauseFallbackTimerText.text = $"Auto-resume in {seconds}s";
    }

    public void OnPauseReadyClicked()
    {
        if (_controller == null) return;
        _controller.ToggleReady();
        if (pauseReadyButtonText != null)
            pauseReadyButtonText.text = _controller.IsReady ? "Unready" : "Ready Up";
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
        if (PausePD.instance != null) PausePD.instance.RequestToggle();
    }

    public void OnReturnToLobbyClicked()
    {
        if (GameManagerPD.instance != null) GameManagerPD.instance.ReturnToLobby();
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
        UpdateSessionPoints();
        UpdateRunClock();
        UpdateBossUI();
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
        healthBar.value      = _stats.UIHealth / _stats.MaxHealth;
        shieldBar.value      = _stats.MaxShield > 0f ? _stats.UIShield / _stats.MaxShield : 0f;
        shieldRegenBar.value = (_stats.shieldRechargeTime - _stats.UIShieldRechargeTimer) / _stats.shieldRechargeTime;
        healthText.text      = $"<b><color=#FF00FF>{FormatStat(_stats.UIShield)}/{FormatStat(_stats.MaxShield)}</color> {FormatStat(_stats.UIHealth)}/{FormatStat(_stats.MaxHealth)}</b>";
    }

    private void UpdateHealthRegen()
    {
        healthRegenText.text = $"<b>+{FormatStat(_stats.HealthRegen, 2)}/s</b>";
    }

    private void UpdateDamageReduction()
    {
        float percent = (1f - _stats.DamageReduction) * 100f;
        string sign = percent >= 0f ? "+" : "";
        damageReductionText.text = $"<b>{sign}{percent:F0}% DR</b>";
    }

    private void UpdateDash()
    {
        dashChargeCooldownImage.fillAmount = _controller.currentState.currentCharges < _stats.dashCharges
            ? 1f - (_controller.currentState.chargeRegenTimer / _stats.DashCooldown)
            : 1f;
        dashChargesText.text = $"<b>{_controller.currentState.currentCharges}</b>";
    }

    private void UpdateEnergy()
    {
        energyImage.fillAmount = _stats.UIEnergy < _stats.MaxEnergy
            ? _stats.UIEnergy / _stats.MaxEnergy
            : 1f;
        energyImage.color = _stats.UIIsSurging || _stats.UIEnergy < _stats.SurgeActivationThreshhold ? Color.red : Color.yellow;
        energyText.text = $"<b>{(int)_stats.UIEnergy}</b>";
    }

    private void UpdateStats()
    {
        _statsSB.Clear();
        _statsSB.AppendLine($" {_stats.WeaponDamage:G4}");
        _statsSB.AppendLine($" {_stats.AttackSpeed:G4}");
        _statsSB.AppendLine($" {_stats.EnergyRegen:G4}");
        _statsSB.AppendLine($" {_stats.WeaponRange * 5f:G4}");
        _statsSB.AppendLine($" {_stats.MoveSpeed * 10f:G4}");
        _statsSB.AppendLine($" {_stats.dashHaste:G4}");
        _statsSB.AppendLine($" {Mathf.Min(_stats.CritChance * 100f, 100f):G4}%");
        _statsSB.AppendLine($" {_stats.CritDamage * 100f:G4}%");
        _statsSB.AppendLine($" {_stats.Luck:G4}");
        _statsSB.AppendLine($" {_stats.OnHitPower * 100f:G4}%");
        statsNumbersText.text = _statsSB.ToString();
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
        statsNumbersTextExpanded.text = _statsExpandedSB.ToString();
    }

    private void UpdateSP()
    {
        spBar.value = _stats.UISP / _stats.MaxSP;
        spText.text = _stats.UISP >= _stats.MaxSP ? "<b>BURST</b>" : $"<b>{(int)_stats.UISP}/{(int)_stats.MaxSP}</b>";
    }

    private void UpdateXP()
    {
        if (GameManager.instance == null) return;
        xpBar.value = GameManager.instance.CurrentXP / GameManager.instance.XPToNextLevel;
        xpText.text = $"<b>Level {GameManager.instance.Level} | {FormatStat(GameManager.instance.CurrentXP, 1)}/{FormatStat(GameManager.instance.XPToNextLevel, 1)} XP</b>";
    }

    private void UpdateSessionPoints()
    {
        if (sessionPointsText == null || GameManager.instance == null) return;
        sessionPointsText.text = $"<b>{GameManager.instance.SessionPoints} pts</b>";
    }

    private void UpdateRunClock()
    {
        float runtime = Time.timeSinceLevelLoad;
        int minutes = (int)(runtime / 60f);
        int seconds = (int)(runtime % 60f);
        timerText.text = $"<b>{minutes:00}:{seconds:00}</b>";
    }

    private void UpdateBossUI()
    {
        if (EnemySpawner.instance == null) return;

        var boss = BossBase.Current;
        bool bossAlive = boss != null && !boss._isDead;

        bossUIContainer.SetActive(bossAlive);
        bossTimerText.gameObject.SetActive(!bossAlive);

        if (bossAlive)
        {
            bossHealthBar.value = boss.maxHealth > 0f ? boss.CurrentHealth / boss.maxHealth : 0f;
            if (bossEnrageBar != null) bossEnrageBar.value = boss.EnrageProgress;
            bossHealthText.text = $"<b>{FormatStat(boss.CurrentHealth)}/{FormatStat(boss.maxHealth)}</b>";
            bossNameText.text = boss.enemyName;
        }
        else
        {
            int bossSeconds = (int)EnemySpawner.instance.CycleTimer;
            bossTimerText.text = $"<b>Next Boss: {bossSeconds / 60:00}:{bossSeconds % 60:00}</b>";
        }
    }
}
