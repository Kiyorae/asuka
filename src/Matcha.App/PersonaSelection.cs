using Matcha.Core;

namespace Matcha.App;

/// <summary>
/// Selects the active and bot identities for the desktop protocol host. Keeping this
/// independent of storage and XAML makes interrupted first-run recovery deterministic.
/// </summary>
public static class PersonaSelection
{
    public static PersonaPair Resolve(
        IReadOnlyList<User> users,
        string? requestedActiveUserId,
        string? requestedBotUserId,
        string? legacyPersonaId = null)
    {
        ArgumentNullException.ThrowIfNull(users);
        if (users.Count == 0)
        {
            throw new ArgumentException("At least one persona is required.", nameof(users));
        }

        var active = Find(users, requestedActiveUserId)
            ?? Find(users, legacyPersonaId)
            ?? users[0];
        var bot = Find(users, requestedBotUserId)
            ?? users.FirstOrDefault(user => user.Id != active.Id)
            ?? active;
        return new PersonaPair(active, bot);
    }

    private static User? Find(IEnumerable<User> users, string? id) =>
        string.IsNullOrWhiteSpace(id)
            ? null
            : users.FirstOrDefault(user => string.Equals(user.Id, id, StringComparison.Ordinal));
}

public sealed record PersonaPair(User Active, User Bot);
