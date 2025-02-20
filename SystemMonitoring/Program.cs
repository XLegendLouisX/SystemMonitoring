using Microsoft.Extensions.Hosting;
using SystemMonitoring;

public class Program
{
    public static void Main(string[] args)
    {
        CreateHostBuilder(args).Build().Run();
    }

    public static IHostBuilder CreateHostBuilder(string[] args) =>
        Host.CreateDefaultBuilder(args)
            .ConfigureServices((hostContext, services) =>
            {
                services.AddHostedService<Worker>();
            })
            .UseWindowsService(); // �o�@������ε{���@�� Windows �A�ȹB��
}