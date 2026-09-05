namespace Asuka.Core;

public sealed class StorePersistenceException : Exception
{
    public StorePersistenceException(string message)
        : base(message)
    {
    }

    public StorePersistenceException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
