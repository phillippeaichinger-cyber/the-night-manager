using System.Text.Json;
using NightManager.Core.Json;
using NightManager.Core.Security;
using NightManager.Core.Sync;

namespace NightManager.Core.Sharing;

/// <summary>
/// People this observatory is shared with. Collaborators never pay to see shared scopes: every limit
/// (how many collaborators, rigs, AI reports) comes from the observatory owner's plan.
/// </summary>
public sealed record CollaboratorList {
    public PlanSummary Plan { get; init; } = new();
    public IReadOnlyList<Member> Members { get; init; } = [];
    public IReadOnlyList<PendingInvitation> Invitations { get; init; } = [];

    public sealed record PlanSummary {
        public string Label { get; init; } = "";
        public int MaxCollaborators { get; init; }
    }

    public sealed record Member {
        public required string Uid { get; init; }
        public string? Email { get; init; }
        public string Role { get; init; } = "viewer";
        public DateTimeOffset? JoinedAt { get; init; }
    }

    public sealed record PendingInvitation {
        public required string InvitationId { get; init; }
        public required string Email { get; init; }
        public string Role { get; init; } = "viewer";
        public DateTimeOffset ExpiresAt { get; init; }
    }
}

public sealed record InvitationLink {
    public required string InvitationId { get; init; }
    public required string Link { get; init; }
    public DateTimeOffset ExpiresAt { get; init; }
}

public static class CollaboratorPaths {
    public const string List = "/v2/observatory/collaborators";
    public const string Invite = "/v2/observatory/collaborators/invite";
    public const string Remove = "/v2/observatory/collaborators/remove";
}

/// <summary>Signed calls with the PC's credential; the backend acts for the observatory owner.</summary>
public sealed class CollaboratorsClient(IApiTransport transport, ApiCredential credential) {
    public async Task<CollaboratorList> ListAsync(CancellationToken token) {
        var result = await transport.PostJsonAsync(CollaboratorPaths.List, "{}", credential, token).ConfigureAwait(false);
        return Read<CollaboratorList>(result, "Couldn't load collaborators.");
    }

    /// <param name="role">viewer (sees everything) or operator (can also send remote actions).</param>
    public async Task<InvitationLink> InviteAsync(string email, string role, CancellationToken token) {
        var body = NmJson.Serialize(new { email = email.Trim(), role });
        var result = await transport.PostJsonAsync(CollaboratorPaths.Invite, body, credential, token).ConfigureAwait(false);
        return Read<InvitationLink>(result, "Couldn't send the invitation.");
    }

    public async Task RemoveMemberAsync(string uid, CancellationToken token) =>
        Check(await transport.PostJsonAsync(CollaboratorPaths.Remove, NmJson.Serialize(new { uid }), credential, token).ConfigureAwait(false), "Couldn't remove that person.");

    public async Task RevokeInvitationAsync(string invitationId, CancellationToken token) =>
        Check(await transport.PostJsonAsync(CollaboratorPaths.Remove, NmJson.Serialize(new { invitationId }), credential, token).ConfigureAwait(false), "Couldn't cancel that invitation.");

    private static T Read<T>(ApiResult result, string fallback) {
        Check(result, fallback);
        return NmJson.Deserialize<T>(result.Body ?? "") ?? throw new SharingException(fallback);
    }

    private static void Check(ApiResult result, string fallback) {
        if (result.IsSuccess) {
            return;
        }
        throw new SharingException(Message(result) ?? (result.StatusCode == 0 ? "No connection to The Night Manager service." : fallback));
    }

    /// <summary>The service's own explanation (e.g. "The Free plan doesn't include collaborators…").</summary>
    public static string? Message(ApiResult result) {
        if (string.IsNullOrWhiteSpace(result.Body)) {
            return null;
        }
        try {
            using var doc = JsonDocument.Parse(result.Body);
            return doc.RootElement.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
        } catch (JsonException) {
            return null;
        }
    }
}

public sealed class SharingException(string message) : Exception(message);
