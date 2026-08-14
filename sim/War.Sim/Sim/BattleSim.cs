using War.Sim.Core;
using War.Sim.Sim.Systems;

namespace War.Sim.Sim;

/// <summary>
/// The battle clock.
///
/// One <see cref="Tick"/> is one thirtieth of a simulated second, always — the simulation
/// never sees a frame delta and never asks what time it is. Given the same seed, army
/// lists, terrain, and sequence of orders, this produces a bit-identical battle every
/// time, on any machine.
///
/// The order of the phases below is part of the design, not an accident of writing them
/// down. Combat resolves against the positions everyone could see at the start of the
/// tick, and movement happens afterward, so a soldier cannot be struck by someone who
/// arrived later in the same tick than he did.
/// </summary>
public sealed class BattleSim
{
    /// <summary>Maximum length of a battle before it is called on the state of the field.</summary>
    public static readonly Fix TimeLimitSeconds = Fix.FromInt(40 * 60);

    public BattleState State { get; }

    public BattleSim(BattleState state) => State = state;

    public static BattleSim Create(BattleSetup setup) => new(BattleBuilder.Build(setup));

    public bool IsOver => State.Result != BattleResult.InProgress;

    /// <summary>True while the armies are drawn up and the clock has not started.</summary>
    public bool IsDeploying => State.Phase == BattlePhase.Deploying;

    /// <summary>
    /// Starts the fighting. Until this is called the battle is frozen: no ticks, no
    /// orders carried out, no arrows in the air, and the player free to rearrange his
    /// line as long as he likes.
    /// </summary>
    public void BeginBattle()
    {
        if (State.Phase != BattlePhase.Deploying) return;

        State.Phase = BattlePhase.Fighting;

        // Whatever the player left them facing is the facing they start on, and their
        // slots are rebuilt around it.
        foreach (Unit unit in State.Units)
        {
            unit.Order = UnitOrder.Hold();
            unit.SlotsBuiltFor = -1;
        }

        State.RefreshUnitAggregates();
        State.RebuildSpatialIndices();
    }

    /// <summary>Advances the battle by exactly one tick.</summary>
    public void Tick()
    {
        if (IsOver || State.Phase != BattlePhase.Fighting) return;

        BattleState state = State;

        // 1. Index the field as it stands. Everything below queries this snapshot.
        state.RebuildSpatialIndices();

        // 2. Recount the living, recentre each unit.
        state.RefreshUnitAggregates();

        // 3. Close ranks where casualties have left holes.
        ReformUnits(state);

        // 4. The commander gives orders.
        CommanderAI.Run(state);

        // 5. Fighting, then shooting. Both resolve against the indexed snapshot.
        MeleeSystem.Run(state);
        MissileSystem.Run(state);

        // 6. Now everyone moves.
        MovementSystem.Run(state);

        // 7. What that cost them, and how they feel about it.
        FatigueSystem.Run(state);
        MoraleSystem.Run(state);

        // 8. Is anyone left?
        CheckVictory(state);

        state.Tick++;
    }

    /// <summary>Advances a whole number of ticks, stopping early if the battle ends.</summary>
    public void Run(int ticks)
    {
        for (int i = 0; i < ticks && !IsOver; i++) Tick();
    }

    /// <summary>Advances by a duration in simulated seconds.</summary>
    public void RunSeconds(Fix seconds) => Run(SimConstants.Ticks(seconds));

    // ----------------------------------------------------------------- reforming

    private static void ReformUnits(BattleState state)
    {
        foreach (Unit unit in state.Units)
        {
            if (unit.IsOutOfAction || unit.Alive == 0) continue;

            // Don't re-form mid-melee: the fight decides where the men are standing,
            // and dragging them back to a diagram would pull them out of contact.
            if (unit.InContact) continue;

            if (state.NeedsReform(unit)) state.RebuildSlots(unit);
        }
    }

    // ------------------------------------------------------------------ victory

    /// <summary>
    /// An army loses when most of it is dead or running — not when the last man falls.
    /// Routing troops count as lost even though they are still on the field, because a
    /// unit that has broken is not going to do anything for you.
    /// </summary>
    private static void CheckVictory(BattleState state)
    {
        Span<bool> broken = stackalloc bool[state.Armies.Length];
        int brokenCount = 0;

        for (int a = 0; a < state.Armies.Length; a++)
        {
            Army army = state.Armies[a];

            int effective = 0;
            foreach (int unitId in army.UnitIds)
            {
                Unit unit = state.Units[unitId];
                if (!unit.IsEffective) continue;
                effective += unit.Alive;
            }

            Fix remaining = army.InitialMen <= 0
                ? Fix.Zero
                : Fix.Ratio(effective, army.InitialMen);

            broken[a] = remaining <= Fix.One - SimConstants.ArmyBreakFraction;
            if (broken[a]) brokenCount++;
        }

        if (brokenCount == 0)
        {
            if (state.ElapsedSeconds >= TimeLimitSeconds) DecideOnPoints(state);
            return;
        }

        if (brokenCount == state.Armies.Length)
        {
            // Both sides collapsed in the same instant. Rare, but it must resolve.
            DecideOnPoints(state);
            return;
        }

        for (int a = 0; a < state.Armies.Length; a++)
        {
            if (broken[a]) continue;
            Finish(state, BattleResult.ArmyVictory, a);
            return;
        }
    }

    /// <summary>Time ran out, or both armies broke at once: whoever has more men left wins.</summary>
    private static void DecideOnPoints(BattleState state)
    {
        int bestArmy = -1;
        Fix bestShare = Fix.MinValue;
        bool tied = false;

        for (int a = 0; a < state.Armies.Length; a++)
        {
            Army army = state.Armies[a];

            int effective = 0;
            foreach (int unitId in army.UnitIds)
            {
                Unit unit = state.Units[unitId];
                if (unit.IsEffective) effective += unit.Alive;
            }

            Fix share = army.InitialMen <= 0 ? Fix.Zero : Fix.Ratio(effective, army.InitialMen);

            if (share > bestShare)
            {
                bestShare = share;
                bestArmy = a;
                tied = false;
            }
            else if (share == bestShare)
            {
                tied = true;
            }
        }

        if (tied || bestArmy < 0) Finish(state, BattleResult.Draw, -1);
        else Finish(state, BattleResult.ArmyVictory, bestArmy);
    }

    private static void Finish(BattleState state, BattleResult result, int victor)
    {
        state.Result = result;
        state.Victor = victor;
        state.Phase = BattlePhase.Decided;

        state.Raise(new BattleEvent
        {
            Type = BattleEventType.BattleEnded,
            A = (int)result,
            B = victor,
        });
    }
}
