// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace CleanArchitecture.Blazor.Application.Features.SecuritySettings.Queries;

/// <summary>
/// The administered idle policy, together with the bounds the screen must hold the administrator to.
/// </summary>
/// <remarks>
/// The bounds travel with the policy deliberately. The screen has to show them ("Between 1 and 120
/// minutes") and enforce them, and a screen that fetched them separately - or hard-coded them -
/// would be a second place for the deployment's limits to live.
/// </remarks>
public class SecurityPolicyDto
{
    public int IdleTimeoutMinutes { get; set; }
    public int CountdownSeconds { get; set; }

    /// <summary>False when the deployment has switched the feature off entirely.</summary>
    public bool Enabled { get; set; }

    public int MinIdleTimeoutMinutes { get; set; }
    public int MaxIdleTimeoutMinutes { get; set; }

    /// <summary>Whether users may shorten their own window; drives the profile screen's visibility.</summary>
    public bool AllowUserOverride { get; set; }
}

[RequestAuthorize(Policy = Permissions.SecuritySettings.View)]
public class GetSecurityPolicyQuery : IRequest<Result<SecurityPolicyDto>>;

public class GetSecurityPolicyQueryHandler : IRequestHandler<GetSecurityPolicyQuery, Result<SecurityPolicyDto>>
{
    private readonly IIdleTimeoutPolicyProvider _provider;
    private readonly IIdleTimeoutSettings _settings;

    public GetSecurityPolicyQueryHandler(
        IIdleTimeoutPolicyProvider provider, IIdleTimeoutSettings settings)
    {
        _provider = provider;
        _settings = settings;
    }

    public async ValueTask<Result<SecurityPolicyDto>> Handle(
        GetSecurityPolicyQuery request, CancellationToken cancellationToken)
    {
        // Through the provider rather than the table: it is the thing that seeds the first row and
        // clamps a stored value into the current bounds, so the screen shows what enforcement will
        // actually use rather than what happens to be persisted.
        var administered = await _provider.GetAdministeredAsync(cancellationToken);

        return await Result<SecurityPolicyDto>.SuccessAsync(new SecurityPolicyDto
        {
            IdleTimeoutMinutes = administered.IdleMinutes,
            CountdownSeconds = administered.CountdownSeconds,
            Enabled = _settings.Enabled,
            MinIdleTimeoutMinutes = _settings.MinIdleTimeoutMinutes,
            MaxIdleTimeoutMinutes = _settings.MaxIdleTimeoutMinutes,
            AllowUserOverride = _settings.AllowUserOverride
        });
    }
}
