using System.Linq;
using Microsoft.Playwright;
using System.Text.Json;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json.Serialization;
using System.Net;
using System.Net.Http;
using System.Text.Encodings.Web; // for JavaScriptEncoder.UnsafeRelaxedJsonEscaping



namespace DomusScrapper
{
    public static class DomusScraper
    {
        // === DB POSTING (mirror Eurobet) ===
        private static readonly HttpClient _http = new(new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        })
        {
            Timeout = TimeSpan.FromSeconds(25)
        };

        private const string POST_ENDPOINT = "https://www.hh24tech.com/connector/index.php";

        private static async Task<(bool ok, string? body)> PostJsonWithRetryAsync<T>(
            string url, T payload, int maxAttempts = 4)
        {
            var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
            {
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // keep "+" in keys like "1 2 + Handicap"
                WriteIndented = false
            });

            Exception? lastEx = null;

            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    using var content = new StringContent(json, Encoding.UTF8, "application/json");
                    using var resp = await _http.PostAsync(url, content);
                    var respBody = await resp.Content.ReadAsStringAsync();

                    if ((int)resp.StatusCode >= 200 && (int)resp.StatusCode < 300)
                        return (true, respBody);

                    // transient → backoff + jitter
                    if (resp.StatusCode is HttpStatusCode.RequestTimeout
                        or HttpStatusCode.TooManyRequests
                        or HttpStatusCode.BadGateway
                        or HttpStatusCode.ServiceUnavailable
                        or HttpStatusCode.GatewayTimeout)
                    {
                        await Task.Delay(400 * attempt * attempt + Random.Shared.Next(0, 250));
                        continue;
                    }

                    return (false, $"HTTP {(int)resp.StatusCode}: {resp.ReasonPhrase}; body: {respBody}");
                }
                catch (Exception ex)
                {
                    lastEx = ex;
                    await Task.Delay(400 * attempt * attempt + Random.Shared.Next(0, 250));
                }
            }

            return (false, $"post-failed after {maxAttempts} attempts: {lastEx?.Message}");
        }

        public static async Task RunAsync(string? startSport = null)
        {
            Console.WriteLine("Launching browser...");
            
            var playwright = await Playwright.CreateAsync();
            var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = false,
                SlowMo = 1000,
                Devtools = false
            });

            var context = await browser.NewContextAsync();
            var page = await context.NewPageAsync();

            Console.WriteLine("Navigating to Domusbet...");
            await page.GotoAsync("https://www.domusbet.it/scommesse-sportive", new()
            {
                WaitUntil = WaitUntilState.Load,
                Timeout = 60000
            });

            // Force remove cookie popup immediately (prevention)
            await page.EvaluateAsync(@"() => {
        const popup = document.querySelector('#CybotCookiebotDialog');
        if (popup) popup.remove();
    }");

            await HandleCookiebot(page);
            await HandleRandomPopup(page);

            // Ensure sports section is loaded
            await page.WaitForSelectorAsync(".card.elemento-competizioni-widget .titolo-accordion a", new() { Timeout = 15000 });

            var sports = await GetSports(page);

            // ======== NEW: honor startSport if provided (minimal, non-invasive) ========
            if (!string.IsNullOrWhiteSpace(startSport))
            {
                string wanted = startSport.Trim();

                // very light aliasing to match common English terms to site labels
                if (wanted.Equals("Soccer", StringComparison.OrdinalIgnoreCase) ||
                    wanted.Equals("Football", StringComparison.OrdinalIgnoreCase))
                    wanted = "Calcio";
                    // American Football aliases → Domusbet label
                else if (wanted.Equals("American Football", StringComparison.OrdinalIgnoreCase) ||
                    wanted.Equals("NFL", StringComparison.OrdinalIgnoreCase) ||
                    wanted.Equals("Football Americano", StringComparison.OrdinalIgnoreCase))
                    wanted = "Football Americano";

                else if (wanted.Equals("Basketball", StringComparison.OrdinalIgnoreCase))
                    wanted = "Pallacanestro"; // some pages might show "Basket" — we handle both below

                // try exact (case-insensitive)
                var filtered = sports.Where(s => s.Equals(wanted, StringComparison.OrdinalIgnoreCase)).ToList();

                // small fallback: allow matching "Basket" vs "Pallacanestro"
                if (filtered.Count == 0 && wanted.Equals("Pallacanestro", StringComparison.OrdinalIgnoreCase))
                    filtered = sports.Where(s => s.Equals("Basket", StringComparison.OrdinalIgnoreCase) ||
                                                 s.Equals("Pallacanestro", StringComparison.OrdinalIgnoreCase)).ToList();

                // if still none, soft contains as last resort
                if (filtered.Count == 0)
                    filtered = sports.Where(s => s.IndexOf(wanted, StringComparison.OrdinalIgnoreCase) >= 0).ToList();

                if (filtered.Count == 0)
                {
                    Console.WriteLine($"⚠ Requested sport '{startSport}' not found in sidebar. Available: {string.Join(", ", sports)}");
                    return;
                }

                sports = filtered;
            }
            // ======== END NEW ========

            if (sports.Count == 0)
            {
                Console.WriteLine("⚠ No sports detected. Taking screenshot for debugging...");
                await page.ScreenshotAsync(new PageScreenshotOptions { Path = "no_sports.png" });
                return;
            }

            foreach (var sport in sports)
            {
                Console.WriteLine($"\n=== Processing sport: {sport} ===");

                await ClickSportSection(page, sport);
                // Focus navigation on American Football only
if (!sport.Equals("Football Americano", StringComparison.OrdinalIgnoreCase))
    continue;

                await HandleRandomPopup(page);

                var leagues = await GetLeaguesForSport(page);

                foreach (var league in leagues)
                {
                    Console.WriteLine($"\n--- Processing league: {league} ---");

                    await HandleRandomPopup(page);
                    await ClickLeagueAndLoadOdds(page, league);
                    await HandleRandomPopup(page);

                    // Extract odds
                    await ExtractOddsAsync(page, $"{sport}_{league.Replace(" ", "_")}.json");

                    // Navigate back for next league
                    await page.GotoAsync("https://www.domusbet.it/scommesse-sportive", new()
                    {
                        WaitUntil = WaitUntilState.Load,
                        Timeout = 60000
                    });

                    // Re-handle popups after navigation
                    await page.EvaluateAsync(@"() => {
                const popup = document.querySelector('#CybotCookiebotDialog');
                if (popup) popup.remove();
            }");

                    await HandleCookiebot(page);
                    await HandleRandomPopup(page);
                    await ClickSportSection(page, sport);
                }
            }
        }

// DomusScraper.cs  (add inside DomusScraper class)
public static async Task RunSinglePageAsync(IPage page, string? startSport = null)
{
    Console.WriteLine("Navigating to Domusbet...");
    await page.GotoAsync("https://www.domusbet.it/scommesse-sportive", new()
    {
        WaitUntil = WaitUntilState.Load,
        Timeout   = 60000
    });

    // Pre-empt cookie banner
    try
    {
        await page.EvaluateAsync(@"() => {
            const p = document.querySelector('#CybotCookiebotDialog');
            if (p) p.remove();
        }");
    } catch {}

    await HandleCookiebot(page);
    await HandleRandomPopup(page);

    // Ensure sports present
    await page.WaitForSelectorAsync(".card.elemento-competizioni-widget .titolo-accordion a", new() { Timeout = 15000 });

    var sports = await GetSports(page);

    // Optional startSport narrowing (same logic you already had)
 // Optional startSport narrowing (only if provided)
if (!string.IsNullOrWhiteSpace(startSport))
{
    string wanted = startSport.Trim();
    if (wanted.Equals("American Football", StringComparison.OrdinalIgnoreCase) ||
        wanted.Equals("NFL", StringComparison.OrdinalIgnoreCase) ||
        wanted.Equals("Football Americano", StringComparison.OrdinalIgnoreCase))
    {
        wanted = "Football Americano";
    }
    else if (wanted.Equals("Soccer", StringComparison.OrdinalIgnoreCase) ||
             wanted.Equals("Football", StringComparison.OrdinalIgnoreCase))
    {
        wanted = "Calcio";
    }
    else if (wanted.Equals("Basketball", StringComparison.OrdinalIgnoreCase))
    {
        wanted = "Pallacanestro";
    }

    var filtered = sports.Where(s => s.Equals(wanted, StringComparison.OrdinalIgnoreCase)).ToList();

    // small fallback: allow matching "Basket" vs "Pallacanestro"
    if (filtered.Count == 0 && wanted.Equals("Pallacanestro", StringComparison.OrdinalIgnoreCase))
        filtered = sports.Where(s => s.Equals("Basket", StringComparison.OrdinalIgnoreCase) ||
                                     s.Equals("Pallacanestro", StringComparison.OrdinalIgnoreCase)).ToList();

    // soft contains last resort
    if (filtered.Count == 0)
        filtered = sports.Where(s => s.IndexOf(wanted, StringComparison.OrdinalIgnoreCase) >= 0).ToList();

    if (filtered.Count == 0)
    {
        Console.WriteLine($"⚠ '{startSport}' not found. Available: {string.Join(", ", sports)}");
        return;
    }

    sports = filtered;
}


    if (sports.Count == 0)
    {
        Console.WriteLine("⚠ No sports found.");
        return;
    }

    foreach (var sport in sports)
    {
        Console.WriteLine($"\n=== Processing sport: {sport} ===");
        await ClickSportSection(page, sport);
        // Focus navigation on American Football only
if (!sport.Equals("Football Americano", StringComparison.OrdinalIgnoreCase))
    continue;

        await HandleRandomPopup(page);

        var leagues = await GetLeaguesForSport(page);

        foreach (var league in leagues)
        {
            Console.WriteLine($"\n--- League: {league} ---");
            await HandleRandomPopup(page);
            await ClickLeagueAndLoadOdds(page, league);
            await HandleRandomPopup(page);

            // file name like before
            await ExtractOddsAsync(page, $"{sport}_{league.Replace(" ", "_")}.json");

            // Back to home for the next league
            await page.GotoAsync("https://www.domusbet.it/scommesse-sportive", new()
            {
                WaitUntil = WaitUntilState.Load,
                Timeout   = 60000
            });

            // handle popups again
            try
            {
                await page.EvaluateAsync(@"() => {
                    const p = document.querySelector('#CybotCookiebotDialog');
                    if (p) p.remove();
                }");
            } catch {}

            await HandleCookiebot(page);
            await HandleRandomPopup(page);

            await ClickSportSection(page, sport);
        }
    }
}



        private static string BuildHcpSummary(Dictionary<string, Dictionary<string, string>> hcp)
        {
            if (hcp == null || hcp.Count == 0) return "";
            var parsed = new SortedDictionary<double, (string One, string Two)>();
            var fallback = new SortedDictionary<string, (string One, string Two)>(StringComparer.Ordinal);

            foreach (var (lineKey, sides) in hcp)
            {
                var one = sides.TryGetValue("1", out var v1) ? v1 : null;
                var two = sides.TryGetValue("2", out var v2) ? v2 : null;

                if (double.TryParse(lineKey.Replace(',', '.'), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var lineNum))
                    parsed[lineNum] = (one ?? "", two ?? "");
                else
                    fallback[lineKey] = (one ?? "", two ?? "");
            }

            var parts = new List<string>();
            foreach (var kv in parsed)
                parts.Add($"{kv.Key.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)} (1: {kv.Value.One ?? "-"}, 2: {kv.Value.Two ?? "-"})");
            foreach (var kv in fallback)
                parts.Add($"{kv.Key} (1: {kv.Value.One ?? "-"}, 2: {kv.Value.Two ?? "-"})");

            return string.Join(" | ", parts);
        }

        private static string BuildOUSummary(Dictionary<string, Dictionary<string, string>> ou)
        {
            if (ou == null || ou.Count == 0) return "";
            var parsed = new SortedDictionary<double, (string Under, string Over)>();
            var fallback = new SortedDictionary<string, (string Under, string Over)>(StringComparer.Ordinal);

            foreach (var (lineKey, sides) in ou)
            {
                var un = sides.TryGetValue("UNDER", out var u) ? u : null;
                var ov = sides.TryGetValue("OVER", out var v) ? v : null;

                if (double.TryParse(lineKey.Replace(',', '.'), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var lineNum))
                    parsed[lineNum] = (un ?? "", ov ?? "");
                else
                    fallback[lineKey] = (un ?? "", ov ?? "");
            }

            var parts = new List<string>();
            foreach (var kv in parsed)
                parts.Add($"{kv.Key:0.##} (UNDER: {kv.Value.Under ?? "-"}, OVER: {kv.Value.Over ?? "-"})");
            foreach (var kv in fallback)
                parts.Add($"{kv.Key} (UNDER: {kv.Value.Under ?? "-"}, OVER: {kv.Value.Over ?? "-"})");

            return string.Join(" | ", parts);
        }

        private static string NormalizeHcpLine(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "";
            var t = s.Trim().Replace(",", ".").Replace('\u00A0', ' ');
            var m = Regex.Match(t, @"[+\-]?\d+(?:\.\d+)?");
            return m.Success ? m.Value : t;
        }

        private static string NormalizeOUTotalLine(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "";
            var t = s.Trim().Replace(",", ".").Replace('\u00A0', ' ');
            var m = Regex.Match(t, @"[+\-]?\d+(?:\.\d+)?");
            return m.Success ? m.Value : t;
        }

        private static async Task<List<(string line, string under, string over)>> ParseNearestOUTotalMenuAsync(IPage page, ILocator anyButtonInRow)
        {
            try { await page.WaitForSelectorAsync(".dropdown-menu.show", new() { State = WaitForSelectorState.Visible, Timeout = 800 }); } catch { }

            var btnHandle = await anyButtonInRow.ElementHandleAsync();
            if (btnHandle == null) return new();

            string json = await page.EvaluateAsync<string>(@"(button) => {
      const T = s => (s||'').toString().trim();
      const vis = el => {
        if (!el) return false;
        const s = getComputedStyle(el), r = el.getBoundingClientRect();
        return s.visibility !== 'hidden' && s.display !== 'none' && r.width > 0 && r.height > 0;
      };
      const center = el => { const r = el.getBoundingClientRect(); return {x:(r.left+r.right)/2, y:(r.top+r.bottom)/2}; };
      const btnC = center(button);

      const menus = Array.from(document.querySelectorAll('.dropdown-menu.show')).filter(vis);
      if (!menus.length) return '[]';
      let box = null, best = Infinity;
      for (const m of menus) {
        const c = center(m), d2 = (c.x - btnC.x)**2 + (c.y - btnC.y)**2;
        if (d2 < best) { best = d2; box = m; }
      }
      if (!box) return '[]';

      let lists = Array.from(box.querySelectorAll('.dropdown-content .lista-quote.colonne-3')).filter(vis);
      lists = lists.filter(l => l.querySelector('a.dropdown-item'));
      if (!lists.length) lists = Array.from(box.querySelectorAll('.lista-quote')).filter(l => vis(l) && l.querySelector('a.dropdown-item'));
      if (!lists.length) return '[]';

      let list = null, b = Infinity;
      for (const L of lists) {
        const c = center(L), d2 = (c.x - btnC.x)**2 + (c.y - btnC.y)**2;
        if (d2 < b) { b = d2; list = L; }
      }
      if (!list) return '[]';

      const kids = Array.from(list.children || []);
      const out = [];
      for (let i = 0; i < kids.length; i++) {
        const k = kids[i];
        if (!(k.matches && k.matches('a.dropdown-item'))) continue;
        const raw = T(k.textContent);

        let under = '', over = '', seen = 0;
        for (let j = i + 1; j < kids.length && seen < 2; j++) {
          const n = kids[j];
          if (!(n.matches && n.matches('.contenitoreSingolaQuota'))) continue;
          const lab = T(n.querySelector('.titoloQuotazione')?.textContent).toUpperCase();
          const val = T(n.querySelector('.tipoQuotazione_1')?.textContent);
          if (lab === 'UNDER' && !under) { under = val; seen++; }
          else if (lab === 'OVER' && !over) { over = val; seen++; }
        }
        if (raw && (under || over)) out.push([raw, under, over]);
      }
      return JSON.stringify(out);
    }", btnHandle);

            var lines = new List<(string, string, string)>();
            try
            {
                using var doc = JsonDocument.Parse(json);
                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    var raw = item[0].GetString() ?? "";
                    var under = item[1].GetString() ?? "";
                    var over = item[2].GetString() ?? "";
                    if (!string.IsNullOrWhiteSpace(raw) && (!string.IsNullOrWhiteSpace(under) || !string.IsNullOrWhiteSpace(over)))
                        lines.Add((raw, under, over));
                }
            }
            catch { }
            return lines;
        }

        private static async Task<List<(string line, string one, string two)>> ParseNearestTTMenuAsync(IPage page, ILocator ttButton)
        {
            try { await page.WaitForSelectorAsync(".dropdown-menu.show", new() { State = WaitForSelectorState.Visible, Timeout = 800 }); } catch { }

            var btnHandle = await ttButton.ElementHandleAsync();
            if (btnHandle == null) return new();

            string json = await page.EvaluateAsync<string>(@"(button) => {
      const T = s => (s||'').toString().trim();
      const vis = el => {
        if (!el) return false;
        const s = getComputedStyle(el), r = el.getBoundingClientRect();
        return s.visibility !== 'hidden' && s.display !== 'none' && r.width > 0 && r.height > 0;
      };
      const center = el => { const r = el.getBoundingClientRect(); return {x:(r.left+r.right)/2, y:(r.top+r.bottom)/2}; };
      const btnC = center(button);

      const menus = Array.from(document.querySelectorAll('.dropdown-menu.show')).filter(vis);
      if (!menus.length) return '[]';
      let box = null, best = Infinity;
      for (const m of menus) {
        const c = center(m), d2 = (c.x - btnC.x)**2 + (c.y - btnC.y)**2;
        if (d2 < best) { best = d2; box = m; }
      }
      if (!box) return '[]';

      let lists = Array.from(box.querySelectorAll('.dropdown-content .lista-quote.colonne-3')).filter(vis);
lists = lists.filter(l => l.querySelector('a.dropdown-item'));
if (!lists.length) lists = Array.from(box.querySelectorAll('.lista-quote.colonne-3')).filter(l => vis(l) && l.querySelector('a.dropdown-item'));
if (!lists.length) return '[]';


      let list = null, b = Infinity;
      for (const L of lists) {
        const c = center(L), d2 = (c.x - btnC.x)**2 + (c.y - btnC.y)**2;
        if (d2 < b) { b = d2; list = L; }
      }
      if (!list) return '[]';

      const kids = Array.from(list.children || []);
      const out = [];
      for (let i = 0; i < kids.length; i++) {
        const node = kids[i];
        if (!(node.matches && node.matches('a.dropdown-item'))) continue;

        const rawLine = T(node.textContent);

        let one = '', two = '', seen = 0;
        for (let j = i + 1; j < kids.length && seen < 2; j++) {
          const n = kids[j];
          if (!(n.matches && n.matches('.contenitoreSingolaQuota'))) continue;

          const lab = T(n.querySelector('.titoloQuotazione')?.textContent).toUpperCase();
          const val = T(n.querySelector('.tipoQuotazione_1')?.textContent);

          if (lab === '1' && !one) { one = val; seen++; }
          else if (lab === '2' && !two) { two = val; seen++; }
        }
        if (rawLine && (one || two)) out.push([rawLine, one, two]);
      }
      return JSON.stringify(out);
    }", btnHandle);

            var lines = new List<(string, string, string)>();
            try
            {
                using var doc = JsonDocument.Parse(json);
                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    var raw = item.GetArrayLength() > 0 ? item[0].GetString() ?? "" : "";
                    var one = item.GetArrayLength() > 1 ? item[1].GetString() ?? "" : "";
                    var two = item.GetArrayLength() > 2 ? item[2].GetString() ?? "" : "";
                    if (!string.IsNullOrWhiteSpace(raw) && (!string.IsNullOrWhiteSpace(one) || !string.IsNullOrWhiteSpace(two)))
                        lines.Add((raw, one, two));
                }
            }
            catch { }
            return lines;
        }



        private static async Task HandleCookiebot(IPage page)
        {
            try
            {
                Console.WriteLine("Checking for Cookiebot popup...");

                for (int attempt = 0; attempt < 5; attempt++)
                {
                    bool popupVisible = await page.Locator("#CybotCookiebotDialog").IsVisibleAsync(new() { Timeout = 2000 });

                    if (popupVisible)
                    {
                        Console.WriteLine($"Cookiebot popup detected. Attempt {attempt + 1}/5");

                        await page.EvaluateAsync(@"() => {
                            const popup = document.querySelector('#CybotCookiebotDialog');
                            if (popup) popup.remove();
                        }");

                        await page.WaitForTimeoutAsync(500);

                        if (!await page.Locator("#CybotCookiebotDialog").IsVisibleAsync(new() { Timeout = 1000 }))
                        {
                            Console.WriteLine("✅ Cookiebot popup successfully removed.");
                            return;
                        }

                        Console.WriteLine("⚠ Popup still visible. Retrying...");
                    }
                    else
                    {
                        Console.WriteLine("No Cookiebot popup detected.");
                        return;
                    }
                }

                Console.WriteLine("⚠ Trying fallback click...");

                await page.EvaluateAsync(@"() => {
                    const declineBtn = document.querySelector('#CybotCookiebotDialogBodyButtonDecline');
                    const acceptBtn = document.querySelector('#CybotCookiebotDialogBodyLevelButtonLevelOptinAllowAll');
                    if (declineBtn) declineBtn.click();
                    else if (acceptBtn) acceptBtn.click();
                }");

                await page.WaitForTimeoutAsync(1000);

                if (!await page.Locator("#CybotCookiebotDialog").IsVisibleAsync(new() { Timeout = 2000 }))
                {
                    Console.WriteLine("✅ Cookiebot popup dismissed with fallback click.");
                }
                else
                {
                    Console.WriteLine("❌ Cookiebot popup could not be dismissed.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error handling Cookiebot popup: {ex.Message}");
            }
        }

        private static async Task<List<string>> GetSports(IPage page)
        {
            var sportsElements = page.Locator(".card.elemento-competizioni-widget .titolo-accordion a");
            int count = await sportsElements.CountAsync();
            var sports = new List<string>();

            for (int i = 0; i < count; i++)
            {
                string sportName = (await sportsElements.Nth(i).InnerTextAsync()).Trim();
                if (!string.IsNullOrEmpty(sportName))
                    sports.Add(sportName);
            }

            return sports;
        }

        private static async Task ClickSportSection(IPage page, string sportName)
        {
            Console.WriteLine($"=== Processing sport: {sportName.ToUpper()} ===");
            Console.WriteLine($"Waiting for '{sportName}' section...");

            try
            {
                var sportLinks = page.Locator(".card.elemento-competizioni-widget .titolo-accordion a");
                int count = await sportLinks.CountAsync();
                bool sportFound = false;

                for (int i = 0; i < count; i++)
                {
                    var link = sportLinks.Nth(i);
                    string text = (await link.InnerTextAsync()).Trim();

                    if (text.Equals(sportName, StringComparison.OrdinalIgnoreCase))
                    {
                        Console.WriteLine($"Clicking on '{sportName}' section...");
                        await HandleRandomPopup(page);
                        await link.ClickAsync();
                        await page.WaitForTimeoutAsync(2000);
                        sportFound = true;
                        break;
                    }
                }

                if (!sportFound)
                    Console.WriteLine($"'{sportName}' section not found!");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error clicking on sport section '{sportName}': {ex.Message}");
            }
        }

        private static async Task<List<string>> GetLeaguesForSport(IPage page)
        {
            var leagueElements = page.Locator(".box-paese .nome-paese");
            int count = await leagueElements.CountAsync();
            var leagues = new List<string>();

            for (int i = 0; i < count; i++)
            {
                string leagueName = (await leagueElements.Nth(i).InnerTextAsync()).Trim();
                if (!string.IsNullOrEmpty(leagueName))
                    leagues.Add(leagueName);
            }

            return leagues;
        }

        private static async Task ClickLeagueAndLoadOdds(IPage page, string leagueName)
        {
            Console.WriteLine($"Clicking '{leagueName}' and loading fixtures...");

            var leagueContainer = page.Locator(".box-paese", new PageLocatorOptions { HasTextString = leagueName }).First;

            if (await leagueContainer.IsVisibleAsync())
            {
                await HandleRandomPopup(page);
                var leagueButton = leagueContainer.Locator(".nome-paese");
                Console.WriteLine($"Clicking '{leagueName}'...");
                await leagueButton.ClickAsync();
                await page.WaitForTimeoutAsync(1000);

                var arrowButton = page.Locator(".widget-sel a:has(i.fas.fa-play)");

                if (await arrowButton.IsVisibleAsync())
                {
                    await HandleRandomPopup(page);

                    for (int i = 0; i < 10; i++)
                    {
                        var isDisabled = await arrowButton.GetAttributeAsync("href-disabled");
                        Console.WriteLine($"Arrow state: {(isDisabled == null ? "enabled" : "disabled")}");
                        if (isDisabled == null) break;
                        await page.WaitForTimeoutAsync(500);
                    }

                    await arrowButton.ScrollIntoViewIfNeededAsync();
                    await arrowButton.HoverAsync();
                    Console.WriteLine("Clicking ▶ arrow...");
                    await arrowButton.ClickAsync();

                    bool fixturesLoaded = false;
                    for (int i = 0; i < 10; i++)
                    {
                        var matchCount = await page.Locator(".tabellaQuoteSquadre").CountAsync();
                        if (matchCount > 0)
                        {
                            fixturesLoaded = true;
                            Console.WriteLine($"Fixtures loaded: {matchCount} matches found.");
                            break;
                        }

                        Console.WriteLine("Fixtures not detected yet, retrying...");
                        await page.WaitForTimeoutAsync(2000);
                    }

                    if (!fixturesLoaded)
                        Console.WriteLine("⚠ Fixtures did not load after multiple retries, but continuing...");
                }
                else
                {
                    Console.WriteLine("▶ play-arrow not found inside widget-sel.");
                }
            }
            else
            {
                Console.WriteLine($"'{leagueName}' section not found!");
            }
        }

        private class MatchData
        {
            public string Teams { get; set; }
            public Dictionary<string, string> Odds { get; set; }

            // Marathonbet parity:
            public Dictionary<string, Dictionary<string, string>> TTPlusHandicap { get; set; }
            public Dictionary<string, Dictionary<string, string>> OUTotals { get; set; }
        }

        // === Eurobet-compatible models (numeric odds) ===
        public class OddsSchemaOutNode
        {
            [JsonPropertyName("U")] public double? U { get; set; }
            [JsonPropertyName("O")] public double? O { get; set; }
        }

        public class OddsSchemaHcapNode
        {
            [JsonPropertyName("1")] public double? One { get; set; }
            [JsonPropertyName("2")] public double? Two { get; set; }
        }

        // Loose numeric parser: "1,85" → 1.85; returns null if not numeric
        private static double? TryParseDoubleLoose(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            s = s.Trim().Replace(",", ".");
            if (double.TryParse(s, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var v)) return v;
            return null;
        }

        // Extract a numeric key like "+3.5", "-2", "2.5" → "+3.5" / "-2" / "2.5" (as text)
        private static string ExtractNumericKey(string? raw, bool allowSign)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "";
            var t = raw.Trim().Replace(",", ".").Replace("\u00A0", " ");
            var m = System.Text.RegularExpressions.Regex.Match(
                t, allowSign ? @"[+\-]?\d+(?:\.\d+)?" : @"\d+(?:\.\d+)?");
            return m.Success ? m.Value : "";
        }


        // Build EXACT Eurobet master payload shape for Domus data
        // Build EXACT Eurobet master payload shape for Domus data (explicit sport label)
private static Dictionary<string, object> BuildClientPayload(
    string teams,
    string sportLabel, // e.g. "americanfootball"
    bool isSoccer,     // only used to decide Handicap removal behavior
    Dictionary<string, string> baseOdds,                                   // "1","X","2", etc.
    Dictionary<string, Dictionary<string, string>> ouTotals,               // line -> { "UNDER": "...", "OVER": "..." }
    Dictionary<string, Dictionary<string, string>> oneTwoHandicapLines     // line -> { "1": "...", "2": "..." }
)
{
    var odds = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

    // Soccer has 1/X/2 + GG/NG; others have 1/2
    if (baseOdds != null)
    {
        if (isSoccer)
        {
            if (baseOdds.TryGetValue("1", out var s1) && TryParseDoubleLoose(s1) is double d1) odds["1"] = d1;
            if (baseOdds.TryGetValue("X", out var sx) && TryParseDoubleLoose(sx) is double dx) odds["X"] = dx;
            if (baseOdds.TryGetValue("2", out var s2) && TryParseDoubleLoose(s2) is double d2) odds["2"] = d2;
            if (baseOdds.TryGetValue("GOAL", out var sgg) && TryParseDoubleLoose(sgg) is double dgg) odds["GG"] = dgg;
            if (baseOdds.TryGetValue("NOGOAL", out var sng) && TryParseDoubleLoose(sng) is double dng) odds["NG"] = dng;
        }
        else
        {
            if (baseOdds.TryGetValue("1", out var s1) && TryParseDoubleLoose(s1) is double d1) odds["1"] = d1;
            if (baseOdds.TryGetValue("2", out var s2) && TryParseDoubleLoose(s2) is double d2) odds["2"] = d2;
        }
    }

    // O/U
    if (ouTotals != null && ouTotals.Count > 0)
    {
        var ouDict = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        foreach (var (lineRaw, sides) in ouTotals)
        {
            var lineKey = ExtractNumericKey(lineRaw, allowSign: false);
            if (string.IsNullOrEmpty(lineKey)) continue;

            sides.TryGetValue("UNDER", out var uStr);
            sides.TryGetValue("OVER", out var oStr);

            var u = TryParseDoubleLoose(uStr);
            var o = TryParseDoubleLoose(oStr);

            if (u.HasValue || o.HasValue)
                ouDict[lineKey] = new Dictionary<string, double?> { { "U", u }, { "O", o } };
        }
        if (ouDict.Count > 0) odds["O/U"] = ouDict;
    }

    // Handicap (keep for non-soccer, i.e., keep for American Football)
    if (!isSoccer && oneTwoHandicapLines != null && oneTwoHandicapLines.Count > 0)
    {
        var hcap = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        foreach (var (lineRaw, sides) in oneTwoHandicapLines)
        {
            var lineKey = ExtractNumericKey(lineRaw, allowSign: true);
            if (string.IsNullOrEmpty(lineKey)) continue;

            sides.TryGetValue("1", out var s1);
            sides.TryGetValue("2", out var s2);

            var v1 = TryParseDoubleLoose(s1);
            var v2 = TryParseDoubleLoose(s2);

            if (v1.HasValue || v2.HasValue)
                hcap[lineKey] = new Dictionary<string, double?> { { "1", v1 }, { "2", v2 } };
        }
        if (hcap.Count > 0) odds["1 2 + Handicap"] = hcap;
    }

    return new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
    {
        ["sport"] = sportLabel,   // <-- will be "americanfootball"
        ["Teams"] = teams,
        ["Bookmaker"] = "Domusbet",
        ["Odds"] = odds
    };
}




        private static async Task ExtractOddsAsync(IPage page, string fileName)
        {
            Console.WriteLine("Extracting odds...");

            var matchesList = new List<MatchData>();
            var matchContainers = page.Locator(".tabellaQuoteSquadre");
            int matchCount = await matchContainers.CountAsync();
            Console.WriteLine("Fixtures loading...");
    
            int validMatchCount = 0;

            const int DROPDOWN_WAIT_MS = 350;

            for (int i = 0; i < matchCount; i++)
            {
                var match = matchContainers.Nth(i);

                // Scroll into view
                try
                {
                    await page.EvaluateAsync(@"(index) => {
                const container = document.querySelector('#primo-blocco-sport');
                const matches = container?.querySelectorAll('.tabellaQuoteSquadre');
                if (matches && matches[index]) {
                    matches[index].scrollIntoView({behavior: 'instant', block: 'center'});
                }
            }", i);
                    await page.WaitForTimeoutAsync(250);
                }
                catch
                {
                    Console.WriteLine($"⚠ Could not scroll match {i}, continuing...");
                }

                // TEAMS
                string teamNames = "Unknown Teams";
                var teams = new List<string>();
                try
                {
                    var bolds = await match.Locator("p.font-weight-bold").AllInnerTextsAsync();
                    foreach (var t in bolds)
                    {
                        var cleaned = (t ?? "").Replace('\u00A0', ' ').Trim();
                        if (string.IsNullOrWhiteSpace(cleaned)) continue;
                        var norm = Regex.Replace(cleaned, @"\s+", " ");
                        if (norm.Equals("vs", StringComparison.OrdinalIgnoreCase)) continue;
                        teams.Add(norm);
                        if (teams.Count == 2) break;
                    }
                }
                catch { }

                if (teams.Count < 2)
                {
                    try
                    {
                        var a = match.Locator("a");
                        if (await a.CountAsync() > 0)
                        {
                            var href = await a.First.GetAttributeAsync("href");
                            if (!string.IsNullOrWhiteSpace(href))
                            {
                                var last = (href.Split('/').LastOrDefault() ?? "").Replace("-", " ");
                                var parts = Regex.Split(last, @"\b(vs?|–|—)\b", RegexOptions.IgnoreCase)
                                                 .Select(p => Regex.Replace(p.Trim(), @"\s+", " "))
                                                 .Where(p => !string.IsNullOrWhiteSpace(p) && !Regex.IsMatch(p, @"^(vs?|–|—)$", RegexOptions.IgnoreCase))
                                                 .ToList();
                                if (parts.Count >= 2)
                                    teams = new List<string> { parts[0], parts[1] };
                            }
                        }
                    }
                    catch { }
                }

                if (teams.Count != 2) continue;

                teamNames = $"{teams[0]} vs {teams[1]}";
                validMatchCount++;
                Console.WriteLine($"\nProcessing match: {teamNames}");

                // SPORT DETECTION
                // NOTE: revert to Domus-style odds dictionary (default comparer)
                var odds = new Dictionary<string, string>();
                  bool isAmericanFootball = fileName.Contains("Football Americano", StringComparison.OrdinalIgnoreCase)
                                       || fileName.Contains("American Football", StringComparison.OrdinalIgnoreCase)
                                       || fileName.Contains("NFL", StringComparison.OrdinalIgnoreCase);


                bool isSoccer = fileName.Contains("Calcio", StringComparison.OrdinalIgnoreCase)
                             || fileName.Contains("Soccer", StringComparison.OrdinalIgnoreCase)
&& !isAmericanFootball;
                bool isBasket = fileName.Contains("Pallacanestro", StringComparison.OrdinalIgnoreCase)
                             || fileName.Contains("Basket", StringComparison.OrdinalIgnoreCase);

                bool isTennis = fileName.Contains("Tennis", StringComparison.OrdinalIgnoreCase);

                // ADD these flags next to isSoccer / isBasket / isTennis:
                bool isRugby = fileName.Contains("Rugby", StringComparison.OrdinalIgnoreCase);

              
                bool isBaseball = fileName.Contains("Baseball", StringComparison.OrdinalIgnoreCase);

                bool isIceHockey = fileName.Contains("Hockey su ghiaccio", StringComparison.OrdinalIgnoreCase)
                                || fileName.Contains("Ice Hockey", StringComparison.OrdinalIgnoreCase)
                                || fileName.Contains("Ice-Hockey", StringComparison.OrdinalIgnoreCase)
                                || fileName.Contains("Hockey", StringComparison.OrdinalIgnoreCase);


                // FAST SKIP GUARD
                bool likelyHasGrid = false;
                bool hasDropdowns = false;
                try
                {
                    var row = match.Locator("xpath=ancestor::div[contains(@class,'contenitoreRiga')]").First;

                    var probeCount = await row.Locator(
                        "div.gridInterernaQuotazioni .contenitoreSingolaQuota .tipoQuotazione_1, " +
                        "div.tabellaQuoteNew .contenitoreSingolaQuota .tipoQuotazione_1, " +
                        ".gridInterernaQuotazioni .contenitoreSingolaQuota .tipoQuotazione_1, " +
                        ".tabellaQuoteNew .contenitoreSingolaQuota .tipoQuotazione_1"
                    ).CountAsync();

                likelyHasGrid = probeCount > 0;
hasDropdowns  = await row.Locator("button.dropdown-toggle").CountAsync() > 0;
}
                catch { }

if (!likelyHasGrid && !hasDropdowns)
                {
                    Console.WriteLine("No visible odds grid and no dropdowns; skipping fast.");
                    // still add to list (original Domus structure)
                    matchesList.Add(new MatchData
                    {
                        Teams = teamNames,
                        Odds = new Dictionary<string, string>(),
                        TTPlusHandicap = null,
                        OUTotals = null
                    });
                    continue;
                }

                // BASE GRID ODDS (Marathon logic, but flattened to Domus Odds)
                try
                {
                    var rowForOdds = match.Locator("xpath=ancestor::div[contains(@class,'contenitoreRiga')]").First;

                    var cells = rowForOdds.Locator(
                        "div.gridInterernaQuotazioni .contenitoreSingolaQuota, " +
                        "div.tabellaQuoteNew .contenitoreSingolaQuota, " +
                        ".gridInterernaQuotazioni .contenitoreSingolaQuota, " +
                        ".tabellaQuoteNew .contenitoreSingolaQuota"
                    );

                    int cCount = await cells.CountAsync();
                    if (cCount > 0)
                    {
                        for (int j = 0; j < cCount; j++)
                        {
                            var cell = cells.Nth(j);
                            try
                            {
                                string label = (await cell.Locator(".titoloQuotazione").InnerTextAsync()).Trim();
                                string value = (await cell.Locator(".tipoQuotazione_1").InnerTextAsync()).Trim();
                                if (string.IsNullOrWhiteSpace(label) || string.IsNullOrWhiteSpace(value)) continue;

                                if (isSoccer || isRugby)
                                {
                                    // Soccer & Rugby: keep 1 / X / 2 plus any other grid markets present
                                    odds[label] = value;
                                }
                                else if (isAmericanFootball || isBaseball || isIceHockey || isBasket || isTennis)
                                {
                                    // AF / Baseball / Ice Hockey / Basket / Tennis: keep match-winner only (1/2)
                                    if (label.Equals("1", StringComparison.OrdinalIgnoreCase) ||
                                        label.Equals("2", StringComparison.OrdinalIgnoreCase))
                                    {
                                        if (!odds.ContainsKey(label)) odds[label] = value;
                                    }
                                }
                                else
                                {
                                    // default fallback (safe)
                                    if (label.Equals("1", StringComparison.OrdinalIgnoreCase) ||
                                        label.Equals("2", StringComparison.OrdinalIgnoreCase))
                                    {
                                        if (!odds.ContainsKey(label)) odds[label] = value;
                                    }
                                }

                            }
                            catch { }
                        }
                    }

                    // Fallback probe via DOM evaluate if grid existed but loop found nothing
                    if (odds.Count == 0 && likelyHasGrid)
                    {
                        try
                        {
                            string json = await rowForOdds.EvaluateAsync<string>(@"(root) => {
                        const T = s => (s||'').toString().trim();
                        const out = {};
                        const sel = [
                          'div.gridInterernaQuotazioni .contenitoreSingolaQuota',
                          'div.tabellaQuoteNew .contenitoreSingolaQuota',
                          '.gridInterernaQuotazioni .contenitoreSingolaQuota',
                          '.tabellaQuoteNew .contenitoreSingolaQuota'
                        ];
                        const nodes = [];
                        sel.forEach(s => nodes.push(...root.querySelectorAll(s)));
                        for (const n of nodes) {
                          const k = T(n.querySelector('.titoloQuotazione')?.textContent);
                          const v = T(n.querySelector('.tipoQuotazione_1')?.textContent);
                          if (k && v) out[k] = v;
                        }
                        return JSON.stringify(out);
                    }");

                            using var doc = JsonDocument.Parse(json);
                            foreach (var kv in doc.RootElement.EnumerateObject())
                            {
                                var k = kv.Name;
                                var v = kv.Value.GetString() ?? "";
                                if (string.IsNullOrWhiteSpace(k) || string.IsNullOrWhiteSpace(v)) continue;

                                if (isSoccer || isRugby)
                                {
                                    odds[k] = v;
                                }
                                else if (isAmericanFootball || isBaseball || isIceHockey || isBasket || isTennis)
                                {
                                    if (k.Equals("1", StringComparison.OrdinalIgnoreCase) || k.Equals("2", StringComparison.OrdinalIgnoreCase))
                                        if (!odds.ContainsKey(k)) odds[k] = v;
                                }

                            }
                        }
                        catch { }
                    }

                    // Soccer alias normalization
                    if (isSoccer)
                    {
                        if (odds.TryGetValue("GOL", out var g) && !odds.ContainsKey("GOAL")) odds["GOAL"] = g;
                        if (odds.TryGetValue("NOGOL", out var ng) && !odds.ContainsKey("NOGOAL")) odds["NOGOAL"] = ng;
                    }

                    // Ensure a fuller soccer set if we missed a few labels
                    if (isSoccer && odds.Count > 0)
                    {
                        string[] wanted = { "1", "X", "2", "1X", "12", "X2", "UNDER", "OVER", "GOAL", "NOGOAL", "GOL", "NOGOL" };
                        var need = wanted.Where(k => !odds.ContainsKey(k)).ToArray();
                        if (need.Length > 0)
                        {
                            try
                            {
                                string json = await rowForOdds.EvaluateAsync<string>(@"(root, wanted) => {
                            const WANT = new Set((wanted||[]).map(s => (s||'').toString().trim().toUpperCase()));
                            const out = {};
                            const nodes = root.querySelectorAll('div.tabellaQuoteNew .contenitoreSingolaQuota, div.gridInterernaQuotazioni .contenitoreSingolaQuota');
                            for (const n of nodes) {
                                const k = (n.querySelector('.titoloQuotazione')?.textContent || '').trim().toUpperCase();
                                const v = (n.querySelector('.tipoQuotazione_1')?.textContent || '').trim();
                                if (!k || !v) continue;
                                if (WANT.has(k)) out[k] = v;
                            }
                            return JSON.stringify(out);
                        }", need);

                                using var ensure = JsonDocument.Parse(json);
                                foreach (var kv in ensure.RootElement.EnumerateObject())
                                {
                                    var k = kv.Name;
                                    var v = kv.Value.GetString() ?? "";
                                    if (!string.IsNullOrWhiteSpace(v) && !odds.ContainsKey(k))
                                        odds[k] = v;
                                }

                                if (odds.TryGetValue("GOL", out var g2) && !odds.ContainsKey("GOAL")) odds["GOAL"] = g2;
                                if (odds.TryGetValue("NOGOL", out var ng2) && !odds.ContainsKey("NOGOAL")) odds["NOGOAL"] = ng2;
                            }
                            catch { }
                        }
                    }
                }
                catch { }

               var hcp = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
var ou  = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

// OLD SCRAPER BEHAVIOR — keep flags separate and run the same block for Tennis and Basketball
bool doTennisExtractions  = isTennis;
bool doBasketExtractions  = isBasket;



                if(doTennisExtractions || doBasketExtractions)
                {
                    var row = match.Locator("xpath=ancestor::div[contains(@class,'contenitoreRiga')]").First;
                    var buttons = row.Locator("button.dropdown-toggle");
                    int btnCount = 0; try { btnCount = await buttons.CountAsync(); } catch { }

                    static async Task CloseMenuAsync(ILocator btn)
                    { try { await btn.ClickAsync(new() { Force = true, Timeout = 300 }); } catch { } }

                    if (btnCount >= 1)
                    {
                        var btnOU = buttons.Nth(0);
                        try
                        {
                            if (await btnOU.IsEnabledAsync(new() { Timeout = 250 }))
                            {
                                await btnOU.ScrollIntoViewIfNeededAsync();
                                await btnOU.ClickAsync(new() { Timeout = 500 });
                                try { await page.WaitForSelectorAsync(".dropdown-menu.show .lista-quote", new() { State = WaitForSelectorState.Visible, Timeout = DROPDOWN_WAIT_MS }); } catch { }
                                await page.WaitForTimeoutAsync(80);

                                var ouTuples = await ParseNearestOUTotalMenuAsync(page, btnOU);
                                if (ouTuples != null && ouTuples.Count > 0)
                                {
                                    foreach (var (raw, under, over) in ouTuples)
                                    {
                                        var line = NormalizeOUTotalLine(raw);
                                        if (string.IsNullOrWhiteSpace(line)) continue;
                                        var sides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                                        if (!string.IsNullOrWhiteSpace(under)) sides["UNDER"] = under.Trim();
                                        if (!string.IsNullOrWhiteSpace(over)) sides["OVER"] = over.Trim();
                                        if (sides.Count > 0) ou[line] = sides;
                                    }
                                }
                                else
                                {
                                    var ttTuples = await ParseNearestTTMenuAsync(page, btnOU);
                                    if (ttTuples != null && ttTuples.Count > 0)
                                    {
                                        foreach (var (raw, one, two) in ttTuples)
                                        {
                                            var line = NormalizeHcpLine(raw);
                                            if (string.IsNullOrWhiteSpace(line)) continue;
                                            var sides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                                            if (!string.IsNullOrWhiteSpace(one)) sides["1"] = one.Trim();
                                            if (!string.IsNullOrWhiteSpace(two)) sides["2"] = two.Trim();
                                            if (sides.Count > 0) hcp[line] = sides;
                                        }
                                    }
                                }
                                await CloseMenuAsync(btnOU);
                            }
                        }
                        catch { }
                    }

                    if (btnCount >= 2)
                    {
                        var btnTT = buttons.Nth(1);
                        try
                        {
                            if (await btnTT.IsEnabledAsync(new() { Timeout = 250 }))
                            {
                                await btnTT.ScrollIntoViewIfNeededAsync();
                                await btnTT.ClickAsync(new() { Timeout = 500 });
                                try { await page.WaitForSelectorAsync(".dropdown-menu.show .lista-quote.colonne-3, .dropdown-menu.show .lista-quote", new() { State = WaitForSelectorState.Visible, Timeout = DROPDOWN_WAIT_MS }); } catch { }
                                await page.WaitForTimeoutAsync(80);

                                var ttTuples = await ParseNearestTTMenuAsync(page, btnTT);
                                if (ttTuples != null && ttTuples.Count > 0)
                                {
                                    foreach (var (raw, one, two) in ttTuples)
                                    {
                                        var line = NormalizeHcpLine(raw);
                                        if (string.IsNullOrWhiteSpace(line)) continue;
                                        var sides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                                        if (!string.IsNullOrWhiteSpace(one)) sides["1"] = one.Trim();
                                        if (!string.IsNullOrWhiteSpace(two)) sides["2"] = two.Trim();
                                        if (sides.Count > 0) hcp[line] = sides;
                                    }
                                }
                                else
                                {
                                    var ouTuples = await ParseNearestOUTotalMenuAsync(page, btnTT);
                                    if (ouTuples != null && ouTuples.Count > 0)
                                    {
                                        foreach (var (raw, under, over) in ouTuples)
                                        {
                                            var line = NormalizeOUTotalLine(raw);
                                            if (string.IsNullOrWhiteSpace(line)) continue;
                                            var sides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                                            if (!string.IsNullOrWhiteSpace(under)) sides["UNDER"] = under.Trim();
                                            if (!string.IsNullOrWhiteSpace(over)) sides["OVER"] = over.Trim();
                                            if (sides.Count > 0) ou[line] = sides;
                                        }
                                    }
                                }
                                await CloseMenuAsync(btnTT);
                            }
                        }
                        catch { }
                    }
                }

                // === NEW: sport-specific enrichments (dropdown / inline) ===
                try
                {
                    if (isRugby)
                    {
                        await EnrichRugbyOUAsync(page, match, ou);
                    }
                    else if (isAmericanFootball)
                    {
                        // Handicap first (gives you many lines), then O/U lines
                        await EnrichAmericanFootballHandicapInRowAsync(page, match, hcp);
                        await EnrichAmericanFootballOUInRowAsync(page, match, ou);
                    }
                    else if (isBaseball)
                    {
                        await EnrichBaseballHandicapInRowAsync(page, match, hcp);
                    }
                    else if (isIceHockey)
                    {
                        await EnrichIceHockeyOUInRowAsync(page, match, ou);
                        await EnrichIceHockeyHandicapInRowAsync(page, match, hcp);
                    }
                }
                catch { /* non-fatal: keep going */ }




                // LOG
                string baseOdds = odds.Count > 0 ? string.Join(" | ", odds.Select(o => $"{o.Key}: {o.Value}")) : "No odds available";
                string hcpSummary = BuildHcpSummary(hcp);
                string ouSummary = BuildOUSummary(ou);

                Console.WriteLine($"Match: {teamNames}");
                Console.WriteLine(
                    "Odds: " + baseOdds +
                    (string.IsNullOrWhiteSpace(ouSummary) ? "" : $" | O/U: {ouSummary}") +
                    (string.IsNullOrWhiteSpace(hcpSummary) ? " | T/T + Handicap: -" : $" | T/T + Handicap: {hcpSummary}")
                );

                // SNAPSHOT before storing (avoid later mutations)
                var oddsSnap = new Dictionary<string, string>(odds);

                var hcpSnap = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
                foreach (var kv in hcp)
                    hcpSnap[kv.Key] = new Dictionary<string, string>(kv.Value, StringComparer.OrdinalIgnoreCase);

                var ouSnap = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
                foreach (var kv in ou)
                    ouSnap[kv.Key] = new Dictionary<string, string>(kv.Value, StringComparer.OrdinalIgnoreCase);

                matchesList.Add(new MatchData
                {
                    Teams = teamNames,
                    Odds = oddsSnap,
                    TTPlusHandicap = hcpSnap,   // kept for logs/debug; NOT emitted in JSON (see projection below)
                    OUTotals = ouSnap           // kept for logs/debug; NOT emitted in JSON (see projection below)
                });
            }

            Console.WriteLine($"\n Fixtures loaded: {validMatchCount} matches found.");

            // === JSON with custom keys: "O/U" and "TT + handicap" ===
            // === Eurobet-identical master JSON ===
            var eurobetShape = new List<Dictionary<string, object>>();

            foreach (var m in matchesList)
{
    // Only emit American Football
    bool isAmericanFootball =
        fileName.Contains("Football Americano", StringComparison.OrdinalIgnoreCase) ||
        fileName.Contains("American Football", StringComparison.OrdinalIgnoreCase) ||
        fileName.Contains("NFL", StringComparison.OrdinalIgnoreCase);

    if (!isAmericanFootball)
        continue; // skip any non-AF page data

    // AF payload: keep Handicap + O/U and set the correct sport label
    string sportLabel = "americanfootball";
    bool isSoccer = false; // ensures handicap is KEPT

    var payloadObj = BuildClientPayload(
        teams: m.Teams,
        sportLabel: sportLabel,
        isSoccer: isSoccer,
        baseOdds: m.Odds ?? new Dictionary<string, string>(),
        ouTotals: m.OUTotals ?? new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase),
        oneTwoHandicapLines: m.TTPlusHandicap ?? new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase)
    );

    eurobetShape.Add(payloadObj);
}


            // === POST to DB (Eurobet-shaped payloads) ===
            int posted = 0, failed = 0;
            foreach (var payload in eurobetShape)
            {
                string teams = payload.TryGetValue("Teams", out var tObj) ? Convert.ToString(tObj) ?? "?" : "?";
                var (ok, respBody) = await PostJsonWithRetryAsync(POST_ENDPOINT, payload);
                if (ok) posted++; else failed++;
                Console.WriteLine($"{(ok ? "✅" : "❌")} [Domusbet] {teams} → {respBody}");
                await Task.Delay(250); // be nice to the endpoint
            }
            Console.WriteLine($"POST summary [Domusbet]: {posted} ok, {failed} failed.");

            // keep your JSON export
            var jsonOutput = JsonSerializer.Serialize(
                eurobetShape,
                new JsonSerializerOptions
                {
                    WriteIndented = true,
                    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                });

            await File.WriteAllTextAsync(fileName, jsonOutput, Encoding.UTF8);
            Console.WriteLine($"\n✅ Eurobet-identical JSON exported to {fileName}");


        }

        // --- Rugby: collect only O/U for the current row ---
        private static async Task EnrichRugbyOUAsync(IPage page, ILocator matchRow, Dictionary<string, Dictionary<string, string>> ou)
        {
            var row = matchRow.Locator("xpath=ancestor::div[contains(@class,'contenitoreRiga')]").First;

            // 1) Inline O/U (selected line visible in grid)
            var inline = await ExtractInlineOUTotalsAsync(row);
            if (inline is { } gotInline)
            {
                var sides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (!string.IsNullOrWhiteSpace(gotInline.under)) sides["UNDER"] = gotInline.under;
                if (!string.IsNullOrWhiteSpace(gotInline.over)) sides["OVER"] = gotInline.over;
                if (sides.Count > 0) ou[NormalizeOUTotalLine(gotInline.line)] = sides;
            }

            // 2) Dropdown O/U (if present) → collect all lines
            var ouBtn = row.Locator("button.dropdown-toggle:has(span.uo-selezionato)");
            if (!await ouBtn.IsVisibleAsync(new() { Timeout = 400 }))
                ouBtn = row.Locator("button.dropdown-toggle").First;

            try
            {
                if (await ouBtn.IsVisibleAsync(new() { Timeout = 400 }))
                {
                    await ouBtn.ClickAsync(new() { Timeout = 1200 });
                    try { await page.WaitForSelectorAsync(".dropdown-menu.show", new() { Timeout = 800, State = WaitForSelectorState.Visible }); } catch { }
                    var tuples = await ParseNearestOUTotalMenuAsync(page, ouBtn);
                    foreach (var (raw, under, over) in tuples)
                    {
                        var line = NormalizeOUTotalLine(raw);
                        if (string.IsNullOrWhiteSpace(line)) continue;

                        var sides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        if (!string.IsNullOrWhiteSpace(under)) sides["UNDER"] = under.Trim();
                        if (!string.IsNullOrWhiteSpace(over)) sides["OVER"] = over.Trim();
                        if (sides.Count > 0) ou[line] = sides;
                    }
                }
            }
            catch { }
            finally { try { await ouBtn.ClickAsync(new() { Timeout = 300, Force = true }); } catch { } }
        }

        // --- Tennis: Handicap (1/2) for current row ---
private static async Task EnrichTennisHandicapInRowAsync(
    IPage page, ILocator matchRow, Dictionary<string, Dictionary<string, string>> hcp)
{
    var row = matchRow.Locator("xpath=ancestor::div[contains(@class,'contenitoreRiga')]").First;

    // A) Inline (selected handicap without arrow)
    var inline = await ExtractInlineHcpNoDropdownAsync(row);
    if (inline is { } h)
    {
        var sides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(h.one)) sides["1"] = h.one;
        if (!string.IsNullOrWhiteSpace(h.two)) sides["2"] = h.two;
        if (sides.Count > 0) hcp[h.line] = sides;
    }

    // B) Dropdown (first visible → parse all handicap lines 1/2)
    var buttons = row.Locator("button.dropdown-toggle");
    int btnCount = 0; try { btnCount = await buttons.CountAsync(); } catch { }
    for (int b = 0; b < btnCount; b++)
    {
        var btn = buttons.Nth(b);
        try
        {
            if (!await btn.IsVisibleAsync(new() { Timeout = 400 })) continue;

            await btn.ClickAsync(new() { Timeout = 1200 });
            try { await page.WaitForSelectorAsync(".dropdown-menu.show", new() { Timeout = 800, State = WaitForSelectorState.Visible }); } catch { }

            var tuples = await ParseNearestTTMenuAsync(page, btn); // (raw, 1, 2)
            if (tuples.Count == 0) continue;

            foreach (var (raw, one, two) in tuples)
            {
                var line = NormalizeHcpLine(raw);
                if (string.IsNullOrWhiteSpace(line)) continue;
                var sides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (!string.IsNullOrWhiteSpace(one)) sides["1"] = one.Trim();
                if (!string.IsNullOrWhiteSpace(two)) sides["2"] = two.Trim();
                if (sides.Count > 0) hcp[line] = sides;
            }
            break; // one dropdown per row is enough
        }
        catch { }
        finally { try { await btn.ClickAsync(new() { Timeout = 300, Force = true }); } catch { } }
    }
}

// --- Tennis: O/U for current row ---
private static async Task EnrichTennisOUInRowAsync(
    IPage page, ILocator matchRow, Dictionary<string, Dictionary<string, string>> ou)
{
    var row = matchRow.Locator("xpath=ancestor::div[contains(@class,'contenitoreRiga')]").First;

    // A) Inline selected O/U (when visible)
    var inline = await ExtractInlineOUTotalsAsync(row);
    if (inline is { } got)
    {
        var sides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(got.under)) sides["UNDER"] = got.under;
        if (!string.IsNullOrWhiteSpace(got.over)) sides["OVER"] = got.over;
        if (sides.Count > 0) ou[NormalizeOUTotalLine(got.line)] = sides;
    }

    // B) Dropdown O/U (closest open menu to button)
    var ouBtn = row.Locator("button.dropdown-toggle:has(span.uo-selezionato)");
    if (!await ouBtn.IsVisibleAsync(new() { Timeout = 400 }))
        ouBtn = row.Locator("button.dropdown-toggle").First;

    try
    {
        if (await ouBtn.IsVisibleAsync(new() { Timeout = 400 }))
        {
            await ouBtn.ClickAsync(new() { Timeout = 1200 });
            try { await page.WaitForSelectorAsync(".dropdown-menu.show", new() { Timeout = 800, State = WaitForSelectorState.Visible }); } catch { }

            var tuples = await ParseNearestOUTotalMenuAsync(page, ouBtn);
            foreach (var (raw, under, over) in tuples)
            {
                var line = NormalizeOUTotalLine(raw);
                if (string.IsNullOrWhiteSpace(line)) continue;
                var sides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (!string.IsNullOrWhiteSpace(under)) sides["UNDER"] = under.Trim();
                if (!string.IsNullOrWhiteSpace(over)) sides["OVER"] = over.Trim();
                if (sides.Count > 0) ou[line] = sides;
            }
        }
    }
    catch { }
    finally { try { await ouBtn.ClickAsync(new() { Timeout = 300, Force = true }); } catch { } }
}


        // --- American Football: Handicap (1/2) for current row ---
        private static async Task EnrichAmericanFootballHandicapInRowAsync(IPage page, ILocator matchRow, Dictionary<string, Dictionary<string, string>> hcp)
        {
            var row = matchRow.Locator("xpath=ancestor::div[contains(@class,'contenitoreRiga')]").First;

            // Inline selected handicap (no arrow)
            var inline = await ExtractInlineHcpNoDropdownAsync(row);
            if (inline is { } h)
            {
                var sides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (!string.IsNullOrWhiteSpace(h.one)) sides["1"] = h.one;
                if (!string.IsNullOrWhiteSpace(h.two)) sides["2"] = h.two;
                if (sides.Count > 0) hcp[h.line] = sides;
            }

            // Dropdown handicap(s)
            var buttons = row.Locator("button.dropdown-toggle");
            int btnCount = 0; try { btnCount = await buttons.CountAsync(); } catch { }
            for (int b = 0; b < btnCount; b++)
            {
                var btn = buttons.Nth(b);
                try
                {
                    if (!await btn.IsVisibleAsync(new() { Timeout = 400 })) continue;
                    await btn.ClickAsync(new() { Timeout = 1200 });
                    try { await page.WaitForSelectorAsync(".dropdown-menu.show", new() { Timeout = 800, State = WaitForSelectorState.Visible }); } catch { }

                    var tuples = await ParseNearestTTMenuAsync(page, btn); // (raw, 1, 2)
                    foreach (var (raw, one, two) in tuples)
                    {
                        var line = NormalizeHcpLine(raw);
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        var sides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        if (!string.IsNullOrWhiteSpace(one)) sides["1"] = one.Trim();
                        if (!string.IsNullOrWhiteSpace(two)) sides["2"] = two.Trim();
                        if (sides.Count > 0) hcp[line] = sides;
                    }
                    break; // one dropdown per row is enough
                }
                catch { }
                finally { try { await btn.ClickAsync(new() { Timeout = 300, Force = true }); } catch { } }
            }
        }

        // --- American Football: O/U for current row ---
        private static async Task EnrichAmericanFootballOUInRowAsync(IPage page, ILocator matchRow, Dictionary<string, Dictionary<string, string>> ou)
        {
            var row = matchRow.Locator("xpath=ancestor::div[contains(@class,'contenitoreRiga')]").First;

            // Inline O/U
            var inline = await ExtractInlineOUTotalsAsync(row);
            if (inline is { } got)
            {
                var sides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (!string.IsNullOrWhiteSpace(got.under)) sides["UNDER"] = got.under;
                if (!string.IsNullOrWhiteSpace(got.over)) sides["OVER"] = got.over;
                if (sides.Count > 0) ou[NormalizeOUTotalLine(got.line)] = sides;
            }

            // Dropdown O/U
            var ouBtn = row.Locator("button.dropdown-toggle:has(span.uo-selezionato)");
            if (!await ouBtn.IsVisibleAsync(new() { Timeout = 400 }))
                ouBtn = row.Locator("button.dropdown-toggle").First;

            try
            {
                if (await ouBtn.IsVisibleAsync(new() { Timeout = 400 }))
                {
                    await ouBtn.ClickAsync(new() { Timeout = 1200 });
                    try { await page.WaitForSelectorAsync(".dropdown-menu.show", new() { Timeout = 800, State = WaitForSelectorState.Visible }); } catch { }
                    var tuples = await ParseNearestOUTotalMenuAsync(page, ouBtn);
                    foreach (var (raw, under, over) in tuples)
                    {
                        var line = NormalizeOUTotalLine(raw);
                        if (string.IsNullOrWhiteSpace(line)) continue;

                        var sides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        if (!string.IsNullOrWhiteSpace(under)) sides["UNDER"] = under.Trim();
                        if (!string.IsNullOrWhiteSpace(over)) sides["OVER"] = over.Trim();
                        if (sides.Count > 0) ou[line] = sides;
                    }
                }
            }
            catch { }
            finally { try { await ouBtn.ClickAsync(new() { Timeout = 300, Force = true }); } catch { } }
        }

        // --- Basketball: Handicap (1/2) for current row ---
        private static async Task EnrichBasketballHandicapInRowAsync(
            IPage page, ILocator matchRow, Dictionary<string, Dictionary<string, string>> hcp)
        {
            var row = matchRow.Locator("xpath=ancestor::div[contains(@class,'contenitoreRiga')]").First;

            // A) Inline (selected handicap without arrow)
            var inline = await ExtractInlineHcpNoDropdownAsync(row);
            if (inline is { } h)
            {
                var sides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (!string.IsNullOrWhiteSpace(h.one)) sides["1"] = h.one;
                if (!string.IsNullOrWhiteSpace(h.two)) sides["2"] = h.two;
                if (sides.Count > 0) hcp[h.line] = sides;
            }

            // B) Dropdown (first visible → parse all handicap lines 1/2)
            var buttons = row.Locator("button.dropdown-toggle");
            int btnCount = 0; try { btnCount = await buttons.CountAsync(); } catch { }
            for (int b = 0; b < btnCount; b++)
            {
                var btn = buttons.Nth(b);
                try
                {
                    if (!await btn.IsVisibleAsync(new() { Timeout = 400 })) continue;

                    await btn.ClickAsync(new() { Timeout = 1200 });
                    try { await page.WaitForSelectorAsync(".dropdown-menu.show", new() { Timeout = 800, State = WaitForSelectorState.Visible }); } catch { }

                    var tuples = await ParseNearestTTMenuAsync(page, btn); // (raw, 1, 2)
                    if (tuples.Count == 0) continue;

                    foreach (var (raw, one, two) in tuples)
                    {
                        var line = NormalizeHcpLine(raw);
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        var sides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        if (!string.IsNullOrWhiteSpace(one)) sides["1"] = one.Trim();
                        if (!string.IsNullOrWhiteSpace(two)) sides["2"] = two.Trim();
                        if (sides.Count > 0) hcp[line] = sides;
                    }
                    break; // one dropdown per row is enough
                }
                catch { }
                finally { try { await btn.ClickAsync(new() { Timeout = 300, Force = true }); } catch { } }
            }
        }

        // --- Basketball: O/U for current row ---
        private static async Task EnrichBasketballOUInRowAsync(
            IPage page, ILocator matchRow, Dictionary<string, Dictionary<string, string>> ou)
        {
            var row = matchRow.Locator("xpath=ancestor::div[contains(@class,'contenitoreRiga')]").First;

            // A) Inline selected O/U (when visible)
            var inline = await ExtractInlineOUTotalsAsync(row);
            if (inline is { } got)
            {
                var sides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (!string.IsNullOrWhiteSpace(got.under)) sides["UNDER"] = got.under;
                if (!string.IsNullOrWhiteSpace(got.over)) sides["OVER"] = got.over;
                if (sides.Count > 0) ou[NormalizeOUTotalLine(got.line)] = sides;
            }

            // B) Dropdown O/U (closest open menu to button)
            var ouBtn = row.Locator("button.dropdown-toggle:has(span.uo-selezionato)");
            if (!await ouBtn.IsVisibleAsync(new() { Timeout = 400 }))
                ouBtn = row.Locator("button.dropdown-toggle").First;

            try
            {
                if (await ouBtn.IsVisibleAsync(new() { Timeout = 400 }))
                {
                    await ouBtn.ClickAsync(new() { Timeout = 1200 });
                    try { await page.WaitForSelectorAsync(".dropdown-menu.show", new() { Timeout = 800, State = WaitForSelectorState.Visible }); } catch { }

                    var tuples = await ParseNearestOUTotalMenuAsync(page, ouBtn);
                    foreach (var (raw, under, over) in tuples)
                    {
                        var line = NormalizeOUTotalLine(raw);
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        var sides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        if (!string.IsNullOrWhiteSpace(under)) sides["UNDER"] = under.Trim();
                        if (!string.IsNullOrWhiteSpace(over)) sides["OVER"] = over.Trim();
                        if (sides.Count > 0) ou[line] = sides;
                    }
                }
            }
            catch { }
            finally { try { await ouBtn.ClickAsync(new() { Timeout = 300, Force = true }); } catch { } }
        }

        // --- Baseball: Handicap (1/2) for current row ---
        private static async Task EnrichBaseballHandicapInRowAsync(IPage page, ILocator matchRow, Dictionary<string, Dictionary<string, string>> hcp)
        {
            var row = matchRow.Locator("xpath=ancestor::div[contains(@class,'contenitoreRiga')]").First;

            // Inline
            var inline = await ExtractInlineHcpNoDropdownAsync(row);
            if (inline is { } h)
            {
                var sides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (!string.IsNullOrWhiteSpace(h.one)) sides["1"] = h.one;
                if (!string.IsNullOrWhiteSpace(h.two)) sides["2"] = h.two;
                if (sides.Count > 0) hcp[h.line] = sides;
            }

            // Dropdown (first visible)
            var buttons = row.Locator("button.dropdown-toggle");
            int btnCount = 0; try { btnCount = await buttons.CountAsync(); } catch { }
            for (int b = 0; b < btnCount; b++)
            {
                var btn = buttons.Nth(b);
                try
                {
                    if (!await btn.IsVisibleAsync(new() { Timeout = 400 })) continue;
                    await btn.ClickAsync(new() { Timeout = 1200 });
                    try { await page.WaitForSelectorAsync(".dropdown-menu.show", new() { Timeout = 800, State = WaitForSelectorState.Visible }); } catch { }

                    var tuples = await ParseNearestTTMenuAsync(page, btn);
                    foreach (var (raw, one, two) in tuples)
                    {
                        var line = NormalizeHcpLine(raw);
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        var sides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        if (!string.IsNullOrWhiteSpace(one)) sides["1"] = one.Trim();
                        if (!string.IsNullOrWhiteSpace(two)) sides["2"] = two.Trim();
                        if (sides.Count > 0) hcp[line] = sides;
                    }
                    break;
                }
                catch { }
                finally { try { await btn.ClickAsync(new() { Timeout = 300, Force = true }); } catch { } }
            }
        }

        // --- Ice Hockey: O/U for current row ---
        private static async Task EnrichIceHockeyOUInRowAsync(IPage page, ILocator matchRow, Dictionary<string, Dictionary<string, string>> ou)
        {
            var row = matchRow.Locator("xpath=ancestor::div[contains(@class,'contenitoreRiga')]").First;

            // Inline
            var inline = await ExtractInlineOUTotalsAsync(row);
            if (inline is { } got)
            {
                var sides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (!string.IsNullOrWhiteSpace(got.under)) sides["UNDER"] = got.under;
                if (!string.IsNullOrWhiteSpace(got.over)) sides["OVER"] = got.over;
                if (sides.Count > 0) ou[NormalizeOUTotalLine(got.line)] = sides;
            }

            // Dropdown near button
            var btn = row.Locator(".gridInterernaQuotazioni button.dropdown-toggle:not(.dropdown-toggle-no-arrow)").First;
            try
            {
                if (await btn.IsVisibleAsync(new() { Timeout = 400 }))
                {
                    await btn.ClickAsync(new() { Timeout = 1200 });
                    try { await page.WaitForSelectorAsync(".dropdown-menu.show", new() { Timeout = 800, State = WaitForSelectorState.Visible }); } catch { }
                    var tuples = await ParseNearestOUTotalMenuAsync(page, btn);
                    foreach (var (raw, under, over) in tuples)
                    {
                        var line = NormalizeOUTotalLine(raw);
                        if (string.IsNullOrWhiteSpace(line)) continue;

                        var sides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        if (!string.IsNullOrWhiteSpace(under)) sides["UNDER"] = under.Trim();
                        if (!string.IsNullOrWhiteSpace(over)) sides["OVER"] = over.Trim();
                        if (sides.Count > 0) ou[line] = sides;
                    }
                }
            }
            catch { }
            finally { try { await btn.ClickAsync(new() { Timeout = 300, Force = true }); } catch { } }
        }

        // --- Ice Hockey: Handicap (1/2) for current row ---
        private static async Task EnrichIceHockeyHandicapInRowAsync(IPage page, ILocator matchRow, Dictionary<string, Dictionary<string, string>> hcp)
        {
            var row = matchRow.Locator("xpath=ancestor::div[contains(@class,'contenitoreRiga')]").First;

            // Inline
            var inline = await ExtractInlineHcpNoDropdownAsync(row);
            if (inline is { } h)
            {
                var sides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (!string.IsNullOrWhiteSpace(h.one)) sides["1"] = h.one;
                if (!string.IsNullOrWhiteSpace(h.two)) sides["2"] = h.two;
                if (sides.Count > 0) hcp[h.line] = sides;
            }

            // Dropdown (first visible)
            var buttons = row.Locator("button.dropdown-toggle");
            int btnCount = 0; try { btnCount = await buttons.CountAsync(); } catch { }
            for (int b = 0; b < btnCount; b++)
            {
                var btn = buttons.Nth(b);
                try
                {
                    if (!await btn.IsVisibleAsync(new() { Timeout = 400 })) continue;
                    await btn.ClickAsync(new() { Timeout = 1200 });
                    try { await page.WaitForSelectorAsync(".dropdown-menu.show", new() { Timeout = 800, State = WaitForSelectorState.Visible }); } catch { }

                    var tuples = await ParseNearestTTMenuAsync(page, btn); // (raw, 1, 2)
                    foreach (var (raw, one, two) in tuples)
                    {
                        var line = NormalizeHcpLine(raw);
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        var sides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        if (!string.IsNullOrWhiteSpace(one)) sides["1"] = one.Trim();
                        if (!string.IsNullOrWhiteSpace(two)) sides["2"] = two.Trim();
                        if (sides.Count > 0) hcp[line] = sides;
                    }
                    break;
                }
                catch { }
                finally { try { await btn.ClickAsync(new() { Timeout = 300, Force = true }); } catch { } }
            }
        }


        // Extract inline selected O/U line and its UNDER/OVER odds from the visible row
        private static async Task<(string line, string under, string over)?> ExtractInlineOUTotalsAsync(ILocator rowContainer)
        {
            try
            {
                var lineSpan = rowContainer.Locator(".gridInterernaQuotazioni span.uo-selezionato");
                if (!await lineSpan.IsVisibleAsync(new() { Timeout = 400 })) return null;

                var rawLine = (await lineSpan.InnerTextAsync())?.Trim() ?? "";
                var line = NormalizeOUTotalLine(rawLine);
                if (string.IsNullOrWhiteSpace(line)) return null;

                var cells = rowContainer.Locator(".gridInterernaQuotazioni .contenitoreSingolaQuota");
                int count = 0; try { count = await cells.CountAsync(); } catch { }
                if (count == 0) return null;

                string u = "", o = "";
                for (int i = 0; i < count; i++)
                {
                    try
                    {
                        var cell = cells.Nth(i);
                        var lab = ((await cell.Locator(".titoloQuotazione").InnerTextAsync()) ?? "").Trim().ToUpperInvariant();
                        var val = ((await cell.Locator(".tipoQuotazione_1").InnerTextAsync()) ?? "").Trim();
                        if (lab == "UNDER" && string.IsNullOrEmpty(u)) u = val;
                        else if (lab == "OVER" && string.IsNullOrEmpty(o)) o = val;
                    }
                    catch { }
                }

                if (string.IsNullOrWhiteSpace(u) && string.IsNullOrWhiteSpace(o)) return null;
                return (line, u, o);
            }
            catch { return null; }
        }

        // Extract inline selected Handicap (no dropdown arrow) for current row
        private static async Task<(string line, string one, string two)?> ExtractInlineHcpNoDropdownAsync(ILocator rowContainer)
        {
            try
            {
                var btnNoArrow = rowContainer.Locator(".gridInterernaQuotazioni > button.dropdown-toggle-no-arrow");
                if (!await btnNoArrow.IsVisibleAsync(new() { Timeout = 400 })) return null;

                var lineSpan = btnNoArrow.Locator("span.uo-selezionato");
                if (!await lineSpan.IsVisibleAsync(new() { Timeout = 400 })) return null;

                var rawLine = (await lineSpan.InnerTextAsync())?.Trim() ?? "";
                var line = NormalizeHcpLine(rawLine);
                if (string.IsNullOrWhiteSpace(line)) return null;

                var cells = rowContainer.Locator(".gridInterernaQuotazioni .contenitoreSingolaQuota");
                int count = 0; try { count = await cells.CountAsync(); } catch { }
                if (count == 0) return null;

                string one = "", two = "";
                for (int i = 0; i < count; i++)
                {
                    try
                    {
                        var cell = cells.Nth(i);
                        var lab = ((await cell.Locator(".titoloQuotazione").InnerTextAsync()) ?? "").Trim().ToUpperInvariant();
                        var val = ((await cell.Locator(".tipoQuotazione_1").InnerTextAsync()) ?? "").Trim();
                        if (lab == "1" && string.IsNullOrEmpty(one)) one = val;
                        else if (lab == "2" && string.IsNullOrEmpty(two)) two = val;
                    }
                    catch { }
                }

                if (string.IsNullOrWhiteSpace(one) && string.IsNullOrWhiteSpace(two)) return null;
                return (line, one, two);
            }
            catch { return null; }
        }

        private static async Task HandleRandomPopup(IPage page)
        {
            try
            {
                var overlay = page.Locator(".kumulos-background-mask").First;
                var prompt = page.Locator(".kumulos-prompt").First;

                bool overlayVisible = false;
                bool promptVisible = false;

                try { overlayVisible = await overlay.IsVisibleAsync(new() { Timeout = 500 }); } catch { }
                try { promptVisible = await prompt.IsVisibleAsync(new() { Timeout = 500 }); } catch { }

                if (!overlayVisible && !promptVisible)
                    return;

                Console.WriteLine("Popup detected. Attempting to dismiss...");

                var dismissButton = page.GetByText("Non ora");
                if (await dismissButton.IsVisibleAsync(new() { Timeout = 500 }))
                {
                    await dismissButton.ClickAsync(new() { Force = true });
                    Console.WriteLine("Clicked 'Non ora' dismiss button.");
                }
                else
                {
                    var promptButton = prompt.Locator("button").First;
                    if (await promptButton.IsVisibleAsync(new() { Timeout = 500 }))
                    {
                        await promptButton.ClickAsync(new() { Force = true });
                        Console.WriteLine("Clicked popup button.");
                    }
                    else if (overlayVisible)
                    {
                        Console.WriteLine("No dismiss button found. Clicking overlay...");
                        await overlay.ClickAsync(new() { Force = true });
                    }
                }

                await page.WaitForTimeoutAsync(500);
                Console.WriteLine("Popup dismissed or ignored. Continuing...");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Popup handler error: {ex.Message} (continuing anyway)");
            }
        }
    }
}
