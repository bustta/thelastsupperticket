using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;

namespace TheLastSupperTicket.Services
{
    public class AvailabilityStateService
    {
        private const string StateKeyAttribute = "StateKey";
        private const string AvailableDatesAttribute = "AvailableDates";
        private const string AvailableDatesByTargetAttribute = "AvailableDatesByTarget";
        private const string DailyObservedDatesAttribute = "DailyObservedDates";
        private const string DailyObservedDatesByTargetAttribute = "DailyObservedDatesByTarget";
        private const string LastUpdatedUtcAttribute = "LastUpdatedUtc";
        private const string LastMorningSummaryDateAttribute = "LastMorningSummaryDate";
        private const string FixedStateKey = "ticket-availability";
        private const int DailyObservedDatesRetentionDays = 14;
        private readonly string _tableName;
        private readonly IAmazonDynamoDB _dynamoDbClient;

        public AvailabilityStateService(string tableName)
        {
            _tableName = tableName;
            _dynamoDbClient = new AmazonDynamoDBClient();
        }

        public async Task<List<string>> LoadAvailableDatesAsync()
        {
            var state = await LoadStateAsync();
            return state.AvailableDates;
        }

        public async Task<AvailabilityState> LoadStateAsync()
        {
            try
            {
                var request = new GetItemRequest
                {
                    TableName = _tableName,
                    Key = new Dictionary<string, AttributeValue>
                    {
                        [StateKeyAttribute] = new AttributeValue { S = FixedStateKey }
                    },
                    ConsistentRead = true
                };

                var response = await _dynamoDbClient.GetItemAsync(request);

                if (response.Item == null || response.Item.Count == 0)
                {
                    return new AvailabilityState();
                }

                response.Item.TryGetValue(AvailableDatesAttribute, out var availableDatesValue);

                var rawDates = availableDatesValue?.L ?? new List<AttributeValue>();

                string? lastMorningSummaryDate = null;
                if (response.Item.TryGetValue(LastMorningSummaryDateAttribute, out var morningSummaryDateValue))
                {
                    lastMorningSummaryDate = morningSummaryDateValue.S;
                }

                var availableDatesByTarget = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                if (response.Item.TryGetValue(AvailableDatesByTargetAttribute, out var availableDatesByTargetValue) && availableDatesByTargetValue.M != null)
                {
                    foreach (var entry in availableDatesByTargetValue.M)
                    {
                        var normalizedTargetDates = (entry.Value?.L ?? new List<AttributeValue>())
                            .Select(item => item.S)
                            .Where(date => !string.IsNullOrWhiteSpace(date))
                            .Select(date => date.Trim())
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .OrderBy(date => date, StringComparer.OrdinalIgnoreCase)
                            .ToList();

                        availableDatesByTarget[entry.Key] = normalizedTargetDates;
                    }
                }

                var dailyObservedDates = new Dictionary<string, List<string>>(StringComparer.Ordinal);
                if (response.Item.TryGetValue(DailyObservedDatesAttribute, out var dailyObservedDatesValue) && dailyObservedDatesValue.M != null)
                {
                    foreach (var entry in dailyObservedDatesValue.M)
                    {
                        var normalizedForDay = (entry.Value?.L ?? new List<AttributeValue>())
                            .Select(item => item.S)
                            .Where(date => !string.IsNullOrWhiteSpace(date))
                            .Select(date => date.Trim())
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .OrderBy(date => date, StringComparer.OrdinalIgnoreCase)
                            .ToList();

                        dailyObservedDates[entry.Key] = normalizedForDay;
                    }
                }

                var dailyObservedDatesByTarget = new Dictionary<string, Dictionary<string, List<string>>>(StringComparer.Ordinal);
                if (response.Item.TryGetValue(DailyObservedDatesByTargetAttribute, out var dailyObservedDatesByTargetValue) && dailyObservedDatesByTargetValue.M != null)
                {
                    foreach (var dayEntry in dailyObservedDatesByTargetValue.M)
                    {
                        var targetsForDay = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                        var targetMap = dayEntry.Value?.M;
                        if (targetMap != null)
                        {
                            foreach (var targetEntry in targetMap)
                            {
                                var normalizedTargetDates = (targetEntry.Value?.L ?? new List<AttributeValue>())
                                    .Select(item => item.S)
                                    .Where(date => !string.IsNullOrWhiteSpace(date))
                                    .Select(date => date.Trim())
                                    .Distinct(StringComparer.OrdinalIgnoreCase)
                                    .OrderBy(date => date, StringComparer.OrdinalIgnoreCase)
                                    .ToList();

                                targetsForDay[targetEntry.Key] = normalizedTargetDates;
                            }
                        }

                        dailyObservedDatesByTarget[dayEntry.Key] = targetsForDay;
                    }
                }

                var availableDates = rawDates
                    .Select(item => item.S)
                    .Where(date => !string.IsNullOrWhiteSpace(date))
                    .Select(date => date.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(date => date, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                return new AvailabilityState
                {
                    AvailableDates = availableDates,
                    AvailableDatesByTarget = availableDatesByTarget,
                    LastMorningSummaryDate = lastMorningSummaryDate,
                    DailyObservedDates = dailyObservedDates,
                    DailyObservedDatesByTarget = dailyObservedDatesByTarget
                };
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠ Failed to load availability state from DynamoDB: {ex.Message}");
                return new AvailabilityState();
            }
        }

        public async Task SaveAvailableDatesAsync(List<string> availableDates)
        {
            await SaveStateAsync(new AvailabilityState { AvailableDates = availableDates });
        }

        public async Task SaveStateAsync(AvailabilityState state)
        {
            try
            {
                var normalizedDates = state.AvailableDates
                    .Where(date => !string.IsNullOrWhiteSpace(date))
                    .Select(date => date.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(date => date, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                var item = new Dictionary<string, AttributeValue>
                {
                    [StateKeyAttribute] = new AttributeValue { S = FixedStateKey },
                    [LastUpdatedUtcAttribute] = new AttributeValue { S = DateTime.UtcNow.ToString("O") },
                    [AvailableDatesAttribute] = new AttributeValue
                    {
                        L = normalizedDates.Select(date => new AttributeValue { S = date }).ToList()
                    }
                };

                var normalizedAvailableDatesByTarget = state.AvailableDatesByTarget
                    .Where(kvp => !string.IsNullOrWhiteSpace(kvp.Key))
                    .Select(kvp => new
                    {
                        TargetUrl = kvp.Key.Trim(),
                        Dates = (kvp.Value ?? new List<string>())
                            .Where(date => !string.IsNullOrWhiteSpace(date))
                            .Select(date => date.Trim())
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .OrderBy(date => date, StringComparer.OrdinalIgnoreCase)
                            .ToList()
                    })
                    .ToDictionary(
                        item => item.TargetUrl,
                        item => new AttributeValue
                        {
                            L = item.Dates.Select(date => new AttributeValue { S = date }).ToList()
                        },
                        StringComparer.OrdinalIgnoreCase);

                item[AvailableDatesByTargetAttribute] = new AttributeValue
                {
                    M = normalizedAvailableDatesByTarget
                };

                var normalizedDailyObservedDates = state.DailyObservedDates
                    .Where(kvp => !string.IsNullOrWhiteSpace(kvp.Key))
                    .Select(kvp => new
                    {
                        DateKey = kvp.Key.Trim(),
                        Dates = (kvp.Value ?? new List<string>())
                            .Where(date => !string.IsNullOrWhiteSpace(date))
                            .Select(date => date.Trim())
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .OrderBy(date => date, StringComparer.OrdinalIgnoreCase)
                            .ToList()
                    })
                    .OrderByDescending(item => item.DateKey, StringComparer.Ordinal)
                    .Take(DailyObservedDatesRetentionDays)
                    .ToDictionary(
                        item => item.DateKey,
                        item => new AttributeValue
                        {
                            L = item.Dates.Select(date => new AttributeValue { S = date }).ToList()
                        },
                        StringComparer.Ordinal);

                item[DailyObservedDatesAttribute] = new AttributeValue
                {
                    M = normalizedDailyObservedDates
                };

                var normalizedDailyObservedDatesByTarget = state.DailyObservedDatesByTarget
                    .Where(kvp => !string.IsNullOrWhiteSpace(kvp.Key))
                    .Select(kvp => new
                    {
                        DateKey = kvp.Key.Trim(),
                        Targets = (kvp.Value ?? new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase))
                            .Where(target => !string.IsNullOrWhiteSpace(target.Key))
                            .Select(target => new
                            {
                                TargetUrl = target.Key.Trim(),
                                Dates = (target.Value ?? new List<string>())
                                    .Where(date => !string.IsNullOrWhiteSpace(date))
                                    .Select(date => date.Trim())
                                    .Distinct(StringComparer.OrdinalIgnoreCase)
                                    .OrderBy(date => date, StringComparer.OrdinalIgnoreCase)
                                    .ToList()
                            })
                            .ToDictionary(
                                target => target.TargetUrl,
                                target => new AttributeValue
                                {
                                    L = target.Dates.Select(date => new AttributeValue { S = date }).ToList()
                                },
                                StringComparer.OrdinalIgnoreCase)
                    })
                    .OrderByDescending(item => item.DateKey, StringComparer.Ordinal)
                    .Take(DailyObservedDatesRetentionDays)
                    .ToDictionary(
                        item => item.DateKey,
                        item => new AttributeValue
                        {
                            M = item.Targets
                        },
                        StringComparer.Ordinal);

                item[DailyObservedDatesByTargetAttribute] = new AttributeValue
                {
                    M = normalizedDailyObservedDatesByTarget
                };

                if (!string.IsNullOrWhiteSpace(state.LastMorningSummaryDate))
                {
                    item[LastMorningSummaryDateAttribute] = new AttributeValue { S = state.LastMorningSummaryDate };
                }

                var request = new PutItemRequest
                {
                    TableName = _tableName,
                    Item = item
                };

                await _dynamoDbClient.PutItemAsync(request);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠ Failed to save availability state to DynamoDB: {ex.Message}");
            }
        }

        public class AvailabilityState
        {
            public List<string> AvailableDates { get; set; } = new List<string>();
            public Dictionary<string, List<string>> AvailableDatesByTarget { get; set; } = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            public string? LastMorningSummaryDate { get; set; }
            public Dictionary<string, List<string>> DailyObservedDates { get; set; } = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            public Dictionary<string, Dictionary<string, List<string>>> DailyObservedDatesByTarget { get; set; } = new Dictionary<string, Dictionary<string, List<string>>>(StringComparer.Ordinal);
        }
    }
}