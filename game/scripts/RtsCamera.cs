using Godot;

namespace War.Game;

/// <summary>
/// The battle camera.
///
/// One continuous range from a tabletop overview down to standing among the ranks, with
/// the pitch flattening as you descend — so zooming in is also lowering your eye, and
/// the camera ends up looking along the line rather than down at it. That single
/// coupling is most of what makes a battle feel like a battle instead of a diagram, and
/// it costs one lerp.
/// </summary>
public sealed partial class RtsCamera : Node3D
{
    private const float MinDistance = 14f;
    private const float MaxDistance = 420f;
    private const float EdgeScrollMargin = 6f;

    private Camera3D _camera = null!;
    private TerrainView _terrain = null!;

    private Vector3 _focus;
    private float _distance = 130f;
    private float _yaw;
    private float _pitchBias;
    private bool _orbiting;

    /// <summary>
    /// Locks the camera in place. Set by the screenshot harness.
    ///
    /// Edge scrolling reads the mouse position, and in an automated window there is no
    /// mouse — the pointer sits at a corner, which is inside the edge margin, so the
    /// camera pans away a little on every frame. Aim a verification shot, come back six
    /// hundred frames later, and it is looking somewhere else entirely. That drift is
    /// what made a renderer look broken for two sessions.
    /// </summary>
    public bool Frozen { get; set; }

    public Camera3D Camera => _camera;

    /// <summary>Where the camera is looking, on the ground.</summary>
    public Vector3 Focus => _focus;

    public void Setup(TerrainView terrain, Vector3 focus, float yaw)
    {
        _terrain = terrain;
        _focus = focus;
        _yaw = yaw;

        _camera = new Camera3D
        {
            Name = "Camera",
            Fov = 60f,
            Near = 0.4f,
            Far = 4000f,
        };
        AddChild(_camera);

        Apply();
    }

    public override void _Process(double delta)
    {
        if (Frozen)
        {
            Apply();
            return;
        }

        float dt = (float)delta;

        // Pan speed scales with height, so a click of the key moves you about the same
        // fraction of what you can see whether you are watching one unit or the field.
        float speed = Mathf.Lerp(24f, 260f, Mathf.InverseLerp(MinDistance, MaxDistance, _distance));

        Vector2 pan = Vector2.Zero;
        if (Input.IsKeyPressed(Key.W) || Input.IsKeyPressed(Key.Up)) pan.Y += 1;
        if (Input.IsKeyPressed(Key.S) || Input.IsKeyPressed(Key.Down)) pan.Y -= 1;
        if (Input.IsKeyPressed(Key.A) || Input.IsKeyPressed(Key.Left)) pan.X -= 1;
        if (Input.IsKeyPressed(Key.D) || Input.IsKeyPressed(Key.Right)) pan.X += 1;

        pan += EdgeScroll();

        if (Input.IsKeyPressed(Key.Q)) _yaw -= 1.4f * dt;
        if (Input.IsKeyPressed(Key.E)) _yaw += 1.4f * dt;

        if (pan != Vector2.Zero)
        {
            pan = pan.LimitLength();
            // Pan in the camera's own frame, so "forward" is always up the screen.
            float sin = Mathf.Sin(_yaw);
            float cos = Mathf.Cos(_yaw);
            _focus += new Vector3(
                (pan.X * cos - pan.Y * sin) * speed * dt,
                0,
                (-pan.X * sin - pan.Y * cos) * speed * dt);
        }

        Apply();
    }

    private Vector2 EdgeScroll()
    {
        Vector2 size = GetViewport().GetVisibleRect().Size;
        Vector2 at = GetViewport().GetMousePosition();

        // Pointer outside the window entirely: no scrolling, or the camera runs away
        // the moment you alt-tab.
        if (at.X < 0 || at.Y < 0 || at.X > size.X || at.Y > size.Y) return Vector2.Zero;

        var scroll = Vector2.Zero;
        if (at.X <= EdgeScrollMargin) scroll.X -= 1;
        if (at.X >= size.X - EdgeScrollMargin) scroll.X += 1;
        if (at.Y <= EdgeScrollMargin) scroll.Y += 1;
        if (at.Y >= size.Y - EdgeScrollMargin) scroll.Y -= 1;

        return scroll;
    }

    public void HandleInput(InputEvent @event)
    {
        if (@event is InputEventMouseButton button)
        {
            switch (button.ButtonIndex)
            {
                case MouseButton.WheelUp when button.Pressed:
                    _distance = Mathf.Clamp(_distance * 0.88f, MinDistance, MaxDistance);
                    break;
                case MouseButton.WheelDown when button.Pressed:
                    _distance = Mathf.Clamp(_distance * 1.14f, MinDistance, MaxDistance);
                    break;
                case MouseButton.Middle:
                    _orbiting = button.Pressed;
                    break;
            }
        }
        else if (@event is InputEventMouseMotion motion && _orbiting)
        {
            _yaw -= motion.Relative.X * 0.006f;
            _pitchBias = Mathf.Clamp(_pitchBias + motion.Relative.Y * 0.004f, -0.5f, 0.6f);
        }
    }

    /// <summary>Sets the zoom directly. Used by the screenshot harness to frame a shot.</summary>
    public void SetDistance(float distance) => _distance = Mathf.Clamp(distance, MinDistance, MaxDistance);

    /// <summary>Centres the camera on a point without changing the zoom or heading.</summary>
    public void LookAtGround(Vector3 point) => _focus = new Vector3(point.X, 0, point.Z);

    private void Apply()
    {
        float world = _terrain.WorldSize;
        _focus.X = Mathf.Clamp(_focus.X, 0, world);
        _focus.Z = Mathf.Clamp(_focus.Z, 0, world);
        _focus.Y = _terrain.HeightAt(_focus.X, _focus.Z);

        // Steep and overhead when far out, shallow and among the men when close in.
        float zoom = Mathf.InverseLerp(MinDistance, MaxDistance, _distance);
        float pitch = Mathf.Clamp(Mathf.Lerp(0.16f, 1.05f, zoom) + _pitchBias, 0.08f, 1.45f);

        var offset = new Vector3(
            Mathf.Sin(_yaw) * Mathf.Cos(pitch),
            Mathf.Sin(pitch),
            Mathf.Cos(_yaw) * Mathf.Cos(pitch));

        Vector3 eye = _focus + offset * _distance;

        // Never let the ground come through the lens on broken terrain.
        float floor = _terrain.HeightAt(eye.X, eye.Z) + 3f;
        if (eye.Y < floor) eye.Y = floor;

        _camera.LookAtFromPosition(eye, _focus + new Vector3(0, 2f, 0), Vector3.Up);
    }
}
