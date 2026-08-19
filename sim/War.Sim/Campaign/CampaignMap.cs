using War.Sim.Core;
using War.Sim.Units;

namespace War.Sim.Campaign;

/// <summary>
/// The Mediterranean, c. 265 BC — the eve of the first war between Rome and Carthage.
///
/// Thirty-two provinces, five powers and four independent regions to expand into before
/// anyone has to declare on anyone. The shape of the map is the shape of the history:
/// Rome holds the Italian peninsula and nothing else, Carthage holds the sea around it —
/// Sicily, Sardinia, the African coast, southern Iberia — and the two of them are pressed
/// against each other across the straits of Messina with no room to grow anywhere but
/// through the other. Gaul sits on Rome's northern door, and Greece and Egypt are far
/// enough east to be somebody else's problem for the first twenty turns.
///
/// Adjacency is declared as an undirected list of links rather than per-province arrays,
/// because a hand-written neighbour list is one typo away from a province you can march
/// into but not out of, and that kind of bug hides for a very long time behind an AI that
/// simply appears to be indecisive.
/// </summary>
public static class CampaignMap
{
    /// <summary>Fresh map at its starting positions. Mutable, so every campaign gets its own.</summary>
    public static List<Province> Create()
    {
        var provinces = new List<Province>();
        int next = 0;

        Province Add(string name, int x, int y, Landscape landscape, int wealth, Faction? owner)
        {
            var province = new Province
            {
                Id = next++,
                Name = name,
                Position = new FixVec2(Fix.FromInt(x), Fix.FromInt(y)),
                Landscape = landscape,
                Wealth = wealth,
                Owner = owner,
            };
            provinces.Add(province);
            return province;
        }

        // ---- Iberia
        Province lusitania = Add("Lusitania", 6, 52, Landscape.Hills, 260, null);
        Province baetica = Add("Baetica", 10, 45, Landscape.Farmland, 420, Faction.Carthage);
        Province tarraco = Add("Tarraconensis", 16, 55, Landscape.Hills, 300, null);
        Province carthaginensis = Add("Carthaginensis", 15, 47, Landscape.Coastal, 460, Faction.Carthage);

        // ---- Gaul
        Province aquitania = Add("Aquitania", 16, 65, Landscape.Farmland, 340, Faction.Gaul);
        Province narbonensis = Add("Narbonensis", 21, 60, Landscape.Coastal, 400, Faction.Gaul);
        Province celtica = Add("Celtica", 20, 72, Landscape.Forest, 300, Faction.Gaul);
        Province belgica = Add("Belgica", 25, 78, Landscape.Forest, 280, Faction.Gaul);
        Province cisalpina = Add("Cisalpine Gaul", 28, 68, Landscape.Farmland, 380, Faction.Gaul);

        // ---- Italy
        Province etruria = Add("Etruria", 30, 62, Landscape.Hills, 380, Faction.Rome);
        Province latium = Add("Latium", 32, 58, Landscape.Farmland, 520, Faction.Rome);
        Province samnium = Add("Samnium", 34, 56, Landscape.Hills, 300, Faction.Rome);
        Province apulia = Add("Apulia", 37, 54, Landscape.Farmland, 360, Faction.Rome);
        Province bruttium = Add("Bruttium", 36, 49, Landscape.Coastal, 340, Faction.Rome);

        // ---- The islands, which is where the first war will be fought
        Province sicilia = Add("Sicilia", 34, 44, Landscape.Coastal, 480, Faction.Carthage);
        Province sardinia = Add("Sardinia", 28, 52, Landscape.Hills, 300, Faction.Carthage);
        Province corsica = Add("Corsica", 28, 58, Landscape.Forest, 220, null);

        // ---- Africa
        Province zeugitana = Add("Zeugitana", 31, 40, Landscape.Coastal, 620, Faction.Carthage);
        Province numidia = Add("Numidia", 24, 39, Landscape.Desert, 280, Faction.Carthage);
        Province mauretania = Add("Mauretania", 12, 38, Landscape.Desert, 240, null);
        Province tripolitania = Add("Tripolitania", 36, 36, Landscape.Desert, 300, Faction.Carthage);
        Province cyrenaica = Add("Cyrenaica", 44, 35, Landscape.Desert, 320, Faction.Egypt);

        // ---- Greece
        Province epirus = Add("Epirus", 42, 55, Landscape.Hills, 300, Faction.Greece);
        Province macedonia = Add("Macedonia", 45, 60, Landscape.Hills, 420, Faction.Greece);
        Province thessaly = Add("Thessaly", 46, 56, Landscape.Farmland, 380, Faction.Greece);
        Province attica = Add("Attica", 48, 52, Landscape.Coastal, 460, Faction.Greece);
        Province peloponnese = Add("Peloponnese", 45, 49, Landscape.Hills, 340, Faction.Greece);
        Province crete = Add("Crete", 49, 43, Landscape.Coastal, 280, Faction.Greece);

        // ---- Egypt and the east
        Province alexandria = Add("Alexandria", 54, 35, Landscape.Coastal, 680, Faction.Egypt);
        Province thebais = Add("Thebais", 56, 27, Landscape.Desert, 380, Faction.Egypt);
        Province judaea = Add("Judaea", 60, 42, Landscape.Hills, 360, Faction.Egypt);
        Province cyprus = Add("Cyprus", 58, 45, Landscape.Coastal, 300, Faction.Egypt);

        Link(provinces,
            // Iberia
            (lusitania, baetica), (lusitania, tarraco), (baetica, tarraco),
            (baetica, carthaginensis), (tarraco, carthaginensis),

            // Across the pillars of Hercules, and the Balearic crossing
            (baetica, mauretania), (carthaginensis, mauretania), (carthaginensis, sardinia),

            // Gaul
            (tarraco, aquitania), (tarraco, narbonensis), (aquitania, narbonensis),
            (aquitania, celtica), (narbonensis, celtica), (celtica, belgica),
            (celtica, cisalpina), (belgica, cisalpina), (narbonensis, cisalpina),
            (narbonensis, corsica),

            // Italy, north to south
            (cisalpina, etruria), (etruria, latium), (latium, samnium),
            (samnium, apulia), (samnium, bruttium), (apulia, bruttium),

            // The straits of Messina — three miles of water, and the war starts here
            (bruttium, sicilia),

            // The Tyrrhenian
            (etruria, corsica), (latium, corsica), (latium, sardinia),
            (corsica, sardinia), (sardinia, sicilia),

            // Africa
            (zeugitana, numidia), (zeugitana, tripolitania), (numidia, tripolitania),
            (numidia, mauretania), (tripolitania, cyrenaica),
            (zeugitana, sicilia), (zeugitana, sardinia), (tripolitania, sicilia),

            // Greece
            (epirus, macedonia), (epirus, thessaly), (macedonia, thessaly),
            (thessaly, attica), (attica, peloponnese), (epirus, peloponnese),
            (attica, crete), (peloponnese, crete),

            // The Adriatic crossing, which is how Rome ends up in Greece
            (apulia, epirus),

            // The eastern sea
            (crete, cyrenaica), (crete, sicilia), (crete, cyprus),
            (cyrenaica, alexandria), (alexandria, thebais), (alexandria, judaea),
            (judaea, cyprus), (alexandria, cyprus));

        return provinces;
    }

    /// <summary>
    /// Wires an undirected link both ways and refuses to do it twice.
    ///
    /// One-way adjacency is the bug this exists to prevent: an army marches into a
    /// province and then finds no legal move out of it, which reads from the outside as
    /// a stuck AI rather than a broken map.
    /// </summary>
    private static void Link(List<Province> provinces, params (Province, Province)[] links)
    {
        var neighbours = new List<int>[provinces.Count];
        for (int i = 0; i < neighbours.Length; i++) neighbours[i] = new List<int>();

        foreach ((Province a, Province b) in links)
        {
            if (a.Id == b.Id) throw new ArgumentException($"{a.Name} is linked to itself");

            if (!neighbours[a.Id].Contains(b.Id)) neighbours[a.Id].Add(b.Id);
            if (!neighbours[b.Id].Contains(a.Id)) neighbours[b.Id].Add(a.Id);
        }

        foreach (Province province in provinces)
        {
            province.Neighbours = neighbours[province.Id].ToArray();

            if (province.Neighbours.Length == 0)
                throw new InvalidOperationException($"{province.Name} is cut off from the map");
        }
    }
}
