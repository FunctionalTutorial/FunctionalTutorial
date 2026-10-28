using System.Collections.Generic;
using System.Linq;
using Content.Shared._Functional.TutorialServer;
using Content.Shared.Chemistry.Reagent;
using Content.Shared.Decals;
using Content.Shared.FixedPoint;
using Content.Shared.Fluids.Components;
using Content.Shared.Light.Components;
using Content.Shared.Light.EntitySystems;
using Content.Shared.Storage.EntitySystems;
using Content.Shared.Tag;
using Robust.Shared.GameStates;
using Robust.Shared.Prototypes;

namespace Content.Server._Functional.TutorialServer;

public sealed partial class TutorialGoalSensorSystem
{
    [Dependency] private EntityProviderSystem _providers = default!;
    [Dependency] private SharedPoweredLightSystem _poweredLights = default!;

    /// <summary>
    /// Cleanable-decal count when a <see cref="TutorialStepComplete.CleanableDecalsReduced"/> beat
    /// started, so "you sprayed one" can be told from "the room was already blank".
    /// </summary>
    private readonly Dictionary<EntityUid, int> _decalBaseline = new();

    /// <summary>Maps whose respawn spill has already been placed for a given sub-goal.</summary>
    private readonly HashSet<(EntityUid Map, string SubGoal)> _respawnReady = new();

    /// <summary>Players who have already been considered for a beat's spare spray bottle.</summary>
    private readonly HashSet<(EntityUid Player, string SubGoal)> _sprayGiven = new();

    /// <summary>The same small dose as the spill they already mopped.</summary>
    private static readonly FixedPoint2 PotassiumDose = FixedPoint2.New(5);

    private const string PuddleSolution = "puddle";

    /// <summary>
    /// True when a tagged entity on this map has a solution inside the beat's bounds.
    /// A reagent check looks for that reagent. A max-only check requires every solution on the
    /// entity to be at or under the cap, so an empty side pocket cannot pass a full tank.
    /// </summary>
    internal bool TryMatchTaggedSolution(EntityUid? mapUid, TutorialSubGoalData sub)
    {
        if (mapUid == null || string.IsNullOrEmpty(sub.Tag))
            return false;

        if (sub.Reagent == null && sub.MaxAmount == null)
            return false;

        var tag = (ProtoId<TagPrototype>) sub.Tag;
        var query = EntityQueryEnumerator<TransformComponent>();
        while (query.MoveNext(out var uid, out var xform))
        {
            if (xform.MapUid != mapUid || TerminatingOrDeleted(uid) || !_tags.HasTag(uid, tag))
                continue;

            if (SolutionMatches(uid, sub))
                return true;
        }

        return false;
    }

    private bool SolutionMatches(EntityUid uid, TutorialSubGoalData sub)
    {
        if (sub.Reagent is { } reagent)
        {
            foreach (var (_, soln) in _solutions.EnumerateSolutions(uid))
            {
                var qty = soln.Comp.Solution.GetTotalPrototypeQuantity(reagent);
                if (qty < sub.MinAmount)
                    continue;

                if (sub.MaxAmount is { } max && qty > max)
                    continue;

                return true;
            }

            return false;
        }

        if (sub.MaxAmount is not { } maxVol)
            return false;

        var any = false;
        foreach (var (_, soln) in _solutions.EnumerateSolutions(uid))
        {
            any = true;
            if (soln.Comp.Solution.Volume > maxVol)
                return false;
        }

        return any;
    }

    /// <summary>True when a tagged light replacer is holding at least one bulb or tube.</summary>
    internal bool IsLightReplacerLoaded(EntityUid? mapUid, string? tag)
    {
        if (mapUid == null || string.IsNullOrEmpty(tag))
            return false;

        var tagId = (ProtoId<TagPrototype>) tag;
        var query = EntityQueryEnumerator<LightReplacerComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out _, out var xform))
        {
            if (xform.MapUid != mapUid || !_tags.HasTag(uid, tagId))
                continue;

            if (_providers.TryGetEntityCounter(uid, out var counter) && counter.Values.Sum() > 0)
                return true;

            if (_containers.TryGetContainer(uid, "entity-provider", out var container) &&
                container.ContainedEntities.Count > 0)
                return true;
        }

        return false;
    }

    /// <summary>True when a tagged fixture is holding a bulb that is not broken or burned out.</summary>
    internal bool IsLightFixed(EntityUid? mapUid, string? tag)
    {
        if (mapUid == null || string.IsNullOrEmpty(tag))
            return false;

        var tagId = (ProtoId<TagPrototype>) tag;
        var query = EntityQueryEnumerator<PoweredLightComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var light, out var xform))
        {
            if (xform.MapUid != mapUid || !_tags.HasTag(uid, tagId))
                continue;

            if (_poweredLights.GetBulb(uid, light) is not { } bulb)
                continue;

            if (TryComp<LightBulbComponent>(bulb, out var bulbComp) && bulbComp.State == LightBulbState.Normal)
                return true;
        }

        return false;
    }

    /// <summary>
    /// True once the player has removed at least one cleanable decal since this beat started,
    /// or the grid was already clean.
    /// </summary>
    internal bool AreCleanableDecalsReduced(EntityUid player, EntityUid? mapUid)
    {
        if (mapUid == null)
            return false;

        var count = CountCleanableDecals(mapUid.Value);
        if (!_decalBaseline.TryGetValue(player, out var baseline))
        {
            baseline = count;
            _decalBaseline[player] = baseline;
        }

        return count == 0 || count < baseline;
    }

    /// <summary>True when the map has no cleanable floor decals left.</summary>
    internal bool AreCleanableDecalsCleared(EntityUid? mapUid)
    {
        return mapUid != null && CountCleanableDecals(mapUid.Value) == 0;
    }

    internal int CountCleanableDecals(EntityUid mapUid)
    {
        // Decal chunks are unparented bookkeeping entities. Their transform has no map, so the
        // chunk's root grid is what ties a decal to the tutorial.
        var count = 0;
        var query = EntityQueryEnumerator<DecalChunkComponent, ChunkEntityComponent>();
        while (query.MoveNext(out _, out var chunk, out var chunkEnt))
        {
            if (!TryComp<TransformComponent>(chunkEnt.Root, out var rootXform) || rootXform.MapUid != mapUid)
                continue;

            foreach (var decal in chunk.Decals.Values)
            {
                if (decal.Cleanable)
                    count++;
            }
        }

        return count;
    }

    /// <summary>
    /// True when a tagged entity is still on the map and none of them contain the reagent.
    /// No entity at all is not a clean: the spill has to be sprayed down to nothing.
    /// </summary>
    internal bool IsTaggedReagentCleared(EntityUid? mapUid, TutorialSubGoalData sub)
    {
        if (mapUid == null || string.IsNullOrEmpty(sub.Tag) || sub.Reagent == null)
            return false;

        var any = false;
        foreach (var uid in CollectTagged(mapUid.Value, sub.Tag))
        {
            any = true;
            if (_solutions.GetTotalPrototypeQuantity(uid, sub.Reagent.Value.Id) > FixedPoint2.Zero)
                return false;
        }

        return any;
    }

    /// <summary>
    /// Puts the spill back once when the beat starts, and again if the player destroys it.
    /// A carried spray that still holds <see cref="TutorialSubGoalData.ProvideReagent"/> is left alone.
    /// </summary>
    internal void EnsureTaggedRespawn(EntityUid player, EntityUid? mapUid, TutorialSubGoalData sub)
    {
        if (mapUid is not { } map || sub.Respawn is not { } proto || sub.Tag is not { Length: > 0 } tag || string.IsNullOrEmpty(sub.Id))
            return;

        var key = (map, sub.Id);
        if (!_respawnReady.Contains(key))
        {
            if (!TryPlaceSpill(map, sub, proto, tag))
                return;

            _respawnReady.Add(key);
        }
        else if (!IsTaggedReagentCleared(map, sub) && CollectTagged(map, tag).Count == 0)
        {
            TryPlaceSpill(map, sub, proto, tag);
        }

        if (sub.ProvideIfMissing is not { } gift || sub.ProvideReagent is not { } reagent)
            return;

        if (!_sprayGiven.Add((player, sub.Id)) || PlayerHasUsableSpray(player, reagent))
            return;

        var bottle = Spawn(gift, Transform(player).Coordinates);
        _hands.TryPickup(player, bottle);
    }

    /// <summary>
    /// Refills a tagged spill that survived the explosion. Spawns one at the marker when the tile is empty.
    /// </summary>
    private bool TryPlaceSpill(EntityUid map, TutorialSubGoalData sub, EntProtoId proto, string tag)
    {
        var existing = CollectTagged(map, tag);
        if (existing.Count > 0)
        {
            if (sub.Reagent is { } reagent)
                RefillSpill(existing[0], reagent);

            for (var i = 1; i < existing.Count; i++)
                QueueDel(existing[i]);

            return true;
        }

        if (string.IsNullOrEmpty(sub.Marker) || !TryFindMarker(map, sub.Marker, out var marker))
            return false;

        Spawn(proto, Transform(marker).Coordinates);
        return true;
    }

    private void RefillSpill(EntityUid puddle, ProtoId<ReagentPrototype> reagent)
    {
        if (!_solutions.TryGetSolution(puddle, PuddleSolution, out var soln))
            return;

        // The first dose already exploded. This one is for the spray, and must not react if they mop it.
        soln.Value.Comp.Solution.CanReact = false;
        _solutions.RemoveAllSolution(soln.Value);
        soln.Value.Comp.Solution.CanReact = false;
        _solutions.TryAddReagent(soln.Value, reagent.Id, PotassiumDose);
        soln.Value.Comp.Solution.CanReact = false;
        Dirty(soln.Value);
    }

    private bool PlayerHasUsableSpray(EntityUid player, ProtoId<ReagentPrototype> reagent)
    {
        foreach (var item in _inventory.GetHandOrInventoryEntities(player))
        {
            if (SprayHasReagent(item, reagent))
                return true;

            foreach (var container in _containers.GetAllContainers(item))
            {
                foreach (var contained in container.ContainedEntities)
                {
                    if (SprayHasReagent(contained, reagent))
                        return true;
                }
            }
        }

        return false;
    }

    private bool SprayHasReagent(EntityUid item, ProtoId<ReagentPrototype> reagent)
    {
        return HasComp<SprayComponent>(item) &&
               _solutions.GetTotalPrototypeQuantity(item, reagent.Id) > FixedPoint2.Zero;
    }

    private List<EntityUid> CollectTagged(EntityUid mapUid, string tag)
    {
        var tagId = (ProtoId<TagPrototype>) tag;
        var found = new List<EntityUid>();
        var query = EntityQueryEnumerator<TransformComponent>();
        while (query.MoveNext(out var uid, out var xform))
        {
            if (xform.MapUid != mapUid || TerminatingOrDeleted(uid) || !_tags.HasTag(uid, tagId))
                continue;

            found.Add(uid);
        }

        return found;
    }

    private bool TryFindMarker(EntityUid mapUid, string markerId, out EntityUid marker)
    {
        var query = EntityQueryEnumerator<TutorialStepMarkerComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var comp, out var xform))
        {
            if (xform.MapUid != mapUid || TerminatingOrDeleted(uid) || comp.MarkerId != markerId)
                continue;

            marker = uid;
            return true;
        }

        marker = default;
        return false;
    }
}
