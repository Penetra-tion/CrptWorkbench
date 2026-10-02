using System.Diagnostics;
using System.Text.Json;
using CrptWorkbench.Domain;

namespace CrptWorkbench.Application;

public sealed class TrueApiAggregationTreeHandler(ITrueApiClient client) : IApiOperationHandler
{
    public const string OperationId = "true-api.aggregation-tree";
    public const int MaxCodes = 100_000;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public ApiOperationDescriptor Descriptor { get; } = new(OperationId, "Получение полного дерева агрегации",
        ApiSystem.TrueApi, "POST", "Вложения кода и цепочка родителей, с возможностью загрузить соседние ветви.",
        typeof(AggregationTreeRequest), typeof(AggregationTreeResult),
        [new("code", "Код", "КИ, КиЗ, КИГУ, КИН, КИТУ или АТК", HelpText:
            "Один код от 18 до 74 символов. Mock: 010460000000000021TREE000000001 или 000000000000000201."),
         new("includeSiblings", "Загружать соседние ветви", Required: false, CheckedValue: "true",
             HelpText: "Загрузить все ветви верхней упаковки. Без галки — вложения введённого кода и его родители.")]);

    public static AggregationTreeRequest Parse(IReadOnlyDictionary<string, string> values)
    {
        var code = MarkingCodeInput.TrimAfterGroupSeparator(values.GetValueOrDefault("code") ?? "");
        if (!ValidCode(code)) throw new ArgumentException("Укажите один код от 18 до 74 символов без управляющих символов и скобок вокруг 01 и 21.");
        var flag = values.GetValueOrDefault("includeSiblings") ?? "";
        if (flag is not ("" or "true" or "false")) throw new ArgumentException("Некорректное значение выбора соседних ветвей.");
        return new(code, flag == "true");
    }

    public async Task<IntegrationExchange> ExecuteAsync(IReadOnlyDictionary<string, string> values,
        ApiOperationContext context, CancellationToken cancellationToken)
    {
        var input = Parse(values);
        var watch = Stopwatch.StartNew();
        var states = new Dictionary<string, NodeState>(StringComparer.Ordinal);
        var links = new Dictionary<(string Parent, string Child), AggregationTreeLink>();
        var exchanges = new List<IntegrationExchange>();
        var diagnostics = new HashSet<string>(StringComparer.Ordinal);
        var processed = 0;
        var stop = false;
        var limited = false;
        var root = input.Code;
        var ascent = new HashSet<string>(StringComparer.Ordinal);

        NodeState? Reserve(string code)
        {
            if (states.TryGetValue(code, out var existing)) return existing;
            if (states.Count >= MaxCodes)
            {
                limited = true;
                diagnostics.Add("Достигнут предел 100 000 кодов. Часть дерева не загружена.");
                return null;
            }
            var state = new NodeState(code);
            states.Add(code, state);
            return state;
        }

        void AddLink(string parent, string child) => links.TryAdd((parent, child), new(parent, child, true));

        async Task Fetch(string[] codes, string stage)
        {
            if (codes.Length == 0 || stop) return;
            var batches = codes.Chunk(1000).ToArray();
            var results = new BatchResult?[batches.Length];
            await Parallel.ForEachAsync(Enumerable.Range(0, batches.Length),
                new ParallelOptions { CancellationToken = cancellationToken,
                    MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 1, 4) },
                async (index, token) =>
                {
                    if (Volatile.Read(ref stop)) return;
                    results[index] = await FetchBatch(batches[index], context, token, () => Volatile.Read(ref stop));
                    if (results[index]!.StatusCode is 401 or 403) Volatile.Write(ref stop, true);
                    var count = Interlocked.Add(ref processed, batches[index].Length);
                    context.Progress?.Report(new(stage, count));
                });
            cancellationToken.ThrowIfCancellationRequested();
            for (var i = 0; i < batches.Length; i++)
            {
                var batch = results[i];
                if (batch is not null) exchanges.AddRange(batch.Exchanges);
                foreach (var code in batches[i])
                {
                    var state = states[code];
                    state.Attempted = true;
                    if (batch is null) { state.Error = "Загрузка остановлена из-за ошибки доступа."; continue; }
                    if (batch.Items is null) { state.Error = batch.Error; continue; }
                    var matches = batch.Items.GetValueOrDefault(code);
                    if (matches is null || matches.Length == 0) { state.Error = "Ответ по коду отсутствует."; continue; }
                    if (matches.Length != 1) { state.Error = "Неоднозначный ответ: по коду получено несколько результатов."; continue; }
                    var item = matches[0];
                    if (!CodeInfoResults.IsFound(item)) { state.Error = "Код недоступен или не найден."; continue; }
                    var info = item.CisInfo!;
                    if (!string.IsNullOrEmpty(info.Cis) && info.Cis != code)
                    {
                        state.Error = "Ответ содержит другой КИ; связь с запрошенным кодом не подтверждена.";
                        continue;
                    }
                    state.Loaded = true;
                    state.Type = info.GeneralPackageType ?? info.PackageType;
                    state.Status = info.Status;
                    state.Parent = string.IsNullOrEmpty(info.Parent) ? null : info.Parent;
                    state.ChildrenKnown = info.Child is not null;
                    var children = info.Child ?? [];
                    if ((state.Parent is not null && !ValidCode(state.Parent)) || children.Any(c => c is null || !ValidCode(c)))
                    {
                        state.Error = "Ответ содержит некорректный код связи.";
                        state.Parent = null;
                        state.Children = [];
                        continue;
                    }
                    state.Children = children.Distinct(StringComparer.Ordinal).ToArray();
                    state.ChildSet = state.Children.ToHashSet(StringComparer.Ordinal);
                    if (!state.ChildrenKnown && state.Type != "UNIT")
                        state.Error = "Состав упаковки отсутствует в ответе; конечный код нельзя определить.";
                }
                if (batch?.UnexpectedItems == true)
                    foreach (var code in batches[i]) states[code].Error = "Ответ содержит неожиданные или неидентифицируемые элементы.";
            }
            if (stop) diagnostics.Add("Дальнейшая загрузка остановлена: сервис отклонил доступ (HTTP 401/403).");
        }

        // Parent chain is read first, and its cached nodes are reused during descent.
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var state = Reserve(root)!;
            if (!state.Attempted) await Fetch([root], "Поиск верхней упаковки");
            if (!ascent.Add(root)) { state.Error = "Цикл в цепочке родителей."; break; }
            if (!state.Loaded || state.Error is not null || stop || state.Parent is null) break;
            if (Reserve(state.Parent) is null)
            {
                state.Error = "Верхний родитель не загружен из-за ограничения размера.";
                break;
            }
            AddLink(state.Parent, root);
            root = state.Parent;
        }

        var queue = new Queue<string>();
        queue.Enqueue(input.IncludeSiblings ? root : input.Code);
        var expanded = new HashSet<string>(StringComparer.Ordinal);
        while (queue.Count > 0 && !stop)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var wave = new List<string>();
            while (queue.TryDequeue(out var code))
                if (expanded.Add(code)) wave.Add(code);
            await Fetch(wave.Where(c => !states[c].Attempted).ToArray(), "Загрузка вложений");
            foreach (var code in wave)
            {
                var state = states[code];
                if (!state.Loaded || state.Error is not null) continue;
                foreach (var child in state.Children)
                {
                    if (Reserve(child) is null) continue;
                    AddLink(code, child);
                    if (!expanded.Contains(child)) queue.Enqueue(child);
                }
            }
        }
        cancellationToken.ThrowIfCancellationRequested();

        // Both observations may be optional, but they must not contradict each other.
        foreach (var entry in links.ToArray())
        {
            var link = entry.Value;
            var parent = states[link.Parent];
            var child = states[link.Child];
            if (!parent.Loaded || !child.Loaded || parent.Error is not null || child.Error is not null)
                links[entry.Key] = link with { Confirmed = false, Error = "Связь не подтверждена: сведения по узлу недоступны." };
            else if ((child.Parent is not null && child.Parent != parent.Code) ||
                (parent.ChildrenKnown && !parent.ChildSet.Contains(child.Code)))
            {
                child.Error = "Противоречие между родителем и составом упаковки.";
                links[entry.Key] = link with { Confirmed = false, Error = child.Error };
            }
        }
        foreach (var group in links.Values.GroupBy(l => l.Child).Where(g => g.Select(l => l.Parent).Distinct().Count() > 1))
        {
            states[group.Key].Error = "Код указан у нескольких родителей.";
            foreach (var link in group) links[(link.Parent, link.Child)] = link with { Confirmed = false, Error = states[group.Key].Error };
        }
        MarkCycles(states, links);
        var nodes = states.Values.Select(s => new AggregationTreeNode(s.Code, s.Type, s.Status, s.Loaded,
            s.Error ?? (!s.Loaded ? "Узел не загружен." : null), s.Children.Length,
            s.Children.Count(c => !links.ContainsKey((s.Code, c))))).ToArray();
        var complete = !limited && !stop && nodes.All(n => n.Loaded && n.Error is null) && links.Values.All(l => l.Confirmed);
        if (nodes.Any(n => n.Error is not null)) diagnostics.Add("Часть узлов недоступна или содержит противоречивые сведения. Проблемные пути исключены из TXT.");
        var tree = new AggregationTreeResult(input.Code, input.IncludeSiblings, root, nodes, links.Values.ToArray(), complete, diagnostics.ToArray());
        watch.Stop();
        var first = exchanges[0];
        return first with { StatusCode = 200, ResponseBody = JsonSerializer.Serialize(tree, JsonOptions),
            Duration = watch.Elapsed, Exchanges = exchanges };
    }

    private async Task<BatchResult> FetchBatch(string[] codes, ApiOperationContext context, CancellationToken token,
        Func<bool> shouldStop)
    {
        var exchanges = new List<IntegrationExchange>();
        var request = new IntegrationRequest(OperationId, "POST", client.BaseAddress.AbsoluteUri.TrimEnd('/') + "/cises/info",
            new Dictionary<string, string> { ["Accept"] = "application/json", ["Content-Type"] = "application/json",
                ["X-Correlation-Id"] = context.CorrelationId }, JsonSerializer.Serialize(codes, JsonOptions), context.CorrelationId,
            new(context.Profile.Id, context.Profile.Inn, context.Profile.CertificateThumbprint));
        for (var attempt = 0; attempt < 3; attempt++)
        {
            token.ThrowIfCancellationRequested();
            if (shouldStop()) return new(null, exchanges, 403, "Загрузка остановлена из-за ошибки доступа.", false);
            IntegrationExchange exchange;
            try { exchange = await client.ExecuteAsync(request, token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception)
            {
                // Never expose raw transport text, credentials or response fragments as diagnostics.
                exchange = new(502, request.Method, request.Url, request.Headers, request.Body,
                    new Dictionary<string, string>(), null, TimeSpan.Zero);
                exchanges.Add(exchange);
                return new(null, exchanges, 502, "Не удалось получить сведения по партии кодов.", false);
            }
            exchanges.Add(exchange);
            if (exchange.StatusCode == 429 && attempt < 2)
            {
                await Task.Delay(TimeSpan.FromSeconds(1 << attempt), token);
                continue;
            }
            if (exchange.StatusCode is not (>= 200 and < 300) and not 404)
                return new(null, exchanges, exchange.StatusCode, $"Сервис вернул HTTP {exchange.StatusCode}.", false);
            var parsed = CodeInfoResults.Parse(exchange.ResponseBody);
            if (parsed is null) return new(null, exchanges, 502, "Некорректный формат ответа по партии кодов.", false);
            var requested = codes.ToHashSet(StringComparer.Ordinal);
            var unexpected = parsed.Items.Any(i => i.CisInfo?.RequestedCis is not { } code || !requested.Contains(code));
            var grouped = parsed.Items.Where(i => i.CisInfo?.RequestedCis is not null)
                .GroupBy(i => i.CisInfo!.RequestedCis!, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
            return new(grouped, exchanges, exchange.StatusCode, null, unexpected);
        }
        throw new UnreachableException();
    }

    private static void MarkCycles(Dictionary<string, NodeState> states,
        Dictionary<(string Parent, string Child), AggregationTreeLink> links)
    {
        var parents = links.Values.GroupBy(l => l.Child).Where(g => g.Count() == 1)
            .ToDictionary(g => g.Key, g => g.First().Parent, StringComparer.Ordinal);
        var done = new HashSet<string>(StringComparer.Ordinal);
        foreach (var start in states.Keys)
        {
            if (done.Contains(start)) continue;
            var path = new List<string>();
            var positions = new Dictionary<string, int>(StringComparer.Ordinal);
            var code = start;
            while (!done.Contains(code))
            {
                if (positions.TryGetValue(code, out var position))
                {
                    foreach (var cyclic in path.Skip(position))
                    {
                        states[cyclic].Error = "Цикл в связях агрегации.";
                        var parent = parents[cyclic];
                        links[(parent, cyclic)] = links[(parent, cyclic)] with { Confirmed = false, Error = states[cyclic].Error };
                    }
                    break;
                }
                positions.Add(code, path.Count);
                path.Add(code);
                if (!parents.TryGetValue(code, out var next)) break;
                code = next;
            }
            done.UnionWith(path);
        }
    }

    private static bool ValidCode(string code) => code.Length is >= 18 and <= 74 && !code.Any(char.IsControl) &&
        !code.Contains("(01)", StringComparison.Ordinal) && !code.Contains("(21)", StringComparison.Ordinal);

    private sealed class NodeState(string code)
    {
        public string Code { get; } = code;
        public bool Attempted { get; set; }
        public bool Loaded { get; set; }
        public string? Type { get; set; }
        public string? Status { get; set; }
        public string? Parent { get; set; }
        public string[] Children { get; set; } = [];
        public HashSet<string> ChildSet { get; set; } = new(StringComparer.Ordinal);
        public bool ChildrenKnown { get; set; }
        public string? Error { get; set; }
    }
    private sealed record BatchResult(Dictionary<string, CodeInfoItem[]>? Items,
        IReadOnlyList<IntegrationExchange> Exchanges, int StatusCode, string? Error, bool UnexpectedItems);
}
