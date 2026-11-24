namespace SupportDesk.Api.Infrastructure;

/// <summary>
/// Lets the container probe itself. The ASP.NET runtime image has no curl or wget, so Docker's HEALTHCHECK runs
/// <c>dotnet SupportDesk.Api.dll --healthcheck</c>, which asks the running service's own /health endpoint and
/// reports the answer as the process exit code (0 healthy, 1 not).
/// </summary>
public static class HealthProbe
{
    public const string Argument = "--healthcheck";

    public static async Task<int> RunAsync(string? httpPorts = null, CancellationToken cancellationToken = default)
    {
        // The image sets ASPNETCORE_HTTP_PORTS; take the first port if several are listed.
        var port = (httpPorts ?? Environment.GetEnvironmentVariable("ASPNETCORE_HTTP_PORTS") ?? "8080")
            .Split(';', StringSplitOptions.RemoveEmptyEntries)[0];

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        try
        {
            using var response = await http.GetAsync(new Uri($"http://localhost:{port}/health"), cancellationToken);
            return response.IsSuccessStatusCode ? 0 : 1;
        }
        catch (HttpRequestException)
        {
            return 1;
        }
        catch (TaskCanceledException)
        {
            return 1;
        }
    }
}
