using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using System.Xml.Linq;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace EcoBite.Integrations;

public class CalculateEnergyRequest
{
    [JsonPropertyName("appliance")]
    public string Appliance { get; set; } = string.Empty;

    [JsonPropertyName("timeMinutes")]
    public int TimeMinutes { get; set; }
}

public class CalculateEnergyResponse
{
    [JsonPropertyName("kwh")]
    public double Kwh { get; set; }

    [JsonPropertyName("co2Kg")]
    public double Co2Kg { get; set; }

    [JsonPropertyName("costEur")]
    public double CostEur { get; set; }
}

public class CalculateEnergy
{
    private const string SoapNamespace = "ecobite.energy.soap";
    private const string SoapEnvelopeNamespace = "http://schemas.xmlsoap.org/soap/envelope/";

    private static readonly HttpClient _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

    private readonly ILogger<CalculateEnergy> _logger;
    private readonly IConfiguration _configuration;


    public CalculateEnergy(ILogger<CalculateEnergy> logger, IConfiguration configuration)
    {
        _logger = logger;
        _configuration = configuration;
    }

    [Function("CalculateEnergy")]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequest req)
    {
        CalculateEnergyRequest? requestBody;
        try
        {
            using var reader = new StreamReader(req.Body);
            var bodyText = await reader.ReadToEndAsync();
            requestBody = JsonSerializer.Deserialize<CalculateEnergyRequest>(bodyText);
        }
        catch (JsonException ex)
        {
            _logger.LogError("Invalid request JSON: {Message}", ex.Message);
            return new BadRequestObjectResult(new { error = "Invalid JSON in request body." });
        }

        if (requestBody is null || string.IsNullOrEmpty(requestBody.Appliance) || requestBody.TimeMinutes <= 0)
        {
            return new BadRequestObjectResult(new { error = "Request must include 'appliance' (string) and 'timeMinutes' (positive integer)." });
        }

        var soapServiceUrl = _configuration["SoapServiceUrl"];
        if (string.IsNullOrEmpty(soapServiceUrl))
        {
            _logger.LogError("SoapServiceUrl app setting is not configured.");
            return new ObjectResult(new { error = "Service misconfigured." }) { StatusCode = 502 };
        }

        try
        {
            var soapRequestXml = BuildSoapRequest(requestBody.Appliance, requestBody.TimeMinutes);

            using var content = new StringContent(soapRequestXml, Encoding.UTF8, "text/xml");
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/xml")
            {
                CharSet = "utf-8"
            };
            
            var response = await _httpClient.PostAsync(soapServiceUrl, content);
            var responseXml = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("SOAP service returned HTTP {StatusCode}", (int)response.StatusCode);
                return new ObjectResult(new { error = "SOAP service returned an error." }) { StatusCode = 502 };
            }

            var result = ParseSoapResponse(responseXml);
            if (result is null)
            {
                _logger.LogError("Could not parse SOAP response or SOAP Fault present.");
                return new ObjectResult(new { error = "Invalid or faulty SOAP response." }) { StatusCode = 502 };
            }

            return new OkObjectResult(result);
        }
        catch (TaskCanceledException)
        {
            _logger.LogError("SOAP service request timed out.");
            return new ObjectResult(new { error = "SOAP service timed out." }) { StatusCode = 502 };
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError("SOAP service request failed: {Message}", ex.Message);
            return new ObjectResult(new { error = "SOAP service request failed." }) { StatusCode = 502 };
        }
    }

    private static string BuildSoapRequest(string appliance, int timeMinutes)
    {
        XNamespace soapenv = SoapEnvelopeNamespace;
        XNamespace spy = SoapNamespace;

        var envelope = new XElement(soapenv + "Envelope",
            new XAttribute(XNamespace.Xmlns + "soapenv", SoapEnvelopeNamespace),
            new XAttribute(XNamespace.Xmlns + "spy", SoapNamespace),
            new XElement(soapenv + "Header"),
            new XElement(soapenv + "Body",
                new XElement(spy + "calculate_energy",
                    new XElement(spy + "appliance", appliance),
                    new XElement(spy + "time_minutes", timeMinutes)
                )
            )
        );

        return new XDeclaration("1.0", "UTF-8", null) + envelope.ToString();
    }

    private CalculateEnergyResponse? ParseSoapResponse(string xml)
    {
        try
        {
            var doc = XDocument.Parse(xml);

            var fault = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Fault");
            if (fault is not null)
            {
                var faultString = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "faultstring")?.Value;
                _logger.LogError("SOAP Fault received: {FaultString}", faultString ?? "Unknown");
                return null;
            }

            var kwhText = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "kwh")?.Value;
            var co2Text = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "co2_kg")?.Value;
            var costText = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "cost_eur")?.Value;

            if (kwhText is null || co2Text is null || costText is null)
            {
                _logger.LogError("SOAP response missing expected fields.");
                return null;
            }

            return new CalculateEnergyResponse
            {
                Kwh = double.Parse(kwhText, System.Globalization.CultureInfo.InvariantCulture),
                Co2Kg = double.Parse(co2Text, System.Globalization.CultureInfo.InvariantCulture),
                CostEur = double.Parse(costText, System.Globalization.CultureInfo.InvariantCulture)
            };
        }
        catch (Exception ex)
        {
            _logger.LogError("Failed to parse SOAP XML response: {Message}", ex.Message);
            return null;
        }
    }
}