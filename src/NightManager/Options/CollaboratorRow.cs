namespace NightManager.Plugin.Options {
    /// <summary>One person in the Collaborators list: a member (Uid) or a pending invitation (InvitationId).</summary>
    public sealed record CollaboratorRow(string Email, string Role, string Status, string Uid, string InvitationId);
}
