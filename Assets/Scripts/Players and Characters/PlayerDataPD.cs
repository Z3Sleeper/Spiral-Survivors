using System;
using PurrNet;
using PurrNet.Prediction;
using UnityEngine;

public struct PlayerShellInput : IPredictedData, IEquatable<PlayerShellInput>
{
    public bool ready;
    public int characterIndex;
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
        setupComplete == other.setupComplete &&
        bonuses.Equals(other.bonuses);
}

public struct PlayerShellState : IPredictedData<PlayerShellState>
{
    public PredictedObjectID characterId;
    public bool spawned;
    public int characterIndex;

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
        input.bonuses = CharacterStatsPD.ComputeProgressionBonuses();
        input.setupComplete = _localSetupComplete;
    }

    protected override void SanitizeInput(ref PlayerShellInput input)
    {
        int count = CharacterRegistry.instance != null ? CharacterRegistry.instance.PrefabCount() : 1;
        input.characterIndex = Mathf.Clamp(input.characterIndex, 0, count - 1);
    }

    protected override void Simulate(PlayerShellInput input, ref PlayerShellState state, float dt)
    {
        if (!input.ready) return;

        // Deterministic and synced (unlike the old CharacterStatsPD.All.Count poll), so every machine
        // agrees this player is ready on the exact same tick. Idempotent on GameManagerPD's side, so
        // calling this every tick once setupComplete latches true is fine.
        if (input.setupComplete && owner.HasValue && GameManagerPD.instance != null)
            GameManagerPD.instance.MarkPlayerReady(owner.Value);

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
    private bool _bonusesApplied;
    private bool _registeredInAll;
    private bool _classPassivesApplied;
    private bool _statTotalsApplied;
    private bool _allReady;
    private bool _inputEnabled;

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

            // Color is no longer anything PlayerDataPD tracks at all — CharacterStatsPD now resolves and
            // applies its own color entirely server-side (see CharacterStatsPD.Simulate's color-resolution
            // block and its self-subscribed _onColorChanged listener), with no client input/state
            // involvement and no per-frame poll here. See GameStartData.HostColorAssignments' own doc
            // comment for why that's the actual fix for the client-trusted-color race this used to have.
            //
            // Registered in CharacterStatsPD.All as soon as bonuses land — deliberately BEFORE class
            // passives below, so an effect-prefab-based passive (spawned by ApplyClassPassives via
            // hierarchy.Create()) can resolve its own owner's Stats the same way every other augment effect
            // does (AugmentEffectPD.LateAwake() reads CharacterStatsPD.All directly). This used to happen
            // after class passives, in the old ready-gate below — meaning an effect-prefab passive's
            // hierarchy.Create() call could fire before this player had an All entry yet, so its
            // LateAwake() would find nothing and silently never apply. No longer waits on color specifically
            // (that was only ever a coincidental proxy from when this method also resolved color itself,
            // not a real dependency — nothing downstream of All[owner]=stats cares whether color has been
            // painted onto the model yet).
            if (!_registeredInAll && owner.HasValue && (!isOwner || _bonusesApplied))
            {
                CharacterStatsPD.All[owner.Value] = stats;
                CharacterStatsPD.RefreshAnyValidTargetExists();
                _registeredInAll = true;
            }

            // Not owner-gated, unlike progression bonuses above — classPassive is a shared asset reference
            // baked into the prefab, not per-machine save data, so every observer applies the same thing.
            if (_registeredInAll && !_classPassivesApplied)
            {
                stats.ApplyClassPassives();
                _classPassivesApplied = true;
            }

            // Reapplies whatever stat-augment totals AugmentManagerPD has on record for this player (see
            // CharacterStatsPD.ApplyStatBoostTotals' own doc comment) — all-zero and a no-op on a normal
            // game start, restores prior gains for a reconnecting player. Gated on _classPassivesApplied
            // purely to run right after it (order doesn't actually matter, these are all plain += fields),
            // not because it depends on anything class-passive-specific.
            if (_classPassivesApplied && !_statTotalsApplied && owner.HasValue && AugmentManagerPD.instance != null)
            {
                stats.ApplyStatBoostTotals(AugmentManagerPD.instance.GetStatTotals(owner.Value));
                _statTotalsApplied = true;
            }

            // _classPassivesApplied can only become true after _registeredInAll (and everything that gates
            // it) already did. Also waits on ClassPassiveAugmentsReady — ApplyClassPassives() above only
            // covers ClassPassiveData's plain stat fields; the passiveAugments[] escape hatch (unique-effect
            // augments like a class-passive stat bonus granted via its own OnApply()) resolves separately
            // and asynchronously through AugmentManagerPD, and defaults to "not ready" (-1 sentinel) until
            // that system has actually registered a real count — see ClassPassiveAugmentsReady's own doc
            // comment. Without this, the loading screen could drop a frame or two before those stats are
            // actually live, producing a visible "stat pops in after load" flash.
            if (!_allReady && _classPassivesApplied && stats.ClassPassiveAugmentsReady)
            {
                _allReady = true;
                _localSetupComplete = true;
            }

            // Deliberately NOT nested inside the one-shot block above, and re-checked every frame instead
            // of latched alongside it — isOwner can resolve true on a LATER frame than the tick _allReady
            // first latches (e.g. a marginal connection whose own login handshake hasn't finished yet, so
            // this client still reads itself as "not owner" at that exact moment and takes the same
            // registration path a remote viewer would). EnableInput() living inside that one-time gate
            // would otherwise permanently skip it once _allReady already latched via the non-owner path —
            // this is what actually "waits until the client is connected" for input specifically, instead
            // of only getting one chance tied to an unrelated transition.
            if (_allReady && !_inputEnabled && isOwner && character.TryGetComponent(out PlayerControllerPD ctrl))
            {
                ctrl.EnableInput();
                _inputEnabled = true;
            }
        }
    }

    protected override void Destroyed()
    {
        if (owner.HasValue)
        {
            // CharacterStatsPD.All never had a removal path anywhere in the codebase — every other reader
            // of this dictionary (FindClosestTarget, AwardXP, ReviveZonePD, AugmentManagerPD, etc.) trusted it to
            // only ever contain live players. Same risk category as the enemy Active*Count drift: no
            // domain reload means this never resets, and unlike a plain counter, a stale entry here is a
            // dangling Component reference other code might dereference mid-Simulate().
            CharacterStatsPD.All.Remove(owner.Value);
            CharacterStatsPD.RefreshAnyValidTargetExists();
        }
    }
}
