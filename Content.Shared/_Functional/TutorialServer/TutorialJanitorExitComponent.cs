using Robust.Shared.GameObjects;

namespace Content.Shared._Functional.TutorialServer;

/// <summary>
/// Entering this cryopod ends the tutorial and returns the player to the role picker.
/// The closet pod is not this. Only the hall of fame pod is.
/// </summary>
[RegisterComponent]
public sealed partial class TutorialJanitorExitComponent : Component
{
}
