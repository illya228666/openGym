using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using OpenGym.Nutrition.Api;

namespace OpenGym.Nutrition.Services;

/// <summary>Public OFF data only. A singleton owns bounded caches, in-flight reads and IP budgets.</summary>
public sealed class OpenFoodFacts(IHttpClientFactory clients, TimeProvider clock) : IDisposable
{
    public const string ClientName = "OpenFoodFacts";
    public const string Source = "openfoodfacts";
    private readonly MemoryCache cache = new(new MemoryCacheOptions { SizeLimit = 1000 });
    private readonly object gate = new();
    private readonly Dictionary<string, Task<Reply>> flights = [];
    private readonly Queue<DateTimeOffset> searches = new(), products = new();
    private DateTimeOffset blockedUntil;
    private sealed record Reply(JsonElement Body, DateTimeOffset RetrievedAt, bool Missing = false);

    public async Task<ExternalProductPage> Search(string? search, string? brand, string? barcode, int page, CancellationToken ct)
    {
        if (page is < 1 or > 100 || search?.Length > 200 || brand?.Length > 100
            || (barcode is not null && !ProductSearch.ValidCode(barcode))) throw new NutritionException(400, "invalid_input");
        if (barcode is not null)
        {
            try
            {
                var p = await Product(barcode, ct);
                return new([new(Source, p.ExternalId, p.Name, p.Brand, p.PackageQuantity, p.SourceUrl)], false, page);
            }
            catch (NutritionException e) when (e.Code == "external_product_not_found") { return new([], false, page); }
        }
        var term = ProductSearch.Normalize(search ?? "");
        var brandTerm = ProductSearch.Normalize(brand ?? "");
        if (term.Length == 0 && brandTerm.Length == 0) throw new NutritionException(400, "invalid_input");
        var path = "/cgi/search.pl?search_simple=1&action=process&json=1&page_size=20&page=" + page
            + "&search_terms=" + Uri.EscapeDataString(term)
            + "&fields=code,product_name,brands,quantity";
        if (brandTerm.Length > 0) path += "&tagtype_0=brands&tag_contains_0=contains&tag_0=" + Uri.EscapeDataString(brandTerm);
        var reply = await Read(path, true, ct);
        if (!reply.Body.TryGetProperty("products", out var rows) || rows.ValueKind != JsonValueKind.Array
            || rows.GetArrayLength() > 20) throw new NutritionException(502, "external_invalid_response");
        var result = new List<ExternalProductCandidate>();
        foreach (var row in rows.EnumerateArray())
        {
            var code = Text(row, "code");
            var name = Text(row, "product_name");
            if (!ProductSearch.ValidCode(code) || string.IsNullOrWhiteSpace(name)) continue;
            code = ProductSearch.CanonicalCode(code!);
            result.Add(new(Source, code!, name, Text(row, "brands"), Text(row, "quantity"), SourceUrl(code!)));
        }
        // Missing counts or discarded candidates must not make an incomplete page look unambiguous.
        var count = reply.Body.TryGetProperty("count", out var n) && n.ValueKind == JsonValueKind.Number && n.TryGetInt64(out var total) ? total : (long?)null;
        return new(result, result.Count != rows.GetArrayLength() || count is null || count > page * 20L, page);
    }

    public async Task<ExternalProductResponse> Product(string code, CancellationToken ct, bool refresh = false)
    {
        if (!ProductSearch.ValidCode(code)) throw new NutritionException(400, "invalid_input");
        code = ProductSearch.CanonicalCode(code);
        var reply = await Read("/api/v3.6/product/" + code + "?fields=code,product_name,brands,quantity,nutrition,data_quality_errors_tags,data_quality_warnings_tags,obsolete", false, ct, refresh);
        if (reply.Missing) throw new NutritionException(404, "external_product_not_found");
        if (!reply.Body.TryGetProperty("product", out var product) || product.ValueKind != JsonValueKind.Object
            || Text(product, "code") != code) throw new NutritionException(502, "external_invalid_response");
        return Parse(product, reply.RetrievedAt);
    }

    private async Task<Reply> Read(string path, bool search, CancellationToken ct, bool refresh = false)
    {
        Task<Reply> pending;
        lock (gate)
        {
            if (!refresh && cache.TryGetValue(path, out Reply? cached)
                && clock.GetUtcNow() - cached!.RetrievedAt < TimeSpan.FromMinutes(cached.Missing ? 1 : search ? 5 : 15)) return cached;
            if (!flights.TryGetValue(path, out pending!))
            {
                if (flights.Count >= 64) throw new NutritionException(429, "external_rate_limited", 1);
                // Detached from an individual caller: cancellation must not cancel other waiters.
                pending = Task.Run(async () =>
                {
                    try
                    {
                        var value = await Fetch(path, search);
                        cache.Set(path, value, new MemoryCacheEntryOptions { Size = 1,
                            AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(value.Missing ? 1 : search ? 5 : 15) });
                        return value;
                    }
                    finally { lock (gate) flights.Remove(path); }
                });
                flights.Add(path, pending);
            }
        }
        return await pending.WaitAsync(ct);
    }

    private async Task<Reply> Fetch(string path, bool search)
    {
        lock (gate)
        {
            var now = clock.GetUtcNow();
            var budget = search ? searches : products;
            while (budget.TryPeek(out var at) && now - at >= TimeSpan.FromMinutes(1)) budget.Dequeue();
            var until = blockedUntil > now ? blockedUntil
                : budget.Count >= (search ? 10 : 15) ? budget.Peek().AddMinutes(1) : now;
            if (until > now) throw new NutritionException(429, "external_rate_limited", Math.Max(1, (int)Math.Ceiling((until - now).TotalSeconds)));
            budget.Enqueue(now);
        }
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var response = await clients.CreateClient(ClientName).GetAsync(path, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var seconds = Math.Clamp((int)Math.Ceiling(response.Headers.RetryAfter?.Delta?.TotalSeconds
                    ?? (response.Headers.RetryAfter?.Date - clock.GetUtcNow())?.TotalSeconds ?? 60), 1, 86400);
                lock (gate) blockedUntil = clock.GetUtcNow().AddSeconds(seconds);
                throw new NutritionException(429, "external_rate_limited", seconds);
            }
            if (!search && response.StatusCode == HttpStatusCode.NotFound) return new(default, clock.GetUtcNow(), true);
            if ((int)response.StatusCode >= 500) throw new NutritionException(503, "external_unavailable");
            if (!response.IsSuccessStatusCode) throw new NutritionException(502, "external_invalid_response");
            const int max = 2 * 1024 * 1024;
            if (response.Content.Headers.ContentLength > max) throw new NutritionException(502, "external_invalid_response");
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var buffer = new MemoryStream();
            var bytes = new byte[8192];
            int read;
            while ((read = await stream.ReadAsync(bytes, timeout.Token)) > 0)
            {
                if (buffer.Length + read > max) throw new NutritionException(502, "external_invalid_response");
                buffer.Write(bytes, 0, read);
            }
            using var json = JsonDocument.Parse(buffer.ToArray());
            if (json.RootElement.ValueKind != JsonValueKind.Object) throw new NutritionException(502, "external_invalid_response");
            return new(json.RootElement.Clone(), clock.GetUtcNow());
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or IOException)
        { throw new NutritionException(503, "external_unavailable"); }
        catch (JsonException) { throw new NutritionException(502, "external_invalid_response"); }
    }

    public static ExternalProductResponse Parse(JsonElement product, DateTimeOffset retrieved)
    {
        var code = Text(product, "code")!;
        var name = Text(product, "product_name") ?? "";
        var brand = Text(product, "brands");
        var quantity = Text(product, "quantity");
        var issues = new List<string>();
        if (string.IsNullOrWhiteSpace(name) || name.Length > 200 || brand?.Length > 100 || quantity?.Length > 200) issues.Add("invalid_identity");
        if (product.TryGetProperty("obsolete", out var obsolete) && obsolete.ToString() == "1") issues.Add("obsolete_product");
        foreach (var field in new[] { "data_quality_errors_tags", "data_quality_warnings_tags" })
            if (product.TryGetProperty(field, out var tags)
                && (tags.ValueKind != JsonValueKind.Array || tags.EnumerateArray().Any(t => t.ValueKind != JsonValueKind.String
                    || field == "data_quality_errors_tags" || new[] { "nutrition", "nutrient", "energy", "calorie", "protein", "carbohydrate", "fat" }
                        .Any(term => t.GetString()!.Contains(term, StringComparison.OrdinalIgnoreCase))))) issues.Add("nutrition_quality_issue");
        Nutrients? values = null;
        string? origin = null;
        if (product.TryGetProperty("nutrition", out var nutrition) && nutrition.ValueKind == JsonValueKind.Object
            && nutrition.TryGetProperty("input_sets", out var sets) && sets.ValueKind == JsonValueKind.Array)
        {
            foreach (var source in new[] { "manufacturer", "packaging" })
            {
                var relevant = sets.EnumerateArray().Where(s => Text(s, "source") == source
                    && Text(s, "preparation") == "as_sold" && Text(s, "per") == "100g").ToArray();
                if (relevant.Length == 0) continue;
                var parsed = relevant.Select(ReadNutrients).ToArray();
                // Never fall back to a lower priority source when the preferred source is incomplete.
                if (parsed.Any(n => n is null)) issues.Add("incomplete_or_invalid_nutrients");
                else if (parsed.Distinct().Count() != 1) issues.Add("conflicting_nutrients");
                else { values = parsed[0]; origin = source; }
                break;
            }
        }
        if (values is null && issues.Count == 0) issues.Add("missing_confirmed_per_100g");
        string? fingerprint = null;
        if (issues.Count == 0 && values is not null)
        {
            var canonical = JsonSerializer.Serialize(new { code, name, brand, quantity,
                values = new[] { values.Calories, values.Protein, values.Fat, values.Carbohydrates }
                    .Select(v => v.ToString("G29", CultureInfo.InvariantCulture)), origin });
            fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        }
        return new(Source, code, name, brand, quantity, SourceUrl(code), issues.Count == 0 ? values : null,
            origin, fingerprint, retrieved, issues.Count == 0 && values is not null, issues.Distinct().ToArray());
    }

    private static Nutrients? ReadNutrients(JsonElement set)
    {
        if ((set.TryGetProperty("per_quantity", out var perQuantity) && (perQuantity.ValueKind != JsonValueKind.Number
            || !perQuantity.TryGetDecimal(out var amount) || amount != 100))
            || (set.TryGetProperty("per_unit", out _) && Text(set, "per_unit") != "g")) return null;
        if (!set.TryGetProperty("nutrients", out var nutrients) || nutrients.ValueKind != JsonValueKind.Object) return null;
        decimal? Read(string key, decimal max, bool energy = false)
        {
            if (!nutrients.TryGetProperty(key, out var n) || n.ValueKind != JsonValueKind.Object
                || !n.TryGetProperty("value", out var v) || v.ValueKind != JsonValueKind.Number || !v.TryGetDecimal(out var value)
                || (n.TryGetProperty("modifier", out var modifier) && modifier.ValueKind != JsonValueKind.Null)) return null;
            var unit = Text(n, "unit");
            var multiplier = energy ? unit == "kcal" ? 1m : unit == "kJ" ? 1m / 4.184m : (decimal?)null
                : unit == "g" ? 1m : unit == "mg" ? 0.001m : unit is "µg" or "μg" ? 0.000001m : (decimal?)null;
            if (multiplier is null || value < 0) return null;
            try
            {
                var converted = value * multiplier.Value;
                return converted <= max ? decimal.Round(converted, 3, MidpointRounding.AwayFromZero) : null;
            }
            catch (OverflowException) { return null; }
        }
        var calories = nutrients.TryGetProperty("energy-kcal", out _) ? Read("energy-kcal", 1000, true) : Read("energy-kj", 1000, true);
        var protein = Read("proteins", 100); var fat = Read("fat", 100); var carbs = Read("carbohydrates", 100);
        return calories is not null && protein is not null && fat is not null && carbs is not null && protein + fat + carbs <= 100.5m
            ? new(calories.Value, protein.Value, fat.Value, carbs.Value) : null;
    }

    private static string? Text(JsonElement obj, string key) => obj.ValueKind == JsonValueKind.Object
        && obj.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()?.Trim() : null;
    private static string SourceUrl(string code) => "https://world.openfoodfacts.org/product/" + code;
    public void Dispose() => cache.Dispose();
}
