using Robust.Shared.Map;

namespace Content.Shared._Functional.TutorialServer;

/// <summary>
/// Walks back and forth across a bar of soap. Each crossing is a new slip, and the pause at
/// either end is what spaces them out.
/// </summary>
[RegisterComponent]
public sealed partial class TutorialSlipLoopComponent : Component
{
    /// <summary>
    /// Tag on the far end of the walk. The near end is wherever this mob was mapped.
    /// The soap has to sit between the two.
    /// </summary>
    [DataField(required: true)]
    public string EndTag = string.Empty;

    /// <summary>Shortest pause at an end before turning around.</summary>
    [DataField]
    public TimeSpan MinWait = TimeSpan.FromSeconds(5);

    /// <summary>Longest pause at an end before turning around.</summary>
    [DataField]
    public TimeSpan MaxWait = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Close enough to an end to start the pause. Wider than the follow range, so arriving
    /// counts even when steering stops a little short.
    /// </summary>
    [DataField]
    public float ArriveRange = 1.25f;

    /// <summary>Where the mob was standing when the loop started.</summary>
    public EntityCoordinates? Near;

    /// <summary>The tagged far end.</summary>
    public EntityCoordinates? Far;

    /// <summary>True while the current leg is toward <see cref="Far"/>.</summary>
    public bool HeadingFar = true;

    /// <summary>When the pause at the current end ends. Null while still walking.</summary>
    public TimeSpan? WaitUntil;

    /// <summary>Set once both ends are known and the first leg has been ordered.</summary>
    public bool Armed;
}
