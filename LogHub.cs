using Microsoft.AspNetCore.SignalR;
using System.Threading.Tasks;

namespace NASDeduplicator
{
    public class LogHub : Hub
    {
        public async Task SendMessage(string message, string severity)
        {
            await Clients.All.SendAsync("ReceiveLog", message, severity);
        }
    }
}
