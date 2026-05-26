using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;

namespace NASDeduplicator
{
    public static class TelemetryEngine
    {
        private static readonly HttpClient _client = new HttpClient();
        public static string TelemetryUrl { get; set; }
        public static string TelemetryToken { get; set; }

        public static void SendEvent(object eventData)
        {
            if (string.IsNullOrEmpty(TelemetryUrl) || string.IsNullOrEmpty(TelemetryToken)) return;

            var payload = new
            {
                time = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                @event = eventData
            };

            string json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

            // Fire and forget
            _ = Task.Run(async () =>
            {
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Post, TelemetryUrl);
                    request.Headers.Authorization = new AuthenticationHeaderValue("Splunk", TelemetryToken);
                    request.Content = content;
                    await _client.SendAsync(request);
                }
                catch { /* Telemetry failure should not stop the app */ }
            });
        }
    }
}
