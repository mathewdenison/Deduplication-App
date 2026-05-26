using Microsoft.Extensions.Hosting;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace NASDeduplicator
{
    public class SchedulerService : BackgroundService
    {
        private readonly DedupeService _dedupeService;
        
        public bool IsEnabled { get; set; } = false;
        public TimeSpan ScheduledTime { get; set; } = new TimeSpan(2, 0, 0); // Default 2:00 AM
        public DateTime? LastRun { get; private set; }

        public SchedulerService(DedupeService dedupeService)
        {
            _dedupeService = dedupeService;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                if (IsEnabled)
                {
                    var now = DateTime.Now;
                    var todayScheduled = now.Date.Add(ScheduledTime);

                    // If we haven't run today and it's past the scheduled time
                    if (now >= todayScheduled && (LastRun == null || LastRun.Value.Date < now.Date))
                    {
                        if (!_dedupeService.IsRunning)
                        {
                            _dedupeService.WriteLog($"[SCHEDULER] Starting automated daily run at {now:HH:mm:ss}", ConsoleColor.Cyan);
                            TelemetryEngine.SendEvent(new { @event = "SCHEDULER_TRIGGER", scheduledTime = ScheduledTime, actualTime = now });
                            LastRun = now;
                            // We don't await the full run here as it would block the scheduler loop
                            // DedupeService.StartRunAsync manages its own internal task
                            _ = _dedupeService.StartRunAsync(stoppingToken);
                        }
                        else
                        {
                            _dedupeService.WriteLog("[SCHEDULER] Skipping scheduled run as a task is already in progress.", ConsoleColor.Yellow);
                        }
                    }
                }

                // Check every minute
                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            }
        }
    }
}
