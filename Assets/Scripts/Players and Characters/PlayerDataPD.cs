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

    public void Dispose() { }

    public bool Equals(PlayerShellInput other) =>
        ready == other.ready &&
        characterIndex == other.characterIndex &&
        colorIndex == other.colorIndex &&
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
    protected override void GetFinalInput(ref PlayerShellInput input)
    {
        input.ready = true;
        input.characterIndex = GameStartData.LocalCharacterIndex;
        input.colorIndex = GameStartData.LocalColorIndex;
        input.bonuses = CharacterStatsPD.ComputeProgressionBonuses();
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
    private bool _colorApplied;
    private bool _bonusesApplied;
    private bool _allReady;

    protected override void UpdateView(PlayerShellState viewState, PlayerShellState? verified)
    {
        if (!owner.HasValue) return;
        GameStartData.PlayerCharacterIndices[owner.Value] = viewState.characterIndex;
        GameStartData.PlayerColorIndices[owner.Value] = viewState.colorIndex;

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

        if ((!_bonusesApplied && isOwner) || (!_colorApplied))
        {
            if (character.TryGetComponent(out CharacterStatsPD stats))
            {
                if (!_bonusesApplied && isOwner)
                {
                    stats.ApplyProgressionBonuses(CharacterStatsPD.ComputeProgressionBonuses());
                    _bonusesApplied = true;
                }

                if (!_colorApplied)
                {
                    int colorIdx = isOwner ? GameStartData.LocalColorIndex
                                 : verified.HasValue ? verified.Value.colorIndex
                                 : -1;
                    if (colorIdx >= 0)
                    {
                        stats.ApplyColor(colorIdx);
                        _colorApplied = true;
                    }
                }
            }
        }

        if (!_allReady && _colorApplied && (!isOwner || _bonusesApplied))
        {
            _allReady = true;
            if (owner.HasValue && character.TryGetComponent(out CharacterStatsPD readyStats))
            {
                CharacterStatsPD.All[owner.Value] = readyStats;
                if (isOwner && character.TryGetComponent(out PlayerControllerPD ctrl))
                    ctrl.EnableInput();
            }
        }
    }

    protected override void Destroyed()
    {
        if (owner.HasValue)
        {
            GameStartData.PlayerCharacterIndices.Remove(owner.Value);
            GameStartData.PlayerColorIndices.Remove(owner.Value);
        }
    }
}
