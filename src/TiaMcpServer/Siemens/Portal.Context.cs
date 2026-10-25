using Siemens.Engineering;
using Siemens.Engineering.Compiler;
using Siemens.Engineering.Online;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace TiaMcpServer.Siemens
{
    public partial class Portal
    {
        // One-call session context for an agent: which portal and project, whether it has unsaved
        // changes, and per PLC the device, CPU type, online state and last compile result.
        //
        // Callers: the GetSessionContext tool in McpServer.Context.cs, and CompilePlcSoftware,
        // which records its result here. Read-only: never connects to a PLC and never compiles.

        private readonly ConcurrentDictionary<string, CompileSnapshot> _lastCompiles = new(StringComparer.OrdinalIgnoreCase);

        private void RecordCompile(string softwarePath, CompilerResult? result)
        {
            if (result == null)
            {
                return;
            }

            _lastCompiles[softwarePath] = new CompileSnapshot
            {
                State = result.State.ToString(),
                ErrorCount = result.ErrorCount,
                WarningCount = result.WarningCount,
                AtUtc = DateTime.UtcNow
            };
        }

        public SessionContext GetSessionContext()
        {
            return Operation.Run(_logger, nameof(GetSessionContext), PortalErrorCode.InvalidState,
                () =>
                {
                    var state = GetState();
                    var context = new SessionContext
                    {
                        PortalId = state.PortalId,
                        IsConnected = state.IsConnected ?? false
                    };

                    if (_project is not ProjectBase project)
                    {
                        return context;
                    }

                    context.ProjectName = project.Name;
                    context.ProjectPath = SafePath(project);
                    context.IsLocalSession = IsLocalSession;
                    context.HasUnsavedChanges = project.IsModified;
                    context.LastModifiedUtc = project.LastModified.ToUniversalTime();

                    foreach (var softwarePath in GetSoftwarePaths())
                    {
                        var plc = new SessionPlc { SoftwarePath = softwarePath };

                        try
                        {
                            var deviceItem = GetPlcDeviceItemOrThrow(softwarePath);

                            plc.DeviceItemName = deviceItem.Name;
                            plc.TypeIdentifier = deviceItem.TypeIdentifier;
                            plc.OnlineState = deviceItem.GetService<OnlineProvider>()?.State.ToString();
                        }
                        catch (Exception ex) when (ex is PortalException || ex is EngineeringException)
                        {
                            // A software that is not a PLC (HMI) still appears, just without PLC detail.
                            plc.Note = ex.Message;
                        }

                        if (_lastCompiles.TryGetValue(softwarePath, out var compile))
                        {
                            plc.LastCompile = compile;
                        }

                        context.Plcs.Add(plc);
                    }

                    return context;
                });
        }
    }

    public class SessionContext
    {
        public int? PortalId { get; set; }
        public bool IsConnected { get; set; }
        public string? ProjectName { get; set; }
        public string? ProjectPath { get; set; }
        public bool IsLocalSession { get; set; }
        public bool HasUnsavedChanges { get; set; }
        public DateTime? LastModifiedUtc { get; set; }
        public List<SessionPlc> Plcs { get; set; } = new List<SessionPlc>();
    }

    public class SessionPlc
    {
        public string SoftwarePath { get; set; } = string.Empty;
        public string? DeviceItemName { get; set; }
        /// <summary>Order number and firmware, e.g. 'OrderNumber:6ES7 215-1AG40-0XB0/V4.6'.</summary>
        public string? TypeIdentifier { get; set; }
        public string? OnlineState { get; set; }
        /// <summary>Result of the last CompilePlcSoftware through this server; null if none yet.</summary>
        public CompileSnapshot? LastCompile { get; set; }
        public string? Note { get; set; }
    }

    public class CompileSnapshot
    {
        public string State { get; set; } = string.Empty;
        public int ErrorCount { get; set; }
        public int WarningCount { get; set; }
        public DateTime AtUtc { get; set; }
    }
}
