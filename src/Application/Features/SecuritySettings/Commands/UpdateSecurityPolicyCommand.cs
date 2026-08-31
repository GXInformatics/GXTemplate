// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace CleanArchitecture.Blazor.Application.Features.SecuritySettings.Commands;

/// <summary>
/// Saves the installation's idle policy.
/// </summary>
/// <remarks>
/// Its own permission rather than a general administration right: how long a session may sit
/// unattended is a security control, and the people who should hold it are not necessarily the
/// people who administer users or picklists.
/// </remarks>
[RequestAuthorize(Policy = Permissions.SecuritySettings.Edit)]
public class UpdateSecurityPolicyCommand : IRequest<Result<int>>
{
    [Description("Idle timeout (minutes)")] public int IdleTimeoutMinutes { get; set; }
    [Description("Countdown (seconds)")] public int CountdownSeconds { get; set; }
}

public class UpdateSecurityPolicyCommandHandler : IRequestHandler<UpdateSecurityPolicyCommand, Result<int>>
{
    private readonly IApplicationDbContextFactory _dbContextFactory;
    private readonly IIdleTimeoutPolicyProvider _provider;

    public UpdateSecurityPolicyCommandHandler(
        IApplicationDbContextFactory dbContextFactory, IIdleTimeoutPolicyProvider provider)
    {
        _dbContextFactory = dbContextFactory;
        _provider = provider;
    }

    public async ValueTask<Result<int>> Handle(
        UpdateSecurityPolicyCommand request, CancellationToken cancellationToken)
    {
        await using var db = await _dbContextFactory.CreateAsync(cancellationToken);

        var policy = await db.SecurityPolicies
            .OrderBy(p => p.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (policy is null)
        {
            // Only reachable if nothing has read the policy yet on a fresh database - the provider
            // seeds the row on first read. Creating it here keeps the save from depending on that
            // ordering.
            policy = new SecurityPolicy();
            db.SecurityPolicies.Add(policy);
        }

        policy.IdleTimeoutMinutes = request.IdleTimeoutMinutes;
        policy.CountdownSeconds = request.CountdownSeconds;

        // SecurityPolicy is IAuditable, so the before/after values land in AuditTrails inside this
        // same transaction - a policy change is a security event and is recorded as one.
        await db.SaveChangesAsync(cancellationToken);

        // Immediately, not on a TTL. The cached policy is read on every authenticated request by the
        // principal check; leaving a stale one in place would mean the change not reaching sessions
        // already open, which is the one behaviour putting this on a screen was meant to provide.
        _provider.Invalidate();

        return await Result<int>.SuccessAsync(policy.Id);
    }
}
