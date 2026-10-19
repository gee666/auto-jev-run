using Microsoft.Data.Sqlite;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AutoJev.Local;

internal sealed class UsageMeasurement
{
    public const string Item = "AutoJev.Usage";
    public string Id { get; } = Guid.NewGuid().ToString("N");
    public long Started { get; } = Stopwatch.GetTimestamp();
    public double QueueMs { get; set; }
    public long? InputTokens { get; set; }
    public long? OutputTokens { get; set; }
    public long? CacheReadTokens { get; set; }
    public long? CacheWriteTokens { get; set; }
    public static UsageMeasurement? From(HttpContext ctx) => ctx.Items.TryGetValue(Item, out var value) ? value as UsageMeasurement : null;
}

internal sealed class UsageStore : IDisposable
{
    private readonly object sync = new();
    private readonly SqliteConnection db;
    public string? LastError { get; private set; }
    private readonly string dashboardKey = "autojev_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
    private static string Now() => DateTimeOffset.UtcNow.ToString("O");
    private static string Hash(string secret) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));
    public UsageStore(string root)
    {
        var folder = Path.Combine(root, "data");
        Directory.CreateDirectory(folder);
        db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(folder, "usage.sqlite"), Pooling = false }.ToString());
        db.Open();
        Execute("PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;");
        Execute("""
            CREATE TABLE IF NOT EXISTS api_keys(
              id TEXT PRIMARY KEY, name TEXT NOT NULL COLLATE NOCASE UNIQUE,
              key_hash TEXT UNIQUE, prefix TEXT, created_utc TEXT NOT NULL,
              revoked_utc TEXT, system INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE IF NOT EXISTS requests(
              id TEXT PRIMARY KEY, key_id TEXT NOT NULL REFERENCES api_keys(id),
              started_utc TEXT NOT NULL, completed_utc TEXT, status INTEGER,
              outcome TEXT NOT NULL DEFAULT 'pending', input_tokens INTEGER, output_tokens INTEGER,
              cache_read_tokens INTEGER, cache_write_tokens INTEGER,
              total_ms REAL, queue_ms REAL, processing_ms REAL);
            CREATE INDEX IF NOT EXISTS requests_started ON requests(started_utc DESC);
            CREATE INDEX IF NOT EXISTS requests_key_started ON requests(key_id,started_utc DESC);
            PRAGMA user_version=1;
            """);
        // Remove the retired anonymous usage group and its historical requests.
        Execute("BEGIN IMMEDIATE; DELETE FROM requests WHERE key_id='without-a-key'; DELETE FROM api_keys WHERE id='without-a-key'; COMMIT;");
        foreach (var name in new[] { "unrecognized-key" })
            Execute("INSERT OR IGNORE INTO api_keys(id,name,created_utc,system) VALUES($id,$id,$now,1)", ("$id",name),("$now",Now()));
        Execute("INSERT INTO api_keys(id,name,key_hash,prefix,created_utc,system) VALUES('dashboard','Dashboard & testing',$hash,$prefix,$now,1) ON CONFLICT(id) DO UPDATE SET key_hash=excluded.key_hash,prefix=excluded.prefix,revoked_utc=NULL",("$hash",Hash(dashboardKey)),("$prefix",dashboardKey[..15]),("$now",Now()));
        Execute("UPDATE requests SET outcome='interrupted',status=499,completed_utc=$now WHERE outcome='pending'",("$now",Now()));
    }
    private int Execute(string sql, params (string, object?)[] args)
    {
        using var cmd = db.CreateCommand(); cmd.CommandText = sql;
        foreach (var (name,value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd.ExecuteNonQuery();
    }
    private List<Dictionary<string,object?>> Query(string sql, params (string, object?)[] args)
    {
        using var cmd=db.CreateCommand();cmd.CommandText=sql;
        foreach(var (name,value) in args)cmd.Parameters.AddWithValue(name,value??DBNull.Value);
        using var rows=cmd.ExecuteReader();var result=new List<Dictionary<string,object?>>();
        while(rows.Read())
        {
            var row=new Dictionary<string,object?>();
            for(var i=0;i<rows.FieldCount;i++)row[rows.GetName(i)]=rows.IsDBNull(i)?null:rows.GetValue(i);
            result.Add(row);
        }
        return result;
    }
    public (string Id,bool Allowed) Identify(HttpContext ctx)
    {
        var auth=ctx.Request.Headers.Authorization;
        var alternative=ctx.Request.Headers["X-API-Key"];
        if(auth.Count==0 && alternative.Count==0)return ("without-a-key",false);
        string? token=null;
        if(auth.Count>0)
        {
            if(auth.Count!=1 || !auth.ToString().StartsWith("Bearer ",StringComparison.OrdinalIgnoreCase))return ("unrecognized-key",false);
            token=auth.ToString()[7..].Trim();
        }
        if(alternative.Count>0)
        {
            if(alternative.Count!=1 || (token is not null && token!=alternative.ToString()))return ("unrecognized-key",false);
            token=alternative.ToString();
        }
        if(string.IsNullOrEmpty(token) || token.Length>512)return ("unrecognized-key",false);
        lock(sync)
        {
            var rows=Query("SELECT id,revoked_utc FROM api_keys WHERE key_hash=$hash",("$hash",Hash(token)));
            return rows.Count==0 ? ("unrecognized-key",false) : ((string)rows[0]["id"]!,rows[0]["revoked_utc"] is null);
        }
    }
    public (string Id,bool Allowed) Begin(UsageMeasurement m,HttpContext ctx)
    {
        lock(sync)
        {
            var identity=Identify(ctx);
            Execute("INSERT INTO requests(id,key_id,started_utc) VALUES($id,$key,$now)",("$id",m.Id),("$key",identity.Id),("$now",Now()));
            return identity;
        }
    }
    public void Finish(UsageMeasurement m,int status,bool cancelled)
    {
        var total=Stopwatch.GetElapsedTime(m.Started).TotalMilliseconds;
        var outcome=cancelled?"cancelled":status<400?"success":"error";
        lock(sync)
        {
            try
            {
                Execute("""
                  UPDATE requests SET completed_utc=$now,status=$status,outcome=$outcome,
                    input_tokens=$input,output_tokens=$output,cache_read_tokens=$read,cache_write_tokens=$write,
                    total_ms=$total,queue_ms=$queue,processing_ms=$processing WHERE id=$id
                  """,("$now",Now()),("$status",cancelled?499:status),("$outcome",outcome),("$input",m.InputTokens),
                  ("$output",m.OutputTokens),("$read",m.CacheReadTokens),("$write",m.CacheWriteTokens),
                  ("$total",total),("$queue",m.QueueMs),("$processing",Math.Max(0,total-m.QueueMs)),("$id",m.Id));
                LastError=null;
            }
            catch(Exception ex){LastError="A request could not be finalized in the usage database. Check logs/launcher.log.";Console.Error.WriteLine($"Usage persistence failed: {ex.Message}");}
        }
    }
    private static string ValidName(string? name)
    {
        name=name?.Trim();
        if(string.IsNullOrEmpty(name)||name.Length>80||name.Any(char.IsControl)||name.Equals("without-a-key",StringComparison.OrdinalIgnoreCase)||name.Equals("unrecognized-key",StringComparison.OrdinalIgnoreCase)||name.Equals("Dashboard & testing",StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Choose an app name of 1 to 80 characters. Built-in group names are reserved.");
        return name;
    }
    public object Create(string? name)
    {
        name=ValidName(name);var id=Guid.NewGuid().ToString("N");var secret="autojev_"+Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        lock(sync)Execute("INSERT INTO api_keys(id,name,key_hash,prefix,created_utc) VALUES($id,$name,$hash,$prefix,$now)",("$id",id),("$name",name),("$hash",Hash(secret)),("$prefix",secret[..15]),("$now",Now()));
        return new{id,name,key=secret};
    }
    public bool Rename(string id,string? name)
    {
        name=ValidName(name);lock(sync)return Execute("UPDATE api_keys SET name=$name WHERE id=$id AND system=0",("$name",name),("$id",id))>0;
    }
    public bool Revoke(string id)
    {
        lock(sync)return Execute("UPDATE api_keys SET revoked_utc=COALESCE(revoked_utc,$now) WHERE id=$id AND system=0",("$now",Now()),("$id",id))>0;
    }
    public int Delete(string id)
    {
        lock(sync)
        {
            if(Query("SELECT id FROM api_keys WHERE id=$id AND system=0",("$id",id)).Count==0)return 404;
            if(Query("SELECT id FROM requests WHERE key_id=$id AND outcome='pending' LIMIT 1",("$id",id)).Count>0)return 409;
            using var transaction=db.BeginTransaction();
            using var cmd=db.CreateCommand();
            cmd.Transaction=transaction;
            cmd.CommandText="DELETE FROM requests WHERE key_id=$id; DELETE FROM api_keys WHERE id=$id AND system=0;";
            cmd.Parameters.AddWithValue("$id",id);
            cmd.ExecuteNonQuery();
            transaction.Commit();
            return 200;
        }
    }
    public object Read(int days,string? keyId,int offset)
    {
        var since=days==0?"":DateTimeOffset.UtcNow.AddDays(-days).ToString("O");
        keyId=string.IsNullOrEmpty(keyId)?null:keyId;
        lock(sync)
        {
            var keys=Query("""
              SELECT k.id,k.name,k.prefix,k.created_utc AS createdUtc,k.revoked_utc AS revokedUtc,k.system,
              COUNT(r.id) AS requests,COALESCE(SUM(r.outcome='success'),0) AS successes,
              COALESCE(SUM(r.outcome IN ('error','cancelled','interrupted')),0) AS failures,
              COALESCE(SUM(r.outcome='pending'),0) AS pending,
              COALESCE(SUM(r.input_tokens),0) AS inputTokens,COALESCE(SUM(r.output_tokens),0) AS outputTokens,
              COALESCE(SUM(r.cache_read_tokens),0) AS cacheReadTokens,COALESCE(SUM(r.cache_write_tokens),0) AS cacheWriteTokens,
              COALESCE(SUM(r.id IS NOT NULL AND r.input_tokens IS NULL),0) AS unmeteredRequests,
              AVG(r.total_ms) AS averageMs,COALESCE(SUM(r.total_ms),0) AS totalMs,MAX(r.started_utc) AS lastUsedUtc
              FROM api_keys k LEFT JOIN requests r ON r.key_id=k.id AND r.started_utc >= $since
              GROUP BY k.id ORDER BY k.system DESC,k.created_utc DESC
              """,("$since",since));
            var filter="r.started_utc >= $since AND ($key IS NULL OR r.key_id=$key)";
            var rows=Query("SELECT r.id,k.name AS keyName,r.key_id AS keyId,r.started_utc AS startedUtc,r.status,r.outcome,r.input_tokens AS inputTokens,r.output_tokens AS outputTokens,r.cache_read_tokens AS cacheReadTokens,r.cache_write_tokens AS cacheWriteTokens,r.total_ms AS totalMs,r.queue_ms AS queueMs,r.processing_ms AS processingMs FROM requests r JOIN api_keys k ON k.id=r.key_id WHERE "+filter+" ORDER BY r.started_utc DESC,r.id DESC LIMIT 50 OFFSET $offset",("$since",since),("$key",keyId),("$offset",offset));
            var count=Query("SELECT COUNT(*) AS count FROM requests r WHERE "+filter,("$since",since),("$key",keyId));
            return new{keys,requests=rows,total=count[0]["count"],offset,pageSize=50,trackingError=LastError};
        }
    }
    public void Map(WebApplication app)
    {
        app.MapGet("/local/dashboard-key", () => Results.Json(new { key = dashboardKey }));
        app.MapGet("/local/usage", (HttpContext ctx) =>
        {
            int.TryParse(ctx.Request.Query["days"],out var days);int.TryParse(ctx.Request.Query["offset"],out var offset);
            return Results.Json(Read(Math.Clamp(days,0,36500),ctx.Request.Query["keyId"],Math.Max(0,offset)));
        });
        app.MapPost("/local/keys",async ctx=>await Mutate(ctx,async () =>
        {
            var input=await ctx.Request.ReadFromJsonAsync<KeyName>();
            await ctx.Response.WriteAsJsonAsync(Create(input?.Name));
        }));
        app.MapPatch("/local/keys/{id}",async ctx=>await Mutate(ctx,async () =>
        {
            var input=await ctx.Request.ReadFromJsonAsync<KeyName>();
            if(!Rename(ctx.Request.RouteValues["id"]!.ToString()!,input?.Name)){await Program.Error(ctx,404,"Key not found.");return;}
            await ctx.Response.WriteAsJsonAsync(new{status="renamed"});
        }));
        app.MapDelete("/local/keys/{id}",async ctx=>await Mutate(ctx,async () =>
        {
            var status=Delete(ctx.Request.RouteValues["id"]!.ToString()!);
            if(status!=200){await Program.Error(ctx,status,status==409?"This key has active or queued requests. Revoke it, wait for them to finish, then delete it.":"Key not found or built-in key cannot be deleted.");return;}
            await ctx.Response.WriteAsJsonAsync(new{status="deleted"});
        }));
        app.MapPost("/local/keys/{id}/revoke",async ctx=>await Mutate(ctx,async () =>
        {
            if(!Revoke(ctx.Request.RouteValues["id"]!.ToString()!)){await Program.Error(ctx,404,"Key not found.");return;}
            await ctx.Response.WriteAsJsonAsync(new{status="revoked"});
        }));
    }
    private static async Task Mutate(HttpContext ctx,Func<Task> work)
    {
        if(!ctx.Request.HasJsonContentType()){await Program.Error(ctx,415,"Use application/json.");return;}
        try{await work();}
        catch(ArgumentException ex){await Program.Error(ctx,422,ex.Message);}
        catch(SqliteException ex) when(ex.SqliteErrorCode==19){await Program.Error(ctx,409,"That app name is already in use.");}
    }
    private sealed record KeyName(string? Name);
    public void Dispose(){lock(sync)db.Dispose();}
}
