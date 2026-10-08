using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Engine.Application.Contracts;
using Engine.Application.Persistence;
using Engine.Application.UseCases;
using Engine.Domain.Outcomes;
using Engine.Domain.Signals;
using Engine.Hosting;
using Engine.Hosting.Options;
using Engine.Infrastructure.Clients.Agents;
using Shouldly;

namespace Engine.Tests.Application.Contracts;

/// <summary>
/// Stage 6's drift check, the engine's half. contracts/openapi.json is the agent service's
/// own OpenAPI document, committed, and Python's suite fails when the service stops
/// generating exactly that file. This suite reads the same file and fails when an endpoint
/// the engine calls, or a DTO it sends or reads, no longer agrees with it.
/// </summary>
/// <remarks>
/// <para>
/// Decision D3: a check, not NSwag. The DTOs stay hand-written, with Disallow and the
/// mappers' caps, and nothing here generates them - so Python never becomes the contract's
/// owner. What the engine gets instead is that a drift fails a test rather than a cycle.
/// </para>
/// <para>
/// The rules are directional, because "agree" means something different for each side of a
/// call. For what the engine <b>reads</b>, every field the agent service may send has to
/// exist on the DTO (Disallow would refuse the whole answer otherwise), every member the DTO
/// requires has to be one the service always sends, and a null the service may send has to
/// fit the engine's type. For what the engine <b>sends</b>, every field has to be one the
/// service declares, every field the service requires has to be one the engine sends, and a
/// null the engine may send has to be one the service accepts.
/// </para>
/// </remarks>
public class OpenApiContractTests
{
    private static readonly string SnapshotPath =
        Path.Combine(AppContext.BaseDirectory, "contracts", "openapi.json");

    private static JsonNode Snapshot() => JsonNode.Parse(File.ReadAllText(SnapshotPath))!;

    private static JsonNode Schema(JsonNode document, string name) =>
        document["components"]!["schemas"]![name]!;

    // What the engine sends besides a body. Headers are listed so that a header the agent
    // service starts to require is checked against them; only path and query parameters are
    // checked the other way, because the service reads X-Correlation-Id in middleware rather
    // than declaring it.
    private static readonly (string In, string Name) ApiKey = ("header", AgentClientExtensions.ApiKeyHeader);
    private static readonly (string In, string Name) CorrelationId = ("header", PythonAgentClient.CorrelationIdHeader);
    private static readonly (string In, string Name) Symbol = ("path", "symbol");

    private static readonly string QuotePath = $"/{PythonAgentClient.QuotesPath}/{{symbol}}";
    private static readonly string HistoryPath = $"{QuotePath}/{PythonAgentClient.HistorySegment}";

    /// <summary>Every call the engine makes, as PythonAgentClient makes it.</summary>
    private static void EveryEndpointTheEngineCalls(OpenApiAgreement agreement)
    {
        agreement.Endpoint("post", $"/{PythonAgentClient.SignalsPath}",
            typeof(TradeSignalRequestDto), typeof(TradeSignalDto), ApiKey, CorrelationId);

        agreement.Endpoint("post", $"/{PythonAgentClient.ScreenPath}",
            typeof(ScreenRequestDto), typeof(ScreenResultDto), ApiKey, CorrelationId);

        agreement.Endpoint("get", QuotePath,
            body: null, typeof(QuoteDto), ApiKey, CorrelationId, Symbol);

        agreement.Endpoint("get", HistoryPath,
            body: null, typeof(HistoryDto), ApiKey, CorrelationId, Symbol,
            ("query", PythonAgentClient.HistoryFromParameter));

        // The answer is a count the engine does not read, so only the request is held.
        agreement.Endpoint("post", $"/{PythonAgentClient.OutcomesPath}",
            typeof(OutcomeReportDto), answer: null, ApiKey, CorrelationId,
            ("header", PythonAgentClient.OutcomesSignatureHeader));
    }

    [Fact]
    public void The_snapshot_is_where_the_test_expects_it()
    {
        // A file the build quietly stopped copying would make every test below fail for the
        // wrong reason - or, worse, a check that skipped a missing file would pass vacuously.
        File.Exists(SnapshotPath).ShouldBeTrue();
        Snapshot()["paths"]!.AsObject().Count.ShouldBeGreaterThanOrEqualTo(5);
    }

    [Fact]
    public void Every_endpoint_the_engine_calls_agrees_with_the_agent_services_document()
    {
        OpenApiAgreement.Check(Snapshot(), EveryEndpointTheEngineCalls).ShouldBeEmpty();
    }

    [Fact]
    public void The_history_date_is_a_date_on_both_sides()
    {
        // The engine formats it with "O", which for a DateOnly is yyyy-MM-dd. A service that
        // started expecting a timestamp would answer 422 to every sweep.
        var from = Snapshot()["paths"]![HistoryPath]!["get"]!["parameters"]!.AsArray()
            .FirstOrDefault(p => (string?)p!["name"] == PythonAgentClient.HistoryFromParameter);

        // A renamed parameter is the drift test's job; this one should say so rather than
        // throw "sequence contains no matching element" at whoever reads the run.
        from.ShouldNotBeNull($"the history takes no '{PythonAgentClient.HistoryFromParameter}'");
        ((string?)from["schema"]!["format"]).ShouldBe("date");
        DateOnly.FromDateTime(new DateTime(2026, 10, 8)).ToString("O").ShouldBe("2026-10-08");
    }

    [Fact]
    public void The_stances_are_the_engines_stances()
    {
        // TradeSignalMapper spells them out in a switch, upper case. A fourth stance in Python
        // would deserialise and then be refused at the seam as "not one of BUY, SELL or HOLD".
        Enumerated(Snapshot(), "Stance")
            .ShouldBe(Enum.GetNames<Stance>().Select(name => name.ToUpperInvariant()), ignoreOrder: true);
    }

    [Fact]
    public void The_outcome_enums_are_spelled_the_way_the_engine_stores_them()
    {
        // outcome.schema.json chose the engine's spelling on purpose, so the two copies of a
        // row compare without a mapping. The DTO carries them as the enums' ToString().
        var document = Snapshot();

        Enumerated(document, "HorizonUnit").ShouldBe(Enum.GetNames<HorizonUnit>(), ignoreOrder: true);
        Enumerated(document, "OutcomeStatus").ShouldBe(Enum.GetNames<OutcomeStatus>(), ignoreOrder: true);
    }

    [Fact]
    public void The_caps_the_engine_enforces_are_the_caps_the_service_declares()
    {
        // Each of these lives in pydantic and in an engine constant, and the hand-written
        // schemas already hold them pairwise. This holds the engine's against what the service
        // actually generates, which is the number a live answer is validated against.
        // Read through the walker's own unwrapping, so a field that became optional - its
        // constraints then sitting under anyOf - is reported as a cap that moved rather than
        // as a missing one. The walker already has to understand that shape; this uses the
        // same understanding instead of a second one.
        var document = Snapshot();

        JsonNode Field(string schema, string name) =>
            OpenApiAgreement.Constraints(document, Schema(document, schema)["properties"]![name]!);

        var thesis = Field("TradeSignal", "thesis");
        var risks = Field("TradeSignal", "key_risks");

        Number("a thesis cap", thesis["maxLength"]).ShouldBe(TradeSignalMapper.MaxThesisLength);
        Number("a risk count", risks["maxItems"]).ShouldBe(TradeSignalMapper.MaxRisks);
        Number("a risk length", risks["items"]!["maxLength"]).ShouldBe(TradeSignalMapper.MaxRiskLength);
        Number("a horizon maximum", Field("TradeSignal", "horizon_days")["maximum"])
            .ShouldBe(TradeSignalMapper.MaxHorizonDays);

        Number("a rejection reason cap", Field("Rejection", "reason")["maxLength"])
            .ShouldBe(ScreenMapper.MaxReasonLength);

        Number("an outcome batch cap", Field("OutcomeReport", "outcomes")["maxItems"])
            .ShouldBe(ReportOutcomesUseCase.MaxPerRequest);
    }

    [Fact]
    public void The_engine_cannot_be_configured_to_send_a_screen_the_service_refuses()
    {
        // The options validator is what stops a universe or a shortlist the service would
        // answer 422 to, and it holds its limits as attributes. A cap lowered on one side only
        // would surface as a cycle that screens nothing.
        var request = Schema(Snapshot(), "ScreenRequest")["properties"]!;

        var universe = typeof(TradingOptions).GetProperty(nameof(TradingOptions.Universe))!
            .GetCustomAttribute<MaxLengthAttribute>()!;
        var shortlist = typeof(TradingOptions).GetProperty(nameof(TradingOptions.ShortlistSize))!
            .GetCustomAttribute<RangeAttribute>()!;

        Number("a universe cap", request["universe"]!["maxItems"]).ShouldBe(universe.Length);
        Number("a shortlist maximum", request["limit"]!["maximum"])
            .ShouldBe(Convert.ToInt32(shortlist.Maximum));
    }

    /// <summary>
    /// The checker has to be able to fail, or a green run proves nothing. Each case is one
    /// realistic drift applied to a copy of the committed document.
    /// </summary>
    public class TheCheckCatchesDrift
    {
        // Only what the drift added. A real drift in the committed file is already reported
        // by the test above, and repeating it in all eight of these would bury it.
        private static IReadOnlyList<string> CheckAfter(Action<JsonNode> drift)
        {
            var before = OpenApiAgreement.Check(Snapshot(), EveryEndpointTheEngineCalls);
            var document = Snapshot();
            drift(document);
            return OpenApiAgreement.Check(document, EveryEndpointTheEngineCalls).Except(before).ToList();
        }

        [Fact]
        public void A_field_the_service_starts_sending()
        {
            // Decision 1's own regression: the agents naming an amount again.
            var failures = CheckAfter(document => Schema(document, "TradeSignal")["properties"]!
                .AsObject().Add("amount_usd", new JsonObject { ["type"] = "number" }));

            failures.ShouldHaveSingleItem().ShouldContain("'amount_usd'");
        }

        [Fact]
        public void A_field_the_service_starts_requiring()
        {
            var failures = CheckAfter(document =>
            {
                var request = Schema(document, "SignalRequest");
                request["properties"]!.AsObject().Add("risk_appetite", new JsonObject { ["type"] = "number" });
                request["required"]!.AsArray().Add("risk_appetite");
            });

            failures.ShouldHaveSingleItem().ShouldContain("requires 'risk_appetite'");
        }

        [Fact]
        public void A_field_the_service_stops_sending()
        {
            var failures = CheckAfter(document =>
            {
                var quote = Schema(document, "InstrumentQuote");
                quote["properties"]!.AsObject().Remove("currency");
                var required = quote["required"]!.AsArray();
                required.Remove(required.Single(r => (string?)r == "currency"));
            });

            failures.ShouldHaveSingleItem().ShouldContain("'currency'");
        }

        [Fact]
        public void An_answer_field_that_may_become_null()
        {
            var failures = CheckAfter(document => Schema(document, "TradeSignal")["properties"]!["thesis"] =
                new JsonObject
                {
                    ["anyOf"] = new JsonArray(
                        new JsonObject { ["type"] = "string" }, new JsonObject { ["type"] = "null" })
                });

            failures.ShouldHaveSingleItem().ShouldContain("may send null");
        }

        [Fact]
        public void A_number_that_becomes_a_string()
        {
            var failures = CheckAfter(document =>
                Schema(document, "Candidate")["properties"]!["score"]!["type"] = "string");

            failures.ShouldHaveSingleItem().ShouldContain("score");
        }

        [Fact]
        public void A_route_that_moves()
        {
            var failures = CheckAfter(document =>
            {
                var paths = document["paths"]!.AsObject();
                var screen = paths["/v1/screen"]!;
                paths.Remove("/v1/screen");
                paths.Add("/v2/screen", screen);
            });

            failures.ShouldHaveSingleItem().ShouldContain("no such endpoint");
        }

        [Fact]
        public void A_query_parameter_that_is_renamed()
        {
            var failures = CheckAfter(document =>
                document["paths"]![HistoryPath]!["get"]!["parameters"]!.AsArray()
                    .Single(p => (string?)p!["name"] == "from")!["name"] = "since");

            failures.ShouldContain(failure => failure.Contains("'since'"));
            failures.ShouldContain(failure => failure.Contains("'from'"));
        }

        [Fact]
        public void An_instrument_type_the_engine_cannot_read()
        {
            // A second variant on the union. The engine refuses an unknown discriminator, so
            // the service answering with one would be a refused answer every time.
            var failures = CheckAfter(document =>
            {
                var schemas = document["components"]!["schemas"]!.AsObject();
                var equity = schemas["Instrument"]!.DeepClone();
                var option = equity.DeepClone();
                option["properties"]!["type"]!["const"] = "option";
                schemas["Instrument"] = new JsonObject { ["oneOf"] = new JsonArray(equity, option) };
            });

            failures.ShouldNotBeEmpty();
            failures.ShouldAllBe(failure => failure.Contains("'option'"));
        }
    }

    private static IEnumerable<string> Enumerated(JsonNode document, string name) =>
        Schema(document, name)["enum"]!.AsArray().Select(value => (string)value!);

    // OpenAPI writes 30 as 30.0 when pydantic's bound was declared on a float-typed path, so
    // a cap is read as a double and compared as an integer. Named rather than dereferenced:
    // a cap that moved - which is what happens to one when its field becomes optional, since
    // pydantic then nests the constraints under anyOf - used to arrive here as a
    // NullReferenceException and a stack trace, in a file whose other failures are sentences.
    private static int Number(string what, JsonNode? node)
    {
        node.ShouldNotBeNull($"the agent service's document declares no {what}");
        return (int)node.GetValue<double>();
    }
}

internal enum Direction
{
    EngineSends,
    EngineReads
}

/// <summary>
/// Walks a DTO and the schema the agent service declares for it side by side, collecting
/// every disagreement rather than stopping at the first, so one run names all of them.
/// </summary>
internal sealed class OpenApiAgreement
{
    private readonly JsonNode _document;
    private readonly List<string> _failures = [];
    private readonly NullabilityInfoContext _nullability = new();

    private OpenApiAgreement(JsonNode document) => _document = document;

    public static IReadOnlyList<string> Check(JsonNode document, Action<OpenApiAgreement> checks)
    {
        var agreement = new OpenApiAgreement(document);
        checks(agreement);
        return agreement._failures;
    }

    /// <summary>
    /// The schema a field really has: a $ref followed, and pydantic's "X or null" read as X.
    /// Exposed so the cap checks read a constraint the same way the walker reads a type -
    /// one understanding of the shape rather than two that can disagree.
    /// </summary>
    public static JsonNode Constraints(JsonNode document, JsonNode schema) =>
        new OpenApiAgreement(document).Unwrap(schema).Node;

    public void Endpoint(
        string method, string path, Type? body, Type? answer, params (string In, string Name)[] sent)
    {
        var where = $"{method.ToUpperInvariant()} {path}";
        var operation = _document["paths"]?[path]?[method];

        if (operation is null)
        {
            Fail(where, "the agent service has no such endpoint");
            return;
        }

        var parameters = operation["parameters"]?.AsArray().Select(p => p!).ToList() ?? [];

        foreach (var parameter in parameters)
        {
            var (location, name) = (Text(parameter["in"]), Text(parameter["name"]));
            var required = parameter["required"]?.GetValue<bool>() ?? false;

            if (required && !sent.Any(s => Same(s, location, name)))
                Fail(where, $"the agent service requires the {location} parameter '{name}', which the engine never sends");
        }

        foreach (var (location, name) in sent.Where(s => s.In is "path" or "query"))
        {
            if (!parameters.Any(p => Same((location, name), Text(p["in"]), Text(p["name"]))))
                Fail(where, $"the engine sends the {location} parameter '{name}', which the agent service does not declare");
        }

        var requestBody = operation["requestBody"];
        var requestSchema = requestBody?["content"]?["application/json"]?["schema"];

        if (body is null)
        {
            if (requestBody?["required"]?.GetValue<bool>() == true)
                Fail(where, "the agent service requires a body, and the engine sends none");
        }
        else if (requestSchema is null)
        {
            Fail(where, "the agent service takes no JSON body, and the engine sends one");
        }
        else
        {
            Compare(body, info: null, requestSchema, Direction.EngineSends, $"{where} request");
        }

        if (answer is not null)
        {
            var answerSchema = operation["responses"]?["200"]?["content"]?["application/json"]?["schema"];

            if (answerSchema is null)
                Fail(where, "the agent service declares no JSON answer for 200");
            else
                Compare(answer, info: null, answerSchema, Direction.EngineReads, $"{where} answer");
        }
    }

    private void Compare(Type clr, NullabilityInfo? info, JsonNode schema, Direction direction, string where)
    {
        var (node, schemaNullable) = Unwrap(schema);

        var clrNullable = Nullable.GetUnderlyingType(clr) is not null
            || info?.ReadState == NullabilityState.Nullable;
        clr = Nullable.GetUnderlyingType(clr) ?? clr;

        if (direction == Direction.EngineReads && schemaNullable && !clrNullable)
            Fail(where, "the agent service may send null, and the engine's type cannot hold it");

        if (direction == Direction.EngineSends && clrNullable && !schemaNullable)
            Fail(where, "the engine may send null, and the agent service refuses it");

        if (clr == typeof(string))
            Expect(where, node, clr, ["string"]);
        else if (clr == typeof(DateTimeOffset))
            Expect(where, node, clr, ["string"], "date-time");
        else if (clr == typeof(DateOnly))
            Expect(where, node, clr, ["string"], "date");
        else if (clr == typeof(bool))
            Expect(where, node, clr, ["boolean"]);
        else if (clr == typeof(int) || clr == typeof(long))
            // An integer the engine sends is also a valid number; one it reads must be whole.
            Expect(where, node, clr, direction == Direction.EngineReads ? ["integer"] : ["integer", "number"]);
        else if (clr == typeof(decimal) || clr == typeof(double))
            // A whole number the engine reads fits a decimal; a decimal it sends may not be whole.
            Expect(where, node, clr, direction == Direction.EngineReads ? ["number", "integer"] : ["number"]);
        else if (ElementType(clr) is { } element)
        {
            if (Expect(where, node, clr, ["array"]) && node["items"] is { } items)
            {
                var elementInfo = info?.ElementType ?? info?.GenericTypeArguments.FirstOrDefault();
                Compare(element, elementInfo, items, direction, $"{where}[]");
            }
        }
        else if (clr.GetCustomAttribute<JsonPolymorphicAttribute>() is { } polymorphic)
            ComparePolymorphic(clr, polymorphic, node, direction, where);
        else
            CompareObject(clr, node, direction, where, discriminator: null);
    }

    private void ComparePolymorphic(
        Type clr, JsonPolymorphicAttribute polymorphic, JsonNode node, Direction direction, string where)
    {
        var discriminator = polymorphic.TypeDiscriminatorPropertyName ?? "$type";
        var derived = clr.GetCustomAttributes<JsonDerivedTypeAttribute>()
            .ToDictionary(d => (string)d.TypeDiscriminator!, d => d.DerivedType);

        // One variant is emitted as a plain object, several as a oneOf/anyOf.
        var variants = (node["oneOf"] ?? node["anyOf"])?.AsArray()
            .Select(v => Deref(v!))
            .Where(v => Text(v["type"]) != "null")
            .ToList() ?? [node];

        var declared = new Dictionary<string, JsonNode>();

        foreach (var variant in variants)
        {
            var tag = variant["properties"]?[discriminator];
            var value = Text(tag?["const"])
                ?? (tag?["enum"] is JsonArray single && single.Count == 1 ? Text(single[0]) : null);

            if (value is null)
            {
                Fail(where, $"a variant has no constant '{discriminator}' to tell it apart by");
                continue;
            }

            if (!Required(variant).Contains(discriminator))
                Fail(where, $"the variant '{value}' does not require '{discriminator}'");

            declared[value] = variant;
        }

        foreach (var (value, variant) in declared)
        {
            if (derived.TryGetValue(value, out var type))
                CompareObject(type, variant, direction, $"{where}<{value}>", discriminator);
            else if (direction == Direction.EngineReads)
                Fail(where, $"the agent service may answer with a '{value}', which the engine cannot read");
        }

        foreach (var value in derived.Keys.Except(declared.Keys))
        {
            if (direction == Direction.EngineSends)
                Fail(where, $"the engine may send a '{value}', which the agent service does not accept");
        }
    }

    private void CompareObject(Type clr, JsonNode schema, Direction direction, string where, string? discriminator)
    {
        if (Text(schema["type"]) != "object")
        {
            Fail(where, $"{clr.Name} is an object, and the agent service's document does not say so");
            return;
        }

        var declared = schema["properties"]?.AsObject()
            .Where(p => p.Key != discriminator)
            .ToDictionary(p => p.Key, p => p.Value!) ?? [];
        var required = Required(schema);

        var members = new Dictionary<string, PropertyInfo>();
        foreach (var property in clr.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetCustomAttribute<JsonIgnoreAttribute>() is not null)
                continue;

            // Without the attribute System.Text.Json writes the C# name, which is never the
            // contract's spelling.
            if (property.GetCustomAttribute<JsonPropertyNameAttribute>() is not { } name)
                Fail(where, $"{clr.Name}.{property.Name} has no [JsonPropertyName]");
            else
                members[name.Name] = property;
        }

        foreach (var (name, propertySchema) in declared)
        {
            if (!members.TryGetValue(name, out var member))
            {
                if (direction == Direction.EngineReads)
                    Fail(where, $"the agent service may send '{name}', which {clr.Name} does not have - Disallow refuses the whole answer");
                else if (required.Contains(name))
                    Fail(where, $"the agent service requires '{name}', which {clr.Name} never sends");

                continue;
            }

            if (direction == Direction.EngineReads && IsRequired(member) && !required.Contains(name))
                Fail(where, $"{clr.Name} requires '{name}', which the agent service may leave out");

            Compare(member.PropertyType, _nullability.Create(member), propertySchema, direction, $"{where}.{name}");
        }

        foreach (var name in members.Keys.Except(declared.Keys))
        {
            if (direction == Direction.EngineSends)
                Fail(where, $"{clr.Name} sends '{name}', which the agent service does not declare");
            else if (IsRequired(members[name]))
                Fail(where, $"{clr.Name} requires '{name}', which the agent service never sends");
        }
    }

    private bool Expect(string where, JsonNode node, Type clr, string[] types, string? format = null)
    {
        var type = Text(node["type"]);

        if (type is null || !types.Contains(type))
        {
            Fail(where, $"is {type ?? "untyped"} in the agent service's document, and {clr.Name} in the engine");
            return false;
        }

        if (format is not null && Text(node["format"]) != format)
        {
            Fail(where, $"is {Text(node["format"]) ?? "an unformatted string"} in the agent service's document, and {clr.Name} in the engine expects {format}");
            return false;
        }

        return true;
    }

    /// <summary>
    /// Follows $ref, and reads "X or null" - pydantic's anyOf for an Optional - as X plus a
    /// flag. A union of several real types is returned as it is, for the polymorphic check.
    /// </summary>
    private (JsonNode Node, bool Nullable) Unwrap(JsonNode schema)
    {
        var node = Deref(schema);

        if ((node["anyOf"] ?? node["oneOf"]) is JsonArray variants)
        {
            var real = variants.Select(v => Deref(v!)).Where(v => Text(v["type"]) != "null").ToList();
            var nullable = real.Count != variants.Count;

            return real.Count == 1 ? (real[0], nullable) : (node, nullable);
        }

        if (node["type"] is JsonArray types)
        {
            var real = types.Select(t => Text(t)).Where(t => t != "null").ToList();
            if (real.Count == 1)
            {
                var copy = node.DeepClone();
                copy["type"] = real[0];
                return (copy, real.Count != types.Count);
            }
        }

        return (node, false);
    }

    private JsonNode Deref(JsonNode node)
    {
        while (Text(node["$ref"]) is { } reference)
        {
            const string prefix = "#/components/schemas/";
            node = _document["components"]?["schemas"]?[reference[prefix.Length..]]
                ?? throw new InvalidOperationException($"{reference} points at nothing");
        }

        return node;
    }

    private static Type? ElementType(Type clr)
    {
        if (clr.IsArray)
            return clr.GetElementType();

        if (clr.IsGenericType && clr != typeof(string)
            && clr.GetGenericTypeDefinition() is var definition
            && (definition == typeof(IReadOnlyList<>) || definition == typeof(IList<>)
                || definition == typeof(List<>) || definition == typeof(IEnumerable<>)
                || definition == typeof(IReadOnlyCollection<>)))
            return clr.GetGenericArguments()[0];

        return null;
    }

    private static bool IsRequired(PropertyInfo property) =>
        property.GetCustomAttribute<RequiredMemberAttribute>() is not null;

    private static HashSet<string> Required(JsonNode schema) =>
        schema["required"]?.AsArray().Select(r => (string)r!).ToHashSet() ?? [];

    private static bool Same((string In, string Name) sent, string? location, string? name) =>
        sent.In == location && string.Equals(sent.Name, name, StringComparison.OrdinalIgnoreCase);

    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private void Fail(string where, string what) => _failures.Add($"{where}: {what}");
}
