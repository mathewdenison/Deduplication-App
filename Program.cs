using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System;
using System.IO;
using System.Threading.Tasks;

namespace NASDeduplicator
{
    public class Program
    {
        public static async Task Main(string[] args)
        {
            if (args.Length > 0)
            {
                await RunCliMode(args);
            }
            else
            {
                await RunWebMode(args);
            }
        }

        private static async Task RunCliMode(string[] args)
        {
            var service = new DedupeService();
            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i].ToLower();
                if (i + 1 >= args.Length) break;
                switch (arg)
                {
                    case "--source": case "-s": service.SourceBase = Path.GetFullPath(args[++i]); break;
                    case "--archive": case "-a": service.ArchiveBase = Path.GetFullPath(args[++i]); break;
                    case "--suspect": case "-p": service.SuspectBase = Path.GetFullPath(args[++i]); break;
                    case "--telemetry-url": case "-u": TelemetryEngine.TelemetryUrl = args[++i]; break;
                    case "--telemetry-token": case "-t": TelemetryEngine.TelemetryToken = args[++i]; break;
                }
            }

            if (string.IsNullOrEmpty(service.SourceBase) || string.IsNullOrEmpty(service.ArchiveBase) || string.IsNullOrEmpty(service.SuspectBase))
            {
                Console.WriteLine("Standalone CLI Mode");
                Console.WriteLine("Usage: NASDeduplicator --source <path> --archive <path> --suspect <path> [-u <telemetry_url> -t <token>]");
                return;
            }

            // Re-normalize to ensure no trailing slashes interfere with math later
            service.SourceBase = service.SourceBase.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            service.ArchiveBase = service.ArchiveBase.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            service.SuspectBase = service.SuspectBase.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            if (!Directory.Exists(FileSystemHandler.ToLongPath(service.SourceBase)))
            {
                Console.WriteLine($"Error: Source path not found: {service.SourceBase}");
                return;
            }

            Console.WriteLine("Starting Standalone Deduplication Run...");
            await service.StartRunAsync();
            Console.WriteLine("Run Complete.");
        }

        private static async Task RunWebMode(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);
            
            // Port 8080 is standard for containers, 5100 for local dev to avoid conflicts
            string port = Environment.GetEnvironmentVariable("ASPNETCORE_URLS") ?? "http://*:5100";
            builder.WebHost.UseUrls(port);

            builder.Services.AddControllers();
            builder.Services.AddSignalR();
            builder.Services.AddSingleton<DedupeService>();
            
            builder.Services.AddCors(options =>
            {
                options.AddPolicy("AllowReact", policy =>
                {
                    policy.AllowAnyHeader()
                          .AllowAnyMethod()
                          .SetIsOriginAllowed(_ => true)
                          .AllowCredentials();
                });
            });

            var app = builder.Build();
            if (app.Environment.IsDevelopment()) app.UseDeveloperExceptionPage();

            app.UseCors("AllowReact");
            app.MapControllers();
            app.MapHub<LogHub>("/logHub");

            TelemetryEngine.TelemetryUrl = Environment.GetEnvironmentVariable("SPLUNK_HEC_URL");
            TelemetryEngine.TelemetryToken = Environment.GetEnvironmentVariable("SPLUNK_HEC_TOKEN");

            await app.RunAsync();
        }
    }
}
