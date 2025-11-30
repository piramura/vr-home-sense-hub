// Serverプログラム
// センサ情報をAPIで提供する

using System.Collections.Concurrent;

var builder = WebApplication.CreateBuilder(args);
// ローカル専用設定（あってもなくてもOK）
builder.Configuration.AddJsonFile(
    "appsettings.Local.json",
    optional: true,
    reloadOnChange: true);

// 1. appsettings.Local.json / appsettings.json の Hub:ApiKey
// 2. なければ環境変数 HUB_API_KEY
var expectedApiKey =
    builder.Configuration["Hub:ApiKey"]
    ?? builder.Configuration["HUB_API_KEY"];


if (string.IsNullOrEmpty(expectedApiKey))
{
    Console.WriteLine("[WARN] HUB_API_KEY が設定されていません。POSTはすべて 401 になります。");
}

// センサー状態のストアを DI に登録
builder.Services.AddSingleton<IRoomStateStore, InMemoryRoomStateStore>();

var app = builder.Build();

// --- Hub からの更新用エンドポイント（書き込み） ---
app.MapPost("/api/room/{roomId}", (
    string roomId,
    RoomUpdateDto dto,
    HttpContext ctx,
    IRoomStateStore store) =>
{
    // 1) APIキーチェック
    var apiKey = ctx.Request.Headers["X-Api-Key"].FirstOrDefault();
    if (string.IsNullOrEmpty(expectedApiKey) || apiKey != expectedApiKey)
    {
        Console.WriteLine($"[WARN] Invalid POST from {ctx.Connection.RemoteIpAddress}");
        return Results.Unauthorized();
    }

    // 2) 値の簡易バリデーション（変な値を弾く）
    if (dto.Co2Ppm is < 0 or > 10000 ||
        dto.Temperature is < -50 or > 60 ||
        dto.Humidity is < 0 or > 100)
    {
        return Results.BadRequest("invalid range");
    }

    var state = new RoomEnvironmentState
    {
        RoomId        = roomId,
        DeviceAddress = dto.DeviceAddress ?? "",
        Co2Ppm        = dto.Co2Ppm,
        Temperature   = dto.Temperature,
        Humidity      = dto.Humidity,
        SourceTime    = dto.SourceTimestamp,
        LastUpdated   = DateTime.UtcNow,
    };

    store.Set(roomId, state);

    Console.WriteLine($"[UPDATE] {roomId} CO2={state.Co2Ppm} temp={state.Temperature} hum={state.Humidity}");
    return Results.NoContent();
});

// --- VRChat / ブラウザからの読み取り用エンドポイント（読み取り専用） ---
app.MapGet("/api/room/{roomId}", (string roomId, IRoomStateStore store) =>
{
    var state = store.Get(roomId);
    return state is null ? Results.NotFound() : Results.Ok(state);
});

// おまけのヘルスチェック
app.MapGet("/", () => "VrHomeSense.Server OK");

app.Run();


// ===== モデル類 =====

public class RoomUpdateDto
{
    public string? DeviceAddress { get; set; }
    public double Co2Ppm { get; set; }
    public double Temperature { get; set; }
    public double Humidity { get; set; }
    public DateTime? SourceTimestamp { get; set; }
}

public class RoomEnvironmentState
{
    public string RoomId { get; set; } = "";
    public string DeviceAddress { get; set; } = "";
    public double Co2Ppm { get; set; }
    public double Temperature { get; set; }
    public double Humidity { get; set; }
    public DateTime? SourceTime { get; set; }
    public DateTime LastUpdated { get; set; }
}

// 状態ストアのインターフェース
public interface IRoomStateStore
{
    RoomEnvironmentState? Get(string roomId);
    void Set(string roomId, RoomEnvironmentState state);
}

// メモリ上だけで管理する実装
public class InMemoryRoomStateStore : IRoomStateStore
{
    private readonly ConcurrentDictionary<string, RoomEnvironmentState> _dict
        = new();

    public RoomEnvironmentState? Get(string roomId)
        => _dict.TryGetValue(roomId, out var s) ? s : null;

    public void Set(string roomId, RoomEnvironmentState state)
        => _dict[roomId] = state;
}
