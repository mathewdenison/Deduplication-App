using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace NASDeduplicator
{
    public static class CacheManager
    {
        public static string CachePath { get; set; }

        public static ConcurrentDictionary<string, CacheEntry> LoadCache(Action<string, ConsoleColor, object> logAction)
        {
            if (File.Exists(CachePath))
            {
                try
                {
                    string json = File.ReadAllText(CachePath);
                    var data = JsonSerializer.Deserialize<Dictionary<string, CacheEntry>>(json);
                    if (data != null)
                    {
                        var dict = new ConcurrentDictionary<string, CacheEntry>(data);
                        logAction($"Loaded {dict.Count} entries from hash cache.", ConsoleColor.DarkGray, null);
                        return dict;
                    }
                }
                catch (Exception ex) { logAction($"Failed to load hash cache: {ex.Message}", ConsoleColor.Red, null); }
            }
            return new ConcurrentDictionary<string, CacheEntry>();
        }

        public static void SaveCache(ConcurrentDictionary<string, CacheEntry> cache, Action<string, ConsoleColor, object> logAction)
        {
            try
            {
                string json = JsonSerializer.Serialize(cache, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(CachePath, json);
                logAction($"Saved {cache.Count} entries to hash cache.", ConsoleColor.DarkGray, null);
            }
            catch (Exception ex) { logAction($"Failed to save hash cache: {ex.Message}", ConsoleColor.Red, null); }
        }
    }
}
