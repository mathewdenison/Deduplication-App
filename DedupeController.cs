using Microsoft.AspNetCore.Mvc;
using System.Threading.Tasks;

namespace NASDeduplicator.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class DedupeController : ControllerBase
    {
        private readonly DedupeService _dedupeService;
        private readonly SchedulerService _schedulerService;
        private readonly SplunkProxyService _splunkService;

        public DedupeController(DedupeService dedupeService, SchedulerService schedulerService, SplunkProxyService splunkService)
        {
            _dedupeService = dedupeService;
            _schedulerService = schedulerService;
            _splunkService = splunkService;
        }

        [HttpPost("logs")]
        public async Task<IActionResult> GetLogs()
        {
            var logs = await _splunkService.GetLogsAsync();
            return Content(logs, "application/json");
        }

        [HttpPost("connect")]
        public async Task<IActionResult> Connect([FromBody] ConnectModel model)
        {
            if (_dedupeService.IsRunning) return BadRequest("Cannot change connection while engine is running.");

            string finalSource = model.SourcePath;
            string finalArchive = model.ArchivePath;
            string finalSuspect = model.SuspectPath;

            if (model.Type == ConnectionType.SMB)
            {
                var (success, message) = await NetworkShareManager.MountShare(model.NetworkPath, model.Username, model.Password, _dedupeService.WriteLog);
                if (!success) return BadRequest(message);

                // On Linux, message is the mount point (/mnt/remote_nas)
                bool isWindows = System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows);
                string root = isWindows ? model.NetworkPath : message;

                finalSource = Path.Combine(root, model.SourcePath.TrimStart('\\', '/'));
                finalArchive = Path.Combine(root, model.ArchivePath.TrimStart('\\', '/'));
                finalSuspect = Path.Combine(root, model.SuspectPath.TrimStart('\\', '/'));
            }

            _dedupeService.SourceBase = finalSource;
            _dedupeService.ArchiveBase = finalArchive;
            _dedupeService.SuspectBase = finalSuspect;

            return Ok(new { source = finalSource, archive = finalArchive, suspect = finalSuspect });
        }

        [HttpGet("status")]

        public IActionResult GetStatus()
        {
            return Ok(new
            {
                isRunning = _dedupeService.IsRunning,
                currentPhase = _dedupeService.CurrentPhase,
                filesDiscovered = _dedupeService.FilesDiscovered,
                totalBytesHashed = _dedupeService.TotalBytesHashed,
                successCount = _dedupeService.SuccessCount,
                failCount = _dedupeService.FailCount,
                elapsedMinutes = _dedupeService.ElapsedMinutes,
                config = new
                {
                    source = _dedupeService.SourceBase,
                    archive = _dedupeService.ArchiveBase,
                    suspect = _dedupeService.SuspectBase,
                    isScheduled = _schedulerService.IsEnabled,
                    scheduleTime = _schedulerService.ScheduledTime.ToString(@"hh\:mm")
                }
            });
        }

        [HttpPost("config")]
        public IActionResult UpdateConfig([FromBody] ConfigModel config)
        {
            if (_dedupeService.IsRunning) return BadRequest("Cannot update config while running.");
            
            _dedupeService.SourceBase = config.Source;
            _dedupeService.ArchiveBase = config.Archive;
            _dedupeService.SuspectBase = config.Suspect;

            _schedulerService.IsEnabled = config.IsScheduled;
            if (TimeSpan.TryParse(config.ScheduleTime, out var time))
            {
                _schedulerService.ScheduledTime = time;
            }
            
            if (config.TelemetryUrl != null) TelemetryEngine.TelemetryUrl = config.TelemetryUrl;
            if (config.TelemetryToken != null) TelemetryEngine.TelemetryToken = config.TelemetryToken;

            return Ok();
        }

        [HttpPost("start")]
        public async Task<IActionResult> Start()
        {
            if (_dedupeService.IsRunning) return BadRequest("Already running.");
            _ = _dedupeService.StartRunAsync();
            return Ok();
        }

        [HttpPost("stop")]
        public IActionResult Stop()
        {
            _dedupeService.StopRun();
            return Ok();
        }

        [HttpPost("verify")]
        public async Task<IActionResult> RunSafetyTest()
        {
            if (_dedupeService.IsRunning) return BadRequest("Cannot run safety test while another job is running.");
            _ = _dedupeService.RunSafetyTestAsync();
            return Ok();
        }

        [HttpPost("testscript")]
        public IActionResult RunTestScript()
        {
            if (_dedupeService.IsRunning) return BadRequest("Cannot run test script while a job is running.");

            Task.Run(() =>
            {
                try
                {
                    _dedupeService.WriteLog("[TEST LAB] Starting Test Data Generation Script...", ConsoleColor.Cyan);
                    
                    bool isWindows = System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows);
                    var psi = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = isWindows ? "powershell.exe" : "pwsh",
                        Arguments = "-ExecutionPolicy Bypass -File CreateTestData.ps1",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };

                    using (var process = System.Diagnostics.Process.Start(psi))
                    {
                        if (process == null) throw new Exception("Failed to start PowerShell.");

                        process.OutputDataReceived += (s, e) => { if (e.Data != null) _dedupeService.WriteLog($"[PS] {e.Data}", ConsoleColor.Gray); };
                        process.ErrorDataReceived += (s, e) => { if (e.Data != null) _dedupeService.WriteLog($"[PS ERROR] {e.Data}", ConsoleColor.Red); };

                        process.BeginOutputReadLine();
                        process.BeginErrorReadLine();
                        process.WaitForExit();

                        _dedupeService.WriteLog($"[TEST LAB] Script completed with exit code {process.ExitCode}", ConsoleColor.Green);
                    }
                }
                catch (Exception ex)
                {
                    _dedupeService.WriteLog($"[TEST LAB] CRITICAL ERROR: {ex.Message}", ConsoleColor.Red);
                }
            });

            return Ok();
        }
    }

    public enum ConnectionType { Local, SMB }

    public class ConnectModel
    {
        public ConnectionType Type { get; set; }
        public string NetworkPath { get; set; } = "";
        public string Username { get; set; } = "";
        public string Password { get; set; } = "";
        public string SourcePath { get; set; } = "";
        public string ArchivePath { get; set; } = "";
        public string SuspectPath { get; set; } = "";
    }

    public class ConfigModel
    {
        public string Source { get; set; } = "";
        public string Archive { get; set; } = "";
        public string Suspect { get; set; } = "";
        public bool IsScheduled { get; set; }
        public string ScheduleTime { get; set; } = "02:00";
        public string? TelemetryUrl { get; set; }
        public string? TelemetryToken { get; set; }
    }
}
