namespace Content.Shared._Functional.TutorialServer;

/// <summary>
/// Sub-goals the player finished with the tool that beat named, so a later line can tell a mop
/// from a spray.
/// </summary>
[RegisterComponent]
public sealed partial class TutorialToolChoiceComponent : Component
{
    public HashSet<string> Expected { get; } = new();
}
