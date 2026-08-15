using War.Sim.Core;
using War.Sim.Units;

namespace War.Sim.Sim;

/// <summary>
/// The enemy commander.
///
/// It is not trying to be a grandmaster. It is trying to punish the three mistakes that
/// separate a player who is thinking from one who is not: leaving a flank open, leaving
/// archers unescorted, and committing everything at once with nothing held back.
///
/// It thinks twice a second, in a fixed order — line, screen, horse, reserve, general —
/// and every decision is made from the same indexed snapshot the rest of the tick uses,
/// so its behaviour is as reproducible as everything else.
/// </summary>
public static class CommanderAI
{
    /// <summary>Ticks between decisions. Twice a second is plenty and keeps the cost invisible.</summary>
    public const int CadenceTicks = 15;

    private static readonly Fix EngagementRange = Fix.FromInt(80);
    private static readonly Fix ScreenStandOff = Fix.FromInt(30);
    private static readonly Fix ScreenRetireDistance = Fix.FromInt(55);

    /// <summary>
    /// How far the enemy must be before skirmishers go forward again.
    ///
    /// Well beyond the distance at which they retire, and the gap is the point. With a
    /// single threshold the screen advanced to its station, was driven in, advanced again
    /// the moment it cleared, and spent the whole battle walking seventy-five metres back
    /// and forth. In one battle the Velites finished exhausted — fatigue at its ceiling —
    /// and fled the field having lost exactly one man to the enemy. They ran themselves
    /// out of the fight. Between the two thresholds they now stand still and shoot, which
    /// is what a skirmisher is for.
    /// </summary>
    private static readonly Fix ScreenAdvanceDistance = Fix.FromInt(85);
    private static readonly Fix FlankClearance = Fix.FromInt(45);
    private static readonly Fix GeneralStandOff = Fix.FromInt(45);
    private static readonly Fix MauledFraction = Fix.Ratio(28, 100);

    public static void Run(BattleState state)
    {
        foreach (Army army in state.Armies)
        {
            if (army.IsPlayer) continue;

            // Stagger the two armies so they never think on the same tick.
            if ((state.Tick + army.Id * 7) % CadenceTicks != 0) continue;

            Command(state, army);
        }
    }

    private static void Command(BattleState state, Army army)
    {
        var line = new List<Unit>();
        var screen = new List<Unit>();
        var horse = new List<Unit>();
        Unit? general = null;

        foreach (int unitId in army.UnitIds)
        {
            Unit unit = state.Units[unitId];
            if (!unit.IsEffective || unit.Alive == 0) continue;

            switch (unit.Type.Class)
            {
                case UnitClass.Missile: screen.Add(unit); break;
                case UnitClass.General: general = unit; break;
                case UnitClass.Cavalry:
                case UnitClass.MissileCavalry:
                case UnitClass.Chariot:
                case UnitClass.Elephant: horse.Add(unit); break;
                default: line.Add(unit); break;
            }
        }

        var enemies = new List<Unit>();
        foreach (int unitId in state.Armies[1 - army.Id].UnitIds)
        {
            Unit unit = state.Units[unitId];
            if (unit.IsOutOfAction || unit.Alive == 0) continue;
            enemies.Add(unit);
        }

        if (enemies.Count == 0)
        {
            foreach (int unitId in army.UnitIds)
            {
                Unit unit = state.Units[unitId];
                if (unit.IsEffective) unit.Order = UnitOrder.Hold();
            }
            return;
        }

        FixVec2 enemyCentre = WeightedCentre(enemies);
        FixVec2 ownCentre = line.Count > 0 ? WeightedCentre(line) : army.DeploymentCentre;

        FixVec2 axis = (enemyCentre - ownCentre).Normalized;
        if (axis.IsZero) axis = army.AdvanceDirection;

        bool inCrisis = InCrisis(state, army);

        CommandLine(state, line, enemies, axis, inCrisis);
        CommandScreen(state, screen, enemies, ownCentre, axis);
        CommandHorse(state, horse, enemies, enemyCentre, axis);
        CommandGeneral(state, general, enemies, ownCentre, axis, inCrisis);

        // Last, because what a unit should be standing in depends on what it has just
        // been told to do.
        CommandFormations(state, army, enemies);
    }

    // ------------------------------------------------------------- formations

    /// <summary>Ticks a unit must stand in a formation before the commander may change it again.</summary>
    private const int ReformLockTicks = 90;

    private static readonly Fix CavalryThreat = Fix.FromInt(55);
    private static readonly Fix ImminentCharge = Fix.FromInt(30);
    private static readonly Fix SquareClearance = Fix.FromInt(45);

    /// <summary>
    /// How far the nearest enemy foot must be before shields go overhead.
    ///
    /// Deliberately beyond <see cref="EngagementRange"/>, so a testudo only ever forms on
    /// the long approach and is back in line well before contact. Set inside engagement
    /// range instead and the commander marches its legions into a melee at forty-five per
    /// cent speed in the one formation that delivers no charge at all — which cost Rome
    /// five battles in fourteen when this was first wired up.
    /// </summary>
    private static readonly Fix TestudoClearance = Fix.FromInt(85);
    private static readonly Fix PhalanxAdopt = Fix.FromInt(60);
    private static readonly Fix PhalanxDrop = Fix.FromInt(90);

    /// <summary>
    /// Puts each unit in the formation its situation calls for.
    ///
    /// Every one of these formations was already implemented and every one of them was
    /// dead weight, because the commander fought the entire battle in line — so a player
    /// never once had to solve a phalanx, and never saw a testudo cross the ground their
    /// archers were beating. The systems existed; the opponent did not use them.
    /// </summary>
    private static void CommandFormations(BattleState state, Army army, List<Unit> enemies)
    {
        foreach (int unitId in army.UnitIds)
        {
            Unit unit = state.Units[unitId];
            if (!unit.IsEffective || unit.Alive == 0) continue;
            if (state.Tick < unit.FormationHoldUntil) continue;

            FormationType want = Preferred(state, unit, enemies);
            if (want == unit.Formation || !unit.Type.CanUse(want)) continue;

            unit.Formation = want;
            unit.Width = 0;                 // let the new formation choose its own depth
            unit.SlotsBuiltFor = -1;        // and re-form the men into it
            unit.FormationHoldUntil = state.Tick + ReformLockTicks;
        }
    }

    private static FormationType Preferred(BattleState state, Unit unit, List<Unit> enemies)
    {
        // Horse: wedge to deliver a charge, line to travel. The wedge is worth forty per
        // cent on the impact and nothing at all once the melee has settled.
        if (unit.Type.IsMounted)
            return unit.Order.Type == OrderType.Attack && !unit.InContact
                ? FormationType.Wedge
                : FormationType.Line;

        // Skirmishers live in loose order. It halves what archery does to them, they move
        // faster, and the melee penalty costs nothing because they were never going to win
        // a melee anyway.
        if (unit.Type.Class == UnitClass.Missile) return FormationType.Skirmish;

        Fix nearestFoot = Fix.MaxValue;
        Fix nearestHorse = Fix.MaxValue;
        bool underArchery = false;

        foreach (Unit enemy in enemies)
        {
            Fix distance = FixVec2.Distance(unit.Centre, enemy.Centre);

            if (enemy.Type.IsMounted)
                nearestHorse = FixMath.Min(nearestHorse, distance);
            else if (enemy.Type.Class != UnitClass.Missile)
                nearestFoot = FixMath.Min(nearestFoot, distance);

            if (enemy.Type.HasMissiles && distance <= enemy.Type.MissileRange && enemy.Ammo(state) > 0)
                underArchery = true;
        }

        // A unit that has been told to go and fight something should go and fight it. The
        // defensive formations below are for troops who are receiving rather than
        // delivering, and handing one to a unit mid-advance halves its speed and throws
        // away its charge for a threat it was already moving away from.
        bool closing = unit.Order.Type == OrderType.Attack && !unit.InContact;

        // Horse closing with no infantry fight to hold the front: face outward. A square
        // moves at six tenths speed and takes a quarter more from archery, which is the
        // price of having no flank to find. Troops already committed to an attack only
        // break off for horse that is genuinely about to arrive.
        Fix horseThreat = closing ? ImminentCharge : CavalryThreat;

        if (!unit.InContact && nearestHorse < horseThreat && nearestFoot > SquareClearance)
            return FormationType.Square;

        // Pikes come down once something is close enough for them to matter, and go back
        // up to march, because a phalanx crosses ground at half speed. The two thresholds
        // differ so a unit hovering at the edge does not raise and level them repeatedly.
        if (unit.Type.CanUse(FormationType.Phalanx))
        {
            Fix threshold = unit.Formation == FormationType.Phalanx ? PhalanxDrop : PhalanxAdopt;
            return unit.InContact || nearestFoot < threshold
                ? FormationType.Phalanx
                : FormationType.Line;
        }

        // Shields overhead to cross beaten ground — but only while there is nothing to
        // fight. A testudo delivers no charge at all and is poor in a melee, so walking
        // one into contact trades an arrow problem for a worse one.
        if (unit.Type.CanUse(FormationType.Testudo) &&
            underArchery && !unit.InContact && nearestFoot > TestudoClearance)
            return FormationType.Testudo;

        return FormationType.Line;
    }

    // -------------------------------------------------------------------- line

    /// <summary>
    /// The battle line: close with the enemy, match their frontage, and keep something
    /// back. The reserve is the whole point — a commander who commits everything has no
    /// answer when one of his units breaks.
    /// </summary>
    private static void CommandLine(
        BattleState state, List<Unit> line, List<Unit> enemies, FixVec2 axis, bool inCrisis)
    {
        if (line.Count == 0) return;

        // Hold one unit in four back, at least one, unless things have gone wrong.
        int reserveCount = inCrisis ? 0 : line.Count / 4;

        // Sort by position across the front so units advance without crossing over
        // each other and tangling the line.
        FixVec2 left = axis.Left;
        var ordered = line.OrderBy(u => FixVec2.Dot(u.Centre, left).Raw).ThenBy(u => u.Id).ToList();

        // The rearmost units make the best reserve: they are already behind the line.
        var reserve = ordered
            .OrderBy(u => FixVec2.Dot(u.Centre, axis).Raw)
            .Take(reserveCount)
            .ToHashSet();

        foreach (Unit unit in ordered)
        {
            // Pull out anything that has been chewed to pieces before it breaks and
            // takes the units either side of it with it.
            if (unit.StrengthFraction < MauledFraction &&
                unit.MoraleState == MoraleState.Wavering && !inCrisis)
            {
                unit.Order = UnitOrder.Withdraw(unit.Centre - axis * Fix.FromInt(70));
                continue;
            }

            if (reserve.Contains(unit))
            {
                // Sit behind the centre of the line, formed up and ready.
                FixVec2 station = WeightedCentre(ordered) - axis * Fix.FromInt(55);
                unit.Order = UnitOrder.MoveTo(station, axis);
                continue;
            }

            Unit? target = BestLineTarget(unit, enemies);

            if (target == null)
            {
                unit.Order = UnitOrder.Hold();
                continue;
            }

            Fix distance = FixVec2.Distance(unit.Centre, target.Centre);

            if (distance <= EngagementRange)
            {
                unit.Order = UnitOrder.Attack(target.Id);
            }
            else
            {
                // Advance at a walk. Arriving out of breath is how you lose the fight
                // you spent the whole approach trying to reach.
                FixVec2 approach = target.Centre - axis * (target.ExtentAlong(axis) + Fix.Two);
                unit.Order = UnitOrder.MoveTo(approach, axis);
            }
        }
    }

    /// <summary>
    /// What a body of foot should walk at.
    ///
    /// Nearest, mostly — but not the front of a levelled phalanx, which is the one place
    /// on the field where walking forward is simply losing. Pikes there add two to attack,
    /// six to defence, reach a rank early and cancel the charge outright; the same men
    /// from the side are worth minus six and cannot turn, because a phalanx moves at half
    /// speed.
    ///
    /// The commander was feeding its line into that wall frontally, one unit at a time.
    /// Teaching it to go and find something else is what makes the phalanx a problem to be
    /// solved rather than a stat block that wins, and it is the same lesson the player has
    /// to learn from the other side of the field.
    /// </summary>
    private static Unit? BestLineTarget(Unit unit, List<Unit> enemies)
    {
        Unit? best = null;
        Fix bestScore = Fix.MinValue;

        foreach (Unit enemy in enemies)
        {
            Fix score = -FixVec2.Distance(unit.Centre, enemy.Centre);

            // Stick with the target already chosen unless something is clearly better.
            //
            // Without this the commander re-scores everything twice a second and walks its
            // line wherever the arithmetic points that instant. It cost Rome an entire
            // battle: five hundred men lost against fifty-four, every unit routing at
            // nine tenths strength with fatigue at the ceiling and almost no fighting on
            // the field at all. The army had marched itself to death chasing a target
            // that changed every half second. Men who are walking are not fighting, and
            // in this simulation they are also losing their nerve.
            if (unit.Order.Type == OrderType.Attack && unit.Order.TargetUnit == enemy.Id)
                score += Fix.FromInt(60);

            // Pile onto something already fighting: it cannot turn to receive us, and two
            // units on one is how a line gets broken rather than ground down.
            if (enemy.InContact) score += Fix.FromInt(35);

            if (enemy.MoraleState == MoraleState.Routing) score -= Fix.FromInt(40);

            if (enemy.FormationProfile.NegatesFrontalCharge && !enemy.InContact)
            {
                // Only the front is a wall. Coming in from behind the pikes is exactly
                // what should be encouraged.
                bool wouldHitTheFront = FixVec2.Dot(unit.Centre - enemy.Centre, enemy.Facing) > Fix.Zero;
                if (wouldHitTheFront) score -= Fix.FromInt(130);
            }

            if (score <= bestScore) continue;
            bestScore = score;
            best = enemy;
        }

        return best;
    }

    // ------------------------------------------------------------------ screen

    /// <summary>
    /// Skirmishers: get out in front, shoot the most valuable thing that can be shot,
    /// and retire through the line before anything reaches them. A skirmisher caught in
    /// melee is a dead skirmisher.
    /// </summary>
    private static void CommandScreen(
        BattleState state, List<Unit> screen, List<Unit> enemies, FixVec2 ownCentre, FixVec2 axis)
    {
        foreach (Unit unit in screen)
        {
            Unit? nearest = NearestEnemy(unit, enemies);
            Fix distance = nearest == null
                ? Fix.MaxValue
                : FixVec2.Distance(unit.Centre, nearest.Centre);

            // Concentrate on whatever hurts most: enemy archers first, then the things
            // that will break the line if they arrive intact.
            unit.PreferredMissileTarget = BestMissileTarget(state, unit, enemies)?.Id ?? -1;
            unit.FireAtWill = true;

            if (unit.InContact || distance < ScreenRetireDistance || unit.Ammo(state) == 0)
            {
                // Fall back behind the line. Out of ammunition they are no use forward
                // and will only get ridden down.
                unit.Order = UnitOrder.Withdraw(ownCentre - axis * Fix.FromInt(45));
                continue;
            }

            // Inside the band between the two thresholds: stand and shoot. Nothing is
            // gained by closing, and the walking is what kills them.
            if (distance < ScreenAdvanceDistance)
            {
                unit.Order = UnitOrder.Hold();
                continue;
            }

            FixVec2 station = ownCentre + axis * ScreenStandOff;
            unit.Order = UnitOrder.MoveTo(station, axis);
        }
    }

    /// <summary>
    /// Missile target priority. Archers are the first thing to silence because they are
    /// the only enemy that hurts you at range; after that, whatever is most dangerous
    /// and least armoured.
    /// </summary>
    private static Unit? BestMissileTarget(BattleState state, Unit shooter, List<Unit> enemies)
    {
        Unit? best = null;
        Fix bestScore = Fix.MinValue;

        foreach (Unit enemy in enemies)
        {
            if (!FixVec2.WithinDistance(shooter.Centre, enemy.Centre, shooter.Type.MissileRange))
                continue;

            Fix score = enemy.Type.Class switch
            {
                UnitClass.Missile => Fix.FromInt(100),
                UnitClass.Elephant => Fix.FromInt(85),
                UnitClass.MissileCavalry => Fix.FromInt(80),
                UnitClass.General => Fix.FromInt(70),
                UnitClass.Chariot => Fix.FromInt(65),
                UnitClass.Cavalry => Fix.FromInt(55),
                _ => Fix.FromInt(30),
            };

            // Shooting armoured infantry is mostly a waste of arrows.
            score -= Fix.FromInt(enemy.Type.Armour + enemy.Type.Shield) * 2;

            // Prefer something close enough to hit reliably.
            score -= FixVec2.Distance(shooter.Centre, enemy.Centre) / 4;

            // Do not shoot into a melee: those arrows land on our own men.
            if (enemy.InContact) score -= Fix.FromInt(45);

            if (score <= bestScore) continue;
            bestScore = score;
            best = enemy;
        }

        return best;
    }

    // ------------------------------------------------------------------- horse

    /// <summary>
    /// Cavalry: go round, not through.
    ///
    /// The manoeuvre is deliberately simple and deliberately correct — swing wide of the
    /// enemy's wing until clear of their frontage, then come in on something that cannot
    /// turn to face you because it is already fighting. That single behaviour is what
    /// makes an unguarded flank cost the player the battle.
    /// </summary>
    private static void CommandHorse(
        BattleState state, List<Unit> horse, List<Unit> enemies, FixVec2 enemyCentre, FixVec2 axis)
    {
        if (horse.Count == 0) return;

        FixVec2 left = axis.Left;

        // How far out the enemy line reaches, so we know what "round the flank" means.
        Fix enemyHalfWidth = Fix.Zero;
        foreach (Unit enemy in enemies)
        {
            Fix lateral = FixMath.Abs(FixVec2.Dot(enemy.Centre - enemyCentre, left)) + enemy.HalfFrontage;
            enemyHalfWidth = FixMath.Max(enemyHalfWidth, lateral);
        }

        foreach (Unit unit in horse)
        {
            // Elephants and chariots do not manoeuvre; they go straight at the line.
            if (unit.Type.Class is UnitClass.Elephant or UnitClass.Chariot)
            {
                Unit? head = NearestEnemy(unit, enemies);
                unit.Order = head == null ? UnitOrder.Hold() : UnitOrder.Attack(head.Id);
                continue;
            }

            Unit? target = BestFlankTarget(unit, enemies);
            if (target == null)
            {
                unit.Order = UnitOrder.Hold();
                continue;
            }

            // Which wing is this unit already nearer? Going the short way round keeps it
            // out of the enemy's reach on the journey.
            Fix ownLateral = FixVec2.Dot(unit.Centre - enemyCentre, left);
            int side = ownLateral.Raw >= 0 ? 1 : -1;

            Fix targetLateral = FixVec2.Dot(target.Centre - enemyCentre, left);
            Fix clearance = enemyHalfWidth + FlankClearance;

            bool clearOfTheLine = FixMath.Abs(ownLateral) > FixMath.Abs(targetLateral) + Fix.FromInt(20);
            bool behindThem = FixVec2.Dot(unit.Centre - target.Centre, axis) > Fix.Zero;

            if (clearOfTheLine || behindThem || target.InContact && FixVec2.Distance(unit.Centre, target.Centre) < Fix.FromInt(70))
            {
                unit.Order = UnitOrder.Attack(target.Id);
                continue;
            }

            // Still in front of the line: swing out wide before turning in.
            FixVec2 waypoint = enemyCentre
                + left * (clearance * side)
                - axis * Fix.FromInt(10);

            unit.Order = UnitOrder.MoveTo(waypoint, axis, run: true);
        }
    }

    /// <summary>
    /// What cavalry should be hitting: something that cannot fight back properly.
    /// Anything already locked in melee is pinned and cannot turn; archers die to a
    /// charge without landing a blow; a routing unit is free kills and free morale.
    /// </summary>
    private static Unit? BestFlankTarget(Unit horse, List<Unit> enemies)
    {
        Unit? best = null;
        Fix bestScore = Fix.MinValue;

        foreach (Unit enemy in enemies)
        {
            Fix score = enemy.Type.Class switch
            {
                UnitClass.Missile => Fix.FromInt(90),
                UnitClass.General => Fix.FromInt(60),
                _ => Fix.FromInt(25),
            };

            // Pinned troops cannot turn to receive us.
            if (enemy.InContact) score += Fix.FromInt(40);

            // A levelled phalanx is the best target on the field for horse, provided the
            // horse arrives anywhere but the front: minus six on the flank, half speed to
            // turn, and every pike pointing the wrong way.
            if (enemy.FormationProfile.NegatesFrontalCharge) score += Fix.FromInt(55);

            // Broken troops are free.
            if (enemy.MoraleState == MoraleState.Routing) score += Fix.FromInt(35);

            // Do not ride a spear wall down from the front. This is the counter working
            // in the AI's favour rather than against it.
            if (enemy.Type.BonusVsMounted >= 5 && !enemy.InContact) score -= Fix.FromInt(70);

            // And do not send horses at elephants. Watching a battle log, the very first
            // unit to break was invariably the Roman cavalry at around fifty seconds,
            // having ridden straight at the Carthaginian elephants and taken the full
            // twenty-five point fear penalty that mounts suffer from them. The commander
            // was reliably throwing away its own cavalry in the first minute.
            if (enemy.Type.CausesFear && !horse.Type.ImmuneToFear) score -= Fix.FromInt(90);

            score -= FixVec2.Distance(horse.Centre, enemy.Centre) / 6;

            if (score <= bestScore) continue;
            bestScore = score;
            best = enemy;
        }

        return best;
    }

    // ----------------------------------------------------------------- general

    /// <summary>
    /// The general is worth more alive than any charge he could make. He sits behind the
    /// centre where his aura covers the most men, and only commits when the target is
    /// already broken — which is both safe and the moment his weight actually matters.
    /// </summary>
    private static void CommandGeneral(
        BattleState state, Unit? general, List<Unit> enemies,
        FixVec2 ownCentre, FixVec2 axis, bool inCrisis)
    {
        if (general == null) return;

        foreach (Unit enemy in enemies)
        {
            bool worthTheRisk = enemy.MoraleState == MoraleState.Routing
                || (inCrisis && enemy.InContact);

            if (!worthTheRisk) continue;
            if (!FixVec2.WithinDistance(general.Centre, enemy.Centre, Fix.FromInt(90))) continue;

            general.Order = UnitOrder.Attack(enemy.Id);
            return;
        }

        general.Order = UnitOrder.MoveTo(ownCentre - axis * GeneralStandOff, axis);
    }

    // ----------------------------------------------------------------- helpers

    /// <summary>True when the army is in enough trouble to spend its reserve.</summary>
    private static bool InCrisis(BattleState state, Army army)
    {
        foreach (int unitId in army.UnitIds)
        {
            Unit unit = state.Units[unitId];
            if (unit.Withdrawn) continue;
            if (unit.MoraleState == MoraleState.Routing) return true;
        }
        return false;
    }

    private static FixVec2 WeightedCentre(List<Unit> units)
    {
        if (units.Count == 0) return FixVec2.Zero;

        // 64-bit accumulation, for the same reason unit centres need it: a position of
        // 900 weighted by 140 men is already past what a Fix can hold, before summing
        // across the whole army.
        var sum = new FixVec2Sum();
        foreach (Unit unit in units) sum.Add(unit.Centre, unit.Alive);

        return sum.Count > 0 ? sum.Mean : units[0].Centre;
    }

    private static Unit? NearestEnemy(Unit unit, List<Unit> enemies)
    {
        Unit? best = null;
        long bestSqr = long.MaxValue;

        foreach (Unit enemy in enemies)
        {
            long sqr = FixVec2.SqrDistanceRaw(unit.Centre, enemy.Centre);
            if (sqr >= bestSqr) continue;
            bestSqr = sqr;
            best = enemy;
        }

        return best;
    }
}

internal static class UnitAmmoExtensions
{
    /// <summary>Total ammunition left across a unit's living men.</summary>
    public static int Ammo(this Unit unit, BattleState state)
    {
        int total = 0;
        for (int s = unit.FirstSoldier; s < unit.EndSoldier; s++)
        {
            if (state.State[s] == SoldierState.Dead) continue;
            total += state.Ammo[s];
        }
        return total;
    }
}
