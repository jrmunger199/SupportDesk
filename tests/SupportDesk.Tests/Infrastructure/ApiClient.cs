using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

using SupportDesk.Api.Contracts;
using SupportDesk.Domain;

namespace SupportDesk.Tests.Infrastructure;

/// <summary>A signed-in user and a ready-to-use HttpClient. Keeps the tests focused on behaviour, not plumbing.</summary>
public sealed class Session(HttpClient http, AuthResponse auth, ApiFactory factory)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public HttpClient Http { get; } = http;

    public AuthResponse Auth { get; } = auth;

    public ApiFactory Factory { get; } = factory;

    public Guid UserId => Auth.User.Id;

    public string Token => Auth.Token;

    public static async Task<Session> RegisterOrganizationAsync(ApiFactory factory, string? organization = null, string? email = null)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var http = factory.CreateClient();
        var response = await http.PostAsJsonAsync(
            "/api/auth/register",
            new RegisterOrganizationRequest(organization ?? $"Org {suffix}", "Admin Person", email ?? $"admin-{suffix}@example.test", "CorrectHorse!123"),
            Json);
        response.EnsureSuccessStatusCode();
        return Create(factory, await response.Content.ReadFromJsonAsync<AuthResponse>(Json) ?? throw new InvalidOperationException());
    }

    public static Session Create(ApiFactory factory, AuthResponse auth)
    {
        var http = factory.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.Token);
        return new Session(http, auth, factory);
    }

    public static async Task<Session> LoginAsync(ApiFactory factory, string email, string password)
    {
        var response = await factory.CreateClient().PostAsJsonAsync("/api/auth/login", new LoginRequest(email, password), Json);
        response.EnsureSuccessStatusCode();
        return Create(factory, await response.Content.ReadFromJsonAsync<AuthResponse>(Json) ?? throw new InvalidOperationException());
    }

    /// <summary>Admin helper: creates a user in this session's organisation and signs them in.</summary>
    public async Task<Session> AddUserAsync(Role role, string? name = null)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var email = $"{role.ToString().ToLowerInvariant()}-{suffix}@example.test";
        var created = await Http.PostAsJsonAsync("/api/users", new CreateUserRequest(name ?? $"{role} {suffix}", email, "CorrectHorse!123", role), Json);
        created.EnsureSuccessStatusCode();
        return await LoginAsync(Factory, email, "CorrectHorse!123");
    }

    public async Task<TicketDetailDto> CreateTicketAsync(string title = "Printer is on fire", TicketPriority? priority = null)
    {
        var response = await Http.PostAsJsonAsync("/api/tickets", new CreateTicketRequest(title, "Details about the problem", priority), Json);
        response.EnsureSuccessStatusCode();
        return await Read<TicketDetailDto>(response);
    }

    public Task<HttpResponseMessage> PatchTicketAsync(TicketDetailDto ticket, TicketStatus? status = null, TicketPriority? priority = null, Guid? assignee = null, bool clearAssignee = false, string? rowVersion = null) =>
        Http.PatchAsJsonAsync($"/api/tickets/{ticket.Id}", new UpdateTicketRequest(status, priority, assignee, clearAssignee, rowVersion ?? ticket.RowVersion), Json);

    public async Task<T> GetAsync<T>(string url)
    {
        var response = await Http.GetAsync(url);
        response.EnsureSuccessStatusCode();
        return await Read<T>(response);
    }

    public static async Task<T> Read<T>(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<T>(Json) ?? throw new InvalidOperationException("Empty response body.");
}
