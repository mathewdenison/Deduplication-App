using Microsoft.AspNetCore.Mvc;
using System.Threading.Tasks;

namespace NASDeduplicator.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class DedupeController : ControllerBase
    {
        private readonly DedupeService _dedupeService;

        public DedupeController(DedupeService dedupeService)
        {
            _dedupeService = dedupeService;
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
                    suspect = _dedupeService.SuspectBase
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
            
            if (config.TelemetryUrl != null) TelemetryEngine.TelemetryUrl = config.TelemetryUrl;
            if (config.TelemetryToken != null) TelemetryEngine.TelemetryToken = config.TelemetryToken;

            return Ok();
        }

        [HttpPost("start")]
        public async Task<IActionResult> Start()
        {
            if (_dedupeService.IsRunning) return BadRequest("Already running.");
            await _dedupeService.StartRunAsync();
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
            await _dedupeService.RunSafetyTestAsync();
            return Ok();
        }
    }

    public class ConfigModel
    {
        public string Source { get; set; } = "";
        public string Archive { get; set; } = "";
        public string Suspect { get; set; } = "";
        public string? TelemetryUrl { get; set; }
        public string? TelemetryToken { get; set; }
    }
}
