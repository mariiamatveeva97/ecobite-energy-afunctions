using System;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace EcoBite.Integrations;

public class SoapHealthCheck
{
    private static readonly HttpClient _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };

    private readonly ILogger _logger;
    private readonly IConfiguration _configuration;

    public SoapHealthCheck(ILoggerFactory loggerFactory, IConfiguration configuration)
    {
        _logger = loggerFactory.CreateLogger<SoapHealthCheck>();
        _configuration = configuration;
    }

    [Function("SoapHealthCheck")]
    public async Task Run([TimerTrigger("0 */15 * * * *")] TimerInfo myTimer)
    {
        var soapServiceUrl = _configuration["SoapServiceUrl"];

        if (string.IsNullOrEmpty(soapServiceUrl))
        {
            _logger.LogError("SoapServiceUrl app setting is not configured.");
            return;
        }

        var wsdlUrl = $"{soapServiceUrl}?wsdl";
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            var response = await _httpClient.GetAsync(wsdlUrl);
            stopwatch.Stop();

            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation(
                    "SOAP service is healthy. Status: {StatusCode}, Latency: {LatencyMs}ms",
                    (int)response.StatusCode, stopwatch.ElapsedMilliseconds);
            }
            else
            {
                _logger.LogError(
                    "SOAP service returned non-success status. Status: {StatusCode}, Latency: {LatencyMs}ms",
                    (int)response.StatusCode, stopwatch.ElapsedMilliseconds);
            }
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.LogError(
                "SOAP service health check failed after {LatencyMs}ms: {Message}",
                stopwatch.ElapsedMilliseconds, ex.Message);
        }
    }
}