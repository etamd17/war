using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using War.Sim.Campaign;
using War.Sim.Units;

namespace War.Game;

/// <summary>
/// The campaign map.
///
/// Drawn rather than modelled. A campaign map is a graph with money on it, and the only
/// things a player needs to read off it are who holds what, where the armies are, and
/// which of them can reach which — none of which is helped by being three-dimensional.
/// The battle is where the geometry matters; this is where the decisions do.
///
/// Everything happens in one <see cref="_Draw"/> pass over the province list, so there is
/// no scene graph to keep in step with the simulation. The state is the drawing.
/// </summary>
public sealed partial class CampaignScreen : Control
{
    private CampaignState _state = null!;
    private Faction _player;

    private int? _selectedArmy;
    private int? _hoveredProvince;

    /// <summary>An army was ordered to a province.</summary>
    public event Action<int, int>? MoveOrdered;

    /// <summary>A province was clicked, with the player army selected there if any.</summary>
    public event Action<int, int?>? SelectionChanged;

    public int? SelectedArmy => _selectedArmy;

    public void Bind(CampaignState state, Faction player)
    {
        _state = state;
        _player = player;
    }

    /// <summary>
    /// Fills the viewport, and keeps filling it.
    ///
    /// Anchors are set here rather than in Bind because a Control only has a rect to anchor
    /// against once it is in the tree. And it is SetAnchorsAndOffsetsPreset rather than
    /// SetAnchorsPreset, which sets the anchors and leaves the offsets exactly where they
    /// were — so the node stays the size it already was, which is nothing. A zero-sized
    /// Control still runs its draw code and still lays every province out; it simply maps
    /// them all into a few pixels in the top corner, which looks identical to a map that
    /// never drew at all.
    /// </summary>
    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Stop;
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        Size = GetViewportRect().Size;

        GetViewport().SizeChanged += () =>
        {
            Size = GetViewportRect().Size;
            QueueRedraw();
        };
    }

    /// <summary>Drops any selection that no longer refers to something on the map.</summary>
    public void Refresh()
    {
        if (_selectedArmy is { } id && !_state.Armies.Any(a => a.Id == id)) _selectedArmy = null;
        QueueRedraw();
    }

    // ------------------------------------------------------------------ layout

    /// <summary>
    /// Where a province sits on screen.
    ///
    /// Map coordinates run roughly 4 to 62 west to east and 25 to 80 south to north.
    /// Screen rows count downward, so the vertical axis is flipped. The margins keep the
    /// far provinces clear of the panels rather than tucked underneath them.
    /// </summary>
    private Vector2 Screen(Province province)
    {
        Vector2 size = Size;
        float left = 44, right = size.X - 320, top = 76, bottom = size.Y - 168;

        float x = Mathf.Remap(province.Position.X.ToFloat(), 4f, 62f, left, right);
        float y = Mathf.Remap(province.Position.Y.ToFloat(), 25f, 80f, bottom, top);
        return new Vector2(x, y);
    }

    private static float RadiusOf(Province province) => 7f + province.Wealth / 90f;

    private Province? ProvinceAt(Vector2 point)
    {
        Province? best = null;
        float bestDistance = 26 * 26;

        foreach (Province province in _state.Provinces)
        {
            float distance = Screen(province).DistanceSquaredTo(point);
            if (distance >= bestDistance) continue;
            bestDistance = distance;
            best = province;
        }

        return best;
    }

    // ----------------------------------------------------------------- drawing

    public override void _Draw()
    {
        if (_state == null) return;

        DrawRect(new Rect2(Vector2.Zero, Size), new Color(0.09f, 0.12f, 0.16f));
        DrawLinks();

        CampaignArmy? selected = _selectedArmy is { } id
            ? _state.Armies.FirstOrDefault(a => a.Id == id)
            : null;

        var reachable = new HashSet<int>();
        if (selected != null)
            foreach (int neighbour in _state[selected.Province].Neighbours) reachable.Add(neighbour);

        foreach (Province province in _state.Provinces) DrawProvince(province, reachable);
        foreach (Province province in _state.Provinces) DrawArmies(province, selected);
    }

    /// <summary>
    /// The adjacency graph, faint.
    ///
    /// The single most important thing on the map and the easiest to leave out. Nobody can
    /// plan a campaign without knowing that Bruttium touches Sicilia and that Apulia reaches
    /// across to Epirus — and the sea crossings especially, because nothing in the shape of
    /// a coastline tells you those are one move.
    /// </summary>
    private void DrawLinks()
    {
        var drawn = new HashSet<(int, int)>();

        foreach (Province province in _state.Provinces)
        {
            Vector2 from = Screen(province);

            foreach (int neighbourId in province.Neighbours)
            {
                var key = (Math.Min(province.Id, neighbourId), Math.Max(province.Id, neighbourId));
                if (!drawn.Add(key)) continue;

                DrawLine(from, Screen(_state[neighbourId]), new Color(1, 1, 1, 0.10f), 1.5f);
            }
        }
    }

    private void DrawProvince(Province province, HashSet<int> reachable)
    {
        Vector2 at = Screen(province);
        float radius = RadiusOf(province);

        Color fill = province.Owner is { } owner
            ? SimBridge.FactionColour(owner)
            : new Color(0.42f, 0.42f, 0.44f);

        // Ground in unrest is ground that is not paying, and that should be visible without
        // opening anything.
        if (province.Unrest > 0) fill = fill.Lerp(new Color(0.15f, 0.15f, 0.15f), 0.45f);

        DrawCircle(at, radius, fill);
        DrawArc(at, radius, 0, Mathf.Tau, 24, new Color(0, 0, 0, 0.5f), 1.5f);

        if (reachable.Contains(province.Id))
            DrawArc(at, radius + 5, 0, Mathf.Tau, 28, new Color(1f, 0.94f, 0.55f, 0.9f), 2f);

        if (_hoveredProvince == province.Id)
            DrawArc(at, radius + 9, 0, Mathf.Tau, 28, new Color(1, 1, 1, 0.55f), 1.5f);

        // A besieged province wears a broken ring in the besieger's colour, which reads as
        // "under pressure" at a glance rather than needing to be read.
        if (province.Besieger is { } besieger)
        {
            Color mark = SimBridge.FactionColour(besieger);
            for (int i = 0; i < 6; i++)
            {
                float start = i * Mathf.Tau / 6;
                DrawArc(at, radius + 3, start, start + Mathf.Tau / 12, 5, mark, 2.5f);
            }
        }

        DrawString(ThemeDB.FallbackFont, at + new Vector2(radius + 6, 4), province.Name,
            HorizontalAlignment.Left, -1, 11, new Color(0.85f, 0.85f, 0.82f, 0.85f));
    }

    /// <summary>
    /// Armies, as a bar beneath the province they stand in.
    ///
    /// Length tracks how many men, so the shape of a war is legible without clicking
    /// anything: a long bar sitting on a border is a threat and two short ones are not.
    /// </summary>
    private void DrawArmies(Province province, CampaignArmy? selected)
    {
        var here = _state.ArmiesIn(province.Id).ToList();
        if (here.Count == 0) return;

        Vector2 at = Screen(province) + new Vector2(0, RadiusOf(province) + 5);

        foreach (CampaignArmy army in here)
        {
            float width = Mathf.Clamp(army.Men / 22f, 8, 46);
            var box = new Rect2(at.X - width / 2, at.Y, width, 6);

            DrawRect(box, SimBridge.FactionColour(army.Owner));
            DrawRect(box, new Color(0, 0, 0, 0.6f), filled: false, width: 1);

            if (selected != null && army.Id == selected.Id)
                DrawRect(box.Grow(3), new Color(1f, 0.94f, 0.55f), filled: false, width: 2);

            at.Y += 9;
        }
    }

    // ------------------------------------------------------------------- input

    public override void _GuiInput(InputEvent @event)
    {
        if (@event is InputEventMouseMotion motion)
        {
            int? was = _hoveredProvince;
            _hoveredProvince = ProvinceAt(motion.Position)?.Id;
            if (was != _hoveredProvince) QueueRedraw();
            return;
        }

        if (@event is not InputEventMouseButton { Pressed: true } click) return;

        Province? province = ProvinceAt(click.Position);
        if (province == null) return;

        if (click.ButtonIndex == MouseButton.Right)
        {
            OrderMove(province);
            return;
        }

        // Left click cycles through this province's own armies, so a stack can be picked
        // apart rather than only ever selecting whichever happens to be listed first.
        var mine = _state.ArmiesIn(province.Id).Where(a => a.Owner == _player).ToList();

        _selectedArmy = mine.Count == 0
            ? null
            : mine[(mine.FindIndex(a => a.Id == _selectedArmy) + 1) % mine.Count].Id;

        SelectionChanged?.Invoke(province.Id, _selectedArmy);
        QueueRedraw();
    }

    private void OrderMove(Province province)
    {
        if (_selectedArmy is not { } id) return;

        CampaignArmy? army = _state.Armies.FirstOrDefault(a => a.Id == id);
        if (army == null) return;

        // Adjacency is checked again in the turn itself; this is only so a click that
        // cannot become an order does not look like one that did.
        if (!Array.Exists(_state[army.Province].Neighbours, n => n == province.Id)) return;

        MoveOrdered?.Invoke(id, province.Id);
        QueueRedraw();
    }
}
