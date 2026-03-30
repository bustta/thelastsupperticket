using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Amazon.Lambda.Core;
using TheLastSupperTicket.Services;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace TheLastSupperTicket
{
    public class Function
    {
        // Configuration loaded from .env or environment variables
        private static readonly ConfigManager Config = ConfigManager.Instance;
        private static readonly TelegramSettings TelegramSettings = null!;
        private static readonly AvailabilityStateService AvailabilityStateService = null!;

        static Function()
        {
            // Initialize Telegram settings from configuration
            TelegramSettings = new TelegramSettings
            {
                BotToken = Config.TelegramBotToken,
                UserIds = Config.TelegramUserIds,
                IsEnabled = Config.TelegramEnabled
            };

            AvailabilityStateService = new AvailabilityStateService(Config.DynamoDbStateTable);

            Console.WriteLine("✓ Function configuration initialized");
        }

        public async Task<TicketAvailabilityResponse> FunctionHandler(ILambdaContext context)
        {
            context.Logger.LogLine("Starting ticket availability check...");
            context.Logger.LogLine($"📱 Telegram notification settings: IsEnabled={TelegramSettings.IsEnabled}, Recipients={TelegramSettings.UserIds}");
            context.Logger.LogLine($"🌐 Ticket targets count: {Config.TicketTargets.Count}");

            for (var index = 0; index < Config.TicketTargets.Count; index++)
            {
                var target = Config.TicketTargets[index];
                context.Logger.LogLine($"  [{index + 1}] {target.TicketType} => {target.Url}");
            }

            try
            {
                var state = await AvailabilityStateService.LoadStateAsync();
                var previousAvailableDates = NormalizeDates(state.AvailableDates);
                var previousAvailableDatesByTarget = state.AvailableDatesByTarget
                    .ToDictionary(
                        kvp => kvp.Key,
                        kvp => NormalizeDates(kvp.Value),
                        StringComparer.OrdinalIgnoreCase);

                if (previousAvailableDatesByTarget.Count == 0 && Config.TicketTargets.Count == 1 && previousAvailableDates.Count > 0)
                {
                    previousAvailableDatesByTarget[Config.TicketTargets[0].Url] = previousAvailableDates;
                }

                LogDateList(context.Logger, "📚 Previous available dates (all targets)", previousAvailableDates, "=");

                var targetDateKeys = ParseNotifyTargetDateKeys(Config.NotifyTargetDates, context.Logger);
                var hasTargetDateFilter = targetDateKeys.Count > 0;

                if (hasTargetDateFilter)
                {
                    context.Logger.LogLine($"🎯 Target date filter enabled ({targetDateKeys.Count} configured date(s))");
                }
                else if (!string.IsNullOrWhiteSpace(Config.NotifyTargetDates))
                {
                    context.Logger.LogLine("⚠ NOTIFY_TARGET_DATES is set but no valid date could be parsed; fallback to default notification logic");
                }

                var allCurrentAvailableDates = new List<string>();
                var allNewAvailableDates = new List<string>();
                var failedTargets = new List<string>();
                var firstSnapshotPath = string.Empty;
                var firstScreenshotPath = string.Empty;
                var successfulTargetChecks = 0;
                var italyNow = GetItalyNow();
                var italyToday = italyNow.ToString("yyyy-MM-dd");
                var italyYesterday = italyNow.Date.AddDays(-1).ToString("yyyy-MM-dd");

                foreach (var target in Config.TicketTargets)
                {
                    var targetUrl = target.Url;
                    context.Logger.LogLine($"\n🔎 Checking target: {targetUrl}");
                    var scraper = new TicketScraperService(targetUrl);
                    var result = await scraper.CheckTicketAvailabilityAsync();

                    if (!result.IsSuccessful)
                    {
                        context.Logger.LogLine($"✗ Check failed for target: {targetUrl}. Error: {result.ErrorMessage}");
                        failedTargets.Add(targetUrl);
                        continue;
                    }

                    successfulTargetChecks++;

                    if (string.IsNullOrWhiteSpace(firstSnapshotPath) && !string.IsNullOrWhiteSpace(result.SnapshotPath))
                    {
                        firstSnapshotPath = result.SnapshotPath;
                    }

                    if (string.IsNullOrWhiteSpace(firstScreenshotPath) && !string.IsNullOrWhiteSpace(result.ScreenshotPath))
                    {
                        firstScreenshotPath = result.ScreenshotPath;
                    }

                    var currentDatesForTarget = NormalizeDates(result.AvailableDates);
                    var previousDatesForTarget = previousAvailableDatesByTarget.TryGetValue(targetUrl, out var previousDates)
                        ? NormalizeDates(previousDates)
                        : new List<string>();

                    var newDatesForTarget = currentDatesForTarget
                        .Except(previousDatesForTarget, StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    var removedDatesForTarget = previousDatesForTarget
                        .Except(currentDatesForTarget, StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    previousAvailableDatesByTarget[targetUrl] = currentDatesForTarget;
                    MergeDailyObservedDatesByTarget(state, italyToday, targetUrl, currentDatesForTarget);
                    allCurrentAvailableDates.AddRange(currentDatesForTarget);
                    allNewAvailableDates.AddRange(newDatesForTarget);

                    LogDateComparison(context.Logger, $"target {targetUrl}", previousDatesForTarget, currentDatesForTarget, newDatesForTarget, removedDatesForTarget);

                    var notificationAvailableDates = hasTargetDateFilter
                        ? currentDatesForTarget.Where(date => IsDateMatchedByKey(date, targetDateKeys)).ToList()
                        : currentDatesForTarget;

                    var notificationNewAvailableDates = hasTargetDateFilter
                        ? newDatesForTarget.Where(date => IsDateMatchedByKey(date, targetDateKeys)).ToList()
                        : newDatesForTarget;

                    if (hasTargetDateFilter)
                    {
                        context.Logger.LogLine($"🎯 Target matched dates count for this URL: {notificationAvailableDates.Count}");
                        if (notificationNewAvailableDates.Count == 0)
                        {
                            context.Logger.LogLine("ℹ No new target matched dates for this URL");
                        }
                    }

                    if (TelegramSettings.IsEnabled && notificationNewAvailableDates.Count > 0)
                    {
                        try
                        {
                            context.Logger.LogLine("🔔 New dates found, attempting to send Telegram notification...");
                            var telegramService = new TelegramNotificationService(TelegramSettings);
                            await telegramService.SendAvailableTicketsNotificationAsync(currentDatesForTarget, notificationNewAvailableDates, targetUrl, target.TicketType);
                            context.Logger.LogLine("✓ Telegram notification sent");
                        }
                        catch (Exception ex)
                        {
                            context.Logger.LogLine($"⚠ Failed to send Telegram notification: {ex.Message}");
                            context.Logger.LogLine($"  Details: {ex.StackTrace}");
                        }
                    }
                    else if (TelegramSettings.IsEnabled)
                    {
                        context.Logger.LogLine("ℹ Skip Telegram notification for this URL (no new matched dates)");
                    }
                    else
                    {
                        context.Logger.LogLine("⚠ Telegram notification is disabled (IsEnabled=false)");
                    }
                }

                if (successfulTargetChecks == 0)
                {
                    var failedMessage = failedTargets.Count > 0
                        ? $"All target checks failed: {string.Join(" | ", failedTargets)}"
                        : "All target checks failed";

                    context.Logger.LogLine($"✗ {failedMessage}");
                    return new TicketAvailabilityResponse
                    {
                        StatusCode = 500,
                        Message = failedMessage,
                        HasAvailableDates = false,
                        AvailableDates = new List<string>()
                    };
                }

                var currentAvailableDates = NormalizeDates(allCurrentAvailableDates);
                var newAvailableDates = NormalizeDates(allNewAvailableDates);
                var removedAvailableDates = previousAvailableDates
                    .Except(currentAvailableDates, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                var isMorningSummaryWindow = italyNow.Hour == 8 && italyNow.Minute < 30;
                var shouldSendMorningSummary = isMorningSummaryWindow &&
                                               !string.Equals(state.LastMorningSummaryDate, italyToday, StringComparison.Ordinal);

                LogDateComparison(context.Logger, "all targets", previousAvailableDates, currentAvailableDates, newAvailableDates, removedAvailableDates);

                MergeDailyObservedDates(state, italyToday, currentAvailableDates);

                var yesterdaySummaryDatesByTicketType = BuildSummaryByTicketType(state, italyYesterday, Config.TicketTargets);

                if (shouldSendMorningSummary)
                {
                    context.Logger.LogLine($"🕗 Italy 08:00 summary window reached ({italyNow:yyyy-MM-dd HH:mm:ss}), will send previous-day summary ({italyYesterday})");
                }

                if (TelegramSettings.IsEnabled && shouldSendMorningSummary)
                {
                    try
                    {
                        context.Logger.LogLine($"📨 Sending daily 08:00 Italy summary for {italyYesterday}...");
                        var telegramService = new TelegramNotificationService(TelegramSettings);
                        await telegramService.SendDailyAvailabilitySummaryAsync(yesterdaySummaryDatesByTicketType, italyNow);
                        context.Logger.LogLine("✓ Daily 08:00 summary sent");
                        state.LastMorningSummaryDate = italyToday;
                    }
                    catch (Exception ex)
                    {
                        context.Logger.LogLine($"⚠ Failed to send daily 08:00 summary: {ex.Message}");
                        context.Logger.LogLine($"  Details: {ex.StackTrace}");
                    }
                }

                state.AvailableDates = currentAvailableDates;
                state.AvailableDatesByTarget = previousAvailableDatesByTarget;

                await AvailabilityStateService.SaveStateAsync(state);

                var partialFailureSuffix = failedTargets.Count > 0
                    ? $" (partial failures: {failedTargets.Count} target(s) failed)"
                    : string.Empty;

                if (currentAvailableDates.Count > 0)
                {
                    context.Logger.LogLine("✓ Found available ticket dates!");
                    context.Logger.LogLine($"Available dates count (all targets): {currentAvailableDates.Count}");
                    context.Logger.LogLine($"🆕 New available dates count (all targets): {newAvailableDates.Count}");

                    return new TicketAvailabilityResponse
                    {
                        StatusCode = 200,
                        Message = $"Found available ticket dates{partialFailureSuffix}",
                        HasAvailableDates = true,
                        AvailableDates = currentAvailableDates,
                        SnapshotPath = firstSnapshotPath,
                        ScreenshotPath = firstScreenshotPath
                    };
                }

                context.Logger.LogLine("✗ No available ticket dates at this time");
                return new TicketAvailabilityResponse
                {
                    StatusCode = 200,
                    Message = $"No available ticket dates found{partialFailureSuffix}",
                    HasAvailableDates = false,
                    AvailableDates = new List<string>(),
                    SnapshotPath = firstSnapshotPath,
                    ScreenshotPath = firstScreenshotPath
                };
            }
            catch (Exception ex)
            {
                context.Logger.LogLine($"✗ Exception error: {ex.Message}");
                return new TicketAvailabilityResponse
                {
                    StatusCode = 500,
                    Message = $"Exception error: {ex.Message}",
                    HasAvailableDates = false,
                    AvailableDates = new System.Collections.Generic.List<string>()
                };
            }
        }

        private static DateTime GetItalyNow()
        {
            var timeZone = TryGetTimeZone("Europe/Rome")
                           ?? TryGetTimeZone("W. Europe Standard Time")
                           ?? TimeZoneInfo.Utc;

            return TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, timeZone);
        }

        private static TimeZoneInfo? TryGetTimeZone(string timeZoneId)
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

        private static HashSet<string> ParseNotifyTargetDateKeys(string? rawConfig, ILambdaLogger logger)
        {
            var dateKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (string.IsNullOrWhiteSpace(rawConfig))
            {
                return dateKeys;
            }

            var tokens = rawConfig
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(token => token.Trim())
                .Where(token => !string.IsNullOrWhiteSpace(token));

            foreach (var token in tokens)
            {
                if (TryParseConfiguredDateKey(token, out var dateKey))
                {
                    dateKeys.Add(dateKey);
                }
                else
                {
                    logger.LogLine($"⚠ Unable to parse configured target date: '{token}'. Required format: dd/MM/yyyy (example: 09/04/2026)");
                }
            }

            return dateKeys;
        }

        private static bool IsDateMatchedByKey(string rawDate, HashSet<string> targetDateKeys)
        {
            if (targetDateKeys.Count == 0)
            {
                return true;
            }

            if (!TryParseScrapedDateKey(rawDate, out var dateKey))
            {
                return false;
            }

            return targetDateKeys.Contains(dateKey);
        }

        private static bool TryParseConfiguredDateKey(string value, out string dateKey)
        {
            if (DateTime.TryParseExact(value, "dd/MM/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var parsed))
            {
                dateKey = BuildDateKey(parsed.Day, parsed.Month);
                return true;
            }

            dateKey = string.Empty;
            return false;
        }

        private static bool TryParseScrapedDateKey(string value, out string dateKey)
        {
            var formats = new[] { "d MMMM", "dd MMMM", "d MMM", "dd MMM" };

            if (DateTime.TryParseExact(value, formats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var parsed)
                || DateTime.TryParseExact(value, formats, new CultureInfo("en-US"), DateTimeStyles.AllowWhiteSpaces, out parsed)
                || DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out parsed)
                || DateTime.TryParse(value, new CultureInfo("en-US"), DateTimeStyles.AllowWhiteSpaces, out parsed))
            {
                dateKey = BuildDateKey(parsed.Day, parsed.Month);
                return true;
            }

            dateKey = string.Empty;
            return false;
        }

        private static string BuildDateKey(int day, int month)
        {
            return $"{month:D2}-{day:D2}";
        }

        private static List<string> NormalizeDates(IEnumerable<string>? dates)
        {
            return (dates ?? Enumerable.Empty<string>())
                .Where(date => !string.IsNullOrWhiteSpace(date))
                .Select(date => date.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(date => date, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static void LogDateComparison(
            ILambdaLogger logger,
            string scope,
            List<string> previousDates,
            List<string> currentDates,
            List<string> newDates,
            List<string> removedDates)
        {
            logger.LogLine($"📊 Date summary for {scope}: previous={previousDates.Count}, current={currentDates.Count}, new={newDates.Count}, removed={removedDates.Count}");

            LogDateList(logger, "📚 Previous available dates", previousDates, "=");
            LogDateList(logger, currentDates.Count > 0 ? "✓ Current available dates" : "✗ Current available dates", currentDates, "-");
            LogDateList(logger, newDates.Count > 0 ? "🆕 New available dates" : "ℹ New available dates", newDates, "+");
            LogDateList(logger, removedDates.Count > 0 ? "🗑 Removed available dates" : "ℹ Removed available dates", removedDates, "x");
        }

        private static void LogDateList(ILambdaLogger logger, string label, List<string> dates, string marker)
        {
            if (dates.Count == 0)
            {
                logger.LogLine($"{label}: 0");
                return;
            }

            logger.LogLine($"{label}: {dates.Count}");
            foreach (var date in dates)
            {
                logger.LogLine($"  {marker} {date}");
            }
        }

        private static void MergeDailyObservedDates(AvailabilityStateService.AvailabilityState state, string italyDateKey, List<string> currentAvailableDates)
        {
            if (!state.DailyObservedDates.TryGetValue(italyDateKey, out var existingDates))
            {
                state.DailyObservedDates[italyDateKey] = currentAvailableDates
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(date => date, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                return;
            }

            state.DailyObservedDates[italyDateKey] = existingDates
                .Concat(currentAvailableDates)
                .Where(date => !string.IsNullOrWhiteSpace(date))
                .Select(date => date.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(date => date, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static void MergeDailyObservedDatesByTarget(AvailabilityStateService.AvailabilityState state, string italyDateKey, string targetUrl, List<string> currentAvailableDates)
        {
            if (!state.DailyObservedDatesByTarget.TryGetValue(italyDateKey, out var targetsForDay))
            {
                targetsForDay = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                state.DailyObservedDatesByTarget[italyDateKey] = targetsForDay;
            }

            if (!targetsForDay.TryGetValue(targetUrl, out var existingDates))
            {
                targetsForDay[targetUrl] = NormalizeDates(currentAvailableDates);
                return;
            }

            targetsForDay[targetUrl] = existingDates
                .Concat(currentAvailableDates)
                .Where(date => !string.IsNullOrWhiteSpace(date))
                .Select(date => date.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(date => date, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static Dictionary<string, List<string>> BuildSummaryByTicketType(AvailabilityStateService.AvailabilityState state, string italyDateKey, IReadOnlyList<TicketTarget> ticketTargets)
        {
            var summaryByTicketType = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            var ticketTypeByUrl = ticketTargets
                .Where(target => !string.IsNullOrWhiteSpace(target.Url))
                .GroupBy(target => target.Url.Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => group.First().TicketType,
                    StringComparer.OrdinalIgnoreCase);

            if (state.DailyObservedDatesByTarget.TryGetValue(italyDateKey, out var targetsForDay) && targetsForDay.Count > 0)
            {
                foreach (var entry in targetsForDay)
                {
                    var ticketType = ticketTypeByUrl.TryGetValue(entry.Key, out var configuredTicketType)
                        ? configuredTicketType
                        : $"Unknown ticket type ({entry.Key})";

                    summaryByTicketType[ticketType] = NormalizeDates(entry.Value);
                }

                return summaryByTicketType;
            }

            if (state.DailyObservedDates.TryGetValue(italyDateKey, out var legacyDates))
            {
                summaryByTicketType["All tickets"] = NormalizeDates(legacyDates);
            }

            return summaryByTicketType;
        }
    }

    public class TicketAvailabilityResponse
    {
        public int StatusCode { get; set; }
        public string Message { get; set; } = string.Empty;
        public bool HasAvailableDates { get; set; }
        public System.Collections.Generic.List<string> AvailableDates { get; set; } = new System.Collections.Generic.List<string>();
        public string? SnapshotPath { get; set; }
        public string? ScreenshotPath { get; set; }
    }
}
