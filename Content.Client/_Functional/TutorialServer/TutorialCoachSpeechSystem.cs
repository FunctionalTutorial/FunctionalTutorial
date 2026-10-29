using Content.Client.Chat;
using Content.Client.UserInterface.Systems.Chat;
using Content.Shared._Functional.TutorialServer;
using Content.Shared.Chat;
using Robust.Client.UserInterface;
using Robust.Shared.Random;
using Robust.Shared.Utility;

namespace Content.Client._Functional.TutorialServer;

/// <summary>
/// Renders coach/mentor speech in the local player's language (solo tutorial instances).
/// </summary>
public sealed partial class TutorialCoachSpeechSystem : EntitySystem
{
    [Dependency] private IUserInterfaceManager _ui = default!;
    [Dependency] private ChatSystem _chat = default!;
    [Dependency] private IRobustRandom _random = default!;

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
        // "says", unless the speaker's speech verb (or a trailing ? / !) picks another.
        var verb = Loc.GetString("chat-speech-verb-default");
        var fontType = "Default";
        var fontSize = 12;
        var bold = false;
        if (TryGetEntity(ev.Speaker, out var speakerUid) &&
            TryComp(speakerUid, out MetaDataComponent? meta))
        {
            name = meta.EntityName;
            speakerNet = ev.Speaker;
            speechBubble = true;

            var speech = _chat.GetSpeechVerb(speakerUid.Value, spoken);
            if (speech.SpeechVerbStrings.Count > 0)
                verb = Loc.GetString(_random.Pick(speech.SpeechVerbStrings));

            fontType = speech.FontId;
            fontSize = speech.FontSize;
            bold = speech.Bold;
        }
        else
        {
            name = Loc.GetString("identity-unknown-name");
        }

        var wrapped = Loc.GetString(
            bold ? "chat-manager-entity-say-bold-wrap-message" : "chat-manager-entity-say-wrap-message",
            ("entityName", name),
            ("verb", verb),
            ("fontType", fontType),
            ("fontSize", fontSize),
            ("message", FormattedMessage.EscapeText(spoken)));

        var msg = new ChatMessage(
            ChatChannel.Local,
            spoken,
            wrapped,
            speakerNet,
            senderKey: null);

        _ui.GetUIController<ChatUIController>().ProcessChatMessage(msg, speechBubble: speechBubble);
    }
}
