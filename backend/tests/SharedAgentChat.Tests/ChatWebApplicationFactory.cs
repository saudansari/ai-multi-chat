using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Hosting;

namespace SharedAgentChat.Tests;

public sealed class ChatWebApplicationFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("SessionStore:Mode", "Memory");
        builder.UseSetting("Agent:Provider", "Stub");
        builder.UseSetting("Agent:StubDelayMilliseconds", "80");
    }
}
