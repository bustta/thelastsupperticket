using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using TheLastSupperTicket;
using TheLastSupperTicket.Services;

class Program
{
    static async Task Main(string[] args)
    {
        Console.WriteLine("=== Local Ticket Scraper Test ===\n");

        // Support command line arguments
        if (args.Length > 0 && args[0].ToLower() == "telegram-test")
        {
            await TestTelegramNotification();
            return;
        }

        if (args.Length > 0 && args[0].ToLower() == "telegram-summary-test")
        {
            await TestDailySummaryNotification();
            return;
        }

        var lambdaFunction = new Function();
        var mockContext = new MockLambdaContext();

        try
        {
            var result = await lambdaFunction.FunctionHandler(mockContext);
            
            Console.WriteLine("\n=== Results ===");
            Console.WriteLine($"Status Code: {result.StatusCode}");
            Console.WriteLine($"Message: {result.Message}");
            Console.WriteLine($"Has Available Dates: {result.HasAvailableDates}");
            
            if (!string.IsNullOrEmpty(result.SnapshotPath))
            {
                Console.WriteLine($"HTML Snapshot: {result.SnapshotPath}");
            }
            
            if (!string.IsNullOrEmpty(result.ScreenshotPath))
            {
                Console.WriteLine($"Screenshot: {result.ScreenshotPath}");
            }
            
            if (result.HasAvailableDates && result.AvailableDates.Count > 0)
            {
                Console.WriteLine("\nAvailable Dates:");
                foreach (var date in result.AvailableDates)
                {
                    Console.WriteLine($"  - {date}");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex.Message}");
            Console.WriteLine($"Stack Trace: {ex.StackTrace}");
        }
    }

    /// <summary>
    /// Test Telegram notification functionality
    /// Usage: dotnet run -- telegram-test
    /// </summary>
    static async Task TestTelegramNotification()
    {
        Console.WriteLine("🧪 Telegram Notification Test Mode\n");
        Console.WriteLine("Loading environment configuration...");

        var config = ConfigManager.Instance;

        Console.WriteLine($"✓ Bot Token: {(config.TelegramBotToken.Length > 0 ? config.TelegramBotToken.Substring(0, 10) + "..." : "Not set")}");
        Console.WriteLine($"✓ Recipients: {config.TelegramUserIds}");
        Console.WriteLine($"✓ Enabled: {config.TelegramEnabled}\n");

        try
        {
            var settings = new TelegramSettings
            {
                BotToken = config.TelegramBotToken,
                UserIds = config.TelegramUserIds,
                IsEnabled = config.TelegramEnabled
            };

            var telegramService = new TelegramNotificationService(settings);
            
            Console.WriteLine("📤 Sending test message...\n");
            await telegramService.SendTestMessageAsync();
            
            Console.WriteLine("\n✅ Test completed!");
            Console.WriteLine("If no errors shown, message should have been sent successfully.");
            Console.WriteLine("Check your Telegram private chat or make sure the bot is started.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Test failed: {ex.Message}");
            Console.WriteLine($"Stack Trace: {ex.StackTrace}");
        }
    }

    /// <summary>
    /// Test daily 08:00 Italy summary notification format
    /// Usage: dotnet run -- telegram-summary-test
    /// </summary>
    static async Task TestDailySummaryNotification()
    {
        Console.WriteLine("🧪 Daily 08:00 Italy Summary Test Mode\n");
        Console.WriteLine("Loading environment configuration...");

        var config = ConfigManager.Instance;

        Console.WriteLine($"✓ Recipients: {config.TelegramUserIds}");
        Console.WriteLine($"✓ Enabled: {config.TelegramEnabled}\n");

        try
        {
            var settings = new TelegramSettings
            {
                BotToken = config.TelegramBotToken,
                UserIds = config.TelegramUserIds,
                IsEnabled = config.TelegramEnabled
            };

            var telegramService = new TelegramNotificationService(settings);

            var italyTimeZone = TryGetTimeZone("Europe/Rome") ?? TryGetTimeZone("W. Europe Standard Time") ?? TimeZoneInfo.Utc;
            var italyNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, italyTimeZone);
            var italyEightAm = new DateTime(italyNow.Year, italyNow.Month, italyNow.Day, 8, 0, 0);

            var sampleDatesByTicketType = new Dictionary<string, List<string>>
            {
                ["Ticket only"] = new List<string> { "13 March", "15 March" },
                ["Ticket + English guided tour"] = new List<string> { "8 March" }
            };

            Console.WriteLine("📤 Sending daily 08:00 summary test message...\n");
            await telegramService.SendDailyAvailabilitySummaryAsync(sampleDatesByTicketType, italyEightAm);

            Console.WriteLine("\n✅ Summary test completed!");
            Console.WriteLine("You should receive the same format used by the daily 08:00 (Italy time) summary.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Summary test failed: {ex.Message}");
            Console.WriteLine($"Stack Trace: {ex.StackTrace}");
        }
    }

    static TimeZoneInfo? TryGetTimeZone(string timeZoneId)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch
        {
            return null;
        }
    }
}

// Mock Lambda context for local testing
public class MockLambdaContext : Amazon.Lambda.Core.ILambdaContext
{
    public string AwsRequestId => "local-request-id";
    public Amazon.Lambda.Core.IClientContext? ClientContext => null;
    public Amazon.Lambda.Core.ICognitoIdentity? Identity => null;
    public Amazon.Lambda.Core.ILambdaLogger Logger => new MockLambdaLogger();
    public string FunctionName => "TicketScraper-Local";
    public string FunctionVersion => "$LATEST";
    public string InvokedFunctionArn => "arn:aws:lambda:local-region:123456789012:function:TicketScraper-Local";
    public int MemoryLimitInMB => 128;
    public TimeSpan RemainingTime => TimeSpan.FromMinutes(5);
    public string LogGroupName => "/aws/lambda/TicketScraper-Local";
    public string LogStreamName => "local-log-stream";
}

// Memory limit info for mock context
public class MockMemoryLimit
{
    public int MemoryLimitInMB => 128;
}

public class MockLambdaLogger : Amazon.Lambda.Core.ILambdaLogger
{
    public void Log(string message)
    {
        Console.WriteLine(message);
    }

    public void LogLine(string message)
    {
        Console.WriteLine(message);
    }
}
