using System.Text.Json;
using Microsoft.Extensions.Options;
using SharedAgentChat.Application;
using SharedAgentChat.Application.Ports;
using SharedAgentChat.Infrastructure;
using SharedAgentChat.Web.Hubs;
using SharedAgentChat.Web.Realtime;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<SessionStoreOptions>(
    builder.Configuration.GetSection(SessionStoreOptions.SectionName));
builder.Services.Configure<AgentOptions>(
    builder.Configuration.GetSection(AgentOptions.SectionName));

builder.Services.AddSharedAgentChatInfrastructure(builder.Environment);
builder.Services.AddSingleton<IRealtimeNotifier, SignalRRealtimeNotifier>();
builder.Services.AddSingleton<JoinSessionHandler>();
builder.Services.AddSingleton<SendMessageHandler>();
builder.Services.AddSingleton<TypingHandler>();
builder.Services.AddSingleton<DisconnectHandler>();

builder.Services.AddSignalR()
    .AddJsonProtocol(options =>
    {
        options.PayloadSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        options.PayloadSerializerOptions.PropertyNameCaseInsensitive = true;
    });

builder.Logging.AddFilter("Microsoft.AspNetCore.SignalR", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.AspNetCore.Http.Connections", LogLevel.Warning);

var app = builder.Build();

var agentProvider = app.Services.GetRequiredService<IOptions<AgentOptions>>().Value.Provider;
app.Logger.LogInformation("Agent provider is {Provider}.", agentProvider);

app.UseDefaultFiles();
app.UseStaticFiles();
app.MapHub<ChatHub>("/hubs/chat");
app.MapGet("/api/agent", (IOptions<AgentOptions> options) =>
    Results.Json(new { provider = options.Value.Provider }));
app.MapFallbackToFile("index.html");

app.Run();

public partial class Program;
