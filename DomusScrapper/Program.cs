// Program.cs (DomusScrapper)
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Playwright;

namespace DomusScrapper
{
    internal static class Program
    {
        // Choose your default rotation order here
        private static readonly string[] DefaultSports = new[]
        {
            "Calcio", "Pallacanestro", "Tennis", "Rugby",
            "american-football", "baseball", "ice-hockey"
        };

        public static async Task Main(string[] args)
        {
            // Resolve sports list (comma-separated arg to override)
            var sports = (args?.Length > 0 && !string.IsNullOrWhiteSpace(args[0]))
                ? args[0].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                : DefaultSports;

            Console.WriteLine("[Startup] Domus sports queue: " + string.Join(", ", sports));
            Console.WriteLine("Press Ctrl+C to stop.\n");

            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (s, e) =>
            {
                e.Cancel = true;
                cts.Cancel();
                Console.WriteLine("\nStopping after current task...");
            };

            using var playwright = await Playwright.CreateAsync();
            var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = false,
                SlowMo   = 0,
                Devtools = false
            });

            try
            {
                while (!cts.IsCancellationRequested)
                {
                    foreach (var sport in sports)
                    {
                        if (cts.IsCancellationRequested) break;

                        Console.WriteLine($"\n==== [{DateTime.Now:HH:mm:ss}] DOMUS START {sport} ====");

                        await using var context = await browser.NewContextAsync(new()
                        {
                            // add persistent locale/timezone if needed
                        });

                        var page = await context.NewPageAsync();

                        try
                        {
                            await DomusScraper.RunSinglePageAsync(page, sport);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[ERROR][Domus] {sport}: {ex.Message}");
                        }
                        finally
                        {
                            await context.CloseAsync();
                        }

                        Console.WriteLine($"==== [{DateTime.Now:HH:mm:ss}] DOMUS END {sport} ====\n");
                    }
                }
            }
            finally
            {
                await browser.CloseAsync();
            }

            Console.WriteLine("Domus loop stopped. Browser closed.");
        }
    }
}
