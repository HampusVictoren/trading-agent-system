namespace Engine.Hosting.Workers;

using Engine.Application.UseCases;
using Engine.Domain.Aggregates.Portfolio;
using Engine.Domain.ValueObjects;

public class TradingWorker : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<TradingWorker> _logger;

    public TradingWorker(IServiceProvider serviceProvider, ILogger<TradingWorker> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var portfolio = new Portfolio(new Money(10000m, "USD"));

        while (!stoppingToken.IsCancellationRequested)
        {
            using (var scope = _serviceProvider.CreateScope())
            {
                var useCase = scope.ServiceProvider.GetRequiredService<ProcessProposalUseCase>();

                try
                {
                    _logger.LogInformation("Begär analys för AAPL...");
                    await useCase.ExecuteAsync(portfolio, "AAPL", stoppingToken);
                    _logger.LogInformation("Kassa kvar i portföljen: ${Cash}", portfolio.CashBalance.Amount);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Fel vid anrop eller exekvering.");
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
        }
    }
}
