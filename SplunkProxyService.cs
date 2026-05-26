using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;

namespace NASDeduplicator
{
    public class SplunkProxyService
    {
        private readonly HttpClient _client;
        
        public SplunkProxyService()
        {
            var handler = new HttpClientHandler();
            handler.ServerCertificateCustomValidationCallback = (message, cert, chain, errors) => true; // Self-signed certs common in Splunk
            _client = new HttpClient(handler);
        }

        public async Task<string> GetLogsAsync()
        {
            // If we can't find Splunk REST API, fallback to local file
            try
            {
                string hecUrl = TelemetryEngine.TelemetryUrl;
                if (string.IsNullOrEmpty(hecUrl)) return GetLocalLogs();

                // Guessing Management URL from HEC URL
                // http://splunk:8088/services/collector -> https://splunk:8089
                Uri uri = new Uri(hecUrl);
                string managementUrl = $"https://{uri.Host}:8089/services/search/jobs/export";

                using var request = new HttpRequestMessage(HttpMethod.Post, managementUrl);
                var authBytes = System.Text.Encoding.UTF8.GetBytes($"admin:SplunkPassword123!");
                request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(authBytes));

                var content = new FormUrlEncodedContent(new[]
                {
                    new KeyValuePair<string, string>("search", "search index=* | sort - _time | head 200"),
                    new KeyValuePair<string, string>("output_mode", "json")
                });
                request.Content = content;

                var response = await _client.SendAsync(request);
                if (response.IsSuccessStatusCode)
                {
                    return await response.Content.ReadAsStringAsync();
                }
            }
            catch { }

            return GetLocalLogs();
        }

        private string GetLocalLogs()
        {
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "dedupe_master_log.txt");
            if (!File.Exists(path)) return "[]";
            
            var lines = File.ReadLines(path).Reverse().Take(200).Select(l => new { message = l, time = DateTime.Now });
            return JsonSerializer.Serialize(lines);
        }
    }
}
