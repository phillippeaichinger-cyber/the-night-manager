using NightManager.Core.Security;
using NightManager.Core.Sharing;
using NightManager.Core.Sync;

namespace NightManager.Core.Tests;

public class CollaboratorsTests {
    private static ApiCredential Credential() {
        ApiCredential.TryParse("nm2_key1_0123456789abcdef0123456789abcdefXYZ", out var credential);
        return credential!;
    }

    [Test]
    public async Task Lists_members_and_pending_invitations_with_the_owners_plan() {
        var transport = new SyncEngineTests.FakeTransport();
        transport.NextResponses.Enqueue(new ApiResult(200, """
            {"plan":{"label":"Pro","maxCollaborators":4},
             "members":[{"uid":"u2","email":"sam@example.com","role":"operator","joinedAt":"2026-10-12T16:00:00.000Z"}],
             "invitations":[{"invitationId":"i1","email":"kim@example.com","role":"viewer","expiresAt":"2026-10-26T16:00:00.000Z"}]}
            """));

        var list = await new CollaboratorsClient(transport, Credential()).ListAsync(CancellationToken.None);

        Assert.That(list.Plan.MaxCollaborators, Is.EqualTo(4));
        Assert.That(list.Members.Single().Email, Is.EqualTo("sam@example.com"));
        Assert.That(list.Invitations.Single().InvitationId, Is.EqualTo("i1"));
        Assert.That(transport.Requests.Single().Path, Is.EqualTo(CollaboratorPaths.List));
        Assert.That(transport.Requests.Single().Credential, Is.Not.Null, "signed with the PC's credential");
    }

    [Test]
    public async Task Invite_sends_trimmed_email_and_role() {
        var transport = new SyncEngineTests.FakeTransport();
        transport.NextResponses.Enqueue(new ApiResult(201, """{"invitationId":"i9","code":"c","link":"https://x.example/invite/c","expiresAt":"2026-10-26T16:00:00.000Z"}"""));

        var link = await new CollaboratorsClient(transport, Credential()).InviteAsync("  sam@example.com ", "operator", CancellationToken.None);

        Assert.That(link.Link, Is.EqualTo("https://x.example/invite/c"));
        Assert.That(transport.Requests.Single().Json, Is.EqualTo("""{"email":"sam@example.com","role":"operator"}"""));
    }

    [Test]
    public void Plan_limits_come_back_in_the_services_words() {
        var transport = new SyncEngineTests.FakeTransport();
        transport.NextResponses.Enqueue(new ApiResult(403, """{"error":"forbidden","message":"Sharing isn't included in the Free plan. Upgrade to add collaborators; they never pay to see your scopes."}"""));

        var ex = Assert.ThrowsAsync<SharingException>(() => new CollaboratorsClient(transport, Credential()).InviteAsync("a@b.c", "viewer", CancellationToken.None));
        Assert.That(ex!.Message, Does.StartWith("Sharing isn't included in the Free plan"));
    }

    [Test]
    public void No_connection_is_explained() {
        var transport = new SyncEngineTests.FakeTransport();
        var ex = Assert.ThrowsAsync<SharingException>(() => new CollaboratorsClient(transport, Credential()).ListAsync(CancellationToken.None));
        Assert.That(ex!.Message, Does.Contain("No connection"));
    }
}
