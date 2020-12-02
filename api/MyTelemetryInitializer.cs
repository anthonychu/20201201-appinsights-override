using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.ApplicationInsights.Channel;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.ApplicationInsights.Extensibility.Implementation;
using Microsoft.Azure.WebJobs.Logging;
using Microsoft.Extensions.Logging;

namespace Company.Function
{
    internal class MyWebJobsTelemetryInitializer : ITelemetryInitializer
    {
        private static readonly string _currentProcessId = Process.GetCurrentProcess().Id.ToString();
        private readonly string _sdkVersion;
        private readonly string _roleInstanceName;

        public MyWebJobsTelemetryInitializer()
        {

            _sdkVersion = "0.0.0";
            _roleInstanceName = new WebJobsRoleInstanceProvider().GetRoleInstanceName();
        }

        public void Initialize(ITelemetry telemetry)
        {
            if (telemetry == null)
            {
                return;
            }

            var telemetryContext = telemetry.Context;
            telemetryContext.Cloud.RoleInstance = _roleInstanceName;
            if (telemetryContext.Location.Ip == null)
            {
                telemetryContext.Location.Ip = "0.0.0.0";
            }

            IDictionary<string, string> telemetryProps = telemetryContext.Properties;
            telemetryProps["ProcessId"] = _currentProcessId;

            // Apply our special scope properties
            IDictionary<string, object> scopeProps = null;
            string invocationId = scopeProps?[ScopeKeys.FunctionInvocationId] as string;
            if (invocationId != null)
            {
                telemetryProps[LogConstants.InvocationIdKey] = invocationId;
            }

            // this could be telemetry tracked in scope of function call - then we should apply the logger scope
            // or RequestTelemetry tracked by the WebJobs SDK or AppInsight SDK - then we should apply Activity.Tags
            if (scopeProps != null && scopeProps.Count > 0)
            {
                telemetryContext.Operation.Name = scopeProps[ScopeKeys.FunctionName] as string;

                // Apply Category and LogLevel to all telemetry
                string category = scopeProps[LogConstants.CategoryNameKey] as string;
                if (category != null)
                {
                    telemetryProps[LogConstants.CategoryNameKey] = category;
                }

                object logLevel = scopeProps[LogConstants.LogLevelKey];
                if (logLevel != null)
                {
                    telemetryProps[LogConstants.LogLevelKey] = logLevel.ToString();
                }

                object eventId = scopeProps[LogConstants.EventIdKey];
                if (eventId != null && Convert.ToInt16(eventId) != 0)
                {
                    telemetryProps[LogConstants.EventIdKey] = eventId.ToString();
                }

                string eventName = scopeProps[LogConstants.EventNameKey] as string;
                if (eventName != null)
                {
                    telemetryProps[LogConstants.EventNameKey] = eventName;
                }
            }

            // we may track traces/dependencies after function scope ends - we don't want to update those
            RequestTelemetry request = telemetry as RequestTelemetry;
            if (request != null)
            {
                UpdateRequestProperties(request);

                Activity currentActivity = Activity.Current;
                if (currentActivity != null)
                {
                    foreach (var tag in currentActivity.Tags)
                    {
                        // Apply well-known tags and custom properties, 
                        // but ignore internal ai tags
                        if (!TryApplyProperty(request, tag) &&
                            !tag.Key.StartsWith("ai_"))
                        {
                            request.Properties[tag.Key] = tag.Value;
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Changes properties of the RequestTelemetry to match what Functions expects.
        /// </summary>
        /// <param name="request">The RequestTelemetry to update.</param>
        private void UpdateRequestProperties(RequestTelemetry request)
        {
            request.Context.GetInternalContext().SdkVersion = _sdkVersion;

            // If the code hasn't been set, it's not an HttpRequest (could be auto-tracked SB, etc).
            // So we'll initialize it to 0
            if (string.IsNullOrEmpty(request.ResponseCode))
            {
                request.ResponseCode = "0";
            }

            // If the Url is not null, it's an actual HttpRequest, as opposed to a
            // Service Bus or other function invocation that we're tracking as a Request
            if (request.Url != null)
            {
                var builder = new UriBuilder(request.Url.AbsoluteUri);
                builder.Host = "hello-world";
                request.Url = builder.Uri;
                if (!request.Properties.ContainsKey(LogConstants.HttpMethodKey))
                {
                    // App Insights sets request.Name as 'VERB /path'. We want to extract the VERB. 
                    var verbEnd = request.Name.IndexOf(' ');
                    if (verbEnd > 0)
                    {
                        request.Properties.Add(LogConstants.HttpMethodKey, request.Name.Substring(0, verbEnd));
                    }
                }

                if (!request.Properties.ContainsKey(LogConstants.HttpPathKey))
                {
                    request.Properties.Add(LogConstants.HttpPathKey, request.Url.LocalPath);
                }

                // sanitize request Url - remove query string
                request.Url = new Uri(request.Url.GetLeftPart(UriPartial.Path));
            }
        }

        /// <summary>
        /// Tries to apply well-known properties from a KeyValuePair onto the RequestTelemetry.
        /// </summary>
        /// <param name="request">The request.</param>
        /// <param name="activityTag">Tag on the request activity.</param>
        /// <returns>True if the tag was applied. Otherwise, false.</returns>
        private bool TryApplyProperty(RequestTelemetry request, KeyValuePair<string, string> activityTag)
        {
            bool wasPropertySet = false;

            if (activityTag.Key == LogConstants.NameKey)
            {
                request.Context.Operation.Name = activityTag.Value;
                request.Name = activityTag.Value;

                wasPropertySet = true;
            }
            else if (activityTag.Key == LogConstants.SucceededKey &&
                bool.TryParse(activityTag.Value, out bool success))
            {
                // no matter what App Insights says about the response, we always
                // want to use the function's result for Succeeded
                request.Success = success;
                wasPropertySet = true;

                // Remove the Succeeded property if set
                if (request.Properties.ContainsKey(LogConstants.SucceededKey))
                {
                    request.Properties.Remove(LogConstants.SucceededKey);
                }
            }
            else if (activityTag.Key == "ClientIp")
            {
                request.Context.Location.Ip = activityTag.Value;
                wasPropertySet = true;
            }

            return wasPropertySet;
        }
    }

    internal class WebJobsRoleInstanceProvider
    {
        internal const string ComputerNameKey = "COMPUTERNAME";
        internal const string WebSiteInstanceIdKey = "WEBSITE_INSTANCE_ID";
        internal const string ContainerNameKey = "CONTAINER_NAME";

        private readonly string _roleInstanceName = GetRoleInstance();

        public string GetRoleInstanceName()
        {
            return _roleInstanceName;
        }

        private static string GetRoleInstance()
        {
            string instanceName = Environment.GetEnvironmentVariable(WebSiteInstanceIdKey);
            if (string.IsNullOrEmpty(instanceName))
            {
                instanceName = Environment.GetEnvironmentVariable(ComputerNameKey);
                if (string.IsNullOrEmpty(instanceName))
                {
                    instanceName = Environment.GetEnvironmentVariable(ContainerNameKey);
                }
            }

            return instanceName;
        }
    }
}