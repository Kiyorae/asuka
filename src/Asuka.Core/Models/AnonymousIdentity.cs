namespace Asuka.Core;

/// <summary>A group-scoped public alias, independent of the sender's account identity.</summary>
public sealed record AnonymousIdentity(long Id, string Name, string Flag);
