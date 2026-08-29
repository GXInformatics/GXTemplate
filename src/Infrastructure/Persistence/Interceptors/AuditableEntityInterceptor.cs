using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using CleanArchitecture.Blazor.Domain.Common.Entities;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;

namespace CleanArchitecture.Blazor.Infrastructure.Persistence.Interceptors;
#nullable disable warnings

/// <summary>
/// Interceptor for auditing entity changes.
/// <para>
/// <b>Audit rows are written in the same transaction as the business change.</b> Store-generated keys
/// are still temporary while <c>SavingChanges</c> runs, so the audit rows cannot simply be added to
/// the same <c>SaveChanges</c>: they would record negative sentinel keys. Instead this interceptor
/// opens a transaction in <c>SavingChanges</c>, lets the business save run inside it, resolves the
/// now-real keys in <c>SavedChanges</c>, writes the audit rows with a second save on the same
/// context, and commits. Either both are durable or neither is.
/// </para>
/// <para>
/// <b>An audit failure rolls the business change back and surfaces to the caller.</b> That is the
/// deliberate trade for a trail that is meant to be complete: an audit row that cannot be written is
/// treated as a failed operation, not as a logged inconvenience.
/// </para>
/// <para>
/// <b>Ordering.</b> This interceptor holds an open transaction across the save, so any other
/// interceptor that calls <c>BeginTransaction</c> on the same connection would throw. It is
/// registered first, and <see cref="DispatchDomainEventsInterceptor"/> deliberately opens no
/// transaction of its own - see the registration comment in <c>Infrastructure.DependencyInjection</c>.
/// </para>
/// <para>
/// <b>State is per DbContext, not per interceptor.</b> The interceptor is registered scoped and is
/// therefore shared by every context created within a scope; keying the pending trails to the
/// context instance is what stops two interleaved saves clobbering one another.
/// </para>
/// </summary>
public class AuditableEntityInterceptor : SaveChangesInterceptor
{
    private readonly IUserContextAccessor _userContextAccessor;
    private readonly IDateTime _dateTime;

    /// <summary>Per-save state, keyed to the context instance so concurrent saves cannot collide.</summary>
    private sealed class SaveState
    {
        public List<AuditTrail> PendingTrails { get; set; } = new();

        /// <summary>Non-null only when this interceptor opened the transaction and must commit it.</summary>
        public IDbContextTransaction OwnedTransaction { get; set; }

        /// <summary>
        /// Set while the audit rows themselves are being saved. Without it, the audit save re-enters
        /// these hooks and recurses until the stack overflows.
        /// </summary>
        public bool IsWritingAuditRows { get; set; }
    }

    private readonly ConditionalWeakTable<DbContext, SaveState> _states = new();

    private static readonly HashSet<string> _auditableMetadataFields = new(StringComparer.OrdinalIgnoreCase)
    {
        nameof(IAuditableEntity.CreatedAt),
        nameof(IAuditableEntity.CreatedById),
        nameof(IAuditableEntity.LastModifiedAt),
        nameof(IAuditableEntity.LastModifiedById)
    };

    /// <summary>
    /// Initializes a new instance of the <see cref="AuditableEntityInterceptor"/> class.
    /// </summary>
    /// <param name="userContextAccessor">The current user context accessor (scoped).</param>
    /// <param name="dateTime">The date and time service.</param>
    public AuditableEntityInterceptor(IUserContextAccessor userContextAccessor, IDateTime dateTime)
    {
        _userContextAccessor = userContextAccessor;
        _dateTime = dateTime;
    }

    // ---- async hooks ---------------------------------------------------------------------------

    /// <inheritdoc/>
    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        var context = eventData.Context;
        if (context == null) return await base.SavingChangesAsync(eventData, result, cancellationToken);

        var state = _states.GetOrCreateValue(context);
        if (state.IsWritingAuditRows) return await base.SavingChangesAsync(eventData, result, cancellationToken);

        PrepareSave(context, state);
        if (state.PendingTrails.Count > 0 && context.Database.CurrentTransaction is null)
        {
            state.OwnedTransaction = await context.Database.BeginTransactionAsync(cancellationToken);
        }

        return await base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    /// <inheritdoc/>
    public override async ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        var context = eventData.Context;
        var saveResult = await base.SavedChangesAsync(eventData, result, cancellationToken);
        if (context == null) return saveResult;

        var state = _states.GetOrCreateValue(context);
        if (state.IsWritingAuditRows) return saveResult;

        try
        {
            var trails = ResolvePendingTrails(state);
            if (trails.Count > 0)
            {
                context.Set<AuditTrail>().AddRange(trails);
                state.IsWritingAuditRows = true;
                using (AuditWriteScope.Enter(context))
                {
                    try
                    {
                        await context.SaveChangesAsync(cancellationToken);
                    }
                    finally
                    {
                        state.IsWritingAuditRows = false;
                    }
                }
            }

            if (state.OwnedTransaction is not null)
            {
                await state.OwnedTransaction.CommitAsync(cancellationToken);
            }
        }
        catch
        {
            // The audit rows are part of the operation: if they cannot be written, the business
            // change goes back with them and the caller is told.
            if (state.OwnedTransaction is not null)
            {
                await state.OwnedTransaction.RollbackAsync(cancellationToken);
            }
            throw;
        }
        finally
        {
            await DisposeStateAsync(context, state);
        }

        return saveResult;
    }

    /// <inheritdoc/>
    public override async Task SaveChangesFailedAsync(
        DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        await base.SaveChangesFailedAsync(eventData, cancellationToken);

        var context = eventData.Context;
        if (context == null) return;

        var state = _states.GetOrCreateValue(context);
        if (state.IsWritingAuditRows) return;

        if (state.OwnedTransaction is not null)
        {
            await state.OwnedTransaction.RollbackAsync(cancellationToken);
        }
        await DisposeStateAsync(context, state);
    }

    // ---- synchronous hooks ---------------------------------------------------------------------
    // A synchronous SaveChanges() must produce audit rows too; overriding only the async hooks left
    // that path silently unaudited.

    /// <inheritdoc/>
    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData, InterceptionResult<int> result)
    {
        var context = eventData.Context;
        if (context == null) return base.SavingChanges(eventData, result);

        var state = _states.GetOrCreateValue(context);
        if (state.IsWritingAuditRows) return base.SavingChanges(eventData, result);

        PrepareSave(context, state);
        if (state.PendingTrails.Count > 0 && context.Database.CurrentTransaction is null)
        {
            state.OwnedTransaction = context.Database.BeginTransaction();
        }

        return base.SavingChanges(eventData, result);
    }

    /// <inheritdoc/>
    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        var context = eventData.Context;
        var saveResult = base.SavedChanges(eventData, result);
        if (context == null) return saveResult;

        var state = _states.GetOrCreateValue(context);
        if (state.IsWritingAuditRows) return saveResult;

        try
        {
            var trails = ResolvePendingTrails(state);
            if (trails.Count > 0)
            {
                context.Set<AuditTrail>().AddRange(trails);
                state.IsWritingAuditRows = true;
                using (AuditWriteScope.Enter(context))
                {
                    try
                    {
                        context.SaveChanges();
                    }
                    finally
                    {
                        state.IsWritingAuditRows = false;
                    }
                }
            }

            state.OwnedTransaction?.Commit();
        }
        catch
        {
            state.OwnedTransaction?.Rollback();
            throw;
        }
        finally
        {
            DisposeState(context, state);
        }

        return saveResult;
    }

    /// <inheritdoc/>
    public override void SaveChangesFailed(DbContextErrorEventData eventData)
    {
        base.SaveChangesFailed(eventData);

        var context = eventData.Context;
        if (context == null) return;

        var state = _states.GetOrCreateValue(context);
        if (state.IsWritingAuditRows) return;

        state.OwnedTransaction?.Rollback();
        DisposeState(context, state);
    }

    // ---- shared logic --------------------------------------------------------------------------

    private void PrepareSave(DbContext context, SaveState state)
    {
        UpdateAuditableEntities(context);
        state.PendingTrails = GenerateAuditTrails(context);
    }

    private static List<AuditTrail> ResolvePendingTrails(SaveState state)
    {
        if (state.PendingTrails.Count == 0) return new List<AuditTrail>();

        var resolved = ResolveAuditTrails(state.PendingTrails).Where(HasChanges).ToList();
        state.PendingTrails = new List<AuditTrail>();
        return resolved;
    }

    private async Task DisposeStateAsync(DbContext context, SaveState state)
    {
        if (state.OwnedTransaction is not null)
        {
            await state.OwnedTransaction.DisposeAsync();
            state.OwnedTransaction = null;
        }
        state.PendingTrails = new List<AuditTrail>();
        _states.Remove(context);
    }

    private void DisposeState(DbContext context, SaveState state)
    {
        state.OwnedTransaction?.Dispose();
        state.OwnedTransaction = null;
        state.PendingTrails = new List<AuditTrail>();
        _states.Remove(context);
    }

    private void UpdateAuditableEntities(DbContext context)
    {
        var currentUser = _userContextAccessor.Current;
        var userId = currentUser?.UserId;
        var tenantId = currentUser?.TenantId;
        var now = _dateTime.UtcNow;

        foreach (var entry in context.ChangeTracker.Entries<IAuditableEntity>())
        {
            switch (entry.State)
            {
                case EntityState.Added:
                    SetCreationAuditInfo(entry.Entity, userId, tenantId, now);
                    break;

                case EntityState.Modified:
                    SetModificationAuditInfo(entry.Entity, userId, now);
                    break;

                case EntityState.Deleted:
                    SetDeletionAuditInfo(entry, userId, now);
                    break;

                case EntityState.Unchanged when entry.HasChangedOwnedEntities():
                    SetModificationAuditInfo(entry.Entity, userId, now);
                    break;
            }
        }
    }

    private static void SetCreationAuditInfo(IAuditableEntity entity, string userId, string tenantId, DateTime now)
    {
        entity.CreatedById = userId;
        entity.CreatedAt = now;
        if (entity is IMustHaveTenant mustTenant && mustTenant.TenantId==null) mustTenant.TenantId = tenantId;
        if (entity is IMayHaveTenant mayTenant && mayTenant.TenantId==null) mayTenant.TenantId = tenantId;
    }

    private static void SetModificationAuditInfo(IAuditableEntity entity, string userId, DateTime now)
    {
        entity.LastModifiedById = userId;
        entity.LastModifiedAt = now;
    }

    private static void SetDeletionAuditInfo(EntityEntry entry, string userId, DateTime now)
    {
        if (entry.Entity is ISoftDelete softDelete)
        {
            softDelete.DeletedById = userId;
            softDelete.DeletedAt = now;
            entry.State = EntityState.Modified;
        }
    }

    private List<AuditTrail> GenerateAuditTrails(DbContext context)
    {
        var currentUser = _userContextAccessor.Current;
        var userId = currentUser?.UserId;
        var now = _dateTime.UtcNow;
        var auditTrails = new List<AuditTrail>();

        foreach (var entry in context.ChangeTracker.Entries<IAuditable>())
        {
            if (IsValidAuditEntry(entry))
            {
                var auditTrail = CreateAuditTrail(entry, userId, now);
                auditTrails.Add(auditTrail);
            }
        }

        return auditTrails;
    }

    private static bool IsValidAuditEntry(EntityEntry entry)
    {
        return entry.Entity is not AuditTrail && entry.State != EntityState.Detached && entry.State != EntityState.Unchanged;
    }

    private AuditTrail CreateAuditTrail(EntityEntry entry, string userId, DateTime now)
    {
        var auditTrail = new AuditTrail
        {
            TableName = entry.Entity.GetType().Name,
            UserId = userId,
            DateTime = now,
            AffectedColumns = new List<string>(),
            Changes = new Dictionary<string, AuditChange>()
        };

        // Set a default audit type based on entry state; property loop can refine details.
        auditTrail.AuditType = entry.State switch
        {
            EntityState.Added => AuditType.Create,
            EntityState.Deleted => AuditType.Delete,
            EntityState.Modified => AuditType.Update,
            _ => auditTrail.AuditType
        };

        foreach (var property in entry.Properties)
        {
            if (property.IsTemporary)
            {
                auditTrail.TemporaryProperties.Add(property);
                continue;
            }

            var propertyName = property.Metadata.Name;
            if (entry.State == EntityState.Modified && _auditableMetadataFields.Contains(propertyName))
            {
                continue;
            }
            if (property.Metadata.IsPrimaryKey() && property.CurrentValue != null)
            {
                auditTrail.PrimaryKey[propertyName] = SerializeValue(property.CurrentValue);
                continue;
            }

            switch (entry.State)
            {
                case EntityState.Added:
                    if (property.CurrentValue != null)
                        auditTrail.Changes[propertyName] = new AuditChange { New = SerializeValue(property.CurrentValue) };
                    break;

                case EntityState.Deleted:
                    if (property.OriginalValue != null)
                        auditTrail.Changes[propertyName] = new AuditChange { Old = SerializeValue(property.OriginalValue) };
                    break;

                case EntityState.Modified when property.IsModified && !Equals(property.OriginalValue, property.CurrentValue):
                    auditTrail.AffectedColumns.Add(propertyName);
                    auditTrail.Changes[propertyName] = new AuditChange
                    {
                        Old = SerializeValue(property.OriginalValue),
                        New = SerializeValue(property.CurrentValue)
                    };
                    break;
            }
        }

        return auditTrail;
    }

    private static string? SerializeValue(object value)
    {
        if (value is null) return null;

        var type = value.GetType();
        if (type.IsPrimitive || value is string || value is decimal || value is DateTime || value is DateTimeOffset || value is Guid || value is TimeSpan || value is Enum)
        {
            return Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        try
        {
            return JsonSerializer.Serialize(value, JsonSerializerOptions.Web);
        }
        catch (NotSupportedException)
        {
            // Fallback to readable string when JSON serialization cannot handle the object.
            return value.ToString();
        }
    }

    private static List<AuditTrail> ResolveAuditTrails(IEnumerable<AuditTrail> auditTrails)
    {
        foreach (var auditTrail in auditTrails)
        {
            foreach (var prop in auditTrail.TemporaryProperties)
            {
                if (prop.Metadata.IsPrimaryKey() && prop.CurrentValue != null)
                {
                    auditTrail.PrimaryKey[prop.Metadata.Name] = SerializeValue(prop.CurrentValue);
                }
                else if (prop.CurrentValue != null)
                {
                    auditTrail.Changes ??= new Dictionary<string, AuditChange>();
                    auditTrail.Changes[prop.Metadata.Name] = new AuditChange { New = SerializeValue(prop.CurrentValue) };
                }
            }
        }

        // project to new instances without PropertyEntry references so nothing keeps a change-tracker
        // entry alive once the rows are handed to the context
        return auditTrails.Select(a => new AuditTrail
        {
            TableName = a.TableName,
            UserId = a.UserId,
            DateTime = a.DateTime,
            AffectedColumns = a.AffectedColumns?.ToList(),
            Changes = a.Changes?.ToDictionary(kv => kv.Key, kv => new AuditChange { Old = kv.Value.Old, New = kv.Value.New }),
            PrimaryKey = new Dictionary<string, string>(a.PrimaryKey),
            AuditType = a.AuditType
        }).ToList();
    }

    private static bool HasChanges(AuditTrail auditTrail)
    {
        return auditTrail.Changes != null && auditTrail.Changes.Any();
    }
}

public static class Extensions
{
    /// <summary>
    /// Checks if the entity entry has any owned entities that have been added or modified.
    /// </summary>
    /// <param name="entry">The entity entry.</param>
    /// <returns><c>true</c> if the entity entry has changed owned entities; otherwise, <c>false</c>.</returns>
    public static bool HasChangedOwnedEntities(this EntityEntry entry)
    {
        return entry.References.Any(r =>
            r.TargetEntry != null &&
            r.TargetEntry.Metadata.IsOwned() &&
            (r.TargetEntry.State == EntityState.Added || r.TargetEntry.State == EntityState.Modified));
    }
}
