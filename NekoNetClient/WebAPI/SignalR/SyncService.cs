using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NekoNetClient.WebAPI.SignalR
{
    public enum SyncService
    {
        NekoNet,    // usually "/mare" (your CurrentServer.ApiEndpoint)
        Lightless,  // "/lightless"
        TeraSync    // "/tera-sync-v2" (or whatever yours is)
    }
}
