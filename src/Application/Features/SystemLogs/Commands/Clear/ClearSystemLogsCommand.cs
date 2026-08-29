// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.


using CleanArchitecture.Blazor.Application.Features.SystemLogs.Caching;

namespace CleanArchitecture.Blazor.Application.Features.SystemLogs.Commands.Clear;

[RequestAuthorize(Policy = Permissions.Logs.Purge)]
public class ClearSystemLogsCommand : ICacheInvalidatorRequest<Result>
{
    public string CacheKey => SystemLogsCacheKey.GetAllCacheKey;
    public IEnumerable<string>? Tags => SystemLogsCacheKey.Tags;
}

public class ClearSystemLogsCommandHandler : IRequestHandler<ClearSystemLogsCommand, Result>

{
    private readonly ILogDbContextFactory _dbContextFactory;

    public ClearSystemLogsCommandHandler(
        ILogDbContextFactory dbContextFactory
    )
    {
        _dbContextFactory = dbContextFactory;
    }

    public async ValueTask<Result> Handle(ClearSystemLogsCommand request, CancellationToken cancellationToken)
    {
        await using var db = await _dbContextFactory.CreateAsync(cancellationToken);

        // PurgeAsync rather than ExecuteDeleteAsync on a DbSet: ILogDbContext exposes an IQueryable,
        // so erasing the log is a capability the interface grants by name rather than a side effect
        // of having handed out something writable.
        await db.PurgeAsync(cancellationToken);
        return await Result.SuccessAsync();
    }
}
