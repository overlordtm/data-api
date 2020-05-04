﻿using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;
using Prometheus;
using SloCovidServer.Services.Abstract;
using System;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SloCovidServer.Controllers
{
    public abstract class MetricsController<TLogger> : ControllerBase
        where TLogger : ControllerBase
    {
        protected static readonly Histogram RequestDuration = Metrics.CreateHistogram("rest_request_duration_milliseconds", 
            "REST request duration",
            new HistogramConfiguration
            {
                Buckets = Histogram.ExponentialBuckets(start: 20, factor: 2, count: 10),
                LabelNames =  new[] { "endpoint", "has_etag", "is_exception" }
            });
        protected static readonly Counter RequestCount = Metrics.CreateCounter("rest_request_total", "Total number of REST requests",
            new CounterConfiguration
            {
                LabelNames = new[] { "endpoint", "has_etag" }
            });
        protected static readonly Counter RequestMissedCache = Metrics.CreateCounter("rest_request_missed_cache_total", "Total number of returning values",
            new CounterConfiguration
            {
                LabelNames = new[] { "endpoint" }
            });
        protected static readonly Counter RequestExceptions = Metrics.CreateCounter("rest_request_exceptions_total", "Total number of returning values",
            new CounterConfiguration
            {
                LabelNames = new[] { "endpoint", "has_etag" }
            });

        protected readonly string endpointName;
        protected ILogger<TLogger> logger;
        protected ICommunicator communicator;
        public MetricsController(ILogger<TLogger> logger, ICommunicator communicator)
        {
            this.logger = logger;
            this.communicator = communicator;
            endpointName = GetType().Name.Replace("Controller", "").ToLower();
        }
        protected async Task<ActionResult<T?>> ProcessRequestAsync<T>(
            Func<string, CancellationToken, Task<(T? Data, string ETag, long? Timestamp)>> retrieval, string endpointName = null)
            where T : struct
        {
            var stopwatch = Stopwatch.StartNew();
            string etag = null;

            if (Request.Headers.TryGetValue(HeaderNames.IfNoneMatch, out var etagValues))
            {
                etag = etagValues.SingleOrDefault() ?? "";
            }
            bool hasETag = !string.IsNullOrEmpty(etag);
            bool exceptionOccured = false;
            try
            {
                if (endpointName == null)
                {
                    endpointName = this.endpointName;
                }
                RequestCount.WithLabels(endpointName, hasETag.ToString()).Inc();
                var result = await retrieval(etag, CancellationToken.None);
                Response.Headers[HeaderNames.ETag] = result.ETag;
                if (result.Timestamp.HasValue)
                {
                    Response.Headers["Timestamp"] = result.Timestamp.Value.ToString();
                }
                if (result.Data.HasValue)
                {
                    if (hasETag)
                    {
                        RequestMissedCache.WithLabels(endpointName).Inc();
                    }
                    return Ok(result.Data);
                }
                else
                {
                    return StatusCode(304);
                }
            }
            catch
            {
                RequestExceptions.WithLabels(endpointName, hasETag.ToString()).Inc();
                exceptionOccured = true;
                throw;
            }
            finally
            {
                RequestDuration.WithLabels(endpointName, hasETag.ToString(), exceptionOccured.ToString()).Observe(stopwatch.Elapsed.Milliseconds);
            }
        }
    }
}
