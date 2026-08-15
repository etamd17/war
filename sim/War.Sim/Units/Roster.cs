using War.Sim.Core;

namespace War.Sim.Units;

/// <summary>
/// The unit roster, c. 270–146 BC.
///
/// These stats are the counter web made concrete. Read down a column and you should be
/// able to predict the tactics: Triarii have a huge bonus against mounts and a phalanx
/// they can form, so they anchor a flank against cavalry. Naked Fanatics have enormous
/// attack and charge, no armour at all, and discipline of two — they win the first
/// twenty seconds of any fight and lose everything after it. Balearic Slingers pierce
/// armour, so they are the answer to a Roman line that laughs off arrows.
///
/// Scale: attack 4–20, defence skill 2–15, shield 0–8, armour 0–12, charge 2–20,
/// morale 4–18, discipline 0–10. Speeds in metres per second, ranges in metres.
/// </summary>
public static class Roster
{
    private static readonly Dictionary<string, UnitType> ById = new(StringComparer.Ordinal);
    private static readonly List<UnitType> AllTypes = new();

    public static IReadOnlyList<UnitType> All => AllTypes;

    public static UnitType Get(string id) =>
        ById.TryGetValue(id, out UnitType? type)
            ? type
            : throw new KeyNotFoundException($"No unit type '{id}' in the roster");

    public static IEnumerable<UnitType> ByFaction(Faction faction) =>
        AllTypes.Where(u => u.Faction == faction);

    public static UnitType GeneralOf(Faction faction) =>
        AllTypes.First(u => u.Faction == faction && u.Class == UnitClass.General);

    private static UnitType Add(UnitType type)
    {
        ById.Add(type.Id, type);
        AllTypes.Add(type);
        _ = type.TurnStepPerTick;   // bake the cached rotation step up front
        return type;
    }

    static Roster()
    {
        AddRome();
        AddCarthage();
        AddGaul();
        AddGreece();
        AddEgypt();
    }

    // ================================================================== ROME
    //
    // The manipular legion: three lines of increasing quality, a javelin screen in
    // front, and cavalry that is frankly an afterthought. Rome wins by grinding.

    private static void AddRome()
    {
        Add(new UnitType
        {
            Id = "rome_velites",
            Name = "Velites",
            Faction = Faction.Rome,
            Class = UnitClass.Missile,
            Description = "Young skirmishers with javelins. Screen the line, then get out of the way.",
            Attack = 5, Charge = 2, DefenceSkill = 3, Shield = 2, Armour = 0,
            Morale = 6, Discipline = 5,
            WalkSpeed = Fix.Ratio(15, 10), RunSpeed = Fix.Ratio(39, 10),
            Missile = MissileType.Javelin, MissileRange = Fix.FromInt(32),
            MissileAttack = 7, Ammunition = 6, ReloadInterval = Fix.FromInt(4),
            DefaultStrength = 80, DefaultFormation = FormationType.Skirmish,
            AllowedFormations = FormationMask.Foot,
            FileSpacing = Fix.Ratio(9, 10), Cost = 190,
        });

        Add(new UnitType
        {
            Id = "rome_hastati",
            Name = "Hastati",
            Faction = Faction.Rome,
            Class = UnitClass.Infantry,
            Description = "The first line. Throw the pilum, close with the sword, and hold.",
            Attack = 9, Charge = 5, DefenceSkill = 6, Shield = 4, Armour = 3,
            BonusVsInfantry = 1,
            Morale = 10, Discipline = 8,
            // The pilum is a single close-range volley, not a missile duel.
            Missile = MissileType.Pilum, MissileRange = Fix.FromInt(24),
            MissileAttack = 9, Ammunition = 2, ReloadInterval = Fix.FromInt(6),
            AllowedFormations = FormationMask.Legionary,
            Cost = 400,
        });

        Add(new UnitType
        {
            Id = "rome_principes",
            Name = "Principes",
            Faction = Faction.Rome,
            Class = UnitClass.Infantry,
            Description = "The second line: men in their prime, in mail. They finish what the Hastati start.",
            Attack = 11, Charge = 5, DefenceSkill = 7, Shield = 4, Armour = 6,
            BonusVsInfantry = 1,
            Morale = 12, Discipline = 9,
            WalkSpeed = Fix.Ratio(12, 10), RunSpeed = Fix.Ratio(3, 1),
            Missile = MissileType.Pilum, MissileRange = Fix.FromInt(24),
            MissileAttack = 9, Ammunition = 2, ReloadInterval = Fix.FromInt(6),
            AllowedFormations = FormationMask.Legionary,
            Cost = 560,
        });

        Add(new UnitType
        {
            Id = "rome_triarii",
            Name = "Triarii",
            Faction = Faction.Rome,
            Class = UnitClass.Spear,
            Description = "Veteran spearmen held in reserve. \"It has come down to the Triarii\" was not a good sign.",
            Attack = 10, Charge = 4, DefenceSkill = 9, Shield = 5, Armour = 7,
            BonusVsMounted = 7,
            Morale = 14, Discipline = 10,
            WalkSpeed = Fix.Ratio(11, 10), RunSpeed = Fix.Ratio(28, 10),
            AttackInterval = Fix.Ratio(13, 10),
            DefaultStrength = 80,
            AllowedFormations = FormationMask.Hoplite,
            Cost = 620,
        });

        Add(new UnitType
        {
            Id = "rome_archers",
            Name = "Roman Archers",
            Faction = Faction.Rome,
            Class = UnitClass.Missile,
            Description = "Auxiliary bowmen. Rome never much cared for archery, and it shows.",
            Attack = 4, Charge = 2, DefenceSkill = 3, Shield = 0, Armour = 1,
            Morale = 6, Discipline = 6,
            WalkSpeed = Fix.Ratio(14, 10), RunSpeed = Fix.Ratio(36, 10),
            Missile = MissileType.Bow, MissileRange = Fix.FromInt(130),
            MissileAttack = 7, Ammunition = 30, ReloadInterval = Fix.FromInt(3),
            DefaultStrength = 80, DefaultFormation = FormationType.Line,
            AllowedFormations = FormationMask.Foot,
            Cost = 350,
        });

        Add(new UnitType
        {
            Id = "rome_equites",
            Name = "Equites",
            Faction = Faction.Rome,
            Class = UnitClass.Cavalry,
            Description = "Roman horse. Adequate for chasing skirmishers and finishing broken men.",
            Attack = 9, Charge = 10, DefenceSkill = 6, Shield = 3, Armour = 4,
            BonusVsInfantry = 2,
            Morale = 10, Discipline = 8,
            WalkSpeed = Fix.FromInt(3), RunSpeed = Fix.FromInt(8),
            TurnRate = Fix.Ratio(14, 10), Mass = Fix.FromInt(3), Radius = Fix.Ratio(8, 10),
            FileSpacing = Fix.Ratio(16, 10), RankSpacing = Fix.Ratio(24, 10),
            DefaultStrength = 60, AllowedFormations = FormationMask.Horse,
            Cost = 480,
        });

        Add(new UnitType
        {
            Id = "rome_general",
            Name = "General's Bodyguard",
            Faction = Faction.Rome,
            Class = UnitClass.General,
            Description = "The commander and his companions. Everything nearby steadies; if he falls, everything nearby does not.",
            Attack = 14, Charge = 14, DefenceSkill = 10, Shield = 5, Armour = 8,
            BonusVsInfantry = 2,
            Morale = 17, Discipline = 10,
            WalkSpeed = Fix.FromInt(3), RunSpeed = Fix.Ratio(78, 10),
            TurnRate = Fix.Ratio(14, 10), Mass = Fix.Ratio(35, 10), Radius = Fix.Ratio(8, 10),
            FileSpacing = Fix.Ratio(16, 10), RankSpacing = Fix.Ratio(24, 10),
            DefaultStrength = 24, AllowedFormations = FormationMask.Horse,
            Cost = 1000,
        });
    }

    // ============================================================== CARTHAGE
    //
    // A coalition army: a solid Libyan core, mercenaries with sharp edges, the best
    // slingers and light horse in the world, and elephants. Carthage wins on the flanks.

    private static void AddCarthage()
    {
        Add(new UnitType
        {
            Id = "carthage_libyan_spearmen",
            Name = "Libyan Spearmen",
            Faction = Faction.Carthage,
            Class = UnitClass.Spear,
            Description = "The African core of the army. Steady, shielded, and very hard to ride down.",
            Attack = 8, Charge = 4, DefenceSkill = 8, Shield = 5, Armour = 4,
            BonusVsMounted = 6,
            Morale = 11, Discipline = 8,
            WalkSpeed = Fix.Ratio(12, 10), RunSpeed = Fix.Ratio(3, 1),
            AttackInterval = Fix.Ratio(13, 10),
            // A shielded close-order spear line, not a pike block. The Sacred Band is the
            // phalanx in this army and says so; these men are the shield wall in front of
            // it, and square is their answer to being ridden at. They carried Hoplite
            // until the commander learned to use formations, at which point Carthage put
            // three levelled phalanxes in the line against Rome's one and won thirteen of
            // fourteen — and fourteen of fourteen with the armies swapped, which is how
            // you know it was the roster and not the ground.
            AllowedFormations = FormationMask.Standard,
            Cost = 440,
        });

        Add(new UnitType
        {
            Id = "carthage_poeni",
            Name = "Poeni Infantry",
            Faction = Faction.Carthage,
            Class = UnitClass.Infantry,
            Description = "Carthaginian citizens under arms. Rare, expensive, and they fight like it.",
            Attack = 10, Charge = 6, DefenceSkill = 7, Shield = 5, Armour = 5,
            Morale = 12, Discipline = 8,
            AllowedFormations = FormationMask.Foot,
            Cost = 520,
        });

        Add(new UnitType
        {
            Id = "carthage_sacred_band",
            Name = "Sacred Band Infantry",
            Faction = Faction.Carthage,
            Class = UnitClass.Spear,
            Description = "The elite of Carthage, in a phalanx that does not move and does not break.",
            Attack = 13, Charge = 5, DefenceSkill = 11, Shield = 6, Armour = 8,
            BonusVsMounted = 6,
            Morale = 16, Discipline = 10,
            WalkSpeed = Fix.Ratio(11, 10), RunSpeed = Fix.Ratio(27, 10),
            AttackInterval = Fix.Ratio(13, 10),
            DefaultStrength = 80, DefaultFormation = FormationType.Phalanx,
            AllowedFormations = FormationMask.Hoplite,
            Cost = 800,
        });

        Add(new UnitType
        {
            Id = "carthage_iberian",
            Name = "Iberian Infantry",
            Faction = Faction.Carthage,
            Class = UnitClass.Infantry,
            Description = "Spanish mercenaries with the falcata, a sword that goes through helmets.",
            Attack = 12, Charge = 7, DefenceSkill = 5, Shield = 4, Armour = 2,
            BonusVsInfantry = 1,
            Morale = 10, Discipline = 6,
            WalkSpeed = Fix.Ratio(14, 10), RunSpeed = Fix.Ratio(34, 10),
            AllowedFormations = FormationMask.Foot,
            Cost = 450,
        });

        Add(new UnitType
        {
            Id = "carthage_balearic_slingers",
            Name = "Balearic Slingers",
            Faction = Faction.Carthage,
            Class = UnitClass.Missile,
            Description = "The finest slingers in the world. A lead shot does not care how good your mail is.",
            Attack = 4, Charge = 2, DefenceSkill = 3, Shield = 0, Armour = 0,
            Morale = 7, Discipline = 6,
            WalkSpeed = Fix.Ratio(15, 10), RunSpeed = Fix.Ratio(38, 10),
            Missile = MissileType.Sling, MissileRange = Fix.FromInt(140),
            MissileAttack = 8, Ammunition = 40, ReloadInterval = Fix.Ratio(7, 2),
            ArmourPiercing = Fix.Ratio(6, 10),
            DefaultStrength = 80, DefaultFormation = FormationType.Line,
            AllowedFormations = FormationMask.Foot,
            Cost = 380,
        });

        Add(new UnitType
        {
            Id = "carthage_numidian_cavalry",
            Name = "Numidian Cavalry",
            Faction = Faction.Carthage,
            Class = UnitClass.MissileCavalry,
            Description = "Bareback javelin horse. They will not stand and fight, and they do not need to.",
            Attack = 6, Charge = 6, DefenceSkill = 4, Shield = 2, Armour = 0,
            Morale = 8, Discipline = 5,
            WalkSpeed = Fix.Ratio(35, 10), RunSpeed = Fix.Ratio(95, 10),
            TurnRate = Fix.Ratio(18, 10), Mass = Fix.Ratio(25, 10), Radius = Fix.Ratio(8, 10),
            Missile = MissileType.Javelin, MissileRange = Fix.FromInt(34),
            MissileAttack = 6, Ammunition = 8, ReloadInterval = Fix.Ratio(7, 2),
            FileSpacing = Fix.Ratio(18, 10), RankSpacing = Fix.Ratio(26, 10),
            DefaultStrength = 60, DefaultFormation = FormationType.Skirmish,
            AllowedFormations = FormationMask.Horse | FormationMask.Skirmish,
            Cost = 420,
        });

        Add(new UnitType
        {
            Id = "carthage_sacred_band_cavalry",
            Name = "Sacred Band Cavalry",
            Faction = Faction.Carthage,
            Class = UnitClass.Cavalry,
            Description = "Heavy horse from the noblest houses. The hammer for the Libyan anvil.",
            Attack = 12, Charge = 13, DefenceSkill = 9, Shield = 4, Armour = 7,
            BonusVsInfantry = 2,
            Morale = 14, Discipline = 9,
            WalkSpeed = Fix.FromInt(3), RunSpeed = Fix.Ratio(79, 10),
            TurnRate = Fix.Ratio(13, 10), Mass = Fix.Ratio(32, 10), Radius = Fix.Ratio(8, 10),
            FileSpacing = Fix.Ratio(16, 10), RankSpacing = Fix.Ratio(24, 10),
            DefaultStrength = 60, AllowedFormations = FormationMask.Horse,
            Cost = 700,
        });

        Add(new UnitType
        {
            Id = "carthage_elephants",
            Name = "War Elephants",
            Faction = Faction.Carthage,
            Class = UnitClass.Elephant,
            Description = "They will shatter any line they hit. They will also, eventually, shatter yours.",
            Attack = 12, Charge = 16, DefenceSkill = 8, Shield = 0, Armour = 6,
            Hitpoints = 8, BonusVsInfantry = 4, AttacksPerStrike = 4,
            Morale = 12, Discipline = 3,
            CausesFear = true, ImmuneToFear = true,
            WalkSpeed = Fix.Ratio(18, 10), RunSpeed = Fix.Ratio(45, 10),
            TurnRate = Fix.Ratio(8, 10), Mass = Fix.FromInt(12), Radius = Fix.Ratio(15, 10),
            AttackInterval = Fix.Ratio(16, 10),
            FileSpacing = Fix.FromInt(4), RankSpacing = Fix.FromInt(5),
            DefaultStrength = 12, AllowedFormations = FormationMask.Line | FormationMask.Column,
            Cost = 900,
        });

        Add(new UnitType
        {
            Id = "carthage_general",
            Name = "General's Bodyguard",
            Faction = Faction.Carthage,
            Class = UnitClass.General,
            Description = "The commander and his companions.",
            Attack = 14, Charge = 14, DefenceSkill = 10, Shield = 5, Armour = 8,
            BonusVsInfantry = 2,
            Morale = 17, Discipline = 10,
            WalkSpeed = Fix.FromInt(3), RunSpeed = Fix.Ratio(78, 10),
            TurnRate = Fix.Ratio(14, 10), Mass = Fix.Ratio(35, 10), Radius = Fix.Ratio(8, 10),
            FileSpacing = Fix.Ratio(16, 10), RankSpacing = Fix.Ratio(24, 10),
            DefaultStrength = 24, AllowedFormations = FormationMask.Horse,
            Cost = 1000,
        });
    }

    // ================================================================== GAUL
    //
    // Everything front-loaded. Enormous first charge, terrible discipline, no reserve
    // worth the name. Gaul wins in the first minute or not at all.

    private static void AddGaul()
    {
        Add(new UnitType
        {
            Id = "gaul_warband",
            Name = "Gallic Warband",
            Faction = Faction.Gaul,
            Class = UnitClass.Infantry,
            Description = "Free men with spears and courage. Numerous, furious, and brittle.",
            Attack = 9, Charge = 8, DefenceSkill = 3, Shield = 3, Armour = 0,
            Morale = 9, Discipline = 3,
            WalkSpeed = Fix.Ratio(14, 10), RunSpeed = Fix.Ratio(35, 10),
            DefaultStrength = 140,
            AllowedFormations = FormationMask.Foot,
            Cost = 280,
        });

        Add(new UnitType
        {
            Id = "gaul_swordsmen",
            Name = "Gallic Swordsmen",
            Faction = Faction.Gaul,
            Class = UnitClass.Infantry,
            Description = "Long iron swords and mail for those who can afford it. The best of the tribes.",
            Attack = 12, Charge = 8, DefenceSkill = 5, Shield = 4, Armour = 3,
            BonusVsInfantry = 1,
            Morale = 11, Discipline = 5,
            WalkSpeed = Fix.Ratio(14, 10), RunSpeed = Fix.Ratio(34, 10),
            AllowedFormations = FormationMask.Foot,
            Cost = 460,
        });

        Add(new UnitType
        {
            Id = "gaul_fanatics",
            Name = "Naked Fanatics",
            Faction = Faction.Gaul,
            Class = UnitClass.Infantry,
            Description = "They fight without armour to show they do not need it. For about a minute, they are right.",
            Attack = 14, Charge = 12, DefenceSkill = 2, Shield = 0, Armour = 0,
            Morale = 17, Discipline = 2,
            CausesFear = true, ImmuneToFear = true,
            WalkSpeed = Fix.Ratio(16, 10), RunSpeed = Fix.Ratio(4, 1),
            AttackInterval = Fix.One,
            DefaultStrength = 80,
            AllowedFormations = FormationMask.Line | FormationMask.Column,
            Cost = 500,
        });

        Add(new UnitType
        {
            Id = "gaul_skirmishers",
            Name = "Gallic Skirmishers",
            Faction = Faction.Gaul,
            Class = UnitClass.Missile,
            Description = "Javelins and speed. Bleed the enemy line before the warbands hit it.",
            Attack = 5, Charge = 3, DefenceSkill = 3, Shield = 2, Armour = 0,
            Morale = 6, Discipline = 3,
            WalkSpeed = Fix.Ratio(15, 10), RunSpeed = Fix.Ratio(39, 10),
            Missile = MissileType.Javelin, MissileRange = Fix.FromInt(32),
            MissileAttack = 7, Ammunition = 7, ReloadInterval = Fix.FromInt(4),
            DefaultStrength = 80, DefaultFormation = FormationType.Skirmish,
            AllowedFormations = FormationMask.Foot,
            Cost = 200,
        });

        Add(new UnitType
        {
            Id = "gaul_slingers",
            Name = "Gallic Slingers",
            Faction = Faction.Gaul,
            Class = UnitClass.Missile,
            Description = "Cheap, long-ranged, and useless the moment anything reaches them.",
            Attack = 3, Charge = 2, DefenceSkill = 2, Shield = 0, Armour = 0,
            Morale = 5, Discipline = 3,
            WalkSpeed = Fix.Ratio(15, 10), RunSpeed = Fix.Ratio(38, 10),
            Missile = MissileType.Sling, MissileRange = Fix.FromInt(125),
            MissileAttack = 6, Ammunition = 35, ReloadInterval = Fix.Ratio(7, 2),
            ArmourPiercing = Fix.Ratio(4, 10),
            DefaultStrength = 80,
            AllowedFormations = FormationMask.Foot,
            Cost = 220,
        });

        Add(new UnitType
        {
            Id = "gaul_cavalry",
            Name = "Gallic Cavalry",
            Faction = Faction.Gaul,
            Class = UnitClass.Cavalry,
            Description = "The best horsemen in the west, and they know it.",
            Attack = 10, Charge = 11, DefenceSkill = 5, Shield = 3, Armour = 3,
            BonusVsInfantry = 2,
            Morale = 11, Discipline = 5,
            WalkSpeed = Fix.Ratio(31, 10), RunSpeed = Fix.Ratio(84, 10),
            TurnRate = Fix.Ratio(15, 10), Mass = Fix.FromInt(3), Radius = Fix.Ratio(8, 10),
            FileSpacing = Fix.Ratio(16, 10), RankSpacing = Fix.Ratio(24, 10),
            DefaultStrength = 60, AllowedFormations = FormationMask.Horse,
            Cost = 520,
        });

        Add(new UnitType
        {
            Id = "gaul_chariots",
            Name = "Gallic Chariots",
            Faction = Faction.Gaul,
            Class = UnitClass.Chariot,
            Description = "Obsolete everywhere else. Still terrifying if you have never seen one.",
            Attack = 8, Charge = 14, DefenceSkill = 5, Shield = 2, Armour = 2,
            Hitpoints = 4, BonusVsInfantry = 3, AttacksPerStrike = 3,
            Morale = 11, Discipline = 4,
            CausesFear = true,
            WalkSpeed = Fix.Ratio(32, 10), RunSpeed = Fix.FromInt(9),
            TurnRate = Fix.Ratio(7, 10), Mass = Fix.FromInt(5), Radius = Fix.Ratio(12, 10),
            FileSpacing = Fix.Ratio(3, 1), RankSpacing = Fix.Ratio(4, 1),
            DefaultStrength = 24, AllowedFormations = FormationMask.Line | FormationMask.Column,
            Cost = 640,
        });

        Add(new UnitType
        {
            Id = "gaul_general",
            Name = "Chieftain's Bodyguard",
            Faction = Faction.Gaul,
            Class = UnitClass.General,
            Description = "The chieftain and his sworn men.",
            Attack = 14, Charge = 15, DefenceSkill = 8, Shield = 4, Armour = 5,
            BonusVsInfantry = 2,
            Morale = 16, Discipline = 7,
            WalkSpeed = Fix.Ratio(31, 10), RunSpeed = Fix.Ratio(82, 10),
            TurnRate = Fix.Ratio(15, 10), Mass = Fix.Ratio(32, 10), Radius = Fix.Ratio(8, 10),
            FileSpacing = Fix.Ratio(16, 10), RankSpacing = Fix.Ratio(24, 10),
            DefaultStrength = 24, AllowedFormations = FormationMask.Horse,
            Cost = 950,
        });
    }

    // ================================================================ GREECE
    //
    // The phalanx and everything that supports it. Unbreakable from the front,
    // and the entire game is about whether the flanks hold.

    private static void AddGreece()
    {
        Add(new UnitType
        {
            Id = "greece_militia_hoplites",
            Name = "Militia Hoplites",
            Faction = Faction.Greece,
            Class = UnitClass.Spear,
            Description = "Citizens with a spear and a big shield. The phalanx does the work, not the men.",
            Attack = 6, Charge = 3, DefenceSkill = 8, Shield = 6, Armour = 2,
            BonusVsMounted = 5,
            Morale = 8, Discipline = 6,
            WalkSpeed = Fix.Ratio(12, 10), RunSpeed = Fix.Ratio(29, 10),
            AttackInterval = Fix.Ratio(13, 10),
            DefaultFormation = FormationType.Phalanx,
            AllowedFormations = FormationMask.Hoplite,
            Cost = 300,
        });

        Add(new UnitType
        {
            Id = "greece_hoplites",
            Name = "Hoplites",
            Faction = Faction.Greece,
            Class = UnitClass.Spear,
            Description = "The classical heavy infantry of the Greek world.",
            Attack = 8, Charge = 4, DefenceSkill = 10, Shield = 6, Armour = 4,
            BonusVsMounted = 6,
            Morale = 11, Discipline = 8,
            WalkSpeed = Fix.Ratio(12, 10), RunSpeed = Fix.Ratio(29, 10),
            AttackInterval = Fix.Ratio(13, 10),
            DefaultFormation = FormationType.Phalanx,
            AllowedFormations = FormationMask.Hoplite,
            Cost = 480,
        });

        Add(new UnitType
        {
            Id = "greece_armoured_hoplites",
            Name = "Armoured Hoplites",
            Faction = Faction.Greece,
            Class = UnitClass.Spear,
            Description = "Bronze cuirass, greaves, and the aspis. Very slow, very hard to move.",
            Attack = 9, Charge = 4, DefenceSkill = 12, Shield = 7, Armour = 7,
            BonusVsMounted = 6,
            Morale = 13, Discipline = 9,
            WalkSpeed = Fix.Ratio(11, 10), RunSpeed = Fix.Ratio(26, 10),
            AttackInterval = Fix.Ratio(13, 10),
            DefaultFormation = FormationType.Phalanx,
            AllowedFormations = FormationMask.Hoplite,
            Cost = 640,
        });

        Add(new UnitType
        {
            Id = "greece_spartans",
            Name = "Spartan Hoplites",
            Faction = Faction.Greece,
            Class = UnitClass.Spear,
            Description = "Professional soldiers in a world of farmers. They do not break.",
            Attack = 12, Charge = 5, DefenceSkill = 15, Shield = 8, Armour = 8,
            BonusVsMounted = 7,
            Morale = 18, Discipline = 10,
            WalkSpeed = Fix.Ratio(12, 10), RunSpeed = Fix.Ratio(28, 10),
            AttackInterval = Fix.Ratio(12, 10),
            DefaultStrength = 80, DefaultFormation = FormationType.Phalanx,
            AllowedFormations = FormationMask.Hoplite,
            Cost = 950,
        });

        Add(new UnitType
        {
            Id = "greece_peltasts",
            Name = "Peltasts",
            Faction = Faction.Greece,
            Class = UnitClass.Missile,
            Description = "Javelin skirmishers who learned the hard way that a phalanx cannot catch them.",
            Attack = 6, Charge = 3, DefenceSkill = 4, Shield = 3, Armour = 0,
            Morale = 7, Discipline = 6,
            WalkSpeed = Fix.Ratio(15, 10), RunSpeed = Fix.Ratio(4, 1),
            Missile = MissileType.Javelin, MissileRange = Fix.FromInt(34),
            MissileAttack = 8, Ammunition = 7, ReloadInterval = Fix.Ratio(38, 10),
            DefaultStrength = 80, DefaultFormation = FormationType.Skirmish,
            AllowedFormations = FormationMask.Foot,
            Cost = 300,
        });

        Add(new UnitType
        {
            Id = "greece_cretan_archers",
            Name = "Cretan Archers",
            Faction = Faction.Greece,
            Class = UnitClass.Missile,
            Description = "Mercenary bowmen from Crete, and worth every drachma.",
            Attack = 5, Charge = 2, DefenceSkill = 4, Shield = 2, Armour = 1,
            Morale = 8, Discipline = 7,
            WalkSpeed = Fix.Ratio(14, 10), RunSpeed = Fix.Ratio(37, 10),
            Missile = MissileType.Bow, MissileRange = Fix.FromInt(150),
            MissileAttack = 10, Ammunition = 32, ReloadInterval = Fix.Ratio(27, 10),
            DefaultStrength = 80,
            AllowedFormations = FormationMask.Foot,
            Cost = 520,
        });

        Add(new UnitType
        {
            Id = "greece_cavalry",
            Name = "Greek Cavalry",
            Faction = Faction.Greece,
            Class = UnitClass.Cavalry,
            Description = "Aristocrats on horseback. Useful for the flank, not for the line.",
            Attack = 8, Charge = 10, DefenceSkill = 6, Shield = 3, Armour = 4,
            BonusVsInfantry = 2,
            Morale = 10, Discipline = 7,
            WalkSpeed = Fix.FromInt(3), RunSpeed = Fix.FromInt(8),
            TurnRate = Fix.Ratio(14, 10), Mass = Fix.FromInt(3), Radius = Fix.Ratio(8, 10),
            FileSpacing = Fix.Ratio(16, 10), RankSpacing = Fix.Ratio(24, 10),
            DefaultStrength = 60, AllowedFormations = FormationMask.Horse,
            Cost = 480,
        });

        Add(new UnitType
        {
            Id = "greece_general",
            Name = "Strategos' Bodyguard",
            Faction = Faction.Greece,
            Class = UnitClass.General,
            Description = "The strategos and his companions.",
            Attack = 14, Charge = 13, DefenceSkill = 11, Shield = 5, Armour = 8,
            BonusVsInfantry = 2,
            Morale = 17, Discipline = 10,
            WalkSpeed = Fix.FromInt(3), RunSpeed = Fix.Ratio(78, 10),
            TurnRate = Fix.Ratio(14, 10), Mass = Fix.Ratio(35, 10), Radius = Fix.Ratio(8, 10),
            FileSpacing = Fix.Ratio(16, 10), RankSpacing = Fix.Ratio(24, 10),
            DefaultStrength = 24, AllowedFormations = FormationMask.Horse,
            Cost = 1000,
        });
    }

    // ================================================================= EGYPT
    //
    // Mass infantry, superb archery, and chariots that have no business still being
    // on a battlefield in this century but remain genuinely frightening.

    private static void AddEgypt()
    {
        Add(new UnitType
        {
            Id = "egypt_nile_spearmen",
            Name = "Nile Spearmen",
            Faction = Faction.Egypt,
            Class = UnitClass.Spear,
            Description = "Levies from the river villages. Cheap, numerous, and they hold longer than they should.",
            Attack = 6, Charge = 3, DefenceSkill = 6, Shield = 4, Armour = 1,
            BonusVsMounted = 5,
            Morale = 8, Discipline = 6,
            WalkSpeed = Fix.Ratio(13, 10), RunSpeed = Fix.Ratio(31, 10),
            AttackInterval = Fix.Ratio(13, 10),
            DefaultStrength = 140,
            AllowedFormations = FormationMask.Hoplite,
            Cost = 250,
        });

        Add(new UnitType
        {
            Id = "egypt_infantry",
            Name = "Egyptian Infantry",
            Faction = Faction.Egypt,
            Class = UnitClass.Infantry,
            Description = "Axemen with the khopesh. They get through shields.",
            Attack = 10, Charge = 6, DefenceSkill = 5, Shield = 4, Armour = 3,
            BonusVsInfantry = 1,
            Morale = 10, Discipline = 7,
            AllowedFormations = FormationMask.Foot,
            Cost = 420,
        });

        Add(new UnitType
        {
            Id = "egypt_nubian_spearmen",
            Name = "Nubian Spearmen",
            Faction = Faction.Egypt,
            Class = UnitClass.Spear,
            Description = "Southern mercenaries. Fast for spearmen, and fierce.",
            Attack = 8, Charge = 5, DefenceSkill = 5, Shield = 3, Armour = 0,
            BonusVsMounted = 5,
            Morale = 10, Discipline = 5,
            WalkSpeed = Fix.Ratio(14, 10), RunSpeed = Fix.Ratio(34, 10),
            AllowedFormations = FormationMask.Foot,
            Cost = 330,
        });

        Add(new UnitType
        {
            Id = "egypt_bowmen",
            Name = "Pharaoh's Bowmen",
            Faction = Faction.Egypt,
            Class = UnitClass.Missile,
            Description = "Composite bows and a very long tradition of using them well.",
            Attack = 5, Charge = 2, DefenceSkill = 4, Shield = 2, Armour = 2,
            Morale = 9, Discipline = 8,
            WalkSpeed = Fix.Ratio(14, 10), RunSpeed = Fix.Ratio(35, 10),
            Missile = MissileType.Bow, MissileRange = Fix.FromInt(145),
            MissileAttack = 9, Ammunition = 34, ReloadInterval = Fix.Ratio(28, 10),
            DefaultStrength = 100,
            AllowedFormations = FormationMask.Foot,
            Cost = 500,
        });

        Add(new UnitType
        {
            Id = "egypt_desert_cavalry",
            Name = "Desert Cavalry",
            Faction = Faction.Egypt,
            Class = UnitClass.Cavalry,
            Description = "Light horse bred for heat and distance.",
            Attack = 7, Charge = 9, DefenceSkill = 5, Shield = 3, Armour = 2,
            BonusVsInfantry = 2,
            Morale = 9, Discipline = 6,
            WalkSpeed = Fix.Ratio(32, 10), RunSpeed = Fix.Ratio(86, 10),
            TurnRate = Fix.Ratio(16, 10), Mass = Fix.Ratio(28, 10), Radius = Fix.Ratio(8, 10),
            FileSpacing = Fix.Ratio(16, 10), RankSpacing = Fix.Ratio(24, 10),
            DefaultStrength = 60, AllowedFormations = FormationMask.Horse,
            Cost = 440,
        });

        Add(new UnitType
        {
            Id = "egypt_chariots",
            Name = "Egyptian Chariots",
            Faction = Faction.Egypt,
            Class = UnitClass.Chariot,
            Description = "Archers on wheels. They shoot your line apart and then drive through the gap.",
            Attack = 7, Charge = 12, DefenceSkill = 5, Shield = 2, Armour = 3,
            Hitpoints = 4, BonusVsInfantry = 3, AttacksPerStrike = 3,
            Morale = 11, Discipline = 6,
            CausesFear = true,
            WalkSpeed = Fix.FromInt(3), RunSpeed = Fix.Ratio(85, 10),
            TurnRate = Fix.Ratio(7, 10), Mass = Fix.FromInt(5), Radius = Fix.Ratio(12, 10),
            Missile = MissileType.Bow, MissileRange = Fix.FromInt(110),
            MissileAttack = 7, Ammunition = 20, ReloadInterval = Fix.FromInt(3),
            FileSpacing = Fix.Ratio(3, 1), RankSpacing = Fix.Ratio(4, 1),
            DefaultStrength = 24, AllowedFormations = FormationMask.Line | FormationMask.Column,
            Cost = 700,
        });

        Add(new UnitType
        {
            Id = "egypt_general",
            Name = "Pharaoh's Guard",
            Faction = Faction.Egypt,
            Class = UnitClass.General,
            Description = "The commander and his household.",
            Attack = 13, Charge = 13, DefenceSkill = 10, Shield = 5, Armour = 7,
            BonusVsInfantry = 2,
            Morale = 16, Discipline = 9,
            WalkSpeed = Fix.FromInt(3), RunSpeed = Fix.Ratio(8, 1),
            TurnRate = Fix.Ratio(14, 10), Mass = Fix.Ratio(33, 10), Radius = Fix.Ratio(8, 10),
            FileSpacing = Fix.Ratio(16, 10), RankSpacing = Fix.Ratio(24, 10),
            DefaultStrength = 24, AllowedFormations = FormationMask.Horse,
            Cost = 950,
        });
    }
}
