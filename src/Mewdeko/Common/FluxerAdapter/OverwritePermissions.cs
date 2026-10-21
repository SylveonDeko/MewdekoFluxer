namespace Mewdeko.Common.FluxerAdapter;

/// <summary>
///     Whether a channel overwrite allows, denies or leaves a permission to the role or member defaults.
/// </summary>
public enum PermValue
{
    /// <summary>The overwrite grants the permission.</summary>
    Allow,

    /// <summary>The overwrite takes the permission away.</summary>
    Deny,

    /// <summary>The overwrite leaves the permission as the roles set it.</summary>
    Inherit
}

/// <summary>
///     The allowed and denied permissions of one channel overwrite.
/// </summary>
/// <param name="AllowValue">The permissions the overwrite grants.</param>
/// <param name="DenyValue">The permissions the overwrite takes away.</param>
public readonly record struct OverwritePermissions(ChannelPermission AllowValue, ChannelPermission DenyValue)
{
    /// <summary>An overwrite that changes nothing.</summary>
    public static OverwritePermissions InheritAll => new(0, 0);

    /// <summary>An overwrite that grants every permission.</summary>
    public static OverwritePermissions AllowAll => new((ChannelPermission)ulong.MaxValue, 0);

    /// <summary>An overwrite that takes away every permission.</summary>
    public static OverwritePermissions DenyAll => new(0, (ChannelPermission)ulong.MaxValue);

    /// <summary>Whether the overwrite allows, denies or inherits viewing the channel.</summary>
    public PermValue ViewChannel => Get(ChannelPermission.ViewChannel);

    /// <summary>Whether the overwrite allows, denies or inherits sending messages.</summary>
    public PermValue SendMessages => Get(ChannelPermission.SendMessages);

    /// <summary>Whether the overwrite allows, denies or inherits adding reactions.</summary>
    public PermValue AddReactions => Get(ChannelPermission.AddReactions);

    /// <summary>Whether the overwrite allows, denies or inherits connecting to voice.</summary>
    public PermValue Connect => Get(ChannelPermission.Connect);

    /// <summary>Whether the overwrite allows, denies or inherits speaking in voice.</summary>
    public PermValue Speak => Get(ChannelPermission.Speak);

    /// <summary>Whether the overwrite allows, denies or inherits sending messages in threads.</summary>
    public PermValue SendMessagesInThreads => Get(ChannelPermission.SendMessagesInThreads);

    /// <summary>Whether the overwrite allows, denies or inherits attaching files.</summary>
    public PermValue AttachFiles => Get(ChannelPermission.AttachFiles);

    /// <summary>Whether the overwrite allows, denies or inherits embedding links.</summary>
    public PermValue EmbedLinks => Get(ChannelPermission.EmbedLinks);

    /// <summary>Whether the overwrite allows, denies or inherits reading message history.</summary>
    public PermValue ReadMessageHistory => Get(ChannelPermission.ReadMessageHistory);

    /// <summary>Whether the overwrite allows, denies or inherits managing messages.</summary>
    public PermValue ManageMessages => Get(ChannelPermission.ManageMessages);

    /// <summary>Whether the overwrite allows, denies or inherits managing the channel.</summary>
    public PermValue ManageChannel => Get(ChannelPermission.ManageChannel);

    /// <summary>Whether the overwrite allows, denies or inherits managing overwrites.</summary>
    public PermValue ManageRoles => Get(ChannelPermission.ManagePermissions);

    /// <summary>Whether the overwrite allows, denies or inherits creating instant invites.</summary>
    public PermValue CreateInstantInvite => Get(ChannelPermission.CreateInstantInvite);

    /// <summary>Whether the overwrite allows, denies or inherits using priority speaker.</summary>
    public PermValue PrioritySpeaker => Get(ChannelPermission.PrioritySpeaker);

    /// <summary>Whether the overwrite allows, denies or inherits streaming video.</summary>
    public PermValue Stream => Get(ChannelPermission.Stream);

    /// <summary>Whether the overwrite allows, denies or inherits sending text-to-speech messages.</summary>
    public PermValue SendTTSMessages => Get(ChannelPermission.SendTTSMessages);

    /// <summary>Whether the overwrite allows, denies or inherits mentioning everyone.</summary>
    public PermValue MentionEveryone => Get(ChannelPermission.MentionEveryone);

    /// <summary>Whether the overwrite allows, denies or inherits using external emojis.</summary>
    public PermValue UseExternalEmojis => Get(ChannelPermission.UseExternalEmojis);

    /// <summary>Whether the overwrite allows, denies or inherits muting members in voice.</summary>
    public PermValue MuteMembers => Get(ChannelPermission.MuteMembers);

    /// <summary>Whether the overwrite allows, denies or inherits deafening members in voice.</summary>
    public PermValue DeafenMembers => Get(ChannelPermission.DeafenMembers);

    /// <summary>Whether the overwrite allows, denies or inherits moving members between voice channels.</summary>
    public PermValue MoveMembers => Get(ChannelPermission.MoveMembers);

    /// <summary>Whether the overwrite allows, denies or inherits using voice activity detection.</summary>
    public PermValue UseVAD => Get(ChannelPermission.UseVAD);

    /// <summary>Whether the overwrite allows, denies or inherits managing webhooks.</summary>
    public PermValue ManageWebhooks => Get(ChannelPermission.ManageWebhooks);

    /// <summary>Whether the overwrite allows, denies or inherits managing threads.</summary>
    public PermValue ManageThreads => Get(ChannelPermission.ManageThreads);

    /// <summary>Whether the overwrite allows, denies or inherits creating public threads.</summary>
    public PermValue CreatePublicThreads => Get(ChannelPermission.CreatePublicThreads);

    /// <summary>Whether the overwrite allows, denies or inherits creating private threads.</summary>
    public PermValue CreatePrivateThreads => Get(ChannelPermission.CreatePrivateThreads);

    /// <summary>Whether the overwrite allows, denies or inherits using external stickers.</summary>
    public PermValue UseExternalStickers => Get(ChannelPermission.UseExternalStickers);

    /// <summary>Whether the overwrite allows, denies or inherits pinning messages.</summary>
    public PermValue PinMessages => Get(ChannelPermission.PinMessages);

    /// <summary>Whether the overwrite allows, denies or inherits bypassing slowmode.</summary>
    public PermValue BypassSlowmode => Get(ChannelPermission.BypassSlowmode);

    /// <summary>Whether the overwrite allows, denies or inherits changing the voice region.</summary>
    public PermValue UpdateRTCRegion => Get(ChannelPermission.UpdateRTCRegion);

    /// <summary>Whether the overwrite allows, denies or inherits viewing the channel member list.</summary>
    public PermValue ViewChannelMembers => Get(ChannelPermission.ViewChannelMembers);

    /// <summary>
    ///     Returns whether the overwrite allows, denies or inherits a permission.
    /// </summary>
    public PermValue Get(ChannelPermission permission) =>
        (AllowValue & permission) == permission ? PermValue.Allow
        : (DenyValue & permission) == permission ? PermValue.Deny
        : PermValue.Inherit;

    /// <summary>
    ///     Returns a copy with one permission set to allow, deny or inherit.
    /// </summary>
    public OverwritePermissions With(ChannelPermission permission, PermValue value) => value switch
    {
        PermValue.Allow => new OverwritePermissions(AllowValue | permission, DenyValue & ~permission),
        PermValue.Deny => new OverwritePermissions(AllowValue & ~permission, DenyValue | permission),
        _ => new OverwritePermissions(AllowValue & ~permission, DenyValue & ~permission)
    };

    /// <summary>
    ///     Returns a copy with the given permissions changed and the rest left as they were.
    /// </summary>
    public OverwritePermissions Modify(PermValue? viewChannel = null, PermValue? sendMessages = null,
        PermValue? addReactions = null, PermValue? connect = null, PermValue? speak = null,
        PermValue? sendMessagesInThreads = null, PermValue? attachFiles = null, PermValue? embedLinks = null,
        PermValue? readMessageHistory = null, PermValue? manageMessages = null, PermValue? manageChannel = null,
        PermValue? manageRoles = null, PermValue? createInstantInvite = null, PermValue? prioritySpeaker = null,
        PermValue? stream = null, PermValue? sendTtsMessages = null, PermValue? mentionEveryone = null,
        PermValue? useExternalEmojis = null, PermValue? muteMembers = null, PermValue? deafenMembers = null,
        PermValue? moveMembers = null, PermValue? useVad = null, PermValue? manageWebhooks = null,
        PermValue? manageThreads = null, PermValue? createPublicThreads = null,
        PermValue? createPrivateThreads = null, PermValue? useExternalStickers = null, PermValue? pinMessages = null,
        PermValue? bypassSlowmode = null, PermValue? updateRtcRegion = null, PermValue? viewChannelMembers = null)
    {
        var result = this;
        Apply(ChannelPermission.ViewChannel, viewChannel);
        Apply(ChannelPermission.SendMessages, sendMessages);
        Apply(ChannelPermission.AddReactions, addReactions);
        Apply(ChannelPermission.Connect, connect);
        Apply(ChannelPermission.Speak, speak);
        Apply(ChannelPermission.SendMessagesInThreads, sendMessagesInThreads);
        Apply(ChannelPermission.AttachFiles, attachFiles);
        Apply(ChannelPermission.EmbedLinks, embedLinks);
        Apply(ChannelPermission.ReadMessageHistory, readMessageHistory);
        Apply(ChannelPermission.ManageMessages, manageMessages);
        Apply(ChannelPermission.ManageChannel, manageChannel);
        Apply(ChannelPermission.ManagePermissions, manageRoles);
        Apply(ChannelPermission.CreateInstantInvite, createInstantInvite);
        Apply(ChannelPermission.PrioritySpeaker, prioritySpeaker);
        Apply(ChannelPermission.Stream, stream);
        Apply(ChannelPermission.SendTTSMessages, sendTtsMessages);
        Apply(ChannelPermission.MentionEveryone, mentionEveryone);
        Apply(ChannelPermission.UseExternalEmojis, useExternalEmojis);
        Apply(ChannelPermission.MuteMembers, muteMembers);
        Apply(ChannelPermission.DeafenMembers, deafenMembers);
        Apply(ChannelPermission.MoveMembers, moveMembers);
        Apply(ChannelPermission.UseVAD, useVad);
        Apply(ChannelPermission.ManageWebhooks, manageWebhooks);
        Apply(ChannelPermission.ManageThreads, manageThreads);
        Apply(ChannelPermission.CreatePublicThreads, createPublicThreads);
        Apply(ChannelPermission.CreatePrivateThreads, createPrivateThreads);
        Apply(ChannelPermission.UseExternalStickers, useExternalStickers);
        Apply(ChannelPermission.PinMessages, pinMessages);
        Apply(ChannelPermission.BypassSlowmode, bypassSlowmode);
        Apply(ChannelPermission.UpdateRTCRegion, updateRtcRegion);
        Apply(ChannelPermission.ViewChannelMembers, viewChannelMembers);
        return result;

        void Apply(ChannelPermission permission, PermValue? value)
        {
            if (value is { } set)
                result = result.With(permission, set);
        }
    }

    /// <summary>
    ///     Reads the overwrite stored on a channel.
    /// </summary>
    public static OverwritePermissions From(PermissionOverwrite overwrite) =>
        new(overwrite.Allow.RawValue, overwrite.Deny.RawValue);
}
