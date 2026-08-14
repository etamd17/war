using System.Text;
using War.Sim.Core;
using War.Sim.Sim;
using War.Sim.Units;
using War.Sim.World;

namespace War.Watch;

/// <summary>
/// Draws a battle into a terminal.
///
/// Deliberately crude and deliberately useful. The whole point of keeping the simulation
/// free of Godot is that it can be run and inspected anywhere; this is what that buys.
/// A balance question — does the phalanx hold, do the elephants break the line, does
/// Carthage ever win — is answered in two seconds here instead of by launching a game.
/// </summary>
public sealed class BattleRenderer
{
    private readonly BattleState _state;
    private readonly int _width;
    private readonly int _height;
    private readonly bool _colour;

    private readonly int[] _livingA;
    private readonly int[] _livingB;
    private readonly int[] _dead;
    private readonly char[] _terrain;
    private readonly string[] _terrainColour;

    /// <summary>Density ramp. More men in a cell, heavier the mark.</summary>
    private const string Ramp = ".:oO@";

    public BattleRenderer(BattleState state, int width, int height, bool colour)
    {
        _state = state;
        _width = width;
        _height = height;
        _colour = colour;

        int cells = width * height;
        _livingA = new int[cells];
        _livingB = new int[cells];
        _dead = new int[cells];
        _terrain = new char[cells];
        _terrainColour = new string[cells];

        BakeTerrain();
    }

    private void BakeTerrain()
    {
        Terrain terrain = _state.Terrain;
        float size = terrain.Size.ToFloat();

        float lowest = float.MaxValue, highest = float.MinValue;
        for (int y = 0; y < terrain.Resolution; y++)
            for (int x = 0; x < terrain.Resolution; x++)
            {
                float h = terrain.GetHeight(x, y).ToFloat();
                lowest = Math.Min(lowest, h);
                highest = Math.Max(highest, h);
            }
        float span = Math.Max(highest - lowest, 0.001f);

        for (int row = 0; row < _height; row++)
        {
            for (int col = 0; col < _width; col++)
            {
                // Row 0 is the top of the screen, which is the north edge of the map.
                float wx = (col + 0.5f) / _width * size;
                float wy = (1f - (row + 0.5f) / _height) * size;
                var at = new FixVec2(Fix.FromDouble(wx), Fix.FromDouble(wy));

                int i = row * _width + col;
                float forest = terrain.ForestAt(at).ToFloat();
                float elevation = (terrain.HeightAt(at).ToFloat() - lowest) / span;

                if (forest > 0.35f)
                {
                    _terrain[i] = '♣';                       // a club, for a tree
                    _terrainColour[i] = Rgb(30, 70 + (int)(50 * forest), 30);
                }
                else
                {
                    // Four shades of ground so the ridge line is visible.
                    _terrain[i] = elevation switch
                    {
                        > 0.75f => '^',
                        > 0.50f => '-',
                        > 0.25f => '.',
                        _ => ' ',
                    };
                    int grey = 45 + (int)(elevation * 70);
                    _terrainColour[i] = terrain.GroundAt(at) switch
                    {
                        GroundType.Ford or GroundType.Mud => Rgb(40, 55, 75),
                        GroundType.Rock => Rgb(grey, grey, grey),
                        _ => Rgb(grey - 8, grey + 6, grey - 14),
                    };
                }
            }
        }
    }

    public string Draw()
    {
        Array.Clear(_livingA);
        Array.Clear(_livingB);
        Array.Clear(_dead);

        float size = _state.Terrain.Size.ToFloat();

        for (int s = 0; s < _state.SoldierCount; s++)
        {
            Unit unit = _state.UnitOf(s);
            if (unit.Withdrawn) continue;

            float wx = _state.Position[s].X.ToFloat();
            float wy = _state.Position[s].Y.ToFloat();

            int col = (int)(wx / size * _width);
            int row = (int)((1f - wy / size) * _height);
            if (col < 0 || row < 0 || col >= _width || row >= _height) continue;

            int i = row * _width + col;
            if (_state.State[s] == SoldierState.Dead) _dead[i]++;
            else if (unit.ArmyId == 0) _livingA[i]++;
            else _livingB[i]++;
        }

        var sb = new StringBuilder(_width * _height * 12);
        string colourA = Rgb(235, 70, 60);
        string colourB = Rgb(180, 110, 220);
        string colourDead = Rgb(85, 70, 65);

        for (int row = 0; row < _height; row++)
        {
            for (int col = 0; col < _width; col++)
            {
                int i = row * _width + col;
                int a = _livingA[i];
                int b = _livingB[i];

                if (a == 0 && b == 0)
                {
                    if (_dead[i] > 0) Append(sb, colourDead, ',');
                    else Append(sb, _terrainColour[i], _terrain[i]);
                    continue;
                }

                int total = a + b;
                char mark = Ramp[Math.Min(total - 1, Ramp.Length - 1)];

                // Where both sides occupy the same cell they are in contact, and that is
                // the most important thing on the screen.
                if (a > 0 && b > 0) Append(sb, Rgb(255, 225, 120), 'X');
                else Append(sb, a > b ? colourA : colourB, mark);
            }
            sb.Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>ANSI escape, spelled out rather than embedded as a raw byte.</summary>
    private const string Escape = "";
    private const string Reset = Escape + "[0m";

    private void Append(StringBuilder sb, string colour, char c)
    {
        if (_colour) sb.Append(colour);
        sb.Append(c);
        if (_colour) sb.Append(Reset);
    }

    private static string Rgb(int r, int g, int b) =>
        $"{Escape}[38;2;{Math.Clamp(r, 0, 255)};{Math.Clamp(g, 0, 255)};{Math.Clamp(b, 0, 255)}m";

    // -------------------------------------------------------------------- text

    public string Status()
    {
        int seconds = _state.Tick / SimConstants.TickRate;
        return $"{seconds / 60:D2}:{seconds % 60:D2}   " +
               $"{_state.Armies[0].Name} {Standing(0),4}/{_state.Armies[0].InitialMen,-4}   " +
               $"{_state.Armies[1].Name} {Standing(1),4}/{_state.Armies[1].InitialMen,-4}";
    }

    public int Standing(int armyId)
    {
        int total = 0;
        foreach (int unitId in _state.Armies[armyId].UnitIds)
        {
            Unit unit = _state.Units[unitId];
            if (unit.IsEffective) total += unit.Alive;
        }
        return total;
    }

    public IEnumerable<string> Roster(int armyId)
    {
        foreach (int unitId in _state.Armies[armyId].UnitIds)
        {
            Unit unit = _state.Units[unitId];
            string state =
                unit.Alive == 0 ? "destroyed" :
                unit.Withdrawn ? "fled the field" :
                unit.MoraleState.ToString().ToLowerInvariant();

            yield return
                $"  {unit.Type.Name,-24} {unit.Alive,4}/{unit.Strength,-4} " +
                $"morale {unit.Morale.ToDouble(),5:F1}  fatigue {unit.Fatigue.ToDouble(),4:F2}  {state}";
        }
    }
}
