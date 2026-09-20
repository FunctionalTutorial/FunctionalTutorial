using Content.Client.UserInterface.Systems.Chat;
using Content.Shared._Functional.TutorialServer;
using Content.Shared.Chat;
using Robust.Client.UserInterface;
using Robust.Shared.Utility;

namespace Content.Client._Functional.TutorialServer;

/// <summary>
/// Renders coach/mentor speech in the local player's language (solo tutorial instances).
/// </summary>
public sealed class TutorialCoachSpeechSystem : EntitySystem
{
    [Dependency] private readonly IUserInterfaceManager _ui = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeNetworkEvent<TutorialCoachSpeechEvent>(OnCoachSpeech);
    }

    private void OnCoachSpeech(TutorialCoachSpeechEvent ev)
    {
        if (string.IsNullOrWhiteSpace(ev.LocId))
            return;

        var spoken = FormattedMessage.RemoveMarkupPermissive(TutorialLoc.Get(ev.LocId));
        if (string.IsNullOrWhiteSpace(spoken))
            return;

        string name;
        var speakerNet = NetEntity.Invalid;
        var speechBubble = false;
        if (TryGetEntity(ev.Speaker, out var speakerUid) &&
            TryComp(speakerUid, out MetaDataComponent? meta))
        {
            name = meta.EntityName;
            speakerNet = ev.Speaker;
            speechBubble = true;
        }
        else
        {
            name = Loc.TryGetString("identity-unknown-name", out var unknown)
                ? unknown
                : "???";
        }

        var verb = ResolveSayVerb();
        var wrapped = WrapCoachSay(name, verb, spoken);

        var msg = new ChatMessage(
            ChatChannel.Local,
            spoken,
            wrapped,
            speakerNet,
            senderKey: null);

        _ui.GetUIController<ChatUIController>().ProcessChatMessage(msg, speechBubble: speechBubble);
    }

    /// <summary>
    /// Prefer tutorial-owned verbs so de/es/fr/pt/uk packs work without a full chat-manager.ftl.
    /// Never return a missing loc id — that is what printed as NANCI's "verb".
    /// </summary>
    private string ResolveSayVerb()
    {
        if (Loc.TryGetString("tutorial-coach-say-verb", out var tutorialVerb) &&
            !string.IsNullOrEmpty(tutorialVerb))
            return tutorialVerb;

        if (Loc.TryGetString("chat-speech-verb-default", out var chatVerb) &&
            !string.IsNullOrEmpty(chatVerb))
            return chatVerb;

        return "says";
    }

    /// <summary>
    /// Tutorial wrap inlines quotes (no Fluent message-name references). chat-manager wrap
    /// uses { chat-manager-speech-double-quote-begin }, which does not fall back across
    /// cultures and was the "name reference" break in incomplete locale packs.
    /// </summary>
    private string WrapCoachSay(string name, string verb, string spoken)
    {
        var escapedName = FormattedMessage.EscapeText(name);
        var escapedSpoken = FormattedMessage.EscapeText(spoken);
        (string, object)[] args =
        [
            ("entityName", escapedName),
            ("verb", verb),
            ("fontType", "Default"),
            ("fontSize", 12),
            ("message", escapedSpoken),
        ];

        if (Loc.TryGetString("tutorial-coach-say-wrap", out var tutorialWrap, args) &&
            !string.IsNullOrEmpty(tutorialWrap))
            return tutorialWrap;

        if (Loc.TryGetString("chat-manager-entity-say-wrap-message", out var chatWrap, args) &&
            !string.IsNullOrEmpty(chatWrap))
            return chatWrap;

        return $"[BubbleHeader][bold][Name]{escapedName}[/Name][/bold][/BubbleHeader] {verb}, “[BubbleContent]{escapedSpoken}[/BubbleContent]”";
    }
}
