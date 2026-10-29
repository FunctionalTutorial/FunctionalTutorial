using Content.Shared._Functional.TutorialServer;
using Robust.Shared.Containers;

namespace Content.Server._Functional.TutorialServer;

/// <summary>
/// The finale cryopod is the way out of the janitor course. Stock cryosleep wants station records
/// and an access card the passenger spawn does not have, so this listens for the insert itself.
/// </summary>
public sealed class TutorialJanitorExitSystem : EntitySystem
{
    [Dependency] private TutorialServerRuleSystem _tutorial = default!;

    public override void Initialize()
    {
        SubscribeLocalEvent<TutorialJanitorExitComponent, EntInsertedIntoContainerMessage>(OnInserted);
    }

    private void OnInserted(Entity<TutorialJanitorExitComponent> ent, ref EntInsertedIntoContainerMessage args)
    {
        if (!HasComp<TutorialParticipantComponent>(args.Entity))
            return;

        _tutorial.ExitToRolePicker(args.Entity);
    }
}
