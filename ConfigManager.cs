using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DotNetEnv;

namespace TheLastSupperTicket
{
    public class TicketTarget
    {
        public string TicketType { get; set; } = string.Empty;
        public string Url { get; set; } = string.Empty;
    }

    /// <summary>
    /// Configuration manager for loading environment variables from .env file or system environment
    /// </summary>
    public class ConfigManager
    {
        private static readonly Lazy<ConfigManager> _instance = new(() => new ConfigManager());
        public static ConfigManager Instance => _instance.Value;

        private const string DefaultMainTargetUrl = "https://cenacolovinciano.vivaticket.it/en/event/cenacolo-vinciano/151991";
        private const string DefaultEnglishGuidedTargetUrl = "https://cenacolovinciano.vivaticket.it/en/event/cenacolo-visite-guidate-a-orario-fisso-in-inglese/238363";
        private const string DefaultMainTicketType = "Ticket only";
        private const string DefaultEnglishGuidedTicketType = "Ticket + English guided tour";

        public string TargetUrl { get; private set; }
        public IReadOnlyList<string> TargetUrls { get; private set; }
        public IReadOnlyList<TicketTarget> TicketTargets { get; private set; }
        public string TelegramBotToken { get; private set; }
        public string TelegramUserIds { get; private set; }
        public bool TelegramEnabled { get; private set; }
        public string NotifyTargetDates { get; private set; }
        public string DynamoDbStateTable { get; private set; }

        private ConfigManager()
        {
            // Load .env file if it exists in current directory
            try
            {
                if (File.Exists(".env"))
                {
                    Console.WriteLine("📂 Loading configuration from .env file...");
                    Env.Load();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠ Warning: Could not load .env file: {ex.Message}");
            }

            // Load configuration from environment variables (with fallbacks)
            var configuredTargetPairs = Environment.GetEnvironmentVariable("TARGET_CONFIGS");
            TicketTargets = ParseTicketTargets(configuredTargetPairs);
            TargetUrls = TicketTargets.Select(target => target.Url).ToList();
            TargetUrl = TargetUrls.FirstOrDefault() ?? string.Empty;

            TelegramBotToken = Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN") ?? "";
            TelegramUserIds = Environment.GetEnvironmentVariable("TELEGRAM_USER_IDS") ?? "1234567890";
            
            if (!bool.TryParse(Environment.GetEnvironmentVariable("TELEGRAM_ENABLED"), out var enabled))
            {
                enabled = true; // Default to enabled
            }
            TelegramEnabled = enabled;

            NotifyTargetDates = Environment.GetEnvironmentVariable("NOTIFY_TARGET_DATES") ?? "";

            DynamoDbStateTable = Environment.GetEnvironmentVariable("DYNAMODB_STATE_TABLE") ?? "the-last-supper-ticket-state";

            // Log configuration (without exposing sensitive data)
            LogConfiguration();
        }

        private void LogConfiguration()
        {
            Console.WriteLine("\n=== Configuration Loaded ===");
            Console.WriteLine($"✓ TARGET_CONFIGS/TARGET_URLS: {TicketTargets.Count} configured target(s)");
            for (var index = 0; index < TicketTargets.Count; index++)
            {
                var target = TicketTargets[index];
                var targetUrl = target.Url;
                Console.WriteLine($"  [{index + 1}] {target.TicketType} => {(targetUrl.Length > 100 ? targetUrl.Substring(0, 100) + "..." : targetUrl)}");
            }
            Console.WriteLine($"✓ TELEGRAM_BOT_TOKEN: {(!string.IsNullOrEmpty(TelegramBotToken) ? TelegramBotToken.Substring(0, Math.Min(15, TelegramBotToken.Length)) + "..." : "NOT SET")}");
            Console.WriteLine($"✓ TELEGRAM_USER_IDS: {TelegramUserIds}");
            Console.WriteLine($"✓ TELEGRAM_ENABLED: {TelegramEnabled}");
            Console.WriteLine($"✓ NOTIFY_TARGET_DATES: {(string.IsNullOrWhiteSpace(NotifyTargetDates) ? "(not set)" : NotifyTargetDates)}");
            Console.WriteLine($"✓ DYNAMODB_STATE_TABLE: {DynamoDbStateTable}");
            Console.WriteLine("=============================\n");
        }

        private static IReadOnlyList<TicketTarget> ParseTicketTargets(string? rawTargetPairs)
        {
            var configuredTargets = ParseConfiguredTicketTargets(rawTargetPairs);
            if (configuredTargets.Count > 0)
            {
                return configuredTargets;
            }

            var configuredTargetUrls = Environment.GetEnvironmentVariable("TARGET_URLS");
            if (string.IsNullOrWhiteSpace(configuredTargetUrls))
            {
                configuredTargetUrls = Environment.GetEnvironmentVariable("TARGET_URL");
            }

            var fallbackUrls = (configuredTargetUrls ?? string.Empty)
                .Split(new[] { ',', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(url => url.Trim())
                .Where(url => !string.IsNullOrWhiteSpace(url))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (fallbackUrls.Count > 0)
            {
                return fallbackUrls
                    .Select((url, index) => new TicketTarget
                    {
                        TicketType = $"Target {index + 1}",
                        Url = url
                    })
                    .ToList();
            }

            return new List<TicketTarget>
            {
                new TicketTarget
                {
                    TicketType = DefaultMainTicketType,
                    Url = DefaultMainTargetUrl
                },
                new TicketTarget
                {
                    TicketType = DefaultEnglishGuidedTicketType,
                    Url = DefaultEnglishGuidedTargetUrl
                }
            };
        }

        private static List<TicketTarget> ParseConfiguredTicketTargets(string? rawTargetPairs)
        {
            if (string.IsNullOrWhiteSpace(rawTargetPairs))
            {
                return new List<TicketTarget>();
            }

            var entries = rawTargetPairs
                .Split(new[] { ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(entry => entry.Trim())
                .Where(entry => !string.IsNullOrWhiteSpace(entry));

            var targets = new List<TicketTarget>();
            var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var entry in entries)
            {
                var separatorIndex = entry.IndexOf('|');
                if (separatorIndex <= 0 || separatorIndex >= entry.Length - 1)
                {
                    continue;
                }

                var ticketType = entry.Substring(0, separatorIndex).Trim();
                var url = entry.Substring(separatorIndex + 1).Trim();

                if (string.IsNullOrWhiteSpace(ticketType) || string.IsNullOrWhiteSpace(url))
                {
                    continue;
                }

                if (!seenUrls.Add(url))
                {
                    continue;
                }

                targets.Add(new TicketTarget
                {
                    TicketType = ticketType,
                    Url = url
                });
            }

            return targets;
        }

        /// <summary>
        /// Validate required configuration
        /// </summary>
        public bool ValidateRequired()
        {
            var isValid = true;

            if (TicketTargets.Count == 0)
            {
                Console.WriteLine("✗ ERROR: TARGET_CONFIGS / TARGET_URLS / TARGET_URL is not configured");
                isValid = false;
            }

            if (TelegramEnabled && string.IsNullOrWhiteSpace(TelegramBotToken))
            {
                Console.WriteLine("✗ ERROR: TELEGRAM_BOT_TOKEN is not configured but Telegram is enabled");
                isValid = false;
            }

            if (string.IsNullOrWhiteSpace(DynamoDbStateTable))
            {
                Console.WriteLine("✗ ERROR: DYNAMODB_STATE_TABLE is not configured");
                isValid = false;
            }

            return isValid;
        }
    }
}
