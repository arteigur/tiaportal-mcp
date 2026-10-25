using ModelContextProtocol;
using System;
using System.Reflection;

namespace TiaMcpServer.ModelContextProtocol
{
    /// <summary>Marks a tool that connects to a real PLC (go online / offline). Needs '--allow-online'.</summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
    public sealed class OnlineToolAttribute : System.Attribute
    {
    }

    /// <summary>Marks a tool that prepares or performs a download to a PLC. Needs '--allow-download'.</summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
    public sealed class DownloadToolAttribute : System.Attribute
    {
    }

    /// <summary>Marks a tool that adds devices or modules to the project. Needs '--allow-hardware'.</summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
    public sealed class HardwareToolAttribute : System.Attribute
    {
    }

    /// <summary>
    /// Gates the tools that reach past the offline project: going online, downloading, and adding
    /// hardware. Each has its own command line flag so a client can allow one without the others.
    ///
    /// Callers: Program.cs (Configure from CliOptions, IsRegistered while building the tool list)
    /// and every [OnlineTool] / [DownloadTool] / [HardwareTool] tool, which calls the matching
    /// Ensure method first because the MSTest suite invokes tool methods directly.
    ///
    /// Same two-layer design as WritePolicy: a gated tool is absent from 'tools/list' unless its
    /// flag is set, and refuses to run if called directly anyway. A download additionally needs a
    /// confirmation token from 'PreviewDownload', see McpServer.Online.cs.
    /// </summary>
    public static class PlantPolicy
    {
        public static bool AllowOnline { get; set; }
        public static bool AllowDownload { get; set; }
        public static bool AllowHardware { get; set; }

        public static void Configure(CliOptions options)
        {
            AllowOnline = options.AllowOnline;
            AllowDownload = options.AllowDownload;
            AllowHardware = options.AllowHardware;
        }

        /// <summary>True when the method has no plant gate, or its gate's flag is set.</summary>
        public static bool IsRegistered(MethodInfo method)
        {
            if (method.GetCustomAttribute<OnlineToolAttribute>() != null && !AllowOnline)
            {
                return false;
            }

            if (method.GetCustomAttribute<DownloadToolAttribute>() != null && !AllowDownload)
            {
                return false;
            }

            if (method.GetCustomAttribute<HardwareToolAttribute>() != null && (!AllowHardware || !WritePolicy.AllowWrite))
            {
                return false;
            }

            return true;
        }

        public static void EnsureOnline(string toolName)
        {
            if (!AllowOnline)
            {
                throw new McpException(
                    $"Tool '{toolName}' connects to a PLC and is disabled. Restart the MCP server with '--allow-online' to enable it.");
            }
        }

        public static void EnsureDownload(string toolName)
        {
            if (!AllowDownload)
            {
                throw new McpException(
                    $"Tool '{toolName}' downloads to a PLC and is disabled. Restart the MCP server with '--allow-download' to enable it.");
            }
        }

        public static void EnsureHardware(string toolName)
        {
            if (!AllowHardware || !WritePolicy.AllowWrite)
            {
                throw new McpException(
                    $"Tool '{toolName}' adds hardware to the project and is disabled. Restart the MCP server with '--allow-write --allow-hardware' to enable it.");
            }
        }
    }
}
