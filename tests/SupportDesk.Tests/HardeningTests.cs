using System.Net;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using SupportDesk.Api.Configuration;
using SupportDesk.Api.Infrastructure;
using SupportDesk.Tests.Infrastructure;

namespace SupportDesk.Tests;

/// <summary>Regression tests for findings from the vulnerability review.</summary>
[Collection(ApiTestGroup.Name)]
public class HardeningTests(ApiFactory factory)
{
    // ---- Oversized bodies are the client's mistake (413), not a server error (500) ----

    private static async Task<(DefaultHttpContext Context, string Body)> Handle(Exception exception, ILogger<GlobalExceptionHandler>? logger = null)
    {
        var provider = new ServiceCollection().AddLogging().AddProblemDetails().BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = provider };
        context.Response.Body = new MemoryStream();

        var handler = new GlobalExceptionHandler(
            provider.GetRequiredService<IProblemDetailsService>(),
            logger ?? NullLogger<GlobalExceptionHandler>.Instance);
        Assert.True(await handler.TryHandleAsync(context, exception, CancellationToken.None));

        context.Response.Body.Position = 0;
        return (context, await new StreamReader(context.Response.Body).ReadToEndAsync());
    }

    [Fact]
    public async Task An_oversized_request_body_is_reported_as_413()
    {
        // Kestrel throws exactly this when a body exceeds MaxRequestBodySize.
        var (context, body) = await Handle(new BadHttpRequestException("Request body too large. The max request body size is 1000000 bytes.", StatusCodes.Status413PayloadTooLarge));

        Assert.Equal(StatusCodes.Status413PayloadTooLarge, context.Response.StatusCode);
        Assert.Contains("The request body is too large.", body, StringComparison.Ordinal);
        Assert.DoesNotContain("1000000", body, StringComparison.Ordinal); // internals are not echoed
    }

    [Fact]
    public async Task Other_client_protocol_errors_keep_their_4xx_status()
    {
        var (context, _) = await Handle(new BadHttpRequestException("Invalid request line", StatusCodes.Status400BadRequest));

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
    }

    private sealed class RecordingLogger : ILogger<GlobalExceptionHandler>
    {
        public List<LogLevel> Levels { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Levels.Add(logLevel);
    }

    [Fact]
    public async Task Client_mistakes_do_not_raise_error_level_logs_but_real_failures_do()
    {
        var logger = new RecordingLogger();

        await Handle(new BadHttpRequestException("too large", StatusCodes.Status413PayloadTooLarge), logger);
        Assert.Empty(logger.Levels); // an attacker must not be able to flood error alerts with oversized requests

        await Handle(new InvalidOperationException("boom"), logger);
        Assert.Contains(LogLevel.Error, logger.Levels);
    }

    [Fact]
    public async Task Unexpected_exceptions_still_return_a_generic_500()
    {
        var (context, body) = await Handle(new InvalidOperationException("secret internal detail"));

        Assert.Equal(StatusCodes.Status500InternalServerError, context.Response.StatusCode);
        Assert.DoesNotContain("secret internal detail", body, StringComparison.Ordinal);
    }

    // ---- Container health probe ----

    [Fact]
    public async Task The_health_probe_maps_the_services_answer_to_an_exit_code()
    {
        var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{port}/");
        listener.Start();
        var status = 200;
        var serving = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                try
                {
                    var context = await listener.GetContextAsync();
                    context.Response.StatusCode = status;
                    context.Response.Close();
                }
                catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
                {
                    break;
                }
            }
        });

        Assert.Equal(0, await SupportDesk.Api.Infrastructure.HealthProbe.RunAsync(port.ToString(System.Globalization.CultureInfo.InvariantCulture)));

        status = 503;
        Assert.Equal(1, await SupportDesk.Api.Infrastructure.HealthProbe.RunAsync(port.ToString(System.Globalization.CultureInfo.InvariantCulture)));

        listener.Stop();
        await serving;
        Assert.Equal(1, await SupportDesk.Api.Infrastructure.HealthProbe.RunAsync(port.ToString(System.Globalization.CultureInfo.InvariantCulture))); // nothing listening
    }

    // ---- CORS: nothing is allowed unless configured ----

    private static async Task<HttpResponseMessage> Preflight(HttpClient client, string origin)
    {
        var request = new HttpRequestMessage(HttpMethod.Options, "/api/tickets");
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", "GET");
        request.Headers.Add("Access-Control-Request-Headers", "authorization");
        return await client.SendAsync(request);
    }

    [Theory]
    [InlineData("http://localhost:4200")]
    [InlineData("https://evil.example")]
    public async Task No_origin_is_allowed_by_default_including_the_dev_server(string origin)
    {
        var response = await Preflight(factory.CreateClient(), origin);

        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task Only_explicitly_configured_origins_are_allowed()
    {
        await using var configured = factory.WithWebHostBuilder(b => b.UseSetting("Cors:AllowedOrigins:0", "https://app.example.com"));
        var client = configured.CreateClient();

        var allowed = await Preflight(client, "https://app.example.com");
        var other = await Preflight(client, "https://evil.example");

        Assert.Equal("https://app.example.com", allowed.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.False(other.Headers.Contains("Access-Control-Allow-Origin"));
    }

    // ---- Signing key guard ----

    [Theory]
    [InlineData("development-only-signing-key-change-me-0123456789")]
    [InlineData("replace-with-a-long-random-string-of-at-least-32-characters")]
    [InlineData("please-CHANGE-ME-before-deploying-this-service-ok")]
    public void Placeholder_and_dev_keys_are_refused_outside_development(string key)
    {
        var validator = new JwtOptionsValidator(new FakeEnvironment("Production"));

        var result = validator.Validate(null, new JwtOptions { Key = key });

        Assert.True(result.Failed);
    }

    [Fact]
    public void Real_keys_are_accepted_and_the_dev_key_is_allowed_in_development()
    {
        var real = new JwtOptions { Key = Convert.ToBase64String(new byte[48].Select((_, i) => (byte)((i * 37) + 11)).ToArray()) };
        var dev = new JwtOptions { Key = "development-only-signing-key-change-me-0123456789" };

        Assert.True(new JwtOptionsValidator(new FakeEnvironment("Production")).Validate(null, real).Succeeded);
        Assert.True(new JwtOptionsValidator(new FakeEnvironment("Development")).Validate(null, dev).Succeeded);
    }

    [Fact]
    public void The_host_refuses_to_start_with_a_placeholder_key_in_production()
    {
        using var broken = factory.WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Production");
            b.UseSetting("Jwt:Key", "replace-with-a-long-random-string-of-at-least-32-characters");
        });

        var failure = Assert.ThrowsAny<Exception>(() => broken.CreateClient());

        Assert.Contains("placeholder", failure.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    private sealed class FakeEnvironment(string name) : Microsoft.Extensions.Hosting.IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;

        public string ApplicationName { get; set; } = "test";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
