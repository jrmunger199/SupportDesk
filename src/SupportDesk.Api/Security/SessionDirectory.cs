using System.Collections.Concurrent;

using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Caching.Memory;

using SupportDesk.Domain;

namespace SupportDesk.Api.Security;

/// <summary>What the server currently believes about a user, used to re-check tokens that are otherwise self-contained.</summary>
public sealed record SessionInfo(bool IsActive, Role Role, Guid TenantId);

/// <summary>
/// A JWT stays cryptographically valid until it expires, even after the person is deactivated. This short-lived cache
/// lets every request cheaply confirm the account is still active without a database round trip each time.
/// Deactivation calls <see cref="Invalidate"/>, so on the instance that handled it the change is immediate;
/// other instances in a scaled-out deployment see it within <see cref="Lifetime"/>.
/// </summary>
public sealed class SessionDirectory(IMemoryCache cache)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(30);

    public async Task<SessionInfo?> GetAsync(Guid userId, Func<Task<SessionInfo?>> load)
    {
        if (cache.TryGetValue(Key(userId), out SessionInfo? cached))
        {
            return cached;
        }

        var loaded = await load();
        cache.Set(Key(userId), loaded, Lifetime);
        return loaded;
    }

    public void Invalidate(Guid userId) => cache.Remove(Key(userId));

    private static string Key(Guid userId) => $"session:{userId:N}";
}

/// <summary>
/// Tracks open SignalR connections per user so they can be closed when an account is deactivated. Token validation
/// only runs when a connection is opened, so without this a deactivated user's socket would keep receiving pushes.
/// In-memory, so it covers connections on this instance; a scaled-out deployment would need a shared mechanism.
/// </summary>
public sealed class HubConnections
{
    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<string, HubCallerContext>> _byUser = new();

    public void Add(Guid userId, HubCallerContext context) =>
        _byUser.GetOrAdd(userId, _ => new ConcurrentDictionary<string, HubCallerContext>())[context.ConnectionId] = context;

    public void Remove(Guid userId, string connectionId)
    {
        if (_byUser.TryGetValue(userId, out var connections))
        {
            connections.TryRemove(connectionId, out _);
        }
    }

    /// <summary>Closes every open connection for the user and returns how many were aborted.</summary>
    public int AbortUser(Guid userId)
    {
        if (!_byUser.TryRemove(userId, out var connections))
        {
            return 0;
        }

        foreach (var context in connections.Values)
        {
            context.Abort();
        }

        return connections.Count;
    }
}
