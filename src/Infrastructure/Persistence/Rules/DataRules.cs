using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using CleanArchitecture.Blazor.Application.Common.Approvals;
using CleanArchitecture.Blazor.Application.Common.Branches;
using CleanArchitecture.Blazor.Application.Common.Concurrency;
using CleanArchitecture.Blazor.Application.Common.Numerics;
using CleanArchitecture.Blazor.Domain.Common.Entities;
using CleanArchitecture.Blazor.Domain.Common.Precision;
using CleanArchitecture.Blazor.Domain.Identity;
using CleanArchitecture.Blazor.Infrastructure.Persistence.Transactions;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace CleanArchitecture.Blazor.Infrastructure.Persistence.Rules;

/// <summary>
/// A save refused by one of the data rules (Pass 59). It is a programming or business-rule error, never a
/// transient one: it is not retried.
/// </summary>
public sealed class DataRuleViolationException(string message) : InvalidOperationException(message);

/// <summary>
/// The save-time rules of Pass 59, run by <see cref="ApplicationDbContext"/>'s <c>SaveChanges</c> override
/// BEFORE any save interceptor.
/// </summary>
/// <remarks>
/// <b>Why an override and not a third interceptor.</b> <c>InterceptorOrderingTests</c> pins exactly two
/// <c>ISaveChangesInterceptor</c> registrations, and running first means the rules see the entry states the
/// handler produced: no soft-delete conversion yet, no audit transaction opened. Identity's
/// <c>UserStore</c> saves through the same override.
/// <para>The rules, in order:</para>
/// <list type="number">
/// <item><description>Users are deactivated, never deleted.</description></item>
/// <item><description><see cref="IAppendOnly"/> rows are never Modified or Deleted.</description></item>
/// <item><description>A posted <see cref="IPostable"/> changes only in allowed properties and is never deleted;
/// its lines are never added or deleted and change only in allowed properties; a line change touches the
/// header so the header's version check covers it. A header posted in the current unit of work is exempt,
/// with its lines (B3), until commit.</description></item>
/// <item><description>A Modified or Deleted <see cref="IVersioned"/> entity had its version applied.</description></item>
/// <item><description>Branch-scoped rows are written only under a branch grant that covers them.</description></item>
/// <item><description>No decimal has more places than its kind.</description></item>
/// </list>
/// </remarks>
internal static class DataRules
{
    private static readonly HashSet<string> AuditFields = new(StringComparer.Ordinal)
    {
        nameof(IAuditableEntity.LastModifiedAt),
        nameof(IAuditableEntity.LastModifiedById),
        nameof(IVersioned.Version)
    };

    /// <summary>Headers posted in the current unit of work, per context (the exemption in rule 3).</summary>
    private static readonly ConditionalWeakTable<DbContext, HashSet<object>> PostedHere = new();

    private static readonly ConcurrentDictionary<Type, HashSet<string>> AllowedAfterPosting = new();
    private static readonly ConcurrentDictionary<Type, PropertyInfo?> LineHeader = new();
    private static readonly ConcurrentDictionary<Type, PropertyInfo?> InvolvedBranches = new();
    private static readonly ConcurrentDictionary<PropertyInfo, DecimalKindAttribute?> Kinds = new();

    public static void BeforeSave(ApplicationDbContext context, IUserContextAccessor? userContextAccessor)
    {
        var tracker = context.ChangeTracker;
        if (tracker.AutoDetectChangesEnabled) tracker.DetectChanges();

        var entries = tracker.Entries().Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted).ToList();
        if (entries.Count == 0) return;

        var inUnitOfWork = UnitOfWorkScope.Find(context) is not null;
        var postedHere = inUnitOfWork ? PostedHere.GetOrCreateValue(context) : null;

        foreach (var entry in entries) CheckUser(entry);
        foreach (var entry in entries) CheckAppendOnly(entry);
        foreach (var entry in entries) CheckPostedHeader(entry, postedHere);
        foreach (var entry in entries) CheckPostedLine(context, entry, postedHere);

        // After the line rule, which may have touched (Modified) a header.
        foreach (var entry in tracker.Entries().Where(e => e.State is EntityState.Modified or EntityState.Deleted))
            CheckVersion(entry);

        var access = userContextAccessor?.Current?.Branches;
        if (access?.IsSystem != true)
        {
            foreach (var entry in entries) CheckBranch(entry);
        }

        foreach (var entry in entries) CheckPrecision(entry);
        foreach (var entry in entries) CheckApproval(entry);
        foreach (var entry in entries) RecordUserAccessChange(context, entry);

        if (postedHere is not null) RecordPostedHere(entries, postedHere);
    }

    public static void AfterSave(ApplicationDbContext context)
    {
        SavedVersions.Refresh(context);
        InvalidateChangedUsers(context);
    }

    // ---- 8. User access (Pass 60, Part D item 4) ------------------------------------------------

    /// <summary>Users whose IsActive or LockoutEnd changed in this context, to invalidate after the save.</summary>
    private static readonly ConditionalWeakTable<DbContext, HashSet<string>> AccessChanged = new();

    /// <summary>
    /// A user who LOSES access in this save - deactivated, or locked out by an administrator
    /// (<see cref="UserAccess"/>) - gets a new security stamp in the same save, so every cookie and circuit holding
    /// the old one fails revalidation. Any change to IsActive or LockoutEnd (reactivation included) marks the user's
    /// cached context for invalidation after the save. Every path saves through here - both deactivation paths,
    /// the user dialog, Identity's own lockout - so none can forget. An automatic lockout (time-limited) does not
    /// revoke access (decision 15) and gets no new stamp. The system account is exempt.
    /// </summary>
    private static void RecordUserAccessChange(DbContext context, EntityEntry entry)
    {
        if (entry.State != EntityState.Modified || entry.Entity is not ApplicationUser user) return;
        if (string.Equals(user.UserName, CleanArchitecture.Blazor.Application.Common.Constants.Users.System, StringComparison.Ordinal)) return;

        var active = entry.Property(nameof(ApplicationUser.IsActive));
        var lockout = entry.Property(nameof(ApplicationUser.LockoutEnd));
        if (Equals(active.OriginalValue, active.CurrentValue) && Equals(lockout.OriginalValue, lockout.CurrentValue)) return;

        var had = UserAccess.HasAccess((bool)active.OriginalValue!, (DateTimeOffset?)lockout.OriginalValue);
        if (had && !UserAccess.HasAccess(user)) user.SecurityStamp = Guid.NewGuid().ToString("N");

        AccessChanged.GetOrCreateValue(context).Add(user.Id);
    }

    private static void InvalidateChangedUsers(ApplicationDbContext context)
    {
        if (!AccessChanged.TryGetValue(context, out var changed) || changed.Count == 0) return;
        var userIds = changed.ToList();
        changed.Clear();

        // The application's services, which the options carry; absent for a directly constructed context, which has
        // no user-context cache to clear.
        var services = context.GetService<IDbContextOptions>().FindExtension<CoreOptionsExtension>()?.ApplicationServiceProvider;
        var invalidator = services?.GetService<IUserContextInvalidator>();
        foreach (var userId in userIds) invalidator?.Invalidate(userId);
    }

    // ---- 1. Users -----------------------------------------------------------------------------

    private static void CheckUser(EntityEntry entry)
    {
        if (entry.State == EntityState.Deleted && entry.Entity is ApplicationUser user)
            throw new DataRuleViolationException(
                $"User '{user.UserName}' cannot be deleted: users are deactivated, never deleted (pass 59), so the " +
                "audit trail's reference to who did something can never become null.");
    }

    // ---- 2. Append-only -----------------------------------------------------------------------

    private static void CheckAppendOnly(EntityEntry entry)
    {
        if (entry.Entity is not IAppendOnly) return;
        if (entry.State is EntityState.Modified or EntityState.Deleted)
            throw new DataRuleViolationException(
                $"{Describe(entry)} is append-only and cannot be {(entry.State == EntityState.Deleted ? "deleted" : "modified")}. " +
                "Post a reversing entry instead.");
    }

    // ---- 3. Posted documents ------------------------------------------------------------------

    private static bool IsPosted(EntityEntry header) =>
        header.State != EntityState.Added
        && header.Property(nameof(IPostable.PostedAt)).OriginalValue is not null;

    private static void CheckPostedHeader(EntityEntry entry, HashSet<object>? postedHere)
    {
        if (entry.Entity is not IPostable || !IsPosted(entry)) return;
        if (postedHere?.Contains(entry.Entity) == true) return;

        if (entry.State == EntityState.Deleted)
            throw new DataRuleViolationException($"{Describe(entry)} is posted and cannot be deleted. Post a reversal instead.");

        var changed = ChangedDisallowed(entry);
        if (changed.Count > 0)
            throw new DataRuleViolationException(
                $"{Describe(entry)} is posted; only properties marked [AllowedAfterPosting] may change. Changed: {string.Join(", ", changed)}.");
    }

    private static void CheckPostedLine(DbContext context, EntityEntry entry, HashSet<object>? postedHere)
    {
        var headerProperty = LineHeader.GetOrAdd(entry.Metadata.ClrType, HeaderPropertyOf);
        if (headerProperty is null) return;

        var header = headerProperty.GetValue(entry.Entity);
        if (header is null)
            throw new DataRuleViolationException(
                $"{Describe(entry)} is a line of a postable document, but its header is not loaded. Load the header " +
                "(with the version the user loaded) to change its lines: a line change also updates the header, so that " +
                "it cannot slip into a document being posted at the same moment.");

        var headerEntry = context.Entry(header);
        if (headerEntry.State == EntityState.Detached)
            throw new DataRuleViolationException($"{Describe(entry)}: its header is not tracked by this context. Load it to change its lines.");

        if (IsPosted(headerEntry))
        {
            var exempt = postedHere?.Contains(header) == true;
            if (entry.State is EntityState.Added or EntityState.Deleted)
                throw new DataRuleViolationException(
                    $"{Describe(entry)} cannot be {(entry.State == EntityState.Added ? "added to" : "deleted from")} " +
                    $"{Describe(headerEntry)}, which is posted.");

            if (!exempt)
            {
                var changed = ChangedDisallowed(entry);
                if (changed.Count > 0)
                    throw new DataRuleViolationException(
                        $"{Describe(entry)} belongs to posted {Describe(headerEntry)}; only line properties marked " +
                        $"[AllowedAfterPosting] may change. Changed: {string.Join(", ", changed)}.");
            }
        }

        // The touch: the header is saved with the line, so its version check serialises the line edit
        // against a concurrent post of the same document.
        if (headerEntry.State == EntityState.Unchanged) headerEntry.State = EntityState.Modified;
    }

    private static PropertyInfo? HeaderPropertyOf(Type clr)
    {
        var line = clr.GetInterfaces().FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IPostableLine<>));
        return line?.GetProperty(nameof(IPostableLine<IPostableStub>.Header));
    }

    private sealed class IPostableStub : IPostable
    {
        public DateTime? PostedAt => null;
    }

    private static List<string> ChangedDisallowed(EntityEntry entry)
    {
        var allowed = AllowedAfterPosting.GetOrAdd(entry.Metadata.ClrType, AllowedPropertiesOf);
        return entry.Properties
            .Where(p => p.IsModified && !allowed.Contains(p.Metadata.Name) && !Equals(p.OriginalValue, p.CurrentValue))
            .Select(p => p.Metadata.Name)
            .ToList();
    }

    internal static HashSet<string> AllowedPropertiesOf(Type clr)
    {
        var allowed = new HashSet<string>(AuditFields, StringComparer.Ordinal);
        foreach (var property in clr.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (property.GetCustomAttribute<AllowedAfterPostingAttribute>(inherit: true) is not null) allowed.Add(property.Name);
        }
        return allowed;
    }

    private static void RecordPostedHere(IEnumerable<EntityEntry> entries, HashSet<object> postedHere)
    {
        foreach (var entry in entries)
        {
            if (entry.Entity is not IPostable postable || postable.PostedAt is null) continue;
            var wasPosted = entry.State != EntityState.Added
                            && entry.Property(nameof(IPostable.PostedAt)).OriginalValue is not null;
            if (!wasPosted) postedHere.Add(entry.Entity);
        }
    }

    // ---- 4. Versions --------------------------------------------------------------------------

    private static void CheckVersion(EntityEntry entry)
    {
        // (P59, owner decision) Inside Mediator requests only - see VersionChecks.
        if (!VersionChecks.InRequest) return;
        if (entry.Entity is not IVersioned || entry.Metadata.FindProperty(nameof(IVersioned.Version)) is null) return;
        if (VersionChecks.IsMarked(entry.Entity)) return;

        throw new DataRuleViolationException(
            $"{Describe(entry)} was {(entry.State == EntityState.Deleted ? "deleted" : "modified")} without the version the user loaded. " +
            "Call db.ApplyLoadedVersion(entity, request.Version), or db.UseCurrentVersion(entity) for a system update made under a row lock.");
    }

    // ---- 5. Branches --------------------------------------------------------------------------

    private static void CheckBranch(EntityEntry entry)
    {
        if (entry.Entity is IMustHaveBranch scoped)
        {
            var grant = BranchGrant.Current ?? throw NoGrant(entry);
            var branchProperty = entry.Property(nameof(IMustHaveBranch.BranchId));
            if (entry.State == EntityState.Modified && !Equals(branchProperty.OriginalValue, branchProperty.CurrentValue))
                throw new DataRuleViolationException(
                    $"{Describe(entry)}: a row is never moved to another branch (BranchId {branchProperty.OriginalValue} → " +
                    $"{branchProperty.CurrentValue}). Record a transfer instead.");

            var branchId = entry.State == EntityState.Deleted ? (int)branchProperty.OriginalValue! : scoped.BranchId;
            if (grant.Count == 0 && BranchGrant.IsAwaitingDefinedBranch)
                throw new DataRuleViolationException(
                    $"{Describe(entry)} belongs to branch {branchId}, but this command defines a branch and has not saved it yet, so no " +
                    "branch is granted yet. Save the branch first; its posting-event handlers may then write its rows (pass 60b).");
            if (!grant.Contains(branchId))
                throw new DataRuleViolationException(
                    $"{Describe(entry)} belongs to branch {branchId}, but this command was granted branch(es) {string.Join(", ", grant)}.");
            return;
        }

        var involvedProperty = InvolvedBranches.GetOrAdd(entry.Metadata.ClrType, InvolvedBranchesPropertyOf);
        if (involvedProperty is null) return;

        var current = BranchGrant.Current ?? throw NoGrant(entry);
        var involved = ((IEnumerable<int>)involvedProperty.GetValue(entry.Entity)!).ToList();
        if (!involved.Any(current.Contains))
            throw new DataRuleViolationException(
                $"{Describe(entry)} involves branch(es) {string.Join(", ", involved)}, none of which this command was granted " +
                $"({string.Join(", ", current)}).");

        // The grant extension (owner decision 9): rows derived from this document, for its other branches,
        // may now be written for the rest of this command.
        BranchGrant.Extend(involved);
    }

    private static DataRuleViolationException NoGrant(EntityEntry entry) =>
        new($"{Describe(entry)} is branch-scoped and was saved outside a branch command. Implement IBranchCommand on the " +
            "request, so that BranchAccessBehaviour checks the branch before the handler runs.");

    private static PropertyInfo? InvolvedBranchesPropertyOf(Type clr)
    {
        var visibility = clr.GetInterfaces().FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IBranchVisibility<>));
        return visibility?.GetProperty("InvolvedBranchIds");
    }

    // ---- 6. Precision -------------------------------------------------------------------------

    private static void CheckPrecision(EntityEntry entry)
    {
        if (entry.State == EntityState.Deleted) return;
        foreach (var property in entry.Properties)
        {
            if (property.CurrentValue is not decimal value) continue;
            if (entry.State == EntityState.Modified && !property.IsModified) continue;
            var info = property.Metadata.PropertyInfo;
            if (info is null) continue;
            var kind = Kinds.GetOrAdd(info, DataModelConventions.KindOf);
            if (kind is null) continue;

            if (GxRound.SignificantScale(value) > kind.Scale)
                throw new DataRuleViolationException(
                    $"{Describe(entry)}.{property.Metadata.Name} is {value}, which has more than the {kind.Scale} decimal places a " +
                    $"{kind.KindName} value holds. Round it when you compute it (GxRound); the database would otherwise round it silently.");
        }
    }

    // ---- 7. Approvals (Pass 60) ------------------------------------------------------------------

    /// <summary>
    /// A change to <c>ApprovedById</c> or <c>ApprovedAt</c> on an <see cref="IApprovable"/> needs an
    /// <see cref="ApprovalGrant"/> for that header - installed only by <c>ApprovalBehaviour</c> after the approval
    /// policy passed - and <c>ApprovedById</c> must be the approver it was granted to. The loud failure for a command
    /// that approves without being an approval command; it also refuses clearing an approval outside one.
    /// </summary>
    private static void CheckApproval(EntityEntry entry)
    {
        if (entry.Entity is not IApprovable approvable || entry.State == EntityState.Deleted) return;

        var approver = entry.Property(nameof(IApprovable.ApprovedById));
        var approvedAt = entry.Property(nameof(IApprovable.ApprovedAt));
        var changed = entry.State == EntityState.Added
            ? approver.CurrentValue is not null || approvedAt.CurrentValue is not null
            : !Equals(approver.OriginalValue, approver.CurrentValue) || !Equals(approvedAt.OriginalValue, approvedAt.CurrentValue);
        if (!changed) return;

        var granted = ApprovalGrant.ApproverOf(approvable.Id);
        if (granted is null)
            throw new DataRuleViolationException(
                $"{Describe(entry)} was approved outside an approval command. Implement IApprovalCommand<{entry.Metadata.ClrType.Name}> " +
                "on the request, so that ApprovalBehaviour checks the approver, the preparer and the limit first.");

        if (approver.CurrentValue is { } approvedBy && !string.Equals((string)approvedBy, granted, StringComparison.Ordinal))
            throw new DataRuleViolationException(
                $"{Describe(entry)}: ApprovedById is '{approvedBy}', but the approval was granted to '{granted}'. Record the approver the policy checked.");
    }

    private static string Describe(EntityEntry entry)
    {
        var key = entry.Metadata.FindPrimaryKey();
        var keyText = key is null
            ? string.Empty
            : " " + string.Join("/", key.Properties.Select(p => entry.Property(p.Name).CurrentValue));
        return entry.Metadata.ClrType.Name + keyText;
    }
}
