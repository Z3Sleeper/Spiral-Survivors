using System.Collections.Generic;
using PurrNet;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using Unity.VisualScripting.Antlr3.Runtime.Misc;

public class LocalUIManager : MonoBehaviour
{
    public static LocalUIManager instance;

    [SerializeField] private Slider healthBar;
    [SerializeField] private Slider shieldBar;
    [SerializeField] private Slider shieldRegenBar;
    [SerializeField] private TextMeshProUGUI healthText;
    [SerializeField] private TextMeshProUGUI healthRegenText;
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
    [SerializeField] private TextMeshProUGUI bossHealthText;
    [SerializeField] private TextMeshProUGUI bossNameText;

    [Header("Level Up Panel")]
    [SerializeField] private GameObject levelUpScreen;
    [SerializeField] private TextMeshProUGUI levelUpTimerText;
    [SerializeField] private AugmentCardUI cardPrefab;
    [SerializeField] private Transform cardContainer;
    private List<AugmentCardUI> _cardPool = new();

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
    private bool _isReadyForUnpause;

    private readonly System.Text.StringBuilder _statsSB = new();
    private readonly System.Text.StringBuilder _statsExpandedSB = new();

    private CharacterStats _stats;
    private PlayerController _controller;
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
    }

    public void SetStatsExpanded(bool expanded)
    {
        statsUI.SetActive(!expanded);
        statsUIExpanded.SetActive(expanded);
    }

    public void SetPlayer(CharacterStats stats)
    {
        _stats = stats;
        _controller = stats.GetComponent<PlayerController>();
        _isRanged = stats.GetComponent<WeaponManager>().IsRanged;
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
        _isReadyForUnpause = false;
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
        _isReadyForUnpause = false;
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
        _isReadyForUnpause = !_isReadyForUnpause;
        if (pauseReadyButtonText != null)
            pauseReadyButtonText.text = _isReadyForUnpause ? "Unready" : "Ready Up";
        GameManager.instance.TogglePauseReadyRpc(NetworkManager.main.localPlayer);
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
        GameManager.instance.RequestPauseRpc();
    }

    public void OnReturnToLobbyClicked()
    {
        GameManager.instance.ReturnToLobbyFromPauseRpc();
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

    private static string FormatStat(float value) => value switch
    {
        >= 1_000_000_000_000f => $"{value / 1_000_000_000_000f:F2}tril",
        >= 1_000_000_000f     => $"{value / 1_000_000_000f:F2}bil",
        >= 1_000_000f         => $"{value / 1_000_000f:F2}mil",
        >= 1_000f             => $"{value / 1_000f:F2}k",
        _                     => $"{value:G3}"
    };

    private void UpdateHealthAndShields()
    {
        healthBar.value = _stats.CurrentHealth / _stats.MaxHealth;
        shieldBar.value = _stats.CurrentShield / _stats.MaxShield;
        shieldRegenBar.value = (_stats.ShieldRegenTime - _stats._shieldRechargeTimer) / _stats.ShieldRegenTime;
        healthText.text = $"<b><color=#FF00FF>{FormatStat(_stats.CurrentShield)}/{FormatStat(_stats.MaxShield)}</color> {FormatStat(_stats.CurrentHealth)}/{FormatStat(_stats.MaxHealth)}</b>";
    }

    private void UpdateHealthRegen()
    {
        healthRegenText.text = $"<b>+{FormatStat(_stats.HealthRegen)}/s</b>";
    }

    private void UpdateDash()
    {
        dashChargeCooldownImage.fillAmount = _controller._currentCharges < _stats.dashCharges
            ? 1f - (_controller._chargeRegenTimer / _stats.DashCooldown)
            : 1f;
        dashChargesText.text = $"<b>{_controller._currentCharges}</b>";
    }

    private void UpdateEnergy()
    {
        energyImage.fillAmount = _stats.CurrentEnergy < _stats.MaxEnergy
            ? _stats.CurrentEnergy / _stats.MaxEnergy
            : 1f;
        energyImage.color = (_controller != null && _controller.isSurging) || _stats.CurrentEnergy < _stats.SurgeActivationThreshhold ? Color.red : Color.yellow;
        energyText.text = $"<b>{(int)_stats.CurrentEnergy}</b>";
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
        _statsSB.AppendLine($" {_stats.OnHitEffectiveness * 100f:G4}%");
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
        spBar.value = _stats.CurrentSP / _stats.MaxSP;
        spText.text = _stats.CurrentSP >= _stats.MaxSP ? "<b>BURST</b>" : $"<b>{(int)_stats.CurrentSP}/{(int)_stats.MaxSP}</b>";
    }

    private void UpdateXP()
    {
        if (GameManager.instance == null) return;
        xpBar.value = GameManager.instance.CurrentXP / GameManager.instance.XPToNextLevel;
        xpText.text = $"<b>Level {GameManager.instance.Level} | {FormatStat(GameManager.instance.CurrentXP)}/{FormatStat(GameManager.instance.XPToNextLevel)} XP</b>";
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
            bossHealthBar.value = boss.maxHealth > 0f
                ? boss.CurrentHealth / boss.maxHealth
                : 0f;
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