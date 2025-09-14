using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

/*
 * Summary
 * -------
 * Defines the set of supported synchronization backends for the client’s
 * SignalR integration. Each enum value corresponds to a server route or hub
 * name used when connecting (e.g., "/mare", "/lightless", "/tera-sync-v2").
 *
 * Note: This enum is currently marked as unused; keep it until any lingering
 * references are verified and then remove if truly obsolete.
 */

/* Not Used Currently, TODO: Remove after verifying no old references still exist. */
namespace NekoNetClient.WebAPI.SignalR
{
    public enum SyncService
    {
        NekoNet,    // usually "/mare" 
        Lightless,  // "/lightless"
        TeraSync,    // "/tera-sync-v2" 
        PlayerSync  // also /mare
    }
}
