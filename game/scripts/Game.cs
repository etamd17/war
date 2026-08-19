using System;
using System.Linq;
using Godot;
using War.Sim.Campaign;
using War.Sim.Sim;
using War.Sim.Units;

namespace War.Game;

/// <summary>
/// The game: a campaign, and the battles it produces.
///
/// This node owns the campaign and nothing else. When two armies meet and one of them is
/// the player's, it builds the tactical battle from the province they are standing in,
/// instantiates the battle scene, and waits. The battle scene knows nothing about
/// provinces, treasuries or turns — it is handed a <see cref="BattleSetup"/> and hands
/// back a finished <see cref="BattleState"/>, which is the only seam between the two
/// halves of the game and the reason either can be worked on without the other.
///
/// The turn is deliberately in three parts rather than one. A campaign nobody is watching
/// resolves a whole turn in a single call; a campaign somebody IS watching has to stop
/// halfway, because the battle in the middle of it takes several minutes and happens in a
/// window.
/// </summary>
public sealed partial class Game : Node
{
    private const Faction PlayerFaction = Faction.Rome;

    private CampaignState _campaign = null!;
    private CampaignScreen _screen = null!;
    private CampaignHud _hud = null!;

    private Main? _battle;
    private PendingBattle? _fighting;
    private uint _battleSeed;

    private int? _province;

    private string? _shotPath;
    private int _settleFrames;

    public override void _Ready()
    {
        _campaign = CampaignBuilder.Build(new CampaignSetup
        {
            Seed = 4471,
            Player = PlayerFaction,
        });

        // The map lives in a CanvasLayer so it has a viewport rect to anchor against and
        // sits beneath the HUD rather than fighting it for draw order.
        var mapLayer = new CanvasLayer { Name = "MapLayer", Layer = 0 };
        AddChild(mapLayer);

        _screen = new CampaignScreen { Name = "Map" };
        _screen.Bind(_campaign, PlayerFaction);
        mapLayer.AddChild(_screen);
        _screen.MoveOrdered += OnMoveOrdered;
        _screen.SelectionChanged += OnSelectionChanged;

        _hud = new CampaignHud { Name = "CampaignHud" };
        AddChild(_hud);
        _hud.Build(_campaign, PlayerFaction);
        _hud.TurnEnded += EndTurn;
        _hud.RecruitRequested += RecruitHere;
        _hud.Refresh();

        // The headless smoke test for this half of the game, same as the battle scene has:
        // `godot --headless --quit-after 300` runs the real _Ready, so this line appearing
        // means the map built, the campaign initialised and the HUD assembled.
        GD.Print($"WAR — campaign begins, {_campaign.Date}, " +
                 $"{_campaign.Provinces.Count} provinces, playing {PlayerFaction}");

        foreach (string argument in OS.GetCmdlineUserArgs())
        {
            if (argument.StartsWith("--turns=", StringComparison.Ordinal) &&
                int.TryParse(argument["--turns=".Length..], out int turns))
                for (int i = 0; i < turns; i++) EndTurn();

            // Deliberately not --shot, which the battle scene already claims. Both read the
            // same user arguments, so sharing the flag meant the battle photographed itself
            // and quit the moment a campaign run reached one, and the map shot never came.
            else if (argument.StartsWith("--map-shot=", StringComparison.Ordinal))
                _shotPath = argument["--map-shot=".Length..];
        }
    }

    public override void _Process(double delta)
    {
        if (_shotPath == null) return;

        // Never photograph the campaign while a battle is up. A verification shot of the
        // map should be of the map, and a run that launches a battle would otherwise
        // capture the deployment screen and quit before the campaign ever came back.
        if (_battle != null) { _settleFrames = 0; return; }

        // Four frames before photographing, because GetViewport().GetTexture() returns the
        // frame that has already been rendered rather than the one being prepared. The
        // battle scene learned this the expensive way.
        if (++_settleFrames < 4) return;

        Error error = GetViewport().GetTexture().GetImage().SavePng(_shotPath);
        GD.Print(error == Error.Ok ? $"WAR — wrote {_shotPath}" : $"WAR — screenshot failed: {error}");

        GetTree().Quit();
    }

    // -------------------------------------------------------------- map orders

    private void OnSelectionChanged(int provinceId, int? armyId)
    {
        _province = provinceId;
        _hud.Select(provinceId);
        _hud.Refresh();
    }

    private void OnMoveOrdered(int armyId, int provinceId)
    {
        CampaignArmy? army = _campaign.Armies.FirstOrDefault(a => a.Id == armyId);
        if (army == null || army.Owner != PlayerFaction) return;

        army.Destination = provinceId;
        _hud.Refresh();
    }

    /// <summary>
    /// Buys one regiment in the selected province.
    ///
    /// Uses the same shape-filling logic the AI recruits with, so a player pressing the
    /// button repeatedly builds a balanced army rather than ten regiments of whatever is
    /// best — and so there is exactly one definition of what an army should look like.
    /// </summary>
    private void RecruitHere()
    {
        if (_province is not { } provinceId) return;
        if (_campaign[provinceId].Owner != PlayerFaction) return;
        if (_campaign.ArmiesIn(provinceId).Any(a => a.Owner != PlayerFaction)) return;

        CampaignAI.RecruitOne(_campaign, _campaign.Power(PlayerFaction), provinceId);

        _screen.Refresh();
        _hud.Refresh();
    }

    // --------------------------------------------------------------- the turn

    private void EndTurn()
    {
        if (_battle != null) return;
        if (CampaignSim.Victor(_campaign) != null) return;

        var battles = CampaignSim.BeginTurn(_campaign);

        // The player's battle is fought properly. Everything else in the world resolves
        // around it in the same instant, which is what a simultaneous turn means.
        PendingBattle? mine = battles.FirstOrDefault(b =>
            b.Attacker.Owner == PlayerFaction || b.Defender.Owner == PlayerFaction);

        foreach (PendingBattle battle in battles)
        {
            if (battle == mine) continue;
            if (battle.Attacker.IsDestroyed || battle.Defender.IsDestroyed) continue;

            var random = new War.Sim.Core.DetRandom(
                _campaign.Seed + (uint)(_campaign.Turn * 7919) + (uint)battle.Province.Id,
                War.Sim.Core.RngStream.CampaignBattle);

            CampaignSim.Settle(_campaign, battle, BattleResolver.Estimate(
                battle.Attacker, battle.Defender, battle.Province.Landscape, random));
        }

        if (mine != null && !mine.Attacker.IsDestroyed && !mine.Defender.IsDestroyed)
        {
            LaunchBattle(mine);
            return;
        }

        FinishTurn();
    }

    private void FinishTurn()
    {
        CampaignSim.CompleteTurn(_campaign);

        _screen.Refresh();
        _hud.Refresh();

        if (CampaignSim.Victor(_campaign) is { } victor)
            GD.Print($"WAR — {_campaign.Power(victor).Name} rules the Mediterranean, {_campaign.Date}");
    }

    // ------------------------------------------------------------- the battle

    private void LaunchBattle(PendingBattle battle)
    {
        _fighting = battle;
        _battleSeed = _campaign.Seed + (uint)(_campaign.Turn * 131) + (uint)battle.Province.Id;

        _campaign.Record(
            $"{battle.Attacker.Owner} meets {battle.Defender.Owner} in {battle.Province.Name}");

        BattleSetup setup = BattleResolver.Setup(
            battle.Attacker, battle.Defender, battle.Province, _battleSeed,
            playerIsAttacker: battle.Attacker.Owner == PlayerFaction,
            deployment: true);

        _battle = GD.Load<PackedScene>("res://Main.tscn").Instantiate<Main>();
        _battle.ExternalSetup = setup;
        _battle.Finished += OnBattleFinished;

        _screen.Visible = false;
        _hud.Visible = false;
        AddChild(_battle);
    }

    /// <summary>
    /// The battle is decided. Copy the survivors back and let the turn finish.
    ///
    /// Deferred rather than immediate: this fires from inside the battle scene's own
    /// _Process, and freeing a node in the middle of its own frame is how a renderer ends
    /// up drawing something that no longer exists.
    /// </summary>
    private void OnBattleFinished(BattleState state)
    {
        if (_fighting is not { } battle) return;

        BattleReport report = BattleResolver.ReadBack(battle.Attacker, battle.Defender, state);
        CampaignSim.Settle(_campaign, battle, report);

        _fighting = null;
        CallDeferred(nameof(ReturnToMap));
    }

    private void ReturnToMap()
    {
        if (_battle != null)
        {
            RemoveChild(_battle);
            _battle.QueueFree();
            _battle = null;
        }

        _screen.Visible = true;
        _hud.Visible = true;

        FinishTurn();
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (_battle != null) return;
        if (@event is not InputEventKey { Pressed: true } key) return;

        if (key.Keycode is Key.Enter or Key.KpEnter) EndTurn();
    }
}
