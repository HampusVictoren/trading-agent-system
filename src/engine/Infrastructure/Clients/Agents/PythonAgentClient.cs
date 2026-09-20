namespace Engine.Infrastructure.Clients.Agents;

using System.Net.Http.Json;
using System.Text.Json;
using Engine.Application.Dtos;
using Engine.Application.Interfaces;
using Polly.Timeout;

public class PythonAgentClient : IAgentClient
{
    private readonly HttpClient _httpClient;
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true
    };

    public PythonAgentClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    /// <summary>
    /// Asks the agent service about one ticker. Transport failures are translated into the two
    /// exceptions the port declares, so that neither Polly nor HttpClient leaks into the
    /// application layer - and so that a timeout does not reach the worker as an unknown bug.
    /// </summary>
    public async Task<InvestmentProposalDto?> AnalyzeTickerAsync(string ticker, CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await _httpClient.PostAsync($"analyze/{ticker}", null, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                // Since stage 1 the agent service answers honestly: 502 and 504 mean the agents
                // failed, 503 that a backend is down. The engine treats them alike - no decision
                // this cycle - but the status code goes in the log, so the reason is not lost.
                throw new AgentServiceUnavailableException(
                    $"The agent service answered {(int)response.StatusCode} for {ticker}.");
            }

            return await response.Content.ReadFromJsonAsync<InvestmentProposalDto>(Options, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // We are shutting down, which is not a failure of the agent service.
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutRejectedException or OperationCanceledException)
        {
            // TimeoutRejectedException comes from the resilience pipeline and
            // TaskCanceledException from HttpClient itself; both mean "no answer in time".
            throw new AgentServiceUnavailableException($"The agent service did not answer for {ticker}.", ex);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            throw new AgentResponseInvalidException(
                $"The agent service answered for {ticker} with something other than the agreed JSON.", ex);
        }
    }
}
