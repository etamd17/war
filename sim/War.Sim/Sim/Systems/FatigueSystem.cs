using War.Sim.Core;

namespace War.Sim.Sim.Systems;

/// <summary>
/// Stamina. Running costs it, fighting costs it, climbing and mud and armour cost more,
/// and standing still slowly gives it back.
///
/// Fatigue is the quiet reason reserves win battles. An exhausted unit swings slower,
/// hits softer, defends worse, moves at little better than a walk, and breaks sooner —
/// so a fresh unit committed at the right moment is worth several tired ones, and an
/// army that sprinted across the field to arrive first has already spent part of what
/// it came to fight with.
/// </summary>
public static class FatigueSystem
{
    /// <summary>Extra fatigue per point of armour worn, as a fraction.</summary>
    private static readonly Fix ArmourBurdenPerPoint = Fix.Ratio(4, 100);

    public static void Run(BattleState state)
    {
        for (int s = 0; s < state.SoldierCount; s++)
        {
            if (state.State[s] == SoldierState.Dead) continue;

            Unit unit = state.UnitOf(s);
            if (unit.Withdrawn) continue;

            Fix perSecond;
            bool exerting = true;

            switch (state.State[s])
            {
                case SoldierState.Fighting:
                    perSecond = SimConstants.FatiguePerSecondFighting;
                    break;

                case SoldierState.Charging:
                case SoldierState.Routing:
                    perSecond = SimConstants.FatiguePerSecondRunning;
                    break;

                case SoldierState.Moving:
                    perSecond = unit.Order.Run || unit.MoraleState == MoraleState.Routing
                        ? SimConstants.FatiguePerSecondRunning
                        : SimConstants.FatiguePerSecondWalking;
                    break;

                default:
                    perSecond = -SimConstants.FatigueRecoveryPerSecond;
                    exerting = false;
                    break;
            }

            if (exerting)
            {
                // Ground and slope multiply what the effort costs. Recovery does not get
                // slower in mud — resting is resting — so this only scales exertion.
                perSecond *= state.Terrain.FatigueMultiplierAt(state.Position[s], state.Facing[s]);

                // Fifty pounds of mail is fifty pounds of mail whether you are winning.
                perSecond *= Fix.One + Fix.FromInt(unit.Type.Armour) * ArmourBurdenPerPoint;
            }

            state.Fatigue[s] = FixMath.Clamp01(state.Fatigue[s] + perSecond * SimConstants.TickSeconds);
        }
    }
}
