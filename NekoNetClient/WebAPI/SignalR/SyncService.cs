using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

/*
    File: SyncService.cs
    Role: Enumeration of built-in sync services supported by the client. Values correspond to hub routes.
    Note: May be unused in newer configured flows; retained for compatibility until fully audited.
*/

/* Not Used Currently, TODO: Remove after verifying no old references still exist. */
namespace NekoNetClient.WebAPI.SignalR
{
    /// <summary>
    /// Known synchronization backends. Historically map to hub paths.
    /// </summary>
    public enum SyncService
    {
        NekoNet,    // usually "/mare" 
        Lightless,  // "/lightless"
        TeraSync,    // "/tera-sync-v2" 
        PlayerSync  // also /mare
    }
}
