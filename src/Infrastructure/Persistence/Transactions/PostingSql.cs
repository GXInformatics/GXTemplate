using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;

namespace CleanArchitecture.Blazor.Infrastructure.Persistence.Transactions;

/// <summary>
/// Shared plumbing for the Pass 58 raw-SQL services: the provider check, commands enlisted in the
/// unit of work's transaction, and table/column names read from the EF model rather than restated.
/// </summary>
internal static class PostingSql
{
    public const string NpgsqlProvider = "Npgsql.EntityFrameworkCore.PostgreSQL";

    public static DbContext AsDbContext(IApplicationDbContext db) =>
        db as DbContext ?? throw new InvalidOperationException($"{db.GetType().Name} is not an EF DbContext.");

    /// <summary>PostgreSQL only (owner decision, Pass 58).</summary>
    public static void RequirePostgreSql(DbContext context, string feature)
    {
        if (context.Database.ProviderName != NpgsqlProvider)
            throw new NotSupportedException(
                $"{feature} is implemented for PostgreSQL only; this context uses {context.Database.ProviderName}.");
    }

    /// <summary>A command on the context's connection, inside its current transaction (which must exist).</summary>
    public static async Task<DbCommand> CommandAsync(DbContext context, string sql, CancellationToken cancellationToken)
    {
        var transaction = context.Database.CurrentTransaction
                          ?? throw new InvalidOperationException("This statement must run inside the unit of work's transaction.");
        var connection = context.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open) await context.Database.OpenConnectionAsync(cancellationToken);

        var command = connection.CreateCommand();
        command.Transaction = transaction.GetDbTransaction();
        command.CommandText = sql;
        return command;
    }

    public static void AddParameter(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }

    /// <summary>The quoted, schema-qualified table and a column-name lookup for <typeparamref name="TEntity"/>.</summary>
    public static (string Table, Func<string, string> Column) Names<TEntity>(DbContext context)
    {
        var entityType = context.Model.FindEntityType(typeof(TEntity))
                         ?? throw new InvalidOperationException($"{typeof(TEntity).Name} is not in the model.");
        var tableName = entityType.GetTableName()!;
        var schema = entityType.GetSchema();
        var store = StoreObjectIdentifier.Table(tableName, schema);

        var table = schema is null ? Quote(tableName) : $"{Quote(schema)}.{Quote(tableName)}";
        return (table, property => Quote(entityType.FindProperty(property)!.GetColumnName(store)!));
    }

    private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";
}
