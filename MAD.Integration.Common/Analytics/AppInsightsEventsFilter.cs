﻿using Hangfire;
using Hangfire.Common;
using Hangfire.Server;
using Hangfire.States;
using Hangfire.Storage;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.ApplicationInsights.Extensibility;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

namespace MAD.Integration.Common.Analytics
{
    public class AppInsightsEventsFilter : IServerFilter, IApplyStateFilter
    {
        [ThreadStatic]
        private static IOperationHolder<RequestTelemetry> operationHolder;

        private readonly TelemetryClient telemetryClient;

        public AppInsightsEventsFilter(TelemetryClient telemetryClient)
        {
            this.telemetryClient = telemetryClient;
        }

        private string GetJobName(BackgroundJob backgroundJob)
        {
            return $"{backgroundJob.Job.Type.Name}.{backgroundJob.Job.Method.Name}";
        }

        private string GetJobArguments(BackgroundJob backgroundJob)
        {
            return JsonConvert.SerializeObject(backgroundJob.Job.Args);
        }

        void IServerFilter.OnPerforming(PerformingContext filterContext)
        {
            operationHolder = this.telemetryClient.StartOperation<RequestTelemetry>(this.GetJobName(filterContext.BackgroundJob));
            operationHolder.Telemetry.Properties.Add("arguments", this.GetJobArguments(filterContext.BackgroundJob));
            operationHolder.Telemetry.Properties.Add("appName", Path.GetFileNameWithoutExtension(Globals.MainModule));

            var eventTelemetry = new EventTelemetry("Job Started");
            eventTelemetry.Context.Operation.Id = operationHolder.Telemetry.Context.Operation.Id;

            this.telemetryClient.TrackEvent(eventTelemetry);   
        }

        void IServerFilter.OnPerformed(PerformedContext filterContext)
        {
            var eventTelemetry = new EventTelemetry();
            eventTelemetry.Context.Operation.Id = operationHolder.Telemetry.Context.Operation.Id;

            if (filterContext.Exception != null)
            {
                var exception = filterContext.Exception;
                if (exception is JobPerformanceException perfEx)
                {
                    exception = perfEx.InnerException;
                }

                var exceptionTelemetry = new ExceptionTelemetry
                {
                    Exception = exception
                };
                
                exceptionTelemetry.Context.Operation.Id = operationHolder.Telemetry.Context.Operation.Id;

                this.telemetryClient.TrackException(exceptionTelemetry);
                operationHolder.Telemetry.Success = false;
                operationHolder.Telemetry.ResponseCode = "Attempt Failed";

                eventTelemetry.Name = "Job Attempt Failed";
            }
            else
            {
                operationHolder.Telemetry.Success = true;
                operationHolder.Telemetry.ResponseCode = "Success";
                eventTelemetry.Name = "Job Succeeded";
            }

            this.telemetryClient.TrackEvent(eventTelemetry);
        }

        public void OnStateApplied(ApplyStateContext context, IWriteOnlyTransaction transaction)
        {
            try
            {
                if (context.NewState.Name != FailedState.StateName) return;

                operationHolder.Telemetry.Success = false;
                operationHolder.Telemetry.ResponseCode = "Failed";

                var eventTelemetry = new EventTelemetry("Job Failed");
                eventTelemetry.Context.Operation.Id = operationHolder.Telemetry.Context.Operation.Id;

                this.telemetryClient.TrackEvent(eventTelemetry);
            }
            finally
            {
                if (operationHolder != null)
                {
                    this.telemetryClient.StopOperation(operationHolder);

                    operationHolder.Dispose();
                    operationHolder = null;
                }
            }  
        }

        public void OnStateUnapplied(ApplyStateContext context, IWriteOnlyTransaction transaction) { }
    }
}
