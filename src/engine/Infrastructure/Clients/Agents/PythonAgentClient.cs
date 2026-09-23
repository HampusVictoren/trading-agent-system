namespace Engine.Infrastructure.Clients.Agents;

using System.Net.Http.Json;
using System.Text.Json;
using Engine.Application.Contracts;
using Engine.Application.Interfaces;
using Polly.Timeout;

public class PythonAgentClient : IAgentClient
{
    /// <summary>The agent service echoes this and puts it in every log line it writes.</summary>
    public const string CorrelationIdHeader = "X-Correlation-Id";

    private const string SignalsPath = "v1/signals";

    private readonly HttpClient _httpClient;

    public PythonAgentClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    /// <summary>
    /// Transport failures are translated into the two exceptions the port declares, so that
    /// neither Polly nor HttpClient leaks into the application layer - and so that a timeout
    /// does not reach the worker as an unknown bug.
    /// </summary>
    public async Task<TradeSignalDto?> GetSignalAsync(
        TradeSignalRequestDto request, CancellationToken cancellationToken = default)
    {
        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, SignalsPath)
            {
                // The same options object the contract test reads the examples with, so what
                // goes out on the wire is what that test proves the contract accepts.
                Content = JsonContent.Create(request, options: ContractSerialization.Options)
            };

            // Set from the request rather than passed separately, so no call path can send a
            // body with one id and a header with another - or forget the header entirely.
            message.Headers.Add(CorrelationIdHeader, request.CorrelationId);

            var response = await _httpClient.SendAsync(message, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                // The agent service answers honestly: 422 means the request was wrong, 502
                // and 504 that the agents failed, 503 that a backend is down. The engine
                // treats them alike - no decision this cycle - but the status goes in the
                // log, so the reason is not lost.
                throw new AgentServiceUnavailableException(
                    $"The agent service answered {(int)response.StatusCode} for {Describe(request)}.");
            }

            return await response.Content.ReadFromJsonAsync<TradeSignalDto>(
                ContractSerialization.Options, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // We are shutting down, which is not a failure of the agent service.
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutRejectedException or OperationCanceledException)
        {
            // TimeoutRejectedException comes from the resilience pipeline and
            // TaskCanceledException from HttpClient itself; both mean "no answer in time".
            throw new AgentServiceUnavailableException(
                $"The agent service did not answer for {Describe(request)}.", ex);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            throw new AgentResponseInvalidException(
                $"The agent service answered for {Describe(request)} with something other than the agreed JSON.", ex);
        }
    }

    private static string Describe(TradeSignalRequestDto request) =>
        request.Instrument is EquityInstrumentDto equity ? equity.Symbol : request.Instrument.GetType().Name;
}
