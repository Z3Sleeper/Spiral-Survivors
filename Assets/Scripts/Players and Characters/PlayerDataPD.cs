using System;
using PurrNet;
using PurrNet.Prediction;
using UnityEngine;

public struct PlayerShellInput : IPredictedData, IEquatable<PlayerShellInput>
{
    public bool ready;
    public int characterIndex;
    public int colorIndex;
    public CharacterStatsPD.ProgressionBonuses bonuses;
    // Latches true once this player's own client confirms (in UpdateView(), off the same _allReady
    // transition that already gates CharacterStatsPD.All population) that its local one-time setup
    // (color + progression bonuses) is done. Feeding this through synced Input rather than reading a
    // plain per-machine dictionary from Simulate() is what makes GameManagerPD's gameStarted gate safe
    // to evaluate deterministically — see GameManagerPD.State.readyPlayers.
    public bool setupComplete;

    public void Dispose() { }

    public bool Equals(PlayerShellInput other) =>
        ready == other.ready &&
        characterIndex == other.characterIndex &&
        colorIndex == other.colorIndex &&
        setupComplete == other.setupComplete &&
        bonuses.Equals(other.bonuses);
}

public struct PlayerShellState : IPredictedData<PlayerShellState>
{
    public PredictedObjectID characterId;
    public bool spawned;
    public int characterIndex;
    public int colorIndex;

    public void Dispose() { }
}

public class PlayerDataPD : PredictedIdentity<PlayerShellInput, PlayerShellState>
{
    // Plain field, not State — set once (off a verified-side transition in UpdateView(), never
    // Simulate()) and only ever read by this same machine's own GetFinalInput() call, same pattern as
    // PlayerControllerPD's _pendingReadyPressed etc.
    private bool _localSetupComplete;

    protected override void GetFinalInput(ref PlayerShellInput input)
    {
        input.ready = true;
        input.characterIndex = GameStartData.LocalCharacterIndex;
        input.colorIndex = GameStartData.LocalColorIndex;
        input.bonuses = CharacterStatsPD.ComputeProgressionBonuses();
        input.setupComplete = _localSetupComplete;
    }

    protected override void SanitizeInput(ref PlayerShellInput input)
    {
        int count = CharacterRegistry.instance != null ? CharacterRegistry.instance.PrefabCount() : 1;
        input.characterIndex = Mathf.Clamp(input.characterIndex, 0, count - 1);
        input.colorIndex = Mathf.Max(0, input.colorIndex);
    }

    protected override void Simulate(PlayerShellInput input, ref PlayerShellState state, float dt)
    {
        if (!input.ready) return;

        // Deterministic and synced (unlike the old CharacterStatsPD.All.Count poll), so every machine
        // agrees this player is ready on the exact same tick. Idempotent on GameManagerPD's side, so
        // calling this every tick once setupComplete latches true is fine.
        if (input.setupComplete && owner.HasValue && GameManagerPD.instance != null)
            GameManagerPD.instance.MarkPlayerReady(owner.Value);

        state.colorIndex = input.colorIndex;

        if (state.spawned) return;

        if (CharacterRegistry.instance == null) return;
        var prefab = CharacterRegistry.instance.GetPredictedPrefab(input.characterIndex);
        if (prefab == null) return;

        var id = predictionManager.hierarchy.Create(prefab, transform.position, transform.rotation, owner);
        if (id.HasValue)
        {
            state.characterId = id.Value;
            state.characterIndex = input.characterIndex;
            state.spawned = true;

            if (!isOwner)
            {
                var character = predictionManager.hierarchy.GetGameObject(id.Value);
                if (character != null && character.TryGetComponent(out CharacterStatsPD stats))
                    stats.ApplyProgressionBonuses(input.bonuses);
            }
        }
    }

    private bool _cameraSet;
    private int _appliedColorIndex = -1;
    private bool _bonusesApplied;
    private bool _registeredInAll;
    private bool _classPassivesApplied;
    private bool _allReady;

    protected override void UpdateView(PlayerShellState viewState, PlayerShellState? verified)
    {
        if (!owner.HasValue) return;

        if (!viewState.spawned) return;

        var character = predictionManager.hierarchy.GetGameObject(viewState.characterId);
        if (character == null) return;

        if (!_cameraSet && isOwner)
        {
            if (Camera.main.TryGetComponent(out CameraController cam))
            {
                var ctrl = character.GetComponent<PlayerControllerPD>();
                cam.SetTarget(ctrl != null ? ctrl.CameraTarget : character.transform);
                _cameraSet = true;
            }
        }

        if (character.TryGetComponent(out CharacterStatsPD stats))
        {
            if (!_bonusesApplied && isOwner)
            {
                stats.ApplyProgressionBonuses(CharacterStatsPD.ComputeProgressionBonuses());
                _bonusesApplied = true;
            }

            // Checked every frame (not latched after the first success) because on non-owners,
            // verified.colorIndex can briefly read as its zero-default (0 is also a legitimate palette
            // index, so it can't be told apart from "not synced yet") before the real synced value lands.
            // Comparing against the last applied index means the (mildly expensive) material swap only
            // runs on an actual change, but a bad early read still self-corrects the moment the real value
            // arrives instead of sticking for the whole session.
            int colorIdx = isOwner ? GameStartData.LocalColorIndex
                         : verified.HasValue ? verified.Value.colorIndex
                         : -1;
            if (colorIdx >= 0 && colorIdx != _appliedColorIndex)
            {
                stats.ApplyColor(colorIdx);
                _appliedColorIndex = colorIdx;
            }

            // Registered in CharacterStatsPD.All as soon as color+bonuses land — deliberately BEFORE class
            // passives below, so an effect-prefab-based passive (spawned by ApplyClassPassives via
            // hierarchy.Create()) can resolve its own owner's Stats the same way every other augment effect
            // does (AugmentEffectPD.LateAwake() reads CharacterStatsPD.All directly). This used to happen
            // after class passives, in the old ready-gate below — meaning an effect-prefab passive's
            // hierarchy.Create() call could fire before this player had an All entry yet, so its
            // LateAwake() would find nothing and silently never apply.
            if (!_registeredInAll && owner.HasValue && _appliedColorIndex >= 0 && (!isOwner || _bonusesApplied))
            {
                CharacterStatsPD.All[owner.Value] = stats;
                _registeredInAll = true;
            }

            // Not owner-gated, unlike progression bonuses above — classPassive is a shared asset reference
            // baked into the prefab, not per-machine save data, so every observer applies the same thing.
            if (_registeredInAll && !_classPassivesApplied)
            {
                stats.ApplyClassPassives();
                _classPassivesApplied = true;
            }
        }

        // _classPassivesApplied can only become true after _registeredInAll (and everything that gates it)
        // already did, so it alone is sufficient here — no need to re-check those conditions.
        if (!_allReady && _classPassivesApplied)
        {
            _allReady = true;
            _localSetupComplete = true;
            if (isOwner && character.TryGetComponent(out PlayerControllerPD ctrl))
                ctrl.EnableInput();
        }
    }

    protected override void Destroyed()
    {
        if (owner.HasValue)
        {
            // CharacterStatsPD.All never had a removal path anywhere in the codebase — every other reader
            // of this dictionary (FindTarget, AwardXP, ReviveZonePD, AugmentManagerPD, etc.) trusted it to
            // only ever contain live players. Same risk category as the enemy Active*Count drift: no
            // domain reload means this never resets, and unlike a plain counter, a stale entry here is a
            // dangling Component reference other code might dereference mid-Simulate().
            CharacterStatsPD.All.Remove(owner.Value);
        }
    }
}
