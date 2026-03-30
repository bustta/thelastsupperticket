using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using HtmlAgilityPack;
using Microsoft.Playwright;

namespace TheLastSupperTicket.Services
{
    public class TicketScraperService
    {
        private const int MaxExecutionSeconds = 90;
        private readonly HttpClient _httpClient;
        private readonly string _targetUrl;
        private readonly string _snapshotDir;

        public TicketScraperService(string targetUrl, string? snapshotDir = null)
        {
            _targetUrl = targetUrl;
            
            // Use /tmp in AWS Lambda; use ./snapshots for local development
            if (snapshotDir != null)
            {
                _snapshotDir = snapshotDir;
            }
            else if (IsRunningInLambda())
            {
                _snapshotDir = Path.Combine("/tmp", "snapshots");
            }
            else
            {
                _snapshotDir = Path.Combine(Directory.GetCurrentDirectory(), "snapshots");
            }
            
            // Create the snapshot directory if needed
            if (!Directory.Exists(_snapshotDir))
            {
                Directory.CreateDirectory(_snapshotDir);
            }
            
            _httpClient = new HttpClient();
            _httpClient.Timeout = TimeSpan.FromSeconds(10); // Set 10-second timeout
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
        }

        private static bool IsRunningInLambda()
        {
            return !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("AWS_LAMBDA_FUNCTION_NAME"));
        }

        private static string? GetChromeExecutablePath()
        {
            var taskRoot = GetLambdaTaskRoot();
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var playwrightBrowsersPath = Environment.GetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH");

            var possiblePaths = new List<string>
            {
                // Fixed paths (backward compatible with older deployments)
                Path.Combine(taskRoot, ".playwright", "chrome-linux", "chrome"),
                Path.Combine(taskRoot, "node_modules", "@sparticuz", "chromium-sharp", "bin", "chromium"),
                "/opt/chrome/chrome",
                "/opt/python/bin/chrome",
                "/opt/bin/chromium"
            };

            // Playwright may be installed in custom or default cache paths; revision varies by version (chromium-xxxx)
            var browserRoots = new List<string>();
            if (!string.IsNullOrWhiteSpace(playwrightBrowsersPath))
            {
                browserRoots.Add(playwrightBrowsersPath);
            }

            browserRoots.Add(Path.Combine(taskRoot, ".cache", "ms-playwright"));
            browserRoots.Add(Path.Combine(userProfile, ".cache", "ms-playwright"));

            foreach (var root in browserRoots.Distinct().Where(Directory.Exists))
            {
                var chromiumDirs = Directory.GetDirectories(root, "chromium-*", SearchOption.TopDirectoryOnly)
                    .OrderByDescending(x => x, StringComparer.Ordinal);

                foreach (var chromiumDir in chromiumDirs)
                {
                    possiblePaths.Add(Path.Combine(chromiumDir, "chrome-linux", "chrome"));
                }
            }

            return possiblePaths.FirstOrDefault(File.Exists);
        }

        private static string GetLambdaTaskRoot()
        {
            return Environment.GetEnvironmentVariable("LAMBDA_TASK_ROOT") ?? 
                   Environment.GetEnvironmentVariable("PWD") ?? 
                   Directory.GetCurrentDirectory();
        }

        private static async Task<string> GetDisplayedMonthNameAsync(IPage page)
        {
            return await page.EvaluateAsync<string>(
                @"() => {
                    try {
                        const nameEl = document.querySelector('.calendar .name, [class*=calendar] .name');
                        return nameEl ? nameEl.textContent.trim() : 'Unknown Month';
                    } catch (e) {
                        return 'Unknown Month';
                    }
                }"
            ) ?? "Unknown Month";
        }

        private static async Task<bool> TryAdvanceToNextMonthAsync(IPage page, string currentMonthName)
        {
            const string nextButtonSelector = ".calendar li.next, .calendar [class*='next'], [class*='calendar'] li.next, [class*='calendar'] [class*='next']";

            var navigationState = await page.EvaluateAsync<string>(
                @"selector => {
                    const nextContainers = Array.from(document.querySelectorAll(selector));
                    if (nextContainers.length === 0) {
                        return 'missing';
                    }

                    const getInteractiveElement = container => {
                        return container.matches('a, button')
                            ? container
                            : container.querySelector('a, button, [role=button]');
                    };

                    const enabledElement = nextContainers
                        .map(container => ({
                            container,
                            interactive: getInteractiveElement(container)
                        }))
                        .find(entry => {
                            const target = entry.interactive || entry.container;
                            const ariaDisabled = (target.getAttribute('aria-disabled') || entry.container.getAttribute('aria-disabled') || '').toLowerCase();
                            const className = `${entry.container.className || ''} ${target.className || ''}`.toLowerCase();
                            const style = window.getComputedStyle(target);
                            const hasDisabledAttribute = target.hasAttribute('disabled') || entry.container.hasAttribute('disabled');
                            const isDisabled = hasDisabledAttribute
                                || ariaDisabled === 'true'
                                || className.includes('disabled')
                                || className.includes('inactive');
                            const isHidden = style.display === 'none'
                                || style.visibility === 'hidden'
                                || style.pointerEvents === 'none';

                            return !isDisabled && !isHidden && !!entry.interactive;
                        });

                    if (!enabledElement) {
                        return 'disabled';
                    }

                    enabledElement.interactive.click();
                    return 'clicked';
                }",
                nextButtonSelector);

            if (string.Equals(navigationState, "missing", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("  ⚠️ Next button not found, stopping scan");
                return false;
            }

            if (string.Equals(navigationState, "disabled", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("  ⚠️ Next button is disabled, stopping scan");
                return false;
            }

            Console.WriteLine("  ✓ Successfully clicked next button");

            for (var attempt = 0; attempt < 10; attempt++)
            {
                await page.WaitForTimeoutAsync(300);
                var updatedMonthName = await GetDisplayedMonthNameAsync(page);

                if (!string.Equals(updatedMonthName, currentMonthName, StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine($"  → Advanced to month: {updatedMonthName}");
                    return true;
                }
            }

            Console.WriteLine($"  ⚠️ Next button clicked but month did not change from '{currentMonthName}', stopping scan");
            return false;
        }

        public async Task<TicketAvailabilityResult> CheckTicketAvailabilityAsync()
        {
            try
            {
                var deadlineUtc = DateTime.UtcNow.AddSeconds(MaxExecutionSeconds);

                // Use Playwright to extract dates and capture screenshots
                var playwright = await Playwright.CreateAsync();
                
                var launchOptions = new BrowserTypeLaunchOptions
                {
                    Headless = true
                };
                
                // In Lambda, configure the executable path correctly
                if (IsRunningInLambda())
                {
                    launchOptions.Args = new[]
                    {
                        "--no-sandbox",
                        "--disable-setuid-sandbox",
                        "--disable-dev-shm-usage",
                        "--disable-gpu",
                        "--no-zygote",
                        "--single-process"
                    };

                    var executablePath = GetChromeExecutablePath();
                    if (!string.IsNullOrEmpty(executablePath))
                    {
                        launchOptions.ExecutablePath = executablePath;
                        Console.WriteLine($"✓ Using Chrome executable: {executablePath}");
                    }
                    else
                    {
                        Console.WriteLine("⚠ Chrome executable not found, attempting auto-discovery");
                    }
                }
                
                var browser = await playwright.Chromium.LaunchAsync(launchOptions);
                
                var page = await browser.NewPageAsync();
                await page.GotoAsync(_targetUrl, new PageGotoOptions
                {
                    WaitUntil = WaitUntilState.DOMContentLoaded,
                    Timeout = 45000
                });
                
                // Avoid infinite wait due to long-lived connections; wait up to 5 seconds for NetworkIdle
                try
                {
                    await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new PageWaitForLoadStateOptions { Timeout = 5000 });
                }
                catch
                {
                    Console.WriteLine("⚠ Waiting for NetworkIdle timed out, continuing with parsing");
                }
                
                // Check whether the page is redirected to /queue/
                var currentUrl = page.Url;
                if (currentUrl.Contains("/queue/"))
                {
                    Console.WriteLine("⏳ Queue page detected, clicking button to proceed to ticket page...");
                    
                    try
                    {
                        // Find the button in MainPart_divWarningBox (anchor or button element)
                        var warningBox = await page.QuerySelectorAsync("#MainPart_divWarningBox");
                        if (warningBox != null)
                        {
                            // Find a clickable element: <button>, <a>, or elements with button role
                            var button = await warningBox.QuerySelectorAsync("button, a, [role='button']");
                            if (button != null)
                            {
                                await button.ClickAsync();
                                Console.WriteLine("✓ Clicked button in warning box");
                                
                                // Wait for navigation to complete
                                try
                                {
                                    await page.WaitForURLAsync(url => !url.Contains("/queue/"), new PageWaitForURLOptions { Timeout = 10000 });
                                }
                                catch
                                {
                                    // If URL wait times out, continue with load state checks
                                }
                                
                                try
                                {
                                    await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new PageWaitForLoadStateOptions { Timeout = 5000 });
                                }
                                catch
                                {
                                    Console.WriteLine("⚠ Post-redirect NetworkIdle wait timed out, continuing with parsing");
                                }
                                Console.WriteLine($"✓ Successfully redirected to: {page.Url}");
                            }
                            else
                            {
                                Console.WriteLine("⚠ No button found in warning box");
                            }
                        }
                        else
                        {
                            Console.WriteLine("⚠ Warning box element not found");
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"⚠ Failed to handle queue page: {ex.Message}");
                    }
                }
                
                // Extract available dates from the calendar
                Console.WriteLine("📅 Starting calendar scan...");
                var calendarDates = await ExtractCalendarDatesFromPageAsync(page, deadlineUtc);
                
                string snapshotPath = "";
                string screenshotPath = "";
                
                // Do not persist files in Lambda environment
                if (!IsRunningInLambda())
                {
                    // Save HTML snapshot
                    var content = await page.ContentAsync();
                    snapshotPath = SaveSnapshot(content);
                    Console.WriteLine($"✓ HTML snapshot saved: {snapshotPath}");
                    
                    // Save JPG screenshot
                    var bodyHeight = await page.EvaluateAsync<int>("() => document.body.scrollHeight");
                    await page.SetViewportSizeAsync(1920, bodyHeight + 100);
                    
                    var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
                    var screenshotFileName = $"screenshot_{timestamp}.jpg";
                    screenshotPath = Path.Combine(_snapshotDir, screenshotFileName);
                    
                    await page.ScreenshotAsync(new PageScreenshotOptions
                    {
                        Path = screenshotPath,
                        FullPage = true,
                        Type = ScreenshotType.Jpeg,
                        Quality = 90
                    });
                    
                    Console.WriteLine($"✓ JPG screenshot saved: {screenshotPath}");
                }
                else
                {
                    Console.WriteLine("⏩ Running in AWS Lambda environment, skipping file persistence");
                }
                
                await browser.CloseAsync();
                
                // Build result object
                var result = new TicketAvailabilityResult
                {
                    IsSuccessful = true,
                    AvailableDates = calendarDates,
                    HasAvailableDates = calendarDates.Count > 0,
                    SnapshotPath = snapshotPath,
                    ScreenshotPath = screenshotPath
                };

                return result;
            }
            catch (TimeoutException ex)
            {
                Console.WriteLine($"⏱ Scraper execution timed out (>{MaxExecutionSeconds}s): {ex.Message}");
                return new TicketAvailabilityResult
                {
                    IsSuccessful = false,
                    ErrorMessage = $"Execution timeout after {MaxExecutionSeconds} seconds",
                    AvailableDates = new List<string>()
                };
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: Unable to fetch webpage content - {ex.Message}");
                return new TicketAvailabilityResult
                {
                    IsSuccessful = false,
                    ErrorMessage = ex.Message,
                    AvailableDates = new List<string>()
                };
            }
        }

        private string SaveSnapshot(string htmlContent)
        {
            // Use timestamp as file name: yyyyMMdd_HHmmss_fff
            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
            var snapshotFileName = $"snapshot_{timestamp}.html";
            var snapshotPath = Path.Combine(_snapshotDir, snapshotFileName);

            File.WriteAllText(snapshotPath, htmlContent);
            return snapshotPath;
        }

        private async Task<List<string>> ExtractCalendarDatesFromPageAsync(IPage page, DateTime deadlineUtc)
        {
            var allDates = new List<string>();

            void ThrowIfExecutionTimedOut()
            {
                if (DateTime.UtcNow > deadlineUtc)
                {
                    throw new TimeoutException("Calendar extraction exceeded allowed execution time");
                }
            }
            
            try
            {
                ThrowIfExecutionTimedOut();

                // First, try locating any calendar container/date picker
                var dateSelectors = new[] { ".calendar", "[class*='calendar']", "[class*='date-picker']", "[class*='datepicker']" };
                object? calendarElement = null;
                
                foreach (var selector in dateSelectors)
                {
                    calendarElement = await page.QuerySelectorAsync(selector);
                    if (calendarElement != null)
                    {
                        Console.WriteLine($"✓ Calendar element found: {selector}");
                        break;
                    }
                }
                
                if (calendarElement == null)
                {
                    Console.WriteLine("⚠ Calendar element not found");
                    return allDates;
                }
                
                // Extract six months (current, next, and the following months)
                for (int monthIndex = 0; monthIndex < 6; monthIndex++)
                {
                    ThrowIfExecutionTimedOut();

                    try
                    {
                        Console.WriteLine($"\n📅 Processing month {monthIndex + 1}...");
                        
                        // Extract month name first
                        string monthName;
                        try
                        {
                            monthName = await GetDisplayedMonthNameAsync(page);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"  ❌ Failed to extract month name: {ex.Message}");
                            monthName = "Unknown Month";
                        }
                        
                        Console.WriteLine($"  📅 Month name: {monthName}");
                        
                        // Extract available dates from calendar
                        List<string> dates = new List<string>();
                        try
                        {
                            var result = await page.EvaluateAsync<string>(
                            @"() => {
                                try {
                                    const result = [];
                                    const seen = new Set();
                                    
                                    // Locate calendar container
                                    const calendar = document.querySelector('.calendar, [class*=calendar]');
                                    if (!calendar) {
                                        return JSON.stringify({ success: false, dates: [], debugInfo: 'No calendar found', dayCount: 0 });
                                    }
                                    
                                    // Try multiple selectors
                                    let dayElements = calendar.querySelectorAll('ul.days li.day');
                                    let selector = 'ul.days li.day';
                                    
                                    if (dayElements.length === 0) {
                                        dayElements = calendar.querySelectorAll('.days li.day');
                                        selector = '.days li.day';
                                    }
                                    
                                    if (dayElements.length === 0) {
                                        dayElements = calendar.querySelectorAll('li.day');
                                        selector = 'li.day';
                                    }
                                    
                                    let debugInfo = `Found ${dayElements.length} day elements with selector: ${selector}`;
                                    
                                    // Count key CSS classes for diagnostics
                                    let inactiveCount = 0, noEventCount = 0, validCount = 0;
                                    
                                    dayElements.forEach((el, idx) => {
                                        try {
                                            if (el.classList.contains('inactive')) {
                                                inactiveCount++;
                                            }
                                            if (el.classList.contains('no-event')) {
                                                noEventCount++;
                                            }
                                        } catch (e) {}
                                    });
                                    
                                    debugInfo += ` | inactive: ${inactiveCount}, no-event: ${noEventCount}`;
                                    
                                    dayElements.forEach((el, idx) => {
                                        try {
                                            // Extract date text
                                            let dateText = '';
                                            
                                            // Try direct text first (e.g., <li>1</li>)
                                            const text = el.innerText || el.textContent || '';
                                            if (text.trim().match(/^\d{1,2}$/)) {
                                                dateText = text.trim();
                                            }
                                            
                                            // If not found, try inside <a> tag (e.g., <li><a>13</a></li>)
                                            if (!dateText) {
                                                const link = el.querySelector('a');
                                                if (link) {
                                                    const linkText = (link.innerText || link.textContent || '').trim();
                                                    if (linkText.match(/^\d{1,2}$/)) {
                                                        dateText = linkText;
                                                    }
                                                }
                                            }
                                            
                                            // If valid and not already added
                                            if (dateText && !seen.has(dateText) && el.offsetHeight > 0) {
                                                // Ignore dates marked as inactive or no-event
                                                const isInvalid = el.classList.contains('inactive') || el.classList.contains('no-event');
                                                
                                                if (!isInvalid) {
                                                    result.push(dateText);
                                                    seen.add(dateText);
                                                    validCount++;
                                                }
                                            }
                                        } catch (e) {
                                            // Skip elements that throw
                                        }
                                    });
                                    
                                    debugInfo += ` | valid extracted: ${validCount}`;
                                    
                                    return JSON.stringify({ success: true, dates: result, debugInfo: debugInfo, dayCount: dayElements.length });
                                } catch (e) {
                                    return JSON.stringify({ success: false, dates: [], debugInfo: `Error: ${e.message}`, dayCount: 0 });
                                }
                            }"
                            ) ?? "{}";
                            
                            // Parse JSON result
                            var data = System.Text.Json.JsonSerializer.Deserialize<System.Collections.Generic.Dictionary<string, object>>(result);
                            
                            if (data != null)
                            {
                                if (data.ContainsKey("debugInfo"))
                                {
                                    Console.WriteLine($"  🔍 {data["debugInfo"]}");
                                }
                                
                                if (data.ContainsKey("dates") && data["dates"] is System.Text.Json.JsonElement dateArray)
                                {
                                    foreach (var item in dateArray.EnumerateArray())
                                    {
                                        var dateStr = item.GetString();
                                        if (!string.IsNullOrEmpty(dateStr) && !dates.Contains(dateStr))
                                        {
                                            dates.Add(dateStr);
                                        }
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"  ❌ Failed to extract dates: {ex.Message}");
                        }
                        
                        if (dates?.Count > 0)
                        {
                            // Normalize month name (e.g., "MARCH 2026" -> "March")
                            string monthNameFormatted = monthName;
                            if (monthName.Contains(" "))
                            {
                                monthNameFormatted = System.Globalization.CultureInfo.CurrentCulture.TextInfo.ToTitleCase(
                                    monthName.Split(' ')[0].ToLower()
                                );
                            }
                            
                            // Format dates as "day month"
                            var formattedDates = dates.Select(d => $"{d} {monthNameFormatted}").ToList();
                            Console.WriteLine($"  ✓ Found {formattedDates.Count} dates: {string.Join(", ", formattedDates)}");
                            
                            // Deduplicate and append
                            foreach (var formattedDate in formattedDates)
                            {
                                if (!string.IsNullOrWhiteSpace(formattedDate) && !allDates.Contains(formattedDate))
                                {
                                    allDates.Add(formattedDate);
                                }
                            }
                        }
                        else
                        {
                            Console.WriteLine("  ℹ️ Found 0 dates");
                        }
                        
                        // Capture screenshot of current month
                        try
                        {
                            var bodyHeight = await page.EvaluateAsync<int>("() => document.body.scrollHeight");
                            await page.SetViewportSizeAsync(1920, Math.Max(bodyHeight + 100, 1080));
                            
                            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
                            var screenshotFileName = $"calendar_month{monthIndex + 1}_{timestamp}.jpg";
                            var screenshotPath = Path.Combine(_snapshotDir, screenshotFileName);
                            
                            await page.ScreenshotAsync(new PageScreenshotOptions
                            {
                                Path = screenshotPath,
                                FullPage = true,
                                Type = ScreenshotType.Jpeg,
                                Quality = 90
                            });
                            
                            Console.WriteLine($"  📸 Screenshot saved: {screenshotFileName}");
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"  ❌ Screenshot failed: {ex.Message}");
                        }
                        
                        // If not the last month, click next to move forward
                        if (monthIndex < 5)
                        {
                            try
                            {
                                Console.WriteLine("  → Attempting to move to the next month...");
                                var advancedToNextMonth = await TryAdvanceToNextMonthAsync(page, monthName);
                                if (!advancedToNextMonth)
                                {
                                    break;
                                }
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"  ❌ Failed to click next button: {ex.Message}");
                                break;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"  ⚠️ Failed to process month {monthIndex + 1}: {ex.GetType().Name}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠ Calendar date extraction failed: {ex.Message}");
            }
            
            if (allDates.Count == 0)
            {
                Console.WriteLine("⚠ No dates were extracted from calendar");
            }
            
            return allDates;
        }

        private TicketAvailabilityResult ParseTicketAvailability(string htmlContent)
        {
            var doc = new HtmlDocument();
            doc.LoadHtml(htmlContent);

            var result = new TicketAvailabilityResult
            {
                IsSuccessful = true,
                AvailableDates = new List<string>()
            };

            // Find form elements related to date selection
            // Locate all selectable date buttons/options
            var dateNodes = doc.DocumentNode.SelectNodes("//button[contains(@class, 'date') or contains(@class, 'calendar')]");
            
            if (dateNodes == null || dateNodes.Count == 0)
            {
                // Try alternative selectors
                dateNodes = doc.DocumentNode.SelectNodes("//input[@type='radio' or @type='checkbox' and contains(@name, 'date')]");
            }

            if (dateNodes == null || dateNodes.Count == 0)
            {
                // Try any interactive element that may contain dates
                dateNodes = doc.DocumentNode.SelectNodes("//div[contains(@class, 'date-picker') or contains(@class, 'calendar')]//button");
            }

            if (dateNodes != null && dateNodes.Count > 0)
            {
                result.HasAvailableDates = true;
                foreach (var dateNode in dateNodes)
                {
                    var dateText = dateNode.InnerText?.Trim();
                    if (!string.IsNullOrEmpty(dateText))
                    {
                        result.AvailableDates.Add(dateText);
                    }
                }
            }
            else
            {
                result.HasAvailableDates = false;
                Console.WriteLine("No selectable dates found");
            }

            return result;
        }
    }

    public class TicketAvailabilityResult
    {
        public bool IsSuccessful { get; set; }
        public bool HasAvailableDates { get; set; }
        public List<string> AvailableDates { get; set; } = new List<string>();
        public string? ErrorMessage { get; set; }
        public string? SnapshotPath { get; set; }
        public string? ScreenshotPath { get; set; }
    }
}
