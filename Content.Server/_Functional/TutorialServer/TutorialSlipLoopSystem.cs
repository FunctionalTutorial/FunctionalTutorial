using Content.Server.NPC;
using Content.Server.NPC.HTN;
using Content.Server.NPC.Systems;
using Content.Shared._Functional.TutorialServer;
using Content.Shared.Tag;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;
using Robust.Shared.Timing;

namespace Content.Server._Functional.TutorialServer;

/// <summary>
/// Sends a <see cref="TutorialSlipLoopComponent"/> mob from its mapped spot to a tagged mark
/// and back, with a pause at each end. The follow plan already knows how to walk; this only
/// swaps which end it is walking to.
/// </summary>
public sealed partial class TutorialSlipLoopSystem : EntitySystem
{
    [Dependency] private HTNSystem _htn = default!;
    [Dependency] private NPCSystem _npc = default!;
    [Dependency] private IRobustRandom _random = default!;
    [Dependency] private TagSystem _tags = default!;
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private SharedTransformSystem _transform = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<TutorialSlipLoopComponent, MapInitEvent>(OnMapInit);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var query = EntityQueryEnumerator<TutorialSlipLoopComponent>();
        while (query.MoveNext(out var uid, out var loop))
        {
            if (TerminatingOrDeleted(uid))
                continue;

            if (!loop.Armed && !TryArm(uid, loop))
                continue;

            Tick(uid, loop);
        }
    }

    private void OnMapInit(Entity<TutorialSlipLoopComponent> ent, ref MapInitEvent args)
    {
        TryArm(ent, ent.Comp);
    }

    /// <summary>
    /// Records the mapped spot and the far mark, then starts the first crossing.
    /// False until both exist, so a mob that initializes before its mark still picks it up.
    /// </summary>
    private bool TryArm(EntityUid uid, TutorialSlipLoopComponent loop)
    {
        if (loop.Armed)
            return true;

        var xform = Transform(uid);
        // GridUid is filled in after parenting. The mark is a sibling on the same parent, which
        // is already set when the map finishes loading, including during this mob's own init.
        if (!TryFindEnd(uid, xform, loop.EndTag, out var far))
            return false;

        loop.Near = xform.Coordinates;
        loop.Far = far;
        loop.HeadingFar = true;
        loop.WaitUntil = null;

        if (!Send(uid, loop))
            return false;

        loop.Armed = true;
        return true;
    }

    private void Tick(EntityUid uid, TutorialSlipLoopComponent loop)
    {
        if (loop.WaitUntil is { } until)
        {
            if (_timing.CurTime < until)
                return;

            loop.HeadingFar = !loop.HeadingFar;
            loop.WaitUntil = null;
            Send(uid, loop);
            return;
        }

        if (!InRange(uid, loop))
            return;

        var min = (float) loop.MinWait.TotalSeconds;
        var max = (float) loop.MaxWait.TotalSeconds;
        if (max < min)
            (min, max) = (max, min);

        loop.WaitUntil = _timing.CurTime + TimeSpan.FromSeconds(_random.NextFloat(min, max));
    }

    private bool InRange(EntityUid uid, TutorialSlipLoopComponent loop)
    {
        if (Current(loop) is not { } target)
            return false;

        var here = _transform.GetWorldPosition(uid);
        var there = _transform.ToMapCoordinates(target).Position;
        return (here - there).Length() <= loop.ArriveRange;
    }

    private static EntityCoordinates? Current(TutorialSlipLoopComponent loop)
    {
        return loop.HeadingFar ? loop.Far : loop.Near;
    }

    /// <summary>
    /// Points the follow plan at the current end. Replan is what makes an in-range idle give
    /// way to the walk: the move branch is the earlier one, so the new plan replaces the pause.
    /// </summary>
    private bool Send(EntityUid uid, TutorialSlipLoopComponent loop)
    {
        if (Current(loop) is not { } destination)
            return false;

        if (!TryComp<HTNComponent>(uid, out var htn))
            return false;

        _npc.SetBlackboard(uid, NPCBlackboard.FollowTarget, destination, htn);
        _htn.Replan(htn);
        return true;
    }

    private bool TryFindEnd(EntityUid mob, TransformComponent mobXform, string tag, out EntityCoordinates coords)
    {
        coords = default;

        if (string.IsNullOrEmpty(tag))
            return false;

        var wanted = new ProtoId<TagPrototype>(tag);
        var query = EntityQueryEnumerator<TagComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var tags, out var xform))
        {
            if (uid == mob || !SameWalk(mobXform, xform) || !_tags.HasTag(tags, wanted))
                continue;

            coords = xform.Coordinates;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Same grid once ids exist, otherwise the same parent. Map load initializes the walker
    /// before every sibling has a grid id, and the mark is parented to that grid directly.
    /// </summary>
    private static bool SameWalk(TransformComponent mob, TransformComponent other)
    {
        if (mob.GridUid is { } grid && other.GridUid == grid)
            return true;

        if (mob.MapUid is { } map && other.MapUid == map)
            return true;

        return mob.ParentUid == other.ParentUid && mob.ParentUid.IsValid();
    }
}
