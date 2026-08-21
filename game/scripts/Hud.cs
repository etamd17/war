using System;
using System.Collections.Generic;
using Godot;
using War.Sim.Core;
using War.Sim.Sim;
using War.Sim.Units;

namespace War.Game;

/// <summary>
/// A unit card: the whole state of a body of men in about seventy pixels.
///
/// Strength, morale and fatigue, because those are the three things a commander decides
/// on. A unit at half strength and steady is still a unit; a unit at full strength and
/// wavering is about to stop being one, and the card has to make that difference obvious
/// at a glance while the battle is moving.
///
/// Formation and stance are on the card for a blunter reason: the keys that change them
/// used to do so invisibly. Pressing F cycled a unit from line to testudo with nothing on
/// screen to say so — the men re-formed, but a hundred and twenty figures shuffling at
/// battle-camera range reads as noise. A tactical option the player cannot confirm is a
/// tactical option the player will not use.
/// </summary>
public sealed partial class UnitCard : Panel
{
    public const int CardWidth = 132;
    public const int CardHeight = 84;

    private readonly Unit _unit;
    private readonly BattleState _state;
    private Label _name = null!;
    private Label _count = null!;
    private Label _formation = null!;
    private Label _status = null!;
    private ColorRect _strengthBar = null!;
    private ColorRect _moraleBar = null!;
    private ColorRect _fatigueBar = null!;
    private ColorRect _highlight = null!;

    public Unit Unit => _unit;
    public event Action<Unit, bool>? Clicked;

    public UnitCard(Unit unit, BattleState state)
    {
        _unit = unit;
        _state = state;
        CustomMinimumSize = new Vector2(CardWidth, CardHeight);
    }

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Stop;

        _highlight = new ColorRect
        {
            Color = new Color(1f, 0.94f, 0.55f, 0f),
            Position = new Vector2(0, 0),
            Size = new Vector2(CardWidth, 3),
            MouseFilter = MouseFilterEnum.Ignore,
        };
        AddChild(_highlight);

        var stripe = new ColorRect
        {
            Color = SimBridge.FactionColour(_unit.Faction),
            Position = new Vector2(0, 4),
            Size = new Vector2(4, CardHeight - 8),
            MouseFilter = MouseFilterEnum.Ignore,
        };
        AddChild(stripe);

        _name = MakeLabel(_unit.Type.Name, new Vector2(9, 2), 11, Colors.White);
        _count = MakeLabel("", new Vector2(9, 17), 10, new Color(0.78f, 0.78f, 0.78f));

        // Right-aligned against the count, so the two read as one line without either
        // being truncated when a name like "General's Bodyguard" is long.
        _formation = MakeLabel("", new Vector2(9, 17), 10, new Color(0.72f, 0.70f, 0.60f));
        _formation.HorizontalAlignment = HorizontalAlignment.Right;

        _status = MakeLabel("", new Vector2(9, 31), 10, Colors.White);

        AddChild(Track(new Vector2(9, 50), new Color(0.12f, 0.12f, 0.12f)));
        _strengthBar = Bar(new Vector2(9, 50), new Color(0.45f, 0.72f, 0.40f));

        AddChild(Track(new Vector2(9, 61), new Color(0.12f, 0.12f, 0.12f)));
        _moraleBar = Bar(new Vector2(9, 61), SimBridge.MoraleColour(MoraleState.Steady));

        AddChild(Track(new Vector2(9, 72), new Color(0.12f, 0.12f, 0.12f)));
        _fatigueBar = Bar(new Vector2(9, 72), new Color(0.70f, 0.52f, 0.30f));
    }

    private Label MakeLabel(string text, Vector2 at, int size, Color colour)
    {
        var label = new Label
        {
            Text = text,
            Position = at,
            Size = new Vector2(CardWidth - 14, 15),
            MouseFilter = MouseFilterEnum.Ignore,
        };
        label.AddThemeFontSizeOverride("font_size", size);
        label.AddThemeColorOverride("font_color", colour);
        AddChild(label);
        return label;
    }

    private static ColorRect Track(Vector2 at, Color colour) => new()
    {
        Color = colour,
        Position = at,
        Size = new Vector2(CardWidth - 18, 7),
        MouseFilter = MouseFilterEnum.Ignore,
    };

    private ColorRect Bar(Vector2 at, Color colour)
    {
        var bar = new ColorRect
        {
            Color = colour,
            Position = at,
            Size = new Vector2(CardWidth - 18, 7),
            MouseFilter = MouseFilterEnum.Ignore,
        };
        AddChild(bar);
        return bar;
    }

    public void Refresh(bool selected)
    {
        float width = CardWidth - 18;

        _count.Text = _unit.Withdrawn
            ? "fled the field"
            : $"{_unit.Alive} / {_unit.Strength}";

        // Chevrons, drawn as chevrons. A veteran regiment carried over from the campaign
        // should be identifiable in the card row without reading a number.
        _name.Text = _unit.Experience > 0
            ? $"{_unit.Type.Name} {new string('›', Math.Min(_unit.Experience, 9))}"
            : _unit.Type.Name;

        _formation.Text = _unit.IsOutOfAction ? "" : FormationName(_unit.Formation);

        (string text, Color colour) = Status();
        _status.Text = text;
        _status.AddThemeColorOverride("font_color", colour);

        _strengthBar.Size = new Vector2(width * _unit.StrengthFraction.ToFloat(), 7);

        float morale = Mathf.Clamp(_unit.Morale.ToFloat() / 100f, 0, 1);
        _moraleBar.Size = new Vector2(width * morale, 7);
        _moraleBar.Color = SimBridge.MoraleColour(_unit.MoraleState);

        _fatigueBar.Size = new Vector2(width * _unit.Fatigue.ToFloat(), 7);

        _highlight.Color = new Color(1f, 0.94f, 0.55f, selected ? 1f : 0f);
        Modulate = _unit.IsOutOfAction ? new Color(1, 1, 1, 0.35f) : Colors.White;
    }

    private static string FormationName(FormationType formation) => formation switch
    {
        FormationType.Line => "line",
        FormationType.Column => "column",
        FormationType.Wedge => "wedge",
        FormationType.Square => "square",
        FormationType.Testudo => "testudo",
        FormationType.Phalanx => "phalanx",
        FormationType.Skirmish => "loose",
        _ => "",
    };

    /// <summary>
    /// One line saying what these men are doing right now.
    ///
    /// Ordered by what would make a commander look: routing beats everything, then being
    /// in contact, then whatever they were told to do. Ammunition displaces the order when
    /// it is nearly gone, because missile troops out of shot are a different unit from the
    /// one you deployed and the moment that happens is the moment you need to know.
    /// </summary>
    private (string, Color) Status()
    {
        var quiet = new Color(0.62f, 0.62f, 0.62f);

        if (_unit.Withdrawn) return ("", quiet);
        if (_unit.IsDestroyed) return ("destroyed", new Color(0.55f, 0.30f, 0.30f));

        switch (_unit.MoraleState)
        {
            case MoraleState.Routing:
                return ("ROUTING", SimBridge.MoraleColour(MoraleState.Routing));
            case MoraleState.Rallying:
                return ("rallying", SimBridge.MoraleColour(MoraleState.Rallying));
        }

        bool shaken = _unit.MoraleState == MoraleState.Wavering;
        Color mood = shaken
            ? SimBridge.MoraleColour(MoraleState.Wavering)
            : new Color(0.82f, 0.82f, 0.78f);

        if (_unit.InContact)
            return (shaken ? "fighting — shaken" : "fighting", mood);

        // Shots per man, not the unit total: what a commander needs to know is how many
        // more volleys are left, and that is the same number whether eighty men or eight
        // are throwing.
        //
        // The warning is a fraction of what they set out with, not a flat count. A legion
        // carries two pila and an archer carries thirty, so a fixed threshold would either
        // never fire for the archer or leave every hastatus in the army announcing "2
        // shots left" from the moment of deployment — a permanent warning, which is no
        // warning.
        if (_unit.Type.HasMissiles)
        {
            int shots = ShotsPerMan();
            int carried = _unit.Type.Ammunition;

            if (shots == 0) return ("out of ammunition", new Color(0.85f, 0.55f, 0.25f));
            if (shots <= Math.Max(1, carried / 4))
                return ($"{shots} shot{(shots == 1 ? "" : "s")} left", new Color(0.88f, 0.75f, 0.35f));
        }

        string doing = _unit.Order.Type switch
        {
            OrderType.Attack => _unit.Order.Run ? "charging" : "closing",
            OrderType.MoveTo => _unit.Order.Run ? "running" : "advancing",
            OrderType.Withdraw => "falling back",
            _ => shaken ? "shaken" : "holding",
        };

        if (!_unit.FireAtWill && _unit.Type.HasMissiles) doing += " — hold fire";

        return (doing, mood);
    }

    private int ShotsPerMan()
    {
        int total = 0;
        int living = 0;

        for (int s = _unit.FirstSoldier; s < _unit.EndSoldier; s++)
        {
            if (_state.State[s] == SoldierState.Dead) continue;
            total += _state.Ammo[s];
            living++;
        }

        return living == 0 ? 0 : total / living;
    }

    public override void _GuiInput(InputEvent @event)
    {
        if (@event is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } click)
            Clicked?.Invoke(_unit, click.ShiftPressed);
    }
}

/// <summary>
/// The minimap. Drawn rather than rendered — a few dozen coloured marks is all the
/// information there is, and it costs nothing next to a second camera.
/// </summary>
public sealed partial class Minimap : Control
{
    private BattleState _state = null!;
    private float _worldSize = 1;
    private RtsCamera? _camera;

    public event Action<Vector2>? Clicked;

    public void Bind(BattleState state, RtsCamera camera)
    {
        _state = state;
        _worldSize = state.Terrain.Size.ToFloat();
        _camera = camera;
    }

    public override void _Draw()
    {
        if (_state == null) return;

        Vector2 size = Size;
        DrawRect(new Rect2(Vector2.Zero, size), new Color(0.10f, 0.12f, 0.10f, 0.85f));

        foreach (Unit unit in _state.Units)
        {
            if (unit.IsOutOfAction || unit.Alive == 0) continue;

            Vector2 at = Project(SimBridge.Plane(unit.Centre), size);
            Color colour = SimBridge.FactionColour(unit.Faction);

            if (unit.MoraleState == MoraleState.Routing)
                colour = colour.Lerp(Colors.Black, 0.45f);

            // Marker size tracks unit strength, so you can see the line thinning.
            float radius = 1.6f + 3.4f * unit.StrengthFraction.ToFloat();
            DrawCircle(at, radius, colour);

            if (unit.IsGeneral) DrawCircle(at, radius + 2.5f, new Color(1f, 0.92f, 0.5f, 0.8f));
        }

        if (_camera != null)
        {
            Vector2 focus = Project(new Vector2(_camera.Focus.X, _camera.Focus.Z), size);
            DrawArc(focus, 7, 0, Mathf.Tau, 20, new Color(1, 1, 1, 0.8f), 1.5f);
        }

        DrawRect(new Rect2(Vector2.Zero, size), new Color(0.6f, 0.6f, 0.6f, 0.6f), filled: false, width: 1);
    }

    private Vector2 Project(Vector2 world, Vector2 size) =>
        new(world.X / _worldSize * size.X, (1f - world.Y / _worldSize) * size.Y);

    public override void _GuiInput(InputEvent @event)
    {
        if (@event is not InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } click)
            return;

        Vector2 local = click.Position / Size;
        Clicked?.Invoke(new Vector2(local.X * _worldSize, (1f - local.Y) * _worldSize));
    }
}

/// <summary>
/// Everything drawn over the battle: the clock, both armies' remaining strength, speed
/// controls, the unit cards, the minimap, and the drag rectangle.
/// </summary>
public sealed partial class Hud : CanvasLayer
{
    private BattleState _state = null!;
    private int _playerArmy;

    private Label _clock = null!;
    private Label _tally = null!;
    private Label _result = null!;
    private Label _hint = null!;
    private HBoxContainer _cardRow = null!;
    private readonly List<UnitCard> _cards = new();

    private Control _overlay = null!;
    private Rect2? _dragRect;

    public Minimap Map { get; private set; } = null!;

    private Button _begin = null!;

    public event Action<Unit, bool>? UnitCardClicked;
    public event Action<int>? SpeedChosen;
    public event Action? PauseToggled;
    public event Action? BeginRequested;

    public void Build(BattleState state, int playerArmy, RtsCamera camera)
    {
        _state = state;
        _playerArmy = playerArmy;

        var root = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
        root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(root);

        // ---- drag rectangle, drawn under everything else
        _overlay = new DragOverlay(this) { MouseFilter = Control.MouseFilterEnum.Ignore };
        _overlay.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        root.AddChild(_overlay);

        // ---- top left: clock and strengths
        var topLeft = new VBoxContainer { Position = new Vector2(14, 10) };
        root.AddChild(topLeft);

        _clock = Heading("00:00", 20);
        topLeft.AddChild(_clock);
        _tally = Heading("", 13);
        topLeft.AddChild(_tally);

        // ---- top right: speed
        // Anchored to the top-right corner. Offsets define the rect when anchors are
        // set; assigning Position as well would fight with them.
        var speeds = new HBoxContainer
        {
            AnchorLeft = 1, AnchorRight = 1,
            AnchorTop = 0, AnchorBottom = 0,
            OffsetLeft = -294, OffsetRight = -14,
            OffsetTop = 10, OffsetBottom = 40,
        };
        root.AddChild(speeds);

        speeds.AddChild(SpeedButton("Pause", () => PauseToggled?.Invoke()));
        speeds.AddChild(SpeedButton("1x", () => SpeedChosen?.Invoke(1)));
        speeds.AddChild(SpeedButton("2x", () => SpeedChosen?.Invoke(2)));
        speeds.AddChild(SpeedButton("4x", () => SpeedChosen?.Invoke(4)));

        // ---- centre: result banner
        _result = new Label
        {
            Text = "",
            HorizontalAlignment = HorizontalAlignment.Center,
            AnchorLeft = 0, AnchorRight = 1,
            AnchorTop = 0.34f, AnchorBottom = 0.34f,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        _result.AddThemeFontSizeOverride("font_size", 46);
        _result.AddThemeColorOverride("font_color", new Color(1f, 0.95f, 0.78f));
        root.AddChild(_result);

        // ---- deployment: the one control that matters before the fighting starts
        _begin = new Button
        {
            Text = "Begin battle  (Enter)",
            AnchorLeft = 0.5f, AnchorRight = 0.5f,
            AnchorTop = 1, AnchorBottom = 1,
            OffsetLeft = -110, OffsetRight = 110,
            OffsetTop = -(UnitCard.CardHeight + 74), OffsetBottom = -(UnitCard.CardHeight + 34),
        };
        _begin.Pressed += () => BeginRequested?.Invoke();
        root.AddChild(_begin);

        // ---- bottom: unit cards
        var cardPanel = new Panel
        {
            AnchorLeft = 0, AnchorRight = 1,
            AnchorTop = 1, AnchorBottom = 1,
            OffsetTop = -(UnitCard.CardHeight + 16),
            OffsetLeft = 176,
        };
        root.AddChild(cardPanel);

        _cardRow = new HBoxContainer { Position = new Vector2(8, 8) };
        _cardRow.AddThemeConstantOverride("separation", 4);
        cardPanel.AddChild(_cardRow);

        foreach (int unitId in state.Armies[playerArmy].UnitIds)
        {
            var card = new UnitCard(state.Units[unitId], state);
            card.Clicked += (unit, additive) => UnitCardClicked?.Invoke(unit, additive);
            _cardRow.AddChild(card);
            _cards.Add(card);
        }

        // ---- bottom left: minimap
        Map = new Minimap
        {
            AnchorLeft = 0, AnchorRight = 0,
            AnchorTop = 1, AnchorBottom = 1,
            OffsetLeft = 12, OffsetRight = 172,
            OffsetTop = -(UnitCard.CardHeight + 16), OffsetBottom = 0,
            MouseFilter = Control.MouseFilterEnum.Stop,
        };
        Map.Bind(state, camera);
        root.AddChild(Map);

        _hint = Heading(
            "LMB select  •  RMB move, drag to set facing  •  F formation  •  R run  •  T hold fire  •  [ ] width  •  Space pause",
            11);
        _hint.AnchorTop = 1;
        _hint.AnchorBottom = 1;
        _hint.OffsetTop = -(UnitCard.CardHeight + 34);
        _hint.OffsetLeft = 178;
        root.AddChild(_hint);
    }

    private static Label Heading(string text, int size)
    {
        var label = new Label { Text = text, MouseFilter = Control.MouseFilterEnum.Ignore };
        label.AddThemeFontSizeOverride("font_size", size);
        label.AddThemeColorOverride("font_color", new Color(0.94f, 0.94f, 0.90f));
        label.AddThemeColorOverride("font_shadow_color", new Color(0, 0, 0, 0.8f));
        label.AddThemeConstantOverride("shadow_offset_y", 1);
        return label;
    }

    private static Button SpeedButton(string text, Action onPressed)
    {
        var button = new Button { Text = text, CustomMinimumSize = new Vector2(64, 28) };
        button.Pressed += onPressed;
        return button;
    }

    public void Refresh(HashSet<int> selected)
    {
        int seconds = _state.Tick / SimConstants.TickRate;
        _clock.Text = $"{seconds / 60:D2}:{seconds % 60:D2}";

        _tally.Text =
            $"{_state.Armies[0].Name} {Standing(0)}   —   {_state.Armies[1].Name} {Standing(1)}";

        foreach (UnitCard card in _cards) card.Refresh(selected.Contains(card.Unit.Id));

        bool deploying = _state.Phase == BattlePhase.Deploying;
        _begin.Visible = deploying;

        if (deploying)
        {
            _result.AddThemeFontSizeOverride("font_size", 22);
            _result.Text = "Draw up your line — drag with the right mouse button to place a unit";
        }
        else
        {
            _result.AddThemeFontSizeOverride("font_size", 46);
            _result.Text = _state.Result switch
            {
                BattleResult.ArmyVictory when _state.Victor == _playerArmy => "THE FIELD IS YOURS",
                BattleResult.ArmyVictory => $"{_state.Armies[_state.Victor].Name.ToUpperInvariant()} HOLDS THE FIELD",
                BattleResult.Draw => "NEITHER SIDE COULD BREAK THE OTHER",
                _ => "",
            };
        }

        Map.QueueRedraw();
    }

    private int Standing(int armyId)
    {
        int total = 0;
        foreach (int unitId in _state.Armies[armyId].UnitIds)
        {
            Unit unit = _state.Units[unitId];
            if (unit.IsEffective) total += unit.Alive;
        }
        return total;
    }

    public void SetDragRect(Rect2? rect)
    {
        _dragRect = rect;
        _overlay.QueueRedraw();
    }

    /// <summary>Draws the selection rectangle. Nested so it can reach the parent's state.</summary>
    private sealed partial class DragOverlay : Control
    {
        private readonly Hud _hud;
        public DragOverlay(Hud hud) => _hud = hud;

        public override void _Draw()
        {
            if (_hud._dragRect is not { } rect) return;
            DrawRect(rect, new Color(1f, 0.94f, 0.55f, 0.12f));
            DrawRect(rect, new Color(1f, 0.94f, 0.55f, 0.85f), filled: false, width: 1.5f);
        }
    }
}
