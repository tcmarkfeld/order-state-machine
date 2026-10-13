using OrderStateMachine.Api.Gateways;
using OrderStateMachine.Api.Orders;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
// Without a server URL, Swagger UI calls the origin it was loaded from, whatever host or port that is.
builder.Services.AddOpenApi(options => options.AddDocumentTransformer((document, _, _) =>
{
    document.Servers = [];
    return Task.CompletedTask;
}));

builder.Services.AddSingleton(builder.Configuration.GetSection("Stubs").Get<StubOptions>() ?? new StubOptions());
builder.Services.AddSingleton<IPaymentGateway, StubPaymentGateway>();
builder.Services.AddSingleton<IOrderFulfillment, StubOrderFulfillment>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<OrderDependencies>();
builder.Services.AddSingleton<OrderService>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.UseSwaggerUI(options => options.SwaggerEndpoint("/openapi/v1.json", "Order State Machine"));
}

app.MapControllers();

app.Run();
