using OpenTelemetry.Proto.Collector.Logs.V1;
using OpenTelemetry.Proto.Collector.Metrics.V1;
using OpenTelemetry.Proto.Collector.Trace.V1;

namespace Monitor.Web.Otlp;

public readonly record struct OtlpProcessingResult<TResponse>(bool Allowed, TResponse? Response)
    where TResponse : class;

public sealed class OtlpIngestionProcessor(
    OtlpComponentScopeValidator scopeValidator,
    OtlpTraceImporter traceImporter,
    OtlpLogImporter logImporter,
    OtlpMetricImporter metricImporter)
{
    public async Task<OtlpProcessingResult<ExportTraceServiceResponse>> ProcessAsync(
        ExportTraceServiceRequest request,
        Guid? authorizedComponentId,
        CancellationToken cancellationToken)
    {
        if (!await scopeValidator.CanIngestAsync(request, authorizedComponentId, cancellationToken))
        {
            return new(false, null);
        }

        var result = await traceImporter.ImportAsync(request, cancellationToken);
        var response = new ExportTraceServiceResponse();
        if (result.RejectedSpans > 0)
        {
            response.PartialSuccess = new ExportTracePartialSuccess
            {
                RejectedSpans = result.RejectedSpans,
                ErrorMessage = "Some spans were rejected because trace_id or span_id was invalid."
            };
        }

        return new(true, response);
    }

    public async Task<OtlpProcessingResult<ExportLogsServiceResponse>> ProcessAsync(
        ExportLogsServiceRequest request,
        Guid? authorizedComponentId,
        CancellationToken cancellationToken)
    {
        if (!await scopeValidator.CanIngestAsync(request, authorizedComponentId, cancellationToken))
        {
            return new(false, null);
        }

        var result = await logImporter.ImportAsync(request, cancellationToken);
        var response = new ExportLogsServiceResponse();
        if (result.RejectedLogs > 0)
        {
            response.PartialSuccess = new ExportLogsPartialSuccess
            {
                RejectedLogRecords = result.RejectedLogs,
                ErrorMessage = "Some log records could not be accepted."
            };
        }

        return new(true, response);
    }

    public async Task<OtlpProcessingResult<ExportMetricsServiceResponse>> ProcessAsync(
        ExportMetricsServiceRequest request,
        Guid? authorizedComponentId,
        CancellationToken cancellationToken)
    {
        if (!await scopeValidator.CanIngestAsync(request, authorizedComponentId, cancellationToken))
        {
            return new(false, null);
        }

        var result = await metricImporter.ImportAsync(request, cancellationToken);
        var response = new ExportMetricsServiceResponse();
        if (result.RejectedPoints > 0)
        {
            response.PartialSuccess = new ExportMetricsPartialSuccess
            {
                RejectedDataPoints = result.RejectedPoints,
                ErrorMessage = "Some metric data points were rejected because their timestamps, values, temporality, or distribution shape were invalid."
            };
        }

        return new(true, response);
    }
}
