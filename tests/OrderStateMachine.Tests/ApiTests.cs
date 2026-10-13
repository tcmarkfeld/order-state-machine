using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using OrderStateMachine.Api.Controllers;
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
}
