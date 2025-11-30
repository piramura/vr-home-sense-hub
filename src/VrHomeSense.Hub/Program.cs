// センサ情報をクラウドAPIに送信する Hub プログラム
// BLE → JSON → Render(予定) に POST する

using System;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Net.Http;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Devices.Bluetooth.Advertisement;
using Microsoft.Extensions.Configuration;
namespace VrHomeSense.Hub
{
    // 1) センサー状態を表すクラス
    public class RoomEnvironmentState
    {
        public string DeviceAddress { get; set; } = "";
        public double Co2Ppm { get; set; }
        public double Temperature { get; set; }
        public double Humidity { get; set; }
        public DateTime LastUpdated { get; set; }
    }

    // 2) グローバルな保存場所（ロック付き）
    public static class StateStore
    {
        public static readonly object Lock = new object();
        public static RoomEnvironmentState Latest { get; set; } = new RoomEnvironmentState();
    }
    public static class HubConfig
    {
        private static readonly IConfigurationRoot _config;

        static HubConfig()
        {
            _config = new ConfigurationBuilder()
                .SetBasePath(AppContext.BaseDirectory)
                .AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true)
                .AddEnvironmentVariables()  // HUB_API_KEY, HUB_SERVER_BASE_URL とか拾える
                .Build();
        }

        public static string ApiKey =>
            _config["Hub:ApiKey"]
            ?? _config["HUB_API_KEY"]
            ?? "";

        public static string ServerBaseUrl =>
            _config["Hub:ServerBaseUrl"]
            ?? _config["HUB_SERVER_BASE_URL"]
            ?? "http://localhost:5000"; // デフォルト
    }

    // 3) クラウド(サーバ)に送信するクラス
    public static class Uploader
    {
        private static readonly HttpClient _client = new HttpClient();

        // ここは固定IDでOK（部屋ID）
        private const string RoomId = "piramura-room";

        public static async void Send(RoomEnvironmentState state)
        {
            try
            {
                var payload = new
                {
                    deviceAddress   = state.DeviceAddress,
                    co2Ppm          = state.Co2Ppm,
                    temperature     = state.Temperature,
                    humidity        = state.Humidity,
                    sourceTimestamp = state.LastUpdated
                };

                var json = JsonSerializer.Serialize(payload);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var baseUrl = HubConfig.ServerBaseUrl;
                var apiKey  = HubConfig.ApiKey;

                if (string.IsNullOrEmpty(apiKey))
                {
                    Console.WriteLine("[WARN] ApiKey が設定されていないため、アップロードをスキップしました。");
                    return;
                }

                var req = new HttpRequestMessage(
                    HttpMethod.Post,
                    $"{baseUrl.TrimEnd('/')}/api/room/{RoomId}")
                {
                    Content = content
                };

                req.Headers.Add("X-Api-Key", apiKey);

                var res = await _client.SendAsync(req);
                if (!res.IsSuccessStatusCode)
                {
                    Console.WriteLine($"Upload failed: {res.StatusCode}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Upload error: {ex.Message}");
            }
        }
    }

    internal class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("VrHomeSense Hub: BLEスキャン → クラウドPOST");

            // --- BLE Watcher 起動 ---
            var watcher = new BluetoothLEAdvertisementWatcher
            {
                ScanningMode = BluetoothLEScanningMode.Active
            };
            watcher.Received += OnWatcherReceived;
            watcher.Start();
            Console.WriteLine("BLE スキャン開始");
            Console.WriteLine("Enterキーで終了します。");

            // ★ ここで待たないと即終了してしまう
            Console.ReadLine();

            watcher.Stop();
        }

        // BLE受信イベント（CO2/温度/湿度 を StateStore に反映 & サーバにPOST）
        private static void OnWatcherReceived(
            BluetoothLEAdvertisementWatcher sender,
            BluetoothLEAdvertisementReceivedEventArgs args)
        {
            foreach (var md in args.Advertisement.ManufacturerData)
            {
                // SwitchBot 以外は無視
                if (md.CompanyId != 0x0969) continue;

                var buffer = md.Data.ToArray();
                if (buffer.Length < 16) continue; // CO2付きパケット以外は無視

                // 先頭 6 バイトがデバイス MAC
                string mac =
                    $"{buffer[0]:X2}:{buffer[1]:X2}:{buffer[2]:X2}:{buffer[3]:X2}:{buffer[4]:X2}:{buffer[5]:X2}";

                // 自分の CO2 センサーだけに絞る
                if (!mac.Equals("B0:E9:FE:DC:15:36", StringComparison.OrdinalIgnoreCase))
                    continue;

                // 温度 / 湿度 / CO2 をパース（Qiita記事ロジック準拠）
                var temperature = (((double)(buffer[8] & 0x0f) / 10) + (buffer[9] & 0x7f))
                                  * ((buffer[9] & 0x80) > 0 ? 1 : -1);

                var humidity = buffer[10] & 0x7f;
                var co2 = (buffer[13] << 8) + buffer[14];

                // コンソール表示（デバッグ用）
                Console.WriteLine(
                    $"MAC={mac}, rssi={args.RawSignalStrengthInDBm}, " +
                    $"temp={temperature}C, hum={humidity}%, co2={co2}ppm");

                RoomEnvironmentState snapshot;
                // グローバル状態に保存
                lock (StateStore.Lock)
                {
                    StateStore.Latest.DeviceAddress = mac;
                    StateStore.Latest.Temperature   = temperature;
                    StateStore.Latest.Humidity      = humidity;
                    StateStore.Latest.Co2Ppm        = co2;
                    StateStore.Latest.LastUpdated   = DateTime.Now;

                    // 送信用にスナップショットを作る
                    snapshot = new RoomEnvironmentState
                    {
                        DeviceAddress = StateStore.Latest.DeviceAddress,
                        Temperature   = StateStore.Latest.Temperature,
                        Humidity      = StateStore.Latest.Humidity,
                        Co2Ppm        = StateStore.Latest.Co2Ppm,
                        LastUpdated   = StateStore.Latest.LastUpdated
                    };
                }

                // 保存した最新状態を、そのままクラウドに送る
                Uploader.Send(snapshot);
            }
        }
    }
}
