using System.Collections.Generic;
using PurrNet;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

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
    [SerializeField] private TextMeshProUGUI statsNumbersText;
    [SerializeField] private Slider xpBar;
    [SerializeField] private TextMeshProUGUI xpText;
    [SerializeField] private TextMeshProUGUI timerText;
    [SerializeField] private TextMeshProUGUI bossTimerText;

    [Header("Level Up Panel")]
    [SerializeField] private GameObject levelUpScreen;
    [SerializeField] private TextMeshProUGUI levelUpTimerText;
    [SerializeField] private AugmentCardUI cardPrefab;
    [SerializeField] private Transform cardContainer;
    private List<AugmentCardUI> _cardPool = new();

    [Header("Pause System")]
    [SerializeField] private GameObject pauseCountdownOverlay;
    [SerializeField] private TextMeshProUGUI pauseCountdownText;
    [SerializeField] private GameObject pauseScreen;
    [SerializeField] private GameObject singlePlayerResumePanel;
    [SerializeField] private GameObject multiplayerPausePanel;
    [SerializeField] private TextMeshProUGUI pauseReadyCountText;
    [SerializeField] private TextMeshProUGUI pauseFallbackTimerText;
    [SerializeField] private TextMeshProUGUI pauseReadyButtonText;
    private bool _isReadyForUnpause;

    private readonly System.Text.StringBuilder _statsSB = new();

    private CharacterStats _stats;
    private PlayerController _controller;

    private void Awake()
    {
        instance = this;
        levelUpScreen.SetActive(false);
        pauseCountdownOverlay.SetActive(false);
        pauseScreen.SetActive(false);
    }

    public void SetPlayer(CharacterStats stats)
    {
        _stats = stats;
        _controller = stats.GetComponent<PlayerController>();
        healthBar.value = 1f;
        shieldBar.value = 1f;
        shieldRegenBar.value = 1f;
        dashChargeCooldownImage.fillAmount = 1f;
        energyImage.fillAmount = 1f;
    }

    public void SetUpLevelUpScreen(List<StatAugmentPair> options, PlayerID playerID)
    {
        levelUpTimerText.text = NetworkManager.main.playerCount > 1 ? "Time Remaining: 30s" : "";
        HideAllCards();
        for (int i = 0; i < options.Count; i++)
            GetCard(i).StatAugmentSetup(options[i], i, playerID);
        GameManager.instance.ReadyToLevelRpc(playerID);
    }

    public void ShowLevelUpScreen()
    {
        levelUpScreen.SetActive(true);
    }

    public void HideLevelUpScreen()
    {
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
        pauseCountdownOverlay.SetActive(true);
        pauseCountdownText.text = seconds.ToString();
    }

    public void UpdatePauseCountdown(int seconds)
    {
        if (seconds <= 0) { pauseCountdownOverlay.SetActive(false); return; }
        pauseCountdownText.text = seconds.ToString();
    }

    public void ShowPauseScreen(bool isSinglePlayer)
    {
        pauseCountdownOverlay.SetActive(false);
        _isReadyForUnpause = false;
        if (pauseReadyButtonText != null) pauseReadyButtonText.text = "Ready Up";
        if (pauseReadyCountText != null) pauseReadyCountText.text = $"0/{NetworkManager.main.playerCount} Ready";
        if (pauseFallbackTimerText != null) pauseFallbackTimerText.gameObject.SetActive(false);
        singlePlayerResumePanel.SetActive(isSinglePlayer);
        multiplayerPausePanel.SetActive(!isSinglePlayer);
        pauseScreen.SetActive(true);
    }

    public void HidePauseScreen()
    {
        _isReadyForUnpause = false;
        pauseScreen.SetActive(false);
        pauseCountdownOverlay.SetActive(false);
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

    public void OnResumeClicked()
    {
        GameManager.instance.RequestPauseRpc();
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

        // Health and Shields
        healthBar.value = _stats.CurrentHealth / _stats.MaxHealth;
        shieldBar.value = _stats.CurrentShield / _stats.MaxShield;
        shieldRegenBar.value = (_stats.ShieldRegenTime - _stats._shieldRechargeTimer) / _stats.ShieldRegenTime;
        healthText.text = $"<b><color=#FF00FF>{(int)_stats.CurrentShield}/{(int)_stats.MaxShield}</color> {(int)_stats.CurrentHealth}/{(int)_stats.MaxHealth}</b>";

        // Health Regen
        healthRegenText.text = $"<b>+{_stats.HealthRegen:G4}/s</b>";

        // Dash
        if (_controller._currentCharges < _stats.dashCharges)
            dashChargeCooldownImage.fillAmount = 1f - (_controller._chargeRegenTimer / _stats.DashCooldown);
        else
            dashChargeCooldownImage.fillAmount = 1f;
        dashChargesText.text = $"<b>{_controller._currentCharges}</b>";

        // Energy / Surge
        if (_stats.CurrentEnergy < _stats.MaxEnergy)
            energyImage.fillAmount = _stats.CurrentEnergy / _stats.MaxEnergy;
        else
            energyImage.fillAmount = 1f;
        if (_stats.CurrentEnergy < _stats.SurgeActivationThreshhold)
            energyImage.color = Color.red;
        else
            energyImage.color = Color.yellow;
        energyText.text = $"<b>{(int)_stats.CurrentEnergy}</b>";

        // Stats
        _statsSB.Clear();
        _statsSB.AppendLine($" {_stats.WeaponDamage:G4}");
        _statsSB.AppendLine($" {_stats.AttackSpeed:G4}");
        _statsSB.AppendLine($" {_stats.EnergyRegen:G4}");
        _statsSB.AppendLine($" {_stats.WeaponRange * 5f:G4}");
        _statsSB.AppendLine($" {_stats.MoveSpeed * 10f:G4}");
        _statsSB.AppendLine($" {_stats.dashHaste:G4}");
        _statsSB.AppendLine($" {_stats.CritChance * 100f:G4}%");
        _statsSB.AppendLine($" {_stats.CritDamage * 100f:G4}%");
        statsNumbersText.text = _statsSB.ToString();

        // XP
        if (GameManager.instance != null)
        {
            xpBar.value = GameManager.instance.CurrentXP / GameManager.instance.XPToNextLevel;
            xpText.text = $"<b>Level {GameManager.instance.Level} | {GameManager.instance.CurrentXP:G4}/{GameManager.instance.XPToNextLevel:G4} XP</b>";
        }

        // Run Clock
        float runtime = Time.timeSinceLevelLoad;
        int minutes = (int)(runtime / 60f);
        int seconds = (int)(runtime % 60f);
        timerText.text = $"<b>{minutes:00}:{seconds:00}</b>";

        // Boss Countdown
        if (EnemySpawner.instance != null)
        {
            int bossSeconds = (int)EnemySpawner.instance.CycleTimer;
            int bossMinutes = bossSeconds / 60;
            int bossSecondsRemainder = bossSeconds % 60;
            bossTimerText.text = $"<b>Next Boss: {bossMinutes:00}:{bossSecondsRemainder:00}</b>";
        }
    }
}