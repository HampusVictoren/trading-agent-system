namespace Engine.Infrastructure.Clients.Agents;

using System.Net.Http.Json;
using System.Text.Json;
using Engine.Application.Dtos;
using Engine.Application.Interfaces;

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

    public async Task<InvestmentProposalDto?> AnalyzeTickerAsync(string ticker, CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.PostAsync($"analyze/{ticker}", null, cancellationToken);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<InvestmentProposalDto>(Options, cancellationToken);
    }
}
