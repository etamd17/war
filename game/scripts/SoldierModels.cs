using System.Collections.Generic;
using Godot;
using War.Sim.Units;

namespace War.Game;

/// <summary>
/// Builds the figure for each kind of soldier, once, at load.
///
/// These are placeholders and are meant to be. What they have to do is be <em>readable
/// at battle-camera distance</em>: you should be able to glance at a line and know it is
/// spearmen rather than swordsmen, whose it is, and which way it is facing. Silhouette
/// and colour do all of that work; polygon count does none of it.
///
/// One mesh per faction-and-class combination, cached — around twenty in total. The
/// faction colour is baked into the tunic and shield rather than tinted per instance,
/// so per-instance colour stays free for the thing that changes: whether a man is still
/// standing.
/// </summary>
public static class SoldierModels
{
    private static readonly Dictionary<(Faction, UnitClass, bool), ArrayMesh> Cache = new();

    private static readonly Color Skin = new(0.76f, 0.60f, 0.47f);
    private static readonly Color Iron = new(0.55f, 0.57f, 0.60f);
    private static readonly Color Bronze = new(0.68f, 0.55f, 0.28f);
    private static readonly Color Wood = new(0.42f, 0.31f, 0.19f);
    private static readonly Color Leather = new(0.36f, 0.27f, 0.19f);
    private static readonly Color Horsehide = new(0.32f, 0.24f, 0.18f);
    private static readonly Color Hide = new(0.46f, 0.45f, 0.44f);

    public static ArrayMesh For(UnitType type)
    {
        bool shielded = type.Shield > 0;
        var key = (type.Faction, type.Class, shielded);

        if (Cache.TryGetValue(key, out ArrayMesh? cached)) return cached;

        Color faction = SimBridge.FactionColour(type.Faction);

        ArrayMesh mesh = type.Class switch
        {
            UnitClass.Elephant => Elephant(faction),
            UnitClass.Chariot => Chariot(faction),
            UnitClass.Cavalry or UnitClass.MissileCavalry or UnitClass.General =>
                Rider(faction, type.Class, shielded),
            _ => Foot(faction, type.Class, shielded),
        };

        Cache[key] = mesh;
        return mesh;
    }

    // -------------------------------------------------------------------- foot

    /// <summary>A man on foot, about 1.8 metres, facing +Z.</summary>
    private static ArrayMesh Foot(Color faction, UnitClass unitClass, bool shielded)
    {
        var b = new MeshBuilder();

        b.AddBox(new Vector3(0, 0.42f, 0), new Vector3(0.34f, 0.84f, 0.24f), Leather);   // legs
        b.AddBox(new Vector3(0, 1.10f, 0), new Vector3(0.44f, 0.60f, 0.28f), faction);   // tunic
        b.AddBox(new Vector3(0, 1.50f, 0), new Vector3(0.22f, 0.22f, 0.22f), Skin);      // head
        b.AddBox(new Vector3(0, 1.63f, 0), new Vector3(0.26f, 0.12f, 0.26f), Iron);      // helmet

        if (shielded)
        {
            // Carried on the left arm, angled slightly across the body — which is also
            // exactly why the shield only counts against attacks from the front and left.
            b.AddRotatedBox(new Vector3(-0.26f, 1.05f, 0.16f),
                new Vector3(0.62f, 0.80f, 0.08f), 0.25f, faction);
            b.AddRotatedBox(new Vector3(-0.26f, 1.05f, 0.20f),
                new Vector3(0.20f, 0.20f, 0.06f), 0.25f, Bronze);
        }

        switch (unitClass)
        {
            case UnitClass.Spear:
            case UnitClass.Pike:
                // Long shaft carried upright and forward — the giveaway silhouette.
                b.AddBox(new Vector3(0.26f, 1.60f, 0.30f), new Vector3(0.06f, 2.90f, 0.06f), Wood);
                b.AddCone(new Vector3(0.26f, 3.02f, 0.30f), 0.09f, 0.34f, Iron);
                break;

            case UnitClass.Missile:
                // A stave held vertically reads as a bow at any distance.
                b.AddBox(new Vector3(0.24f, 1.20f, 0.10f), new Vector3(0.05f, 1.15f, 0.10f), Wood);
                break;

            default:
                b.AddBox(new Vector3(0.28f, 1.02f, 0.06f), new Vector3(0.07f, 0.70f, 0.07f), Iron);
                break;
        }

        return b.Build();
    }

    // ------------------------------------------------------------------- horse

    /// <summary>A rider, facing +Z. Roughly 2.6 metres to the head.</summary>
    private static ArrayMesh Rider(Color faction, UnitClass unitClass, bool shielded)
    {
        var b = new MeshBuilder();

        // Horse
        b.AddBox(new Vector3(0, 1.15f, 0), new Vector3(0.60f, 0.80f, 2.00f), Horsehide);
        b.AddBox(new Vector3(0, 1.45f, 0.95f), new Vector3(0.40f, 0.70f, 0.45f), Horsehide);   // neck
        b.AddBox(new Vector3(0, 1.62f, 1.25f), new Vector3(0.28f, 0.32f, 0.62f), Horsehide);   // head

        foreach (float x in new[] { -0.22f, 0.22f })
        {
            foreach (float z in new[] { -0.70f, 0.70f })
                b.AddBox(new Vector3(x, 0.38f, z), new Vector3(0.15f, 0.78f, 0.16f), Horsehide);
        }

        // Rider
        b.AddBox(new Vector3(0, 1.90f, -0.05f), new Vector3(0.42f, 0.58f, 0.28f), faction);
        b.AddBox(new Vector3(0, 2.28f, -0.05f), new Vector3(0.21f, 0.21f, 0.21f), Skin);
        b.AddBox(new Vector3(0, 2.40f, -0.05f), new Vector3(0.25f, 0.12f, 0.25f), Iron);

        // A general flies a crest, so you can pick him out of a battle line.
        if (unitClass == UnitClass.General)
            b.AddBox(new Vector3(0, 2.56f, -0.05f), new Vector3(0.06f, 0.22f, 0.34f), faction);

        if (shielded)
            b.AddRotatedBox(new Vector3(-0.28f, 1.86f, 0.10f),
                new Vector3(0.52f, 0.62f, 0.07f), 0.25f, faction);

        b.AddBox(new Vector3(0.28f, 2.10f, 0.40f), new Vector3(0.06f, 0.06f, 2.20f), Wood);
        b.AddCone(new Vector3(0.28f, 2.10f, 1.50f), 0.08f, 0.30f, Iron);

        return b.Build();
    }

    // ---------------------------------------------------------------- elephant

    private static ArrayMesh Elephant(Color faction)
    {
        var b = new MeshBuilder();

        b.AddBox(new Vector3(0, 2.10f, 0), new Vector3(1.70f, 1.70f, 3.20f), Hide);
        b.AddBox(new Vector3(0, 2.35f, 1.85f), new Vector3(1.15f, 1.25f, 0.90f), Hide);      // head
        b.AddBox(new Vector3(0, 1.55f, 2.35f), new Vector3(0.34f, 1.50f, 0.34f), Hide);      // trunk
        b.AddBox(new Vector3(-0.85f, 2.55f, 2.05f), new Vector3(0.55f, 0.10f, 0.10f), Hide); // ears
        b.AddBox(new Vector3(0.85f, 2.55f, 2.05f), new Vector3(0.55f, 0.10f, 0.10f), Hide);

        // Tusks
        b.AddBox(new Vector3(-0.38f, 1.95f, 2.30f), new Vector3(0.11f, 0.11f, 0.95f), new Color(0.88f, 0.86f, 0.78f));
        b.AddBox(new Vector3(0.38f, 1.95f, 2.30f), new Vector3(0.11f, 0.11f, 0.95f), new Color(0.88f, 0.86f, 0.78f));

        foreach (float x in new[] { -0.62f, 0.62f })
        {
            foreach (float z in new[] { -1.10f, 1.05f })
                b.AddBox(new Vector3(x, 0.63f, z), new Vector3(0.44f, 1.30f, 0.46f), Hide);
        }

        // The howdah, in faction colours — the part you actually see across a field.
        b.AddBox(new Vector3(0, 3.20f, -0.30f), new Vector3(1.40f, 0.55f, 1.60f), Wood);
        b.AddBox(new Vector3(0, 3.62f, -0.30f), new Vector3(1.50f, 0.35f, 1.70f), faction);
        b.AddBox(new Vector3(0, 3.95f, -0.30f), new Vector3(0.35f, 0.40f, 0.30f), Skin);

        return b.Build();
    }

    // ----------------------------------------------------------------- chariot

    private static ArrayMesh Chariot(Color faction)
    {
        var b = new MeshBuilder();

        // Two horses abreast
        foreach (float x in new[] { -0.55f, 0.55f })
        {
            b.AddBox(new Vector3(x, 1.10f, 1.30f), new Vector3(0.52f, 0.72f, 1.80f), Horsehide);
            b.AddBox(new Vector3(x, 1.45f, 2.20f), new Vector3(0.30f, 0.55f, 0.55f), Horsehide);
            b.AddBox(new Vector3(x, 0.36f, 0.75f), new Vector3(0.14f, 0.72f, 0.15f), Horsehide);
            b.AddBox(new Vector3(x, 0.36f, 1.90f), new Vector3(0.14f, 0.72f, 0.15f), Horsehide);
        }

        // Pole, axle, car
        b.AddBox(new Vector3(0, 0.85f, 0.70f), new Vector3(0.12f, 0.12f, 1.80f), Wood);
        b.AddBox(new Vector3(0, 0.62f, -0.30f), new Vector3(1.70f, 0.14f, 0.14f), Wood);
        b.AddBox(new Vector3(0, 0.90f, -0.25f), new Vector3(1.05f, 0.62f, 0.85f), faction);

        // Wheels, as flat boxes — at this distance nobody counts spokes.
        foreach (float x in new[] { -0.82f, 0.82f })
            b.AddBox(new Vector3(x, 0.62f, -0.30f), new Vector3(0.10f, 1.20f, 1.20f), Wood);

        b.AddBox(new Vector3(0, 1.55f, -0.25f), new Vector3(0.40f, 0.58f, 0.26f), faction);
        b.AddBox(new Vector3(0, 1.94f, -0.25f), new Vector3(0.21f, 0.21f, 0.21f), Skin);

        return b.Build();
    }

    // ------------------------------------------------------------------ banner

    /// <summary>
    /// The unit standard. Banners are how a battle is read at a glance — you track the
    /// shape of the line by where the standards are, not by counting men.
    /// </summary>
    public static ArrayMesh Banner(Color faction)
    {
        return new MeshBuilder()
            .AddBox(new Vector3(0, 1.7f, 0), new Vector3(0.09f, 3.4f, 0.09f), Wood)
            .AddBox(new Vector3(0.55f, 3.05f, 0), new Vector3(1.05f, 0.75f, 0.05f), faction)
            .AddBox(new Vector3(0, 3.55f, 0), new Vector3(0.22f, 0.22f, 0.22f), Bronze)
            .Build();
    }
}
