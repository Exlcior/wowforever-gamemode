using Sandbox;

namespace WoWForever;

/// <summary>Locomotion adapter for the mount's character models. Reusable on a native PlayerController.
/// Other controllers can set Velocity, Facing and state directly, leaving Controller unassigned.</summary>
public sealed class WowCharacterAnimator : Component
{
    [Property] public SkinnedModelRenderer Renderer { get; set; }
    [Property] public PlayerController Controller { get; set; }
    [Property] public Vector3 Velocity { get; set; }
    [Property] public float Facing { get; set; }
    [Property] public bool Grounded { get; set; } = true;
    [Property] public bool Crouching { get; set; }
    [Property] public bool Swimming { get; set; }
    /// <summary>Seated on a mount: the client's riding pose.</summary>
    [Property] public bool Mounted { get; set; }
    /// <summary>Holding a spell: the client's ready-to-cast pose.</summary>
    [Property] public bool Casting { get; set; }
    private string current;
    private Model model;

    protected override void OnUpdate()
    {
        if (!Renderer.IsValid() || Renderer.Model is null) return;
        if (Controller.IsValid())
        {
            Controller.UseAnimatorControls = false;
            Velocity = Controller.Velocity; Facing = Controller.EyeAngles.yaw;
            Grounded = Controller.IsOnGround; Crouching = Controller.IsDucking; Swimming = Controller.IsSwimming;
        }
        Renderer.UseAnimGraph = false;
        if (model != Renderer.Model) { model = Renderer.Model; current = null; }
        float speed = Velocity.WithZ(0).Length;
        bool backwards = Vector3.Dot(Velocity, Rotation.FromYaw(Facing).Forward) < -10;
        string sequence = Mounted ? "mount" : Casting && speed < 10 ? "spell_ready" : Swimming ? (speed > 10 ? "swim" : "swim_idle") :
            !Grounded ? (Velocity.z > 20 ? "jump" : "fall") :
            speed > 10 ? (Crouching ? "crouch_walk" : backwards ? "walk_back" : speed > 180 ? "run" : "walk") : "idle";
        if (current != sequence)
        {
            Renderer.Sequence.Name = sequence;
            Renderer.Sequence.Blending = true;
            current = sequence;
        }
        Renderer.Sequence.PlaybackRate = sequence is "walk" or "walk_back" or "run" or "crouch_walk"
            ? System.Math.Clamp(speed / (sequence == "run" ? 250f : 100f), .5f, 2f) : 1;
        Renderer.WorldRotation = Rotation.FromYaw(Facing - 90);
    }
}
