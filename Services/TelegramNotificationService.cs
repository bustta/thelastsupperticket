using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Telegram.Bot;
using Telegram.Bot.Types;

namespace TheLastSupperTicket.Services
{
    /// <summary>
    /// Configuration for Telegram notifications
    /// </summary>
    public class TelegramSettings
    {
        /// <summary>
        /// Telegram Bot Token (obtained from BotFather)
        /// Environment variable: TELEGRAM_BOT_TOKEN
        /// </summary>
        public string BotToken { get; set; } = "";

        /// <summary>
        /// User ID or Username list (multiple separated by comma)
        /// Environment variable: TELEGRAM_USER_IDS
        /// 
        /// Recommended to use numeric ID (more reliable)
        /// Numeric ID: "1234567890" or "123456789,987654321"
        /// 
        /// Username support (requires specific conditions):
        /// - "@username" format needs to be public username
        /// - Some bot settings do not support sending messages to username
        /// - If username fails, numeric ID is more reliable
        /// 
        /// Mixed usage: "1234567890,@publicusername"
        /// </summary>
        public string UserIds { get; set; } = "";

        /// <summary>
        /// Whether to enable Telegram notifications (default: false)
        /// Environment variable: TELEGRAM_ENABLED
        /// </summary>
        public bool IsEnabled { get; set; } = false;
    }

    public class TelegramNotificationService
    {
        private readonly ITelegramBotClient _botClient;
        private readonly List<string> _recipients; // Can be username or numeric ID
        private readonly TelegramSettings _settings;

        public TelegramNotificationService(TelegramSettings settings)
        {
            _settings = settings;
            _recipients = ParseRecipients(settings.UserIds);

            if (string.IsNullOrWhiteSpace(settings.BotToken))
            {
                throw new ArgumentException("Telegram Bot Token cannot be empty");
            }

            _botClient = new TelegramBotClient(settings.BotToken);
        }

        /// <summary>
        /// Parse recipient string into list (supports username and numeric ID)
        /// </summary>
        private List<string> ParseRecipients(string recipientString)
        {
            var recipients = new List<string>();
            if (string.IsNullOrWhiteSpace(recipientString))
            {
                return recipients;
            }

            var items = recipientString.Split(',');
            foreach (var item in items)
            {
                var trimmed = item.Trim();
                if (!string.IsNullOrEmpty(trimmed))
                {
                    recipients.Add(trimmed);
                }
            }

            return recipients;
        }

        /// <summary>
        /// Send notification when tickets are available
        /// </summary>
        public async Task SendAvailableTicketsNotificationAsync(List<string> availableDates, List<string> newAvailableDates, string targetUrl, string ticketType)
        {
            if (!_settings.IsEnabled)
            {
                Console.WriteLine("⚠ Telegram notification is disabled (IsEnabled = false)");
                return;
            }

            if (_recipients.Count == 0)
            {
                Console.WriteLine("⚠ No recipients configured, unable to send Telegram notification");
                Console.WriteLine($"  TELEGRAM_USER_IDS value: '{_settings.UserIds}'");
                return;
            }

            Console.WriteLine($"📱 Preparing to send Telegram notification to {_recipients.Count} recipient(s)...");
            foreach (var recipient in _recipients)
            {
                Console.WriteLine($"  - {recipient}");
            }

            try
            {
                var message = BuildTicketNotificationMessage(availableDates, newAvailableDates, targetUrl, ticketType);
                await SendMessageToRecipientsAsync(message);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"✗ Failed to send Telegram notification: {ex.Message}");
                Console.WriteLine($"  Stack trace: {ex.StackTrace}");
            }
        }

        /// <summary>
        /// Build ticket availability notification message
        /// </summary>
        private string BuildTicketNotificationMessage(List<string> availableDates, List<string> newAvailableDates, string targetUrl, string ticketType)
        {
            var ticketTypeLabel = string.IsNullOrWhiteSpace(ticketType) ? "Unknown ticket type" : ticketType.Trim();
            var newDatesList = FormatDatesGroupedByMonth(newAvailableDates, "  • No new dates");
            var allDatesList = FormatDatesGroupedByMonth(availableDates, "  • No available dates");

            var message = $@"🎉 *New Ticket Dates Available!*

🎟 Ticket type: *{ticketTypeLabel}*

🆕 New dates:
{newDatesList}

📅 Current available dates:
{allDatesList}

📌 Ticket page: {targetUrl}

Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}";

            return message;
        }

        /// <summary>
        /// Send message to all configured recipients
        /// </summary>
        private async Task SendMessageToRecipientsAsync(string message)
        {
            var tasks = new List<Task>();

            foreach (var recipient in _recipients)
            {
                tasks.Add(SendMessageAsync(recipient, message));
            }

            await Task.WhenAll(tasks);
        }

        /// <summary>
        /// Send message to single recipient (supports @username or numeric ID)
        /// </summary>
        private async Task SendMessageAsync(string recipient, string message)
        {
            try
            {
                ChatId chatId;
                if (recipient.StartsWith("@"))
                {
                    // Use username
                    chatId = new ChatId(recipient);
                    Console.WriteLine($"  📤 Sending message to Username: {recipient}");
                }
                else if (long.TryParse(recipient, out var userId))
                {
                    // Use numeric ID
                    chatId = new ChatId(userId);
                    Console.WriteLine($"  📤 Sending message to User ID: {userId}");
                }
                else
                {
                    // Try as username even without @ symbol
                    chatId = new ChatId(recipient);
                    Console.WriteLine($"  📤 Sending message to (Unknown format): {recipient}");
                }

                await _botClient.SendTextMessageAsync(
                    chatId: chatId,
                    text: message,
                    parseMode: Telegram.Bot.Types.Enums.ParseMode.Markdown
                );

                Console.WriteLine($"    ✓ Message sent successfully to {recipient}");
            }
            catch (Telegram.Bot.Exceptions.ApiRequestException apEx)
            {
                if (apEx.ErrorCode == 400 && apEx.Message.Contains("can't parse entities", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        Console.WriteLine("    ⚠ Markdown parse error detected, retrying with plain text...");

                        ChatId fallbackChatId;
                        if (recipient.StartsWith("@"))
                        {
                            fallbackChatId = new ChatId(recipient);
                        }
                        else if (long.TryParse(recipient, out var fallbackUserId))
                        {
                            fallbackChatId = new ChatId(fallbackUserId);
                        }
                        else
                        {
                            fallbackChatId = new ChatId(recipient);
                        }

                        await _botClient.SendTextMessageAsync(
                            chatId: fallbackChatId,
                            text: ToPlainTextMessage(message)
                        );

                        Console.WriteLine($"    ✓ Message sent successfully to {recipient} (plain text fallback)");
                        return;
                    }
                    catch (Exception fallbackEx)
                    {
                        Console.WriteLine($"    ✗ Plain text fallback failed: {fallbackEx.GetType().Name}: {fallbackEx.Message}");
                    }
                }

                Console.WriteLine($"    ✗ Telegram API error (Code: {apEx.ErrorCode}): {apEx.Message}");
                Console.WriteLine($"      Possible reasons:");
                if (apEx.ErrorCode == 403)
                {
                    Console.WriteLine($"      - Bot is blocked by user or group");
                    Console.WriteLine($"      - User has not started this bot");
                }
                else if (apEx.ErrorCode == 400)
                {
                    if (apEx.Message.Contains("chat not found") && recipient.StartsWith("@"))
                    {
                        Console.WriteLine($"      - Username '{recipient}' could not be recognized");
                        Console.WriteLine($"      💡 Recommendation: Using numeric ID instead of username is more reliable");
                        Console.WriteLine($"      Solution: Search for @userinfobot in Telegram to get numeric ID");
                    }
                    else
                    {
                        Console.WriteLine($"      - Username or ID format is incorrect");
                        Console.WriteLine($"      - Some bots don't support sending to usernames (use numeric ID)");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"    ✗ Unable to send message to {recipient}");
                Console.WriteLine($"      Error: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static string ToPlainTextMessage(string markdownMessage)
        {
            if (string.IsNullOrWhiteSpace(markdownMessage))
            {
                return string.Empty;
            }

            return markdownMessage
                .Replace("*", string.Empty)
                .Replace("_", string.Empty)
                .Replace("`", string.Empty);
        }

        /// <summary>
        /// Send test message
        /// </summary>
        public async Task SendTestMessageAsync()
        {
            if (!_settings.IsEnabled || _recipients.Count == 0)
            {
                Console.WriteLine("⚠ Telegram notification is not enabled or no recipients configured");
                return;
            }

            var testMessage = $@"🧪 *Telegram Notification Test*

This is a test message. If you received this message, it means Telegram notifications are configured successfully!

Test time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}";

            await SendMessageToRecipientsAsync(testMessage);
        }

        public async Task SendDailyAvailabilitySummaryAsync(Dictionary<string, List<string>> availableDatesByTicketType, DateTime italyTime)
        {
            if (!_settings.IsEnabled)
            {
                Console.WriteLine("⚠ Telegram notification is disabled (IsEnabled = false)");
                return;
            }

            if (_recipients.Count == 0)
            {
                Console.WriteLine("⚠ No recipients configured, unable to send Telegram notification");
                return;
            }

            var message = BuildDailyAvailabilitySummaryMessage(availableDatesByTicketType);
            await SendMessageToRecipientsAsync(message);
        }

        private string BuildDailyAvailabilitySummaryMessage(Dictionary<string, List<string>> availableDatesByTicketType)
        {
            var summaryDate = DateTime.Now.Date.AddDays(-1).ToString("yyyy-MM-dd");

            var sections = new List<string>();
            foreach (var entry in availableDatesByTicketType)
            {
                var ticketType = string.IsNullOrWhiteSpace(entry.Key) ? "Unknown ticket type" : entry.Key;
                var dates = entry.Value ?? new List<string>();
                var datesList = FormatDatesGroupedByMonth(dates, "  • No available dates");

                sections.Add($"🎟 {ticketType}:\n{datesList}");
            }

            if (sections.Count == 0)
            {
                sections.Add("🎟 All tickets:\n  • No available dates");
            }

            var availabilitySection = string.Join("\n\n", sections);

            return $@"🇮🇹 *Yesterday's Ticket Summary*

Date: {summaryDate}

{availabilitySection}

Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}";
        }

        private static string FormatDatesGroupedByMonth(List<string> dates, string emptyMessage)
        {
            var normalizedDates = (dates ?? new List<string>())
                .Where(date => !string.IsNullOrWhiteSpace(date))
                .Select(date => date.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(date => new
                {
                    RawDate = date,
                    SortKey = TryParseDate(date, out var parsedDate) ? parsedDate : DateTime.MaxValue,
                    MonthLabel = TryParseDate(date, out parsedDate)
                        ? parsedDate.ToString("MMMM yyyy", CultureInfo.InvariantCulture)
                        : "Other"
                })
                .OrderBy(item => item.SortKey)
                .ThenBy(item => item.RawDate, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (normalizedDates.Count == 0)
            {
                return emptyMessage;
            }

            var groupedLines = normalizedDates
                .GroupBy(item => item.MonthLabel)
                .Select(group => $"*{group.Key}*\n{string.Join("\n", group.Select(item => $"  • {item.RawDate}"))}")
                .ToList();

            return string.Join("\n", groupedLines);
        }

        private static bool TryParseDate(string rawDate, out DateTime parsedDate)
        {
            var formats = new[] { "d MMMM", "dd MMMM", "d MMM", "dd MMM" };

            return DateTime.TryParseExact(rawDate, formats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out parsedDate)
                || DateTime.TryParseExact(rawDate, formats, new CultureInfo("en-US"), DateTimeStyles.AllowWhiteSpaces, out parsedDate)
                || DateTime.TryParse(rawDate, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out parsedDate)
                || DateTime.TryParse(rawDate, new CultureInfo("en-US"), DateTimeStyles.AllowWhiteSpaces, out parsedDate);
        }
    }
}
