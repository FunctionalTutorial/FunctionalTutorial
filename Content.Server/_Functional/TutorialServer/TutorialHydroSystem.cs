using Content.Shared._Functional.TutorialServer;
using Content.Shared.Botany.Components;
using Content.Shared.Botany.Events;
using Content.Shared.Botany.Items.Systems;
using Content.Shared.Botany.Systems;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Interaction;
using Content.Shared.Tag;
using Robust.Shared.Prototypes;

namespace Content.Server._Functional.TutorialServer;

/// <summary>
/// Forces tutorial hydro trays to harvest-ready after planting, and advances HydroHarvest goals.
/// Subscribes on <see cref="TutorialHydroTrayComponent"/> so we do not steal
/// <see cref="PlantHarvestSystem"/> / <see cref="BotanySeedSystem"/> directed events.
/// </summary>
public sealed class TutorialHydroSystem : EntitySystem
{
    [Dependency] private readonly PlantTraySystem _plantTray = default!;
    [Dependency] private readonly SharedHandsSystem _hands = default!;
    [Dependency] private readonly TagSystem _tags = default!;
    [Dependency] private readonly TutorialServerRuleSystem _tutorial = default!;

    private static readonly ProtoId<TagPrototype> HydroTag = "TutorialHydroTray";

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<TutorialHydroTrayComponent, PlantingSeedAttemptEvent>(OnPlantingSeed, after: [typeof(BotanySeedSystem)]);
        SubscribeLocalEvent<TutorialHydroTrayComponent, InteractHandEvent>(OnInteractHand, after: [typeof(PlantHarvestSystem)]);
    }

    private void OnPlantingSeed(Entity<TutorialHydroTrayComponent> ent, ref PlantingSeedAttemptEvent args)
    {
        if (args.Cancelled)
            return;

        if (!_tags.HasTag(ent.Owner, HydroTag))
            return;

        if (!ForceHarvestReady(ent.Owner))
            return;

        ent.Comp.AwaitingHarvestResult = true;
        Dirty(ent);
    }

    private void OnInteractHand(Entity<TutorialHydroTrayComponent> ent, ref InteractHandEvent args)
    {
        if (!_tags.HasTag(ent.Owner, HydroTag))
            return;

        if (!ent.Comp.AwaitingHarvestResult)
            return;

        if (!_plantTray.TryGetPlant(ent.Owner, out var plantUid)
            || !TryComp<PlantHolderComponent>(plantUid.Value, out var holder))
        {
            // NoRepeat harvest deletes the plant; treat that as success.
        }
        else if (holder.ReadyForHarvest)
        {
            // Click did not harvest.
            return;
        }

        ent.Comp.AwaitingHarvestResult = false;
        ent.Comp.Harvested = true;
        Dirty(ent);

        if (!TryComp<TutorialParticipantComponent>(args.User, out var part))
            return;

        if (!_tutorial.TryGetCurrentSubGoal(args.User, part, out var sub))
            return;

        if (sub.Complete != TutorialStepComplete.HydroHarvest)
            return;

        if (sub.Entity != null)
        {
            var matched = false;
            foreach (var held in _hands.EnumerateHeld(args.User))
            {
                var meta = MetaData(held);
                if (meta.EntityPrototype?.ID == sub.Entity.Value.Id)
                {
                    matched = true;
                    break;
                }
            }

            if (!matched)
                return;
        }

        _tutorial.AdvanceSubGoal(args.User);
    }

    private bool ForceHarvestReady(EntityUid tray)
    {
        if (!_plantTray.TryGetPlant(tray, out var plantUid))
            return false;

        if (!TryComp<PlantHolderComponent>(plantUid.Value, out var holder)
            || !TryComp<PlantComponent>(plantUid.Value, out var plant))
            return false;

        holder.Dead = false;
        holder.Age = (int) Math.Max(plant.Maturation, plant.Production) + 1;
        holder.LastHarvest = holder.Age - (int) plant.Production - 1;
        holder.ReadyForHarvest = true;
        holder.Health = plant.Endurance;
        Dirty(plantUid.Value, holder);
        return true;
    }
}
