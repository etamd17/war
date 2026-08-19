using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using War.Sim.Campaign;
using War.Sim.Units;

namespace War.Game;

/// <summary>
/// Everything drawn over the campaign map: the date and treasury, the standings, what is
/// standing in the selected province, and the two buttons that spend a turn.
///
/// The panel on the right is the whole strategic picture in one column. A campaign is won
/// by noticing that somebody has quietly doubled their army while you were taking a
/// province, and there is nowhere else to notice it.
/// </summary>
public sealed partial class CampaignHud : CanvasLayer
{
    private CampaignState _state = null!;
    private Faction _player;

    private Label _date = null!;
    private Label _treasury = null!;
    private Label _standings = null!;
    private Label _selection = null!;
    private Label _chronicle = null!;
    private Button _recruit = null!;
    private Label _result = null!;

    public event Action? TurnEnded;
    public event Action? RecruitRequested;

    private int? _province;

    public void Build(CampaignState state, Faction player)
    {
        _state = state;
        _player = player;

        var root = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
        root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(root);

        _date = Heading("", 20, new Vector2(20, 14), 420, wrap: false);
        root.AddChild(_date);

        _treasury = Heading("", 13, new Vector2(20, 44), 460, wrap: false);
        root.AddChild(_treasury);

        // ---- right column: who is winning
        var panel = new Panel
        {
            AnchorLeft = 1, AnchorRight = 1, AnchorTop = 0, AnchorBottom = 1,
            OffsetLeft = -290, OffsetRight = -12, OffsetTop = 12, OffsetBottom = -12,
        };
        root.AddChild(panel);

        _standings = Heading("", 12, new Vector2(14, 12), 258);
        panel.AddChild(_standings);

        _selection = Heading("", 12, new Vector2(14, 210), 258);
        panel.AddChild(_selection);

        _recruit = new Button
        {
            Text = "Recruit here",
            AnchorTop = 1, AnchorBottom = 1,
            OffsetLeft = 14, OffsetRight = 274, OffsetTop = -96, OffsetBottom = -62,
        };
        _recruit.Pressed += () => RecruitRequested?.Invoke();
        panel.AddChild(_recruit);

        var end = new Button
        {
            Text = "End turn  (Enter)",
            AnchorTop = 1, AnchorBottom = 1,
            OffsetLeft = 14, OffsetRight = 274, OffsetTop = -52, OffsetBottom = -14,
        };
        end.Pressed += () => TurnEnded?.Invoke();
        panel.AddChild(end);

        // ---- bottom left: what just happened
        _chronicle = Heading("", 11, new Vector2(20, 0), 900, wrap: false);
        _chronicle.AnchorTop = 1;
        _chronicle.AnchorBottom = 1;
        _chronicle.OffsetLeft = 20;
        _chronicle.OffsetTop = -140;
        _chronicle.OffsetBottom = -10;
        root.AddChild(_chronicle);

        _result = new Label
        {
            Text = "",
            HorizontalAlignment = HorizontalAlignment.Center,
            AnchorLeft = 0, AnchorRight = 1, AnchorTop = 0.4f, AnchorBottom = 0.4f,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        _result.AddThemeFontSizeOverride("font_size", 40);
        _result.AddThemeColorOverride("font_color", new Color(1f, 0.95f, 0.78f));
        root.AddChild(_result);
    }

    /// <summary>
    /// A label with a width.
    ///
    /// The width is not optional. A Label with word wrap on and no size wraps to its
    /// minimum, which is one character — the campaign date came out as a vertical column
    /// of single letters down the left edge of the screen, one per line.
    /// </summary>
    private static Label Heading(string text, int size, Vector2 at, int width, bool wrap = true)
    {
        var label = new Label
        {
            Text = text,
            Position = at,
            CustomMinimumSize = new Vector2(width, 0),
            Size = new Vector2(width, 0),
            MouseFilter = Control.MouseFilterEnum.Ignore,
            AutowrapMode = wrap ? TextServer.AutowrapMode.WordSmart : TextServer.AutowrapMode.Off,
        };
        label.AddThemeFontSizeOverride("font_size", size);
        label.AddThemeColorOverride("font_color", new Color(0.92f, 0.92f, 0.88f));
        label.AddThemeColorOverride("font_shadow_color", new Color(0, 0, 0, 0.85f));
        label.AddThemeConstantOverride("shadow_offset_y", 1);
        return label;
    }

    public void Select(int? provinceId) => _province = provinceId;

    public void Refresh()
    {
        CampaignPower me = _state.Power(_player);

        _date.Text = _state.Date;
        _treasury.Text = $"{me.Name} — {me.Treasury} coin, " +
                         $"{_state.ProvinceCount(_player)} provinces";

        _standings.Text = "THE POWERS\n\n" + string.Join("\n", _state.Powers.Values
            .OrderByDescending(p => _state.ProvinceCount(p.Faction))
            .Select(p =>
            {
                int men = _state.Armies.Where(a => a.Owner == p.Faction).Sum(a => a.Men);
                return p.Destroyed
                    ? $"{p.Name} — finished"
                    : $"{p.Name}\n   {_state.ProvinceCount(p.Faction)} provinces, {men} men";
            }));

        _selection.Text = DescribeSelection();
        _recruit.Disabled = !CanRecruitHere();

        // The last few lines only. A campaign chronicle grows without limit and the useful
        // part is always the end of it.
        _chronicle.Text = string.Join("\n", _state.Chronicle.TakeLast(7));

        _result.Text = CampaignSim.Victor(_state) is { } victor
            ? victor == _player
                ? "THE MEDITERRANEAN IS YOURS"
                : $"{_state.Power(victor).Name.ToUpperInvariant()} RULES THE SEA"
            : "";
    }

    private bool CanRecruitHere() =>
        _province is { } id
        && _state[id].Owner == _player
        && !_state.ArmiesIn(id).Any(a => a.Owner != _player);

    private string DescribeSelection()
    {
        if (_province is not { } id) return "";

        Province province = _state[id];
        var lines = new List<string>
        {
            province.Name.ToUpperInvariant(),
            "",
            $"{province.Landscape}, worth {province.Wealth}",
            $"held by {province.Owner?.ToString() ?? "nobody"}",
            $"levy {province.Militia} of {province.MilitiaCap}",
        };

        if (province.Unrest > 0) lines.Add($"in unrest for {province.Unrest} more turns");
        if (province.Besieger is { } besieger)
            lines.Add($"besieged by {besieger}, {province.Siege} of {province.SiegeLength}");

        foreach (CampaignArmy army in _state.ArmiesIn(id))
        {
            lines.Add("");
            lines.Add($"{army.Owner} — {army.Men} men");

            // Only the player's own regiments are listed. Reading an enemy stack down to
            // the last unit is a thing a campaign should make you pay a scout for.
            if (army.Owner != _player) continue;

            foreach (Regiment regiment in army.Regiments)
                lines.Add($"   {regiment.Type.Name} {regiment.Strength}/{regiment.Establishment}");
        }

        return string.Join("\n", lines);
    }
}
