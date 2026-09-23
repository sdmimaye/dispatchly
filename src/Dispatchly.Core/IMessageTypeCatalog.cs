namespace Dispatchly;

/// <summary>Message types registered with Dispatchly.</summary>
public interface IMessageTypeCatalog
{
    /// <summary>Registered message types.</summary>
    IReadOnlyCollection<MessageTypeRegistration> Registrations { get; }

    /// <summary>Looks up a registration by CLR type.</summary>
    bool TryGet(Type messageType, out MessageTypeRegistration registration);

    /// <summary>Looks up a registration by PostgreSQL table name.</summary>
    bool TryGetByTable(string tableName, out MessageTypeRegistration registration);

    /// <summary>Returns the registration for <paramref name="messageType" />.</summary>
    MessageTypeRegistration GetRequired(Type messageType);
}
