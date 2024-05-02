using System.Data;
using System.Data.Common;
using System.Dynamic;
using AzureOpenAIProxy.Management.Components.EventManagement;
using AzureOpenAIProxy.Management.Database;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace AzureOpenAIProxy.Management.Services;

public class ModelCounts
{
    public string Resource { get; set; }
    public int Count { get; set; }
    public long PromptTokens { get; set; }
    public long CompletionTokens { get; set; }
    public long TotalTokens { get; set; }
}

public class ChartData
{
    public string EventId { get; set; } = null!;
    public DateTime DateStamp { get; set; }
    public string Resource { get; set; } = null!;
    public long PromptTokens { get; set; }
    public long CompletionTokens { get; set; }
    public long TotalTokens { get; set; }
    public long Requests { get; set; }
}

public class ModelData
{
    public IEnumerable<ModelCounts> ModelCounts { get; set; } = [];
    public IEnumerable<ChartData> ChartData { get; set; } = [];
}

public class EventService(IAuthService authService, AoaiProxyContext db) : IEventService, IDisposable
{
    private readonly DbConnection conn = db.Database.GetDbConnection();

    public async Task<Event?> CreateEventAsync(EventEditorModel model)
    {
        if (string.IsNullOrEmpty(model.EventSharedCode))
        {
            model.EventSharedCode = null;
        }

        if (string.IsNullOrEmpty(model.EventImageUrl))
        {
            model.EventImageUrl = null;
        }

        Event newEvent = new()
        {
            EventCode = model.Name!,
            EventSharedCode = model.EventSharedCode,
            EventImageUrl = model.EventImageUrl!,
            EventMarkdown = model.Description!,
            StartTimestamp = model.Start!.Value,
            EndTimestamp = model.End!.Value,
            TimeZoneOffset = model.SelectedTimeZone!.BaseUtcOffset.Minutes,
            TimeZoneLabel = model.SelectedTimeZone!.Id,
            OrganizerName = model.OrganizerName!,
            OrganizerEmail = model.OrganizerEmail!,
            MaxTokenCap = model.MaxTokenCap,
            DailyRequestCap = model.DailyRequestCap,
            Active = model.Active
        };

        string entraId = await authService.GetCurrentUserEntraIdAsync();

        if (conn.State != ConnectionState.Open)
            await conn.OpenAsync();

        using DbCommand cmd = conn.CreateCommand();

        cmd.CommandText = $"SELECT * FROM aoai.add_event(@OwnerId, @EventCode, @EventSharedCode, @EventMarkdown, @StartTimestamp, @EndTimestamp, @TimeZoneOffset, @TimeZoneLabel,  @OrganizerName, @OrganizerEmail, @MaxTokenCap, @DailyRequestCap, @Active, @EventImageUrl)";

        cmd.Parameters.Add(new NpgsqlParameter("OwnerId", entraId));
        cmd.Parameters.Add(new NpgsqlParameter("EventCode", newEvent.EventCode));
        cmd.Parameters.Add(new NpgsqlParameter("EventMarkdown", newEvent.EventMarkdown));
        cmd.Parameters.Add(new NpgsqlParameter("StartTimestamp", newEvent.StartTimestamp));
        cmd.Parameters.Add(new NpgsqlParameter("EndTimestamp", newEvent.EndTimestamp));
        cmd.Parameters.Add(new NpgsqlParameter("TimeZoneOffset", newEvent.TimeZoneOffset));
        cmd.Parameters.Add(new NpgsqlParameter("TimeZoneLabel", newEvent.TimeZoneLabel));
        cmd.Parameters.Add(new NpgsqlParameter("OrganizerName", newEvent.OrganizerName));
        cmd.Parameters.Add(new NpgsqlParameter("OrganizerEmail", newEvent.OrganizerEmail));
        cmd.Parameters.Add(new NpgsqlParameter("MaxTokenCap", newEvent.MaxTokenCap));
        cmd.Parameters.Add(new NpgsqlParameter("DailyRequestCap", newEvent.DailyRequestCap));
        cmd.Parameters.Add(new NpgsqlParameter("Active", newEvent.Active));

        var parameter_event_shared_code = new NpgsqlParameter("@EventSharedCode", NpgsqlDbType.Text);
        parameter_event_shared_code.Value = newEvent.EventSharedCode ?? (object)DBNull.Value;
        cmd.Parameters.Add(parameter_event_shared_code);

        var parameter = new NpgsqlParameter("@EventImageUrl", NpgsqlDbType.Text);
        parameter.Value = newEvent.EventImageUrl ?? (object)DBNull.Value;
        cmd.Parameters.Add(parameter);

        var reader = await cmd.ExecuteReaderAsync();

        if (reader.HasRows)
        {
            while (await reader.ReadAsync())
            {
                newEvent.EventId = reader.GetString(0);
            }
        }

        return newEvent;
    }

    public Task<Event?> GetEventAsync(string id) => db.Events.Include(e => e.Catalogs).FirstOrDefaultAsync(e => e.EventId == id);

    public async Task<EventMetric> GetEventMetricsAsync(string eventId)
    {
        (int attendeeCount, int requestCount) = await GetAttendeeMetricsAsync(eventId);
        ModelData modeldata = await GetModelCountAsync(eventId);

        return new()
        {
            EventId = eventId,
            AttendeeCount = attendeeCount,
            RequestCount = requestCount,
            ModelData = modeldata
        };
    }

    private async Task<ModelData> GetModelCountAsync(string eventId)
    {
        if (conn.State != ConnectionState.Open)
            await conn.OpenAsync();

        using var modelCountCommand = conn.CreateCommand();
        modelCountCommand.CommandText = """
        SELECT event_id, date_stamp, resource, SUM(prompt_tokens) AS prompt_tokens, SUM(completion_tokens) AS completion_tokens, SUM(total_tokens) AS total_tokens, COUNT(*) AS requests
        FROM aoai.metric_view where event_id = @EventId
        GROUP BY date_stamp, event_id, resource
        ORDER BY requests DESC
        """;

        modelCountCommand.Parameters.Add(new NpgsqlParameter("EventId", eventId));
        using var reader = await modelCountCommand.ExecuteReaderAsync();

        var chartData = new List<ChartData>();
        while (reader.Read())
        {
            DateTime dateStamp = reader.GetDateTime(1);
            var resource = reader.GetString(2);
            var promptTokens = reader.IsDBNull(3) ? 0 : reader.GetInt64(3);
            var completionTokens = reader.IsDBNull(4) ? 0 : reader.GetInt64(4);
            var totalTokens = reader.IsDBNull(5) ? 0 : reader.GetInt64(5);
            var requests = reader.GetInt64(6);

            var item = new ChartData
            {
                EventId = eventId,
                DateStamp = dateStamp,
                Resource = resource,
                PromptTokens = promptTokens,
                CompletionTokens = completionTokens,
                TotalTokens = totalTokens,
                Requests = requests
            };

            chartData.Add(item);
        };

        var summary = chartData
            .GroupBy(r => new { EventId = r.EventId, Resource = r.Resource })
            .Select(g => new
            {
                g.Key.Resource,
                PromptTokens = g.Sum(x => x.PromptTokens is DBNull ? 0 : (long)x.PromptTokens),
                CompletionTokens = g.Sum(x => x.CompletionTokens is DBNull ? 0 : (long)x.CompletionTokens),
                TotalTokens = g.Sum(x => x.TotalTokens is DBNull ? 0 : (long)x.TotalTokens),
                Requests = g.Sum(x => (long)x.Requests)
            })
            .OrderByDescending(x => x.Requests);

        List<ModelCounts> modelCounts = summary.Select(item => new ModelCounts
        {
            Resource = item.Resource,
            Count = (int)item.Requests,
            PromptTokens = item.PromptTokens,
            CompletionTokens = item.CompletionTokens,
            TotalTokens = item.TotalTokens
        }).ToList();

        ModelData md = new()
        {
            ModelCounts = modelCounts,
            ChartData = chartData
        };

        return md;

    }

    private async Task<(int attendeeCount, int requestCount)> GetAttendeeMetricsAsync(string eventId)
    {
        if (conn.State != ConnectionState.Open)
            await conn.OpenAsync();

        using var eventAttendeeCommand = conn.CreateCommand();
        eventAttendeeCommand.CommandText = """
        SELECT
            COUNT(user_id) as user_count,
            (SELECT count(api_key) FROM aoai.metric WHERE event_id = @EventId) as request_count
        FROM aoai.event_attendee
        WHERE event_id = @EventId
        """;

        eventAttendeeCommand.Parameters.Add(new NpgsqlParameter("EventId", eventId));

        using var reader = await eventAttendeeCommand.ExecuteReaderAsync();
        if (reader.HasRows)
        {
            while (await reader.ReadAsync())
            {
                return (reader.GetInt32(0), reader.GetInt32(1));
            }
        }

        return (0, 0);
    }

    public async Task<IEnumerable<Event>> GetOwnerEventsAsync()
    {
        string entraId = await authService.GetCurrentUserEntraIdAsync();
        return await db.Events
            .Where(e => e.OwnerEventMaps.Any(o => o.Owner.OwnerId == entraId))
            .OrderByDescending(e => e.Active)
            .ThenBy(e => e.StartTimestamp)
            .ToListAsync();
    }

    public async Task<Event?> UpdateEventAsync(string id, EventEditorModel model)
    {
        Event? evt = await db.Events.FindAsync(id);

        if (evt is null)
        {
            return null;
        }

        if (string.IsNullOrEmpty(model.EventSharedCode))
        {
            model.EventSharedCode = null;
        }

        if (string.IsNullOrEmpty(model.EventImageUrl))
        {
            model.EventImageUrl = null;
        }

        evt.EventCode = model.Name!;
        evt.EventSharedCode = model.EventSharedCode;
        evt.EventMarkdown = model.Description!;
        evt.StartTimestamp = model.Start!.Value;
        evt.EndTimestamp = model.End!.Value;
        evt.EventImageUrl = model.EventImageUrl!;
        evt.OrganizerEmail = model.OrganizerEmail!;
        evt.OrganizerName = model.OrganizerName!;
        evt.Active = model.Active;
        evt.MaxTokenCap = model.MaxTokenCap;
        evt.DailyRequestCap = model.DailyRequestCap;
        evt.TimeZoneLabel = model.SelectedTimeZone!.Id;
        evt.TimeZoneOffset = (int)model.SelectedTimeZone.BaseUtcOffset.TotalMinutes;

        await db.SaveChangesAsync();

        return evt;
    }

    public async Task<Event?> UpdateModelsForEventAsync(string id, IEnumerable<Guid> modelIds)
    {
        Event? evt = await db.Events.Include(e => e.Catalogs).FirstOrDefaultAsync(e => e.EventId == id);

        if (evt is null)
        {
            return null;
        }

        evt.Catalogs.Clear();

        IEnumerable<OwnerCatalog> catalogs = await db.OwnerCatalogs.Where(oc => modelIds.Contains(oc.CatalogId)).ToListAsync();

        foreach (OwnerCatalog catalog in catalogs)
        {
            evt.Catalogs.Add(catalog);
        }

        await db.SaveChangesAsync();
        return evt;
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
        {
            conn.Dispose();
        }
    }
}
