using Sandbox;
using System.Linq;

namespace WoWForever;

/// <summary>Free-flight explorer camera, no collision, first or third person framing.</summary>
public sealed class AzerothFlight : Component
{
    public bool MenuOpen { get; set; }
    public bool ThirdPerson { get; private set; } = true;
    public float Speed { get; set; } = 12;
    public Vector3 AvatarPosition { get; private set; }
    private Angles look = new(12, 45, 0);
    private Vector3 cameraOffset;
    private int revision = -1;
    private AzerothWorld world;
    private AzerothDetails details;
    private AzerothPlayer player;

    protected override void OnUpdate()
    {
        if (!world.IsValid()) world = Scene.GetAllComponents<AzerothWorld>().FirstOrDefault();
        if (!world.IsValid() || !world.Camera.IsValid()) return;
        // F9: free in WoW, s&box and Windows (F10 is Windows' menu key). The menu also closes with its own button.
        if (Input.Pressed("Destinations")) MenuOpen = !MenuOpen;
        if (Input.Pressed("Reload")) ThirdPerson = !ThirdPerson;
        Mouse.Visibility = MenuOpen ? MouseVisibility.Visible : MouseVisibility.Hidden;
        if (!player.IsValid()) player = Scene.GetAllComponents<AzerothPlayer>().FirstOrDefault();
        if (player?.Walking == true)
        {
            // Preserve the player's view when returning to flight.
            look = world.Camera.WorldRotation.Angles();
            cameraOffset = Vector3.Up * 60 + (ThirdPerson ? look.ToRotation().Forward * -140 : Vector3.Zero);
            return;
        }
        if (revision != world.Revision) { cameraOffset = Vector3.Zero; revision = world.Revision; }
        AvatarPosition = world.Camera.WorldPosition - cameraOffset;
        if (!details.IsValid()) details = Scene.GetAllComponents<AzerothDetails>().FirstOrDefault();
        if (!MenuOpen && !world.Warming && details?.Warming != true)
        {
            look.pitch = (look.pitch + Input.AnalogLook.pitch).Clamp(-89, 89);
            look.yaw += Input.AnalogLook.yaw;
            var direction = look.ToRotation() * Input.AnalogMove;
            direction += Vector3.Up * ((Input.Down("Jump") ? 1 : 0) - (Input.Down("Duck") ? 1 : 0));
            if (direction.Length > 1) direction = direction.Normal;
            float speed = Input.Down("Run") ? 160 : Input.Down("Walk") ? 3 : Speed;
            AvatarPosition += direction * speed * AzerothWorld.InchesPerSourceUnit * Time.Delta;
        }
        cameraOffset = Vector3.Up * 60 + (ThirdPerson ? look.ToRotation().Forward * -140 : Vector3.Zero);
        world.Camera.WorldPosition = AvatarPosition + cameraOffset;
        world.Camera.WorldRotation = look.ToRotation();
    }
}
