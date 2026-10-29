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
using Robust.Shared.Containers;
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

    /// <summary>
    /// How many loose pieces a player's litter beat may leave on the floor. Taken once, when the
    /// beat is first seen, so picking litter up by hand before then lowers the bar instead of
    /// making the quota impossible.
    /// </summary>
    private readonly Dictionary<EntityUid, int> _litterSpare = new();

    internal const string JanitorDisposalTag = "TutorialJanitorDisposal";
    internal const string JanitorTrashBagTag = "TutorialJanitorTrashBag";
    internal const string JanitorLitterTag = "Trash";

    internal const string JanitorFlushBagHint = "tutorial-janitor-control-flush-bag";
    internal const string JanitorFlushUnitHint = "tutorial-janitor-control-flush-unit";
    internal const string JanitorFlushHandsHint = "tutorial-janitor-control-flush-hands";

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
    /// True when no tagged entity still contains <see cref="TutorialSubGoalData.Reagent"/>.
    /// No entity at all is not a clean, unless <see cref="TutorialSubGoalData.ClearedWhenAbsent"/>
    /// is set: the potassium spill is done once the explosion deletes it.
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

        // The spray beat has to see the spill. The mop beat is done when the explosion deletes it.
        return any || sub.ClearedWhenAbsent;
    }

    /// <summary>
    /// Remembers that the player used this beat's named tool on its target. The click still does
    /// whatever the tool does; the beat itself waits until the spill is gone.
    /// </summary>
    internal void NoteExpectedToolUse(EntityUid player, TutorialSubGoalData sub, EntityUid used, EntityUid target)
    {
        if (!sub.ClearedWhenAbsent ||
            sub.Complete != TutorialStepComplete.TaggedReagentCleared ||
            string.IsNullOrEmpty(sub.Id) ||
            string.IsNullOrEmpty(sub.UsedTag) ||
            string.IsNullOrEmpty(sub.Tag))
            return;

        if (!_tags.HasTag(target, (ProtoId<TagPrototype>) sub.Tag) ||
            !_tags.HasTag(used, (ProtoId<TagPrototype>) sub.UsedTag))
            return;

        var choice = EnsureComp<TutorialToolChoiceComponent>(player);
        choice.Expected.Add(sub.Id);
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

            // An entity with no container manager makes GetAllContainers return a default
            // enumerator, and the first MoveNext throws. The mop is still in hand here.
            if (!TryComp<ContainerManagerComponent>(item, out var manager))
                continue;

            foreach (var container in _containers.GetAllContainers(item, manager))
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

    /// <summary>
    /// True once loose litter is down to the spare this beat started with.
    /// A full pile still means "clear <see cref="TutorialSubGoalData.MinCount"/>". A pile the
    /// player already thinned means "clear whatever is left", and an empty floor is already done.
    /// </summary>
    internal bool IsLooseTrashCleared(EntityUid player, EntityUid? mapUid, TutorialSubGoalData sub)
    {
        if (mapUid == null || string.IsNullOrEmpty(sub.Tag))
            return false;

        var loose = CountLooseTagged(mapUid.Value, sub.Tag);
        if (!_litterSpare.TryGetValue(player, out var spare))
        {
            var quota = sub.MinCount > 0 ? sub.MinCount : 1;
            spare = Math.Max(0, loose - quota);
            _litterSpare[player] = spare;
        }

        return loose <= spare;
    }

    /// <summary>Tagged entities on this map that are sitting in the world, not inside anything.</summary>
    internal int CountLooseTagged(EntityUid mapUid, string tag)
    {
        var tagId = (ProtoId<TagPrototype>) tag;
        var count = 0;
        var query = EntityQueryEnumerator<TransformComponent>();
        while (query.MoveNext(out var uid, out var xform))
        {
            if (xform.MapUid != mapUid || TerminatingOrDeleted(uid) || !_tags.HasTag(uid, tagId))
                continue;

            if (_containers.IsEntityInContainer(uid))
                continue;

            count++;
        }

        return count;
    }

    /// <summary>How many entities tagged holders on this map currently contain.</summary>
    internal int CountContentsOfTagged(EntityUid? mapUid, string holderTag)
    {
        if (mapUid == null || string.IsNullOrEmpty(holderTag))
            return 0;

        var tagId = (ProtoId<TagPrototype>) holderTag;
        var count = 0;
        var query = EntityQueryEnumerator<ContainerManagerComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var manager, out var xform))
        {
            if (xform.MapUid != mapUid || TerminatingOrDeleted(uid) || !_tags.HasTag(uid, tagId))
                continue;

            foreach (var container in _containers.GetAllContainers(uid, manager))
            {
                foreach (var contained in container.ContainedEntities)
                {
                    if (!TerminatingOrDeleted(contained))
                        count++;
                }
            }
        }

        return count;
    }

    /// <summary>
    /// The janitor flush banner. Dumping is a normal click, and flushing is the chute's alt click,
    /// so the banner names whichever of those the litter still needs.
    /// </summary>
    internal string ResolveJanitorFlushHint(EntityUid player, EntityUid? mapUid)
    {
        if (CountContentsOfTagged(mapUid, JanitorTrashBagTag) > 0)
            return JanitorFlushBagHint;

        if (CountContentsOfTagged(mapUid, JanitorDisposalTag) > 0)
            return JanitorFlushUnitHint;

        if (mapUid != null &&
            (CountLooseTagged(mapUid.Value, JanitorLitterTag) > 0 || IsCarryingTag(player, JanitorLitterTag)))
            return JanitorFlushHandsHint;

        return JanitorFlushBagHint;
    }

    private void TryUpdateJanitorFlushHint(EntityUid player, EntityUid? mapUid, TutorialSubGoalData sub)
    {
        if (sub.Tag != JanitorDisposalTag)
            return;

        _tutorial.SetControlHint(player, ResolveJanitorFlushHint(player, mapUid));
    }

    /// <summary>True when this body, or something it is holding or wearing, contains the tag.</summary>
    private bool IsCarryingTag(EntityUid mob, string tag, int depth = 0)
    {
        const int maxDepth = 6;

        if (depth > maxDepth || !TryComp<ContainerManagerComponent>(mob, out var containers))
            return false;

        var tagId = (ProtoId<TagPrototype>) tag;
        foreach (var container in _containers.GetAllContainers(mob, containers))
        {
            foreach (var contained in container.ContainedEntities)
            {
                if (TerminatingOrDeleted(contained))
                    continue;

                if (_tags.HasTag(contained, tagId) || IsCarryingTag(contained, tag, depth + 1))
                    return true;
            }
        }

        return false;
    }
}
