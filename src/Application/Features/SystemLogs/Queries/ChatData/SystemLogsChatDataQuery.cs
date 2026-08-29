// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using CleanArchitecture.Blazor.Application.Features.SystemLogs.Caching;
using CleanArchitecture.Blazor.Application.Features.SystemLogs.DTOs;

namespace CleanArchitecture.Blazor.Application.Features.SystemLogs.Queries.ChatData;

[RequestAuthorize(Policy = Permissions.Logs.View)]
public class SystemLogsTimeLineChatDataQuery : ICacheableRequest<List<SystemLogTimeLineDto>>
{
    /// <summary>
    /// The start of the chart's window. UTC, not local: this value becomes a query PARAMETER
    /// (see the handler's Where clause), and under timestamptz Npgsql refuses to bind a
    /// Kind=Local or Kind=Unspecified DateTime to a "timestamp with time zone" column. Pass 14
    /// measured it: DateTime.Now here made the SystemLogs chart the second of two writes that
    /// broke the moment the legacy switch was removed.
    /// <para>
    /// It also feeds <see cref="CacheKey"/> through ToString(), so the key text changed with it.
    /// That is harmless - the key only has to be stable within a run and distinct per window.
    /// </para>
    /// </summary>
    public DateTime LastDateTime { get; set; } = DateTime.UtcNow.AddDays(-60);
    public string CacheKey => SystemLogsCacheKey.GetChartDataCacheKey(LastDateTime.ToString());
    public IEnumerable<string>? Tags => SystemLogsCacheKey.Tags;
    
    /// <summary>system-wide log chart data, not principal-filtered.</summary>
    public CacheScope Scope => CacheScope.Global;
}

public class SystemLogsChatDataQueryHandler : IRequestHandler<SystemLogsTimeLineChatDataQuery, List<SystemLogTimeLineDto>>

{
    private readonly ILogDbContextFactory _dbContextFactory;

    public SystemLogsChatDataQueryHandler(
        ILogDbContextFactory dbContextFactory
    )
    {
        _dbContextFactory = dbContextFactory;
    }

    public async ValueTask<List<SystemLogTimeLineDto>> Handle(SystemLogsTimeLineChatDataQuery request,
        CancellationToken cancellationToken)
    {
        await using var db = await _dbContextFactory.CreateAsync(cancellationToken);
        var data = await db.SystemLogs.Where(x => x.TimeStamp >= request.LastDateTime)
            .GroupBy(x => new { x.TimeStamp.Date })
            .Select(x => new { x.Key.Date, Total = x.Count() })
            .OrderBy(x => x.Date)
            .ToListAsync(cancellationToken);

        List<SystemLogTimeLineDto> result = new();
        // UTC, to match the window's start and the rows themselves. This value never reaches the
        // database - it only bounds the in-memory loop that fills empty days - but with
        // LastDateTime now UTC, a local-time bound would run the loop over a differently-anchored
        // day grid than the data, adding or dropping a bin by the host's offset.
        DateTime end = DateTime.UtcNow.Date;
        var start = request.LastDateTime.Date;

        while (start <= end)
        {
            var item = data.FirstOrDefault(x => x.Date == start.Date);
            result.Add(item != null
                ? new SystemLogTimeLineDto { dt = item.Date, total = item.Total }
                : new SystemLogTimeLineDto { dt = start, total = 0 });

            start = start.AddDays(1);
        }

        return result.OrderBy(x => x.dt).ToList();
    }
}
