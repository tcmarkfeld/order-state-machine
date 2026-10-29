using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using OrderStateMachine.Api.Controllers;
using OrderStateMachine.Api.Gateways;
using OrderStateMachine.Api.Orders;

namespace OrderStateMachine.Tests;

public class ApiTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task CreateAuthorizeCompleteAndQuery()
    {
        var created = await _client.PostAsync("/orders", null);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var order = (await created.Content.ReadFromJsonAsync<OrderResponse>())!;
        Assert.Equal(OrderState.Initialized, order.State);

        var completeTooEarly = await _client.PostAsync($"/orders/{order.Id}/complete", null);
        Assert.Equal(HttpStatusCode.Conflict, completeTooEarly.StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await _client.PostAsync($"/orders/{order.Id}/authorize", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _client.PostAsync($"/orders/{order.Id}/complete", null)).StatusCode);

        var fetched = (await _client.GetFromJsonAsync<OrderResponse>($"/orders/{order.Id}"))!;
        Assert.Equal(OrderState.Complete, fetched.State);
        Assert.Equal(3, fetched.History.Count);

        var completeAgain = await _client.PostAsync($"/orders/{order.Id}/complete", null);
        Assert.Equal(HttpStatusCode.Conflict, completeAgain.StatusCode);
    }

    [Fact]
    public async Task UnknownOrder_Returns404()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/orders/{Guid.NewGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.PostAsync($"/orders/{Guid.NewGuid()}/authorize", null)).StatusCode);
    }

    [Fact]
    public async Task ResolveNeedsAttentionOrder()
    {
        var stubs = new StubOptions { FailCompletion = true, FailVoid = true };
        var client = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services => services.AddSingleton(stubs)))
            .CreateClient();

        var order = (await (await client.PostAsync("/orders", null)).Content.ReadFromJsonAsync<OrderResponse>())!;
        var resolveTooEarly = await client.PostAsJsonAsync($"/orders/{order.Id}/resolve", new { action = "Complete", reason = "test" });
        Assert.Equal(HttpStatusCode.Conflict, resolveTooEarly.StatusCode);

        await client.PostAsync($"/orders/{order.Id}/authorize", null);
        await client.PostAsync($"/orders/{order.Id}/complete", null);

        var missingReason = await client.PostAsJsonAsync($"/orders/{order.Id}/resolve", new { action = "Complete" });
        Assert.Equal(HttpStatusCode.BadRequest, missingReason.StatusCode);
        var missingAction = await client.PostAsJsonAsync($"/orders/{order.Id}/resolve", new { reason = "test" });
        Assert.Equal(HttpStatusCode.BadRequest, missingAction.StatusCode);

        stubs.FailCompletion = false;
        var resolved = await client.PostAsJsonAsync($"/orders/{order.Id}/resolve", new { action = "Complete", reason = "Tickets restocked" });
        Assert.Equal(HttpStatusCode.OK, resolved.StatusCode);
        var body = (await resolved.Content.ReadFromJsonAsync<OrderResponse>())!;
        Assert.Equal(OrderState.Complete, body.State);
        Assert.Equal("Tickets restocked", body.ResolutionReason);
    }
}
