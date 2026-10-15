using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using OpenGym.Nutrition.Api;

namespace OpenGym.Nutrition.Services;

/// <summary>Reads public-domain USDA reference data. Caller URLs are never HTTP targets.</summary>
public sealed class FoodCompositionClient(IHttpClientFactory clients, IConfiguration configuration)
{
    public const string ClientName = "USDA";
    private const int MaxResponseBytes = 2 * 1024 * 1024;
    private static readonly string[] DataTypes = ["Foundation", "SR Legacy", "Survey (FNDDS)"];
    public static string SourceUrl(string id) => $"https://fdc.nal.usda.gov/food-details/{id}/nutrients";

    public static void ValidateSource(FoodSourceRequest? source)
    {
        if (source is null || source.Provider != "usda" || source.FoodId is null
            || !Regex.IsMatch(source.FoodId, "^[1-9][0-9]{0,8}$", RegexOptions.CultureInvariant)
            || (source.Url != SourceUrl(source.FoodId)
                && source.Url != $"https://fdc.nal.usda.gov/fdc-app.html#/food-details/{source.FoodId}/nutrients"))
            throw new NutritionException(400, "unsupported_source");
    }

    public async Task<ReferenceFoodPage> Search(string? search, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(search) || search.Length > 200) throw new NutritionException(400, "invalid_input");
        using var json = await Fetch("foods/search", ct,
            JsonContent.Create(new { query = search.Trim(), pageSize = 10, dataType = DataTypes }));
        try
        {
            if (!json.RootElement.TryGetProperty("foods", out var foods) || foods.ValueKind != JsonValueKind.Array) throw InvalidData();
            var result = new List<ReferenceFood>();
            foreach (var food in foods.EnumerateArray().Take(10))
            {
                var dataType = food.GetProperty("dataType").GetString()!;
                if (!DataTypes.Contains(dataType)) continue;
                var id = food.GetProperty("fdcId").GetInt32().ToString(CultureInfo.InvariantCulture);
                var description = food.GetProperty("description").GetString();
                if (!Regex.IsMatch(id, "^[1-9][0-9]{0,8}$") || string.IsNullOrWhiteSpace(description) || description.Length > 2000) throw InvalidData();
                Nutrients? nutrients;
                try { nutrients = ReadNutrients(food, searchResult: true); }
                catch (Exception ex) when (ex is NutritionException or KeyNotFoundException or InvalidOperationException or FormatException) { nutrients = null; }
                result.Add(new(id, description, dataType, SourceUrl(id), nutrients));
            }
            return new(result);
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or FormatException)
        { throw InvalidData(); }
    }

    public async Task<(FoodSource Source, Nutrients Nutrients)> Get(FoodSourceRequest source, CancellationToken ct)
    {
        ValidateSource(source);
        using var json = await Fetch("food/" + source.FoodId, ct);
        try
        {
            var food = json.RootElement;
            if (food.GetProperty("fdcId").GetInt32().ToString(CultureInfo.InvariantCulture) != source.FoodId) throw InvalidData();
            var dataType = food.GetProperty("dataType").GetString()!;
            var description = food.GetProperty("description").GetString();
            if (!DataTypes.Contains(dataType) || string.IsNullOrWhiteSpace(description) || description.Length > 2000) throw InvalidData();
            return (new("usda", source.FoodId, SourceUrl(source.FoodId), description, dataType,
                "source_verified_reference", DateTime.UtcNow), ReadNutrients(food, searchResult: false));
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or FormatException)
        { throw InvalidData(); }
    }

    private static Nutrients ReadNutrients(JsonElement food, bool searchResult)
    {
        if (!food.TryGetProperty("foodNutrients", out var list) || list.ValueKind != JsonValueKind.Array) throw InvalidData();
        decimal Read(int id, string unit)
        {
            var matches = list.EnumerateArray().Where(row => (searchResult ? row.GetProperty("nutrientId")
                : row.GetProperty("nutrient").GetProperty("id")).GetInt32() == id).ToArray();
            if (matches.Length != 1) throw InvalidData();
            var row = matches[0];
            var actualUnit = (searchResult ? row : row.GetProperty("nutrient")).GetProperty("unitName").GetString();
            if (!string.Equals(actualUnit, unit, StringComparison.OrdinalIgnoreCase)
                || !row.TryGetProperty(searchResult ? "value" : "amount", out var amount)
                || !amount.TryGetDecimal(out var value) || value < 0 || value > (unit == "kcal" ? 1000 : 100)) throw InvalidData();
            return decimal.Round(value, 3);
        }
        // USDA 1008 is kcal, not the similarly named kJ field. New Foundation foods use Atwater energy.
        var energyId = new[] { 1008, 2048, 2047 }.FirstOrDefault(id => list.EnumerateArray().Any(row =>
            (searchResult ? row.GetProperty("nutrientId") : row.GetProperty("nutrient").GetProperty("id")).GetInt32() == id));
        if (energyId == 0) throw InvalidData();
        var result = new Nutrients(Read(energyId, "kcal"), Read(1003, "g"), Read(1004, "g"), Read(1005, "g"));
        if (result.Protein + result.Fat + result.Carbohydrates > 100.003m) throw InvalidData();
        return result;
    }

    public static bool PreparationAnalog(FoodSource source, string state, string? method, bool confirmed)
    {
        var description = source.Description.ToLowerInvariant();
        bool Has(string term) => Regex.IsMatch(description, @"\b" + Regex.Escape(term) + @"\b", RegexOptions.CultureInvariant);
        var raw = Has("raw") || Has("uncooked");
        var cooked = new[] { "cooked", "roasted", "baked", "grilled", "boiled", "fried", "steamed", "stewed", "braised" }.Any(Has);
        if ((state == "raw" && (!raw || cooked)) || (state == "cooked" && (raw || !cooked))
            || (state == "ready" && raw)) throw new NutritionException(400, "food_state_mismatch");
        if (state != "cooked") return false;
        var exact = method == "cooked" || (method == "air_fried" ? Has("air fried") || Has("air-fried") : Has(method!));
        if (!exact && !confirmed) throw new NutritionException(400, "preparation_confirmation_required");
        return !exact;
    }

    private async Task<JsonDocument> Fetch(string relativePath, CancellationToken ct, HttpContent? content = null)
    {
        try
        {
            using var request = new HttpRequestMessage(content is null ? HttpMethod.Get : HttpMethod.Post,
                "/fdc/v1/" + relativePath) { Content = content };
            request.Headers.Add("X-Api-Key", configuration["Nutrition:UsdaApiKey"] ?? "DEMO_KEY");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            using var response = await clients.CreateClient(ClientName).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (response.StatusCode == HttpStatusCode.NotFound) throw new NutritionException(404, "source_not_found");
            if (!response.IsSuccessStatusCode) throw new NutritionException(503, "source_unavailable");
            if (response.Content.Headers.ContentLength > MaxResponseBytes) throw InvalidData();
            await using var input = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var output = new MemoryStream();
            var buffer = new byte[8192];
            int read;
            while ((read = await input.ReadAsync(buffer, timeout.Token)) > 0)
            {
                if (output.Length + read > MaxResponseBytes) throw InvalidData();
                output.Write(buffer, 0, read);
            }
            return JsonDocument.Parse(output.ToArray());
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException && !ct.IsCancellationRequested)
        { throw new NutritionException(503, "source_unavailable"); }
        catch (JsonException) { throw InvalidData(); }
    }

    private static NutritionException InvalidData() => new(400, "source_data_incomplete");
}
