using System.Net;
using System.Net.Http.Headers;
using FluentAssertions;
using FreshCart.CustomerSupport.Api.Domain;
using Xunit;

namespace FreshCart.CustomerSupport.Tests.Endpoints;

public sealed class SupportEndpointsTests(SupportApiFactory factory) : IClassFixture<SupportApiFactory>
{
    private const string CustomerRole = "Customer";
    private const string SupportAgentRole = "SupportAgent";
    private const string AdministratorRole = "Administrator";
    private const string Topic = "Where is my order?";

    private static readonly DateTimeOffset StartedOnUtc = new(2026, 6, 26, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ActiveSessionsRejectsAnUnauthenticatedCaller()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/support/sessions/active");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ACustomerSeesTheirOwnOpenSession()
    {
        var customerId = Guid.NewGuid();
        await factory.Sessions.SaveAsync(
            ChatSession.Start(Guid.NewGuid(), Topic, customerId, "Demo Customer", StartedOnUtc),
            CancellationToken.None);

        var response = await SendAsync(HttpMethod.Get, "/support/sessions/active", customerId, CustomerRole);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ACustomerWithNoOpenSessionGetsNoContent()
    {
        var response = await SendAsync(HttpMethod.Get, "/support/sessions/active", Guid.NewGuid(), CustomerRole);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task AnAgentSeesTheirActiveSessions()
    {
        var agentId = Guid.NewGuid();
        var session = ChatSession.Start(Guid.NewGuid(), Topic, Guid.NewGuid(), "Demo Customer", StartedOnUtc);
        session.AssignTo(agentId, "Demo Agent");
        await factory.Sessions.SaveAsync(session, CancellationToken.None);

        var response = await SendAsync(HttpMethod.Get, "/support/sessions/active", agentId, SupportAgentRole);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ACustomerCannotReadAnotherCustomersTranscript()
    {
        var ownerCustomerId = Guid.NewGuid();
        var session = ChatSession.Start(Guid.NewGuid(), Topic, ownerCustomerId, "Owner", StartedOnUtc);
        session.AssignTo(Guid.NewGuid(), "Demo Agent");
        await factory.Sessions.SaveAsync(session, CancellationToken.None);

        var response = await SendAsync(
            HttpMethod.Get, $"/support/sessions/{session.Id}/messages", Guid.NewGuid(), CustomerRole);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task AParticipantCanReadTheirOwnTranscript()
    {
        var customerId = Guid.NewGuid();
        var session = ChatSession.Start(Guid.NewGuid(), Topic, customerId, "Owner", StartedOnUtc);
        await factory.Sessions.SaveAsync(session, CancellationToken.None);

        var response = await SendAsync(
            HttpMethod.Get, $"/support/sessions/{session.Id}/messages", customerId, CustomerRole);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task AnAdministratorCanReadAnyTranscriptEvenWhenNotAParticipant()
    {
        var session = ChatSession.Start(Guid.NewGuid(), Topic, Guid.NewGuid(), "Owner", StartedOnUtc);
        await factory.Sessions.SaveAsync(session, CancellationToken.None);

        var response = await SendAsync(
            HttpMethod.Get, $"/support/sessions/{session.Id}/messages", Guid.NewGuid(), AdministratorRole);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ReadingTheTranscriptOfAnUnknownSessionReturnsNotFound()
    {
        var response = await SendAsync(
            HttpMethod.Get, $"/support/sessions/{Guid.NewGuid()}/messages", Guid.NewGuid(), AdministratorRole);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ACustomerCannotBrowseTheFullSessionList()
    {
        var response = await SendAsync(HttpMethod.Get, "/support/sessions/", Guid.NewGuid(), CustomerRole);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task BackOfficeStaffCanBrowseTheFullSessionList()
    {
        var response = await SendAsync(HttpMethod.Get, "/support/sessions/", Guid.NewGuid(), AdministratorRole);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string uri, Guid userId, string role)
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", SupportApiFactory.CreateAccessToken(userId, role));
        return await client.SendAsync(request);
    }
}
