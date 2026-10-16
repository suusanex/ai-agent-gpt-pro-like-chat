#:property TargetFramework=net10.0

using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

// Facade の C# は参照しない。公開契約がプロセスの標準入出力であり、BCL の Process で足りる。
// Copilot SDK は、Issue #1 の決定変更により最初の実行先にしていない。
Environment.ExitCode = await App.RunAsync(args);

internal static class App
{
    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            if (args.Length == 0)
            {
                Console.Error.WriteLine(Usage);
                return 2;
            }

            return args[0] switch
            {
                "self-test" => await SelfTest.RunAsync(),
                "run" => await RunComparisonCommandAsync(ParseFlags(args, 1)),
                "baseline" => await RunBaselineCommandAsync(ParseFlags(args, 1)),
                "continue" => await ContinueCommandAsync(ParseFlags(args, 1)),
                _ => UnknownCommand(args[0]),
            };
        }
        catch (Exception ex)
        {
            Trace.TraceError(ex.ToString());
            Console.Error.WriteLine(ex.ToString());
            return 1;
        }
    }

    private static async Task<int> RunComparisonCommandAsync(Dictionary<string, string> flags)
    {
        var loaded = ConfigLoader.Load(Require(flags, "config"), requireComparison: true, requireBaseline: false);
        var question = ReadQuestion(Require(flags, "question"));
        var runDirectory = Path.GetFullPath(Require(flags, "out"));
        RejectUnknown(flags, "config", "question", "out");
        var runner = new ProtocolRunner(new FacadeCliRoleExecutor(loaded.Config.FacadeCliPath), DefaultWorkRoot);
        await runner.RunComparisonAsync(loaded.Config, question, runDirectory, CancellationToken.None);
        Console.WriteLine(runDirectory);
        return 0;
    }

    private static async Task<int> RunBaselineCommandAsync(Dictionary<string, string> flags)
    {
        var loaded = ConfigLoader.Load(Require(flags, "config"), requireComparison: false, requireBaseline: true);
        var question = ReadQuestion(Require(flags, "question"));
        var runDirectory = Path.GetFullPath(Require(flags, "out"));
        RejectUnknown(flags, "config", "question", "out");
        var runner = new ProtocolRunner(new FacadeCliRoleExecutor(loaded.Config.FacadeCliPath), DefaultWorkRoot);
        await runner.RunBaselineAsync(loaded.Config, question, runDirectory, CancellationToken.None);
        Console.WriteLine(runDirectory);
        return 0;
    }

    private static async Task<int> ContinueCommandAsync(Dictionary<string, string> flags)
    {
        var runDirectory = Path.GetFullPath(Require(flags, "run-dir"));
        var downstream = Require(flags, "downstream");
        RejectUnknown(flags, "run-dir", "downstream");
        var snapshotPath = Path.Combine(runDirectory, RunFiles.ConfigSnapshot);
        var loaded = ConfigLoader.Load(snapshotPath, requireComparison: false, requireBaseline: false);
        ConfigLoader.RequireDownstream(loaded.Config, downstream);
        var runner = new ProtocolRunner(new FacadeCliRoleExecutor(loaded.Config.FacadeCliPath), DefaultWorkRoot);
        await runner.ContinueAsync(loaded.Config, runDirectory, downstream, CancellationToken.None);
        Console.WriteLine(runDirectory);
        return 0;
    }

    internal static string DefaultWorkRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ai-agent-gpt-pro-like-chat",
        "work");

    private static string ReadQuestion(string path)
    {
        if (!File.Exists(path))
        {
            throw new ProtocolException($"Question file not found: {path}");
        }

        var text = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ProtocolException($"Question file is empty: {path}");
        }

        return text.Trim();
    }

    private static int UnknownCommand(string command)
    {
        Console.Error.WriteLine($"Unknown command '{command}'.");
        Console.Error.WriteLine(Usage);
        return 2;
    }

    private static Dictionary<string, string> ParseFlags(string[] args, int start)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = start; i < args.Length; i++)
        {
            var token = args[i];
            if (!token.StartsWith("--", StringComparison.Ordinal) || token.Length < 3)
            {
                throw new ProtocolException($"Unexpected argument '{token}'.");
            }

            var key = token[2..];
            if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                throw new ProtocolException($"Missing value for --{key}.");
            }

            if (!result.TryAdd(key, args[++i]))
            {
                throw new ProtocolException($"Duplicate option --{key}.");
            }
        }

        return result;
    }

    private static string Require(Dictionary<string, string> flags, string name)
    {
        if (!flags.TryGetValue(name, out var value) || string.IsNullOrWhiteSpace(value))
        {
            throw new ProtocolException($"Option --{name} is required.");
        }

        return value;
    }

    private static void RejectUnknown(Dictionary<string, string> flags, params string[] allowed)
    {
        var allowedSet = new HashSet<string>(allowed, StringComparer.Ordinal);
        foreach (var key in flags.Keys)
        {
            if (!allowedSet.Contains(key))
            {
                throw new ProtocolException($"Unknown option --{key}.");
            }
        }
    }

    private const string Usage = """
        Usage:
          dotnet run --file src/ProLikeRunner.cs -- run --config <file> --question <file> --out <run-dir>
          dotnet run --file src/ProLikeRunner.cs -- baseline --config <file> --question <file> --out <run-dir>
          dotnet run --file src/ProLikeRunner.cs -- continue --run-dir <run-dir> --downstream pro-like|fable-like|both
          dotnet run --file src/ProLikeRunner.cs -- self-test
        """;
}

internal static class RunFiles
{
    public const string Question = "question.md";
    public const string ConfigSnapshot = "config.snapshot.json";
    public const string Disagreement = "disagreement.md";
    public const string Critic = "critic.md";
    public const string CriticSkipped = "critic.skipped";
    public const string Adjudication = "adjudication.md";
    public const string FinalProLike = "final-pro-like.md";
    public const string FinalFableLike = "final-fable-like.md";
    public const string FinalBaseline = "final-baseline.md";
    public const string Trace = "trace.log";

    public static string AnalystDirectory(int index) => Path.Combine("analysts", index.ToString(CultureInfo.InvariantCulture));
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = true)]
[JsonSerializable(typeof(ExperimentConfig))]
[JsonSerializable(typeof(FacadeEnvelope))]
[JsonSerializable(typeof(FacadeRequestDto))]
[JsonSerializable(typeof(RoleResultRecord))]
internal partial class AppJsonContext : JsonSerializerContext
{
}

internal static class ConfigLoader
{
    private static readonly HashSet<string> Agents = new(StringComparer.Ordinal)
    {
        "github-copilot",
        "grok-build",
        "cursor",
        "codex",
    };

    public static LoadedConfig Load(string configPath, bool requireComparison, bool requireBaseline)
    {
        if (!File.Exists(configPath))
        {
            throw new ProtocolException($"Config file not found: {configPath}");
        }

        ExperimentConfig config;
        try
        {
            config = JsonSerializer.Deserialize(File.ReadAllText(configPath), AppJsonContext.Default.ExperimentConfig)
                ?? throw new ProtocolException($"Config file is empty: {configPath}");
        }
        catch (JsonException ex)
        {
            Trace.TraceError(ex.ToString());
            throw new ProtocolException($"Config file is not valid JSON: {configPath}", ex);
        }

        var configDirectory = Path.GetDirectoryName(Path.GetFullPath(configPath))
            ?? throw new ProtocolException($"Config directory cannot be resolved: {configPath}");
        config.FacadeCliPath = ResolvePath(configDirectory, config.FacadeCliPath, "facadeCliPath");
        config.PromptsDirectory = ResolvePath(configDirectory, config.PromptsDirectory, "promptsDirectory");
        if (!File.Exists(config.FacadeCliPath))
        {
            throw new ProtocolException($"Facade CLI was not found: {config.FacadeCliPath}");
        }

        if (!Directory.Exists(config.PromptsDirectory))
        {
            throw new ProtocolException($"Prompts directory was not found: {config.PromptsDirectory}");
        }

        if (requireComparison)
        {
            RequireComparison(config);
        }

        if (requireBaseline)
        {
            ValidateBinding(config.Baseline, "baseline");
        }

        return new LoadedConfig(config);
    }

    public static void RequireComparison(ExperimentConfig config)
    {
        if (config.Analysts is null || config.Analysts.Count == 0)
        {
            throw new ProtocolException("Config analysts must contain at least one role.");
        }

        for (var i = 0; i < config.Analysts.Count; i++)
        {
            ValidateBinding(config.Analysts[i], "analysts[" + i.ToString(CultureInfo.InvariantCulture) + "]");
        }

        ValidateBinding(config.Disagreement, "disagreement");
        ValidateBinding(config.Critic, "critic");
        ValidateBinding(config.Arbiter, "arbiter");
        ValidateBinding(config.Adjudicator, "adjudicator");
        ValidateBinding(config.Synthesizer, "synthesizer");
    }

    public static void RequireDownstream(ExperimentConfig config, string downstream)
    {
        switch (downstream)
        {
            case "pro-like":
                ValidateBinding(config.Arbiter, "arbiter");
                return;
            case "fable-like":
                ValidateBinding(config.Adjudicator, "adjudicator");
                ValidateBinding(config.Synthesizer, "synthesizer");
                return;
            case "both":
                ValidateBinding(config.Arbiter, "arbiter");
                ValidateBinding(config.Adjudicator, "adjudicator");
                ValidateBinding(config.Synthesizer, "synthesizer");
                return;
            default:
                throw new ProtocolException("Option --downstream must be pro-like, fable-like, or both.");
        }
    }

    public static void ValidateBinding(RoleBinding? binding, string name)
    {
        if (binding is null)
        {
            throw new ProtocolException($"Config {name} is required.");
        }

        if (string.IsNullOrWhiteSpace(binding.Agent) || !Agents.Contains(binding.Agent))
        {
            throw new ProtocolException($"Config {name}.agent must be github-copilot, grok-build, cursor, or codex.");
        }

        if (string.IsNullOrWhiteSpace(binding.Model))
        {
            throw new ProtocolException($"Config {name}.model is required.");
        }

        if (binding.TimeoutSeconds is < 1 or > 86400)
        {
            throw new ProtocolException($"Config {name}.timeoutSeconds must be from 1 to 86400.");
        }

        if (binding.AutoApprove is null)
        {
            throw new ProtocolException($"Config {name}.autoApprove is required.");
        }
    }

    private static string ResolvePath(string configDirectory, string? path, string name)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ProtocolException($"Config {name} is required.");
        }

        var full = Path.IsPathRooted(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(Path.Combine(configDirectory, path));
        return full;
    }
}

internal sealed record LoadedConfig(ExperimentConfig Config);

internal sealed class ExperimentConfig
{
    public string FacadeCliPath { get; set; } = "";

    public string PromptsDirectory { get; set; } = "";

    public bool IncludeAnalystTextsInSynthesizer { get; set; }

    public List<RoleBinding> Analysts { get; set; } = [];

    public RoleBinding? Disagreement { get; set; }

    public RoleBinding? Critic { get; set; }

    public RoleBinding? Arbiter { get; set; }

    public RoleBinding? Adjudicator { get; set; }

    public RoleBinding? Synthesizer { get; set; }

    public RoleBinding? Baseline { get; set; }
}

internal sealed class FacadeRequestDto
{
    [JsonPropertyName("agent")]
    public string Agent { get; set; } = "";

    [JsonPropertyName("prompt")]
    public string Prompt { get; set; } = "";

    [JsonPropertyName("working_directory")]
    public string WorkingDirectory { get; set; } = "";

    [JsonPropertyName("model")]
    public string Model { get; set; } = "";

    [JsonPropertyName("reasoning_effort")]
    public string? ReasoningEffort { get; set; }

    [JsonPropertyName("fast")]
    public bool? Fast { get; set; }

    [JsonPropertyName("timeout_seconds")]
    public int TimeoutSeconds { get; set; }

    [JsonPropertyName("auto_approve")]
    public bool AutoApprove { get; set; }
}

internal sealed class RoleResultRecord
{
    public string Agent { get; set; } = "";

    public string Model { get; set; } = "";

    public string? SessionId { get; set; }

    public string? OutputKind { get; set; }

    public string? RunId { get; set; }

    public int ExitCode { get; set; }

    public string? EventsLogPath { get; set; }

    public string? TextLogPath { get; set; }

    public string WorkingDirectory { get; set; } = "";

    public int WorkingDirectoryEntryCount { get; set; }

    public List<string> WorkingDirectoryEntries { get; set; } = [];
}

internal sealed class RoleBinding
{
    public string Agent { get; set; } = "";

    public string Model { get; set; } = "";

    public string? ReasoningEffort { get; set; }

    public bool? Fast { get; set; }

    public int TimeoutSeconds { get; set; }

    public bool? AutoApprove { get; set; }
}

internal enum DisagreementMarker
{
    Present,
    None,
    Invalid,
}

internal static class DisagreementMarkers
{
    public const string PresentLine = "DISAGREEMENT: present";
    public const string NoneLine = "DISAGREEMENT: none";

    public static DisagreementMarker Parse(string text)
    {
        var line = LastNonEmptyLine(text);
        if (line == PresentLine)
        {
            return DisagreementMarker.Present;
        }

        if (line == NoneLine)
        {
            return DisagreementMarker.None;
        }

        return DisagreementMarker.Invalid;
    }

    public static string? LastNonEmptyLine(string text)
    {
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var lines = normalized.Split('\n');
        for (var i = lines.Length - 1; i >= 0; i--)
        {
            if (!string.IsNullOrWhiteSpace(lines[i]))
            {
                return lines[i].Trim();
            }
        }

        return null;
    }
}

internal static class PromptComposer
{
    public static string Analyst(string rolePrompt, string question)
    {
        return Join(rolePrompt, Section("元質問", question));
    }

    public static string Disagreement(string rolePrompt, string question, IReadOnlyList<string> analystOutputs)
    {
        return Join(rolePrompt, Section("元質問", question), AnalystSections(analystOutputs));
    }

    public static string Critic(string rolePrompt, string question, string disagreement)
    {
        return Join(rolePrompt, Section("元質問", question), Section("Disagreement", disagreement));
    }

    public static string Judgment(
        string rolePrompt,
        string question,
        IReadOnlyList<string> analystOutputs,
        string disagreement,
        string? critic)
    {
        var criticSection = critic is null
            ? Section("Critic", "Critic は起動していない。")
            : Section("Critic", critic);
        return Join(rolePrompt, Section("元質問", question), AnalystSections(analystOutputs), Section("Disagreement", disagreement), criticSection);
    }

    public static string Synthesizer(
        string rolePrompt,
        string question,
        string adjudication,
        IReadOnlyList<string>? analystOutputs)
    {
        var parts = new List<string>
        {
            rolePrompt.Trim(),
            Section("元質問", question),
            Section("裁定", adjudication),
        };
        if (analystOutputs is not null)
        {
            parts.Add(AnalystSections(analystOutputs));
        }

        return string.Join("\n\n", parts);
    }

    public static string Baseline(string question) => question.Trim();

    private static string AnalystSections(IReadOnlyList<string> analystOutputs)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < analystOutputs.Count; i++)
        {
            if (i > 0)
            {
                builder.Append("\n\n");
            }

            builder.Append(Section("Analyst " + (i + 1).ToString(CultureInfo.InvariantCulture), analystOutputs[i]));
        }

        return builder.ToString();
    }

    private static string Section(string title, string body) => "## " + title + "\n\n" + body.Trim();

    private static string Join(params string[] parts) => string.Join("\n\n", parts.Select(part => part.Trim()));
}

internal static class FacadeRequests
{
    public static string BuildJson(RoleExecutionRequest request)
    {
        var payload = new FacadeRequestDto
        {
            Agent = request.Agent,
            Prompt = request.Prompt,
            WorkingDirectory = request.WorkingDirectory,
            Model = request.Model,
            TimeoutSeconds = request.TimeoutSeconds,
            AutoApprove = request.AutoApprove,
            ReasoningEffort = string.IsNullOrWhiteSpace(request.ReasoningEffort) ? null : request.ReasoningEffort,
            Fast = request.Fast,
        };
        return JsonSerializer.Serialize(payload, AppJsonContext.Default.FacadeRequestDto);
    }

    public static RoleExecution ParseStdout(string stdout, int exitCode)
    {
        FacadeEnvelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize(stdout, AppJsonContext.Default.FacadeEnvelope);
        }
        catch (JsonException ex)
        {
            Trace.TraceError(ex.ToString());
            throw new ProtocolException($"Facade CLI stdout was not JSON. exitCode={exitCode}.", ex);
        }

        if (envelope is null)
        {
            throw new ProtocolException($"Facade CLI stdout was empty. exitCode={exitCode}.");
        }

        if (!string.Equals(envelope.Status, "completed", StringComparison.Ordinal) || exitCode != 0)
        {
            throw new ProtocolException(
                "Facade CLI failed. status=" + envelope.Status
                + " exitCode=" + exitCode.ToString(CultureInfo.InvariantCulture)
                + " kind=" + envelope.Failure?.Kind
                + " summary=" + envelope.Failure?.Summary
                + " error=" + envelope.Error);
        }

        if (string.IsNullOrWhiteSpace(envelope.Result?.OutputText))
        {
            throw new ProtocolException("Facade CLI completed with empty outputText.");
        }

        return new RoleExecution(
            envelope.Result.OutputText,
            envelope.Result.SessionId,
            envelope.Result.OutputKind,
            envelope.Result.RunId,
            envelope.Result.ExitCode,
            envelope.Result.EventsLogPath,
            envelope.Result.TextLogPath);
    }
}

internal sealed class FacadeEnvelope
{
    public string? Status { get; set; }

    public FacadeResultBody? Result { get; set; }

    public string? Error { get; set; }

    public FacadeFailureBody? Failure { get; set; }
}

internal sealed class FacadeResultBody
{
    public string? SessionId { get; set; }

    public int ExitCode { get; set; }

    public string? OutputText { get; set; }

    public string? RunId { get; set; }

    public string? EventsLogPath { get; set; }

    public string? TextLogPath { get; set; }

    public string? OutputKind { get; set; }
}

internal sealed class FacadeFailureBody
{
    public string? Kind { get; set; }

    public string? Summary { get; set; }
}

internal sealed record RoleExecutionRequest(
    string RoleName,
    string Agent,
    string Prompt,
    string WorkingDirectory,
    string Model,
    string? ReasoningEffort,
    bool? Fast,
    int TimeoutSeconds,
    bool AutoApprove);

internal sealed record RoleExecution(
    string OutputText,
    string? SessionId,
    string? OutputKind,
    string? RunId,
    int ExitCode,
    string? EventsLogPath,
    string? TextLogPath);

internal interface IRoleExecutor
{
    Task<RoleExecution> ExecuteAsync(RoleExecutionRequest request, CancellationToken cancellationToken);
}

internal sealed class FacadeCliRoleExecutor : IRoleExecutor
{
    private readonly string _facadeCliPath;
    private readonly TimeSpan _deadlineGrace;

    public FacadeCliRoleExecutor(string facadeCliPath, TimeSpan? deadlineGrace = null)
    {
        _facadeCliPath = facadeCliPath;
        _deadlineGrace = deadlineGrace ?? TimeSpan.FromSeconds(30);
    }

    public async Task<RoleExecution> ExecuteAsync(RoleExecutionRequest request, CancellationToken cancellationToken)
    {
        var json = FacadeRequests.BuildJson(request);
        var psi = new ProcessStartInfo
        {
            FileName = _facadeCliPath,
            WorkingDirectory = request.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        using var process = new Process { StartInfo = psi };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(request.TimeoutSeconds) + _deadlineGrace);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        if (!process.Start())
        {
            throw new ProtocolException("Facade CLI process did not start.");
        }

        using var registration = linked.Token.Register(() => ProcessControl.KillTree(process));
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.StandardInput.WriteAsync(json.AsMemory(), CancellationToken.None);
        process.StandardInput.Close();

        try
        {
            await process.WaitForExitAsync(linked.Token);
        }
        catch (OperationCanceledException ex)
        {
            Trace.TraceError(ex.ToString());
            ProcessControl.KillTree(process);
            await process.WaitForExitAsync(CancellationToken.None);
            _ = await stdoutTask;
            _ = await stderrTask;
            if (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException("Facade CLI process was killed.", ex, cancellationToken);
            }

            throw new ProtocolException(
                "Runner deadline exceeded. The Facade CLI process was killed. Missing JSON is expected after a forced process kill.",
                ex);
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        if (!string.IsNullOrWhiteSpace(stderr))
        {
            Trace.TraceInformation("Facade CLI stderr bytes=" + Encoding.UTF8.GetByteCount(stderr).ToString(CultureInfo.InvariantCulture));
        }

        return FacadeRequests.ParseStdout(stdout, process.ExitCode);
    }
}

internal static class ProcessControl
{
    public static void KillTree(Process process)
    {
        try
        {
            if (process.HasExited)
            {
                return;
            }

            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            // 終了済みプロセスの Kill は実行結果を変えない。捨てる前に全文を残す。
            Trace.TraceError(ex.ToString());
        }
    }
}

internal sealed class ProtocolRunner
{
    private readonly IRoleExecutor _executor;
    private readonly string _workRoot;

    public ProtocolRunner(IRoleExecutor executor, string workRoot)
    {
        _executor = executor;
        _workRoot = workRoot;
    }

    public async Task RunComparisonAsync(
        ExperimentConfig config,
        string question,
        string runDirectory,
        CancellationToken cancellationToken)
    {
        runDirectory = Path.GetFullPath(runDirectory);
        PrepareNewRun(config, question, runDirectory);
        using var trace = TraceScope.Start(Path.Combine(runDirectory, RunFiles.Trace));
        var analysts = await RunAnalystsAsync(config, question, runDirectory, cancellationToken);
        var disagreement = await RunSingleAsync(
            config.Disagreement!,
            "disagreement",
            PromptComposer.Disagreement(ReadPrompt(config, "disagreement.md"), question, analysts),
            runDirectory,
            RunFiles.Disagreement,
            cancellationToken);
        var marker = RequireMarker(disagreement);
        string? critic = null;
        if (marker == DisagreementMarker.Present)
        {
            critic = await RunSingleAsync(
                config.Critic!,
                "critic",
                PromptComposer.Critic(ReadPrompt(config, "critic.md"), question, disagreement),
                runDirectory,
                RunFiles.Critic,
                cancellationToken);
        }
        else
        {
            WriteText(Path.Combine(runDirectory, RunFiles.CriticSkipped), DisagreementMarkers.NoneLine + "\n");
        }

        await RunProLikeAsync(config, question, analysts, disagreement, critic, runDirectory, cancellationToken);
        await RunFableLikeAsync(config, question, analysts, disagreement, critic, runDirectory, cancellationToken);
    }

    public async Task RunBaselineAsync(
        ExperimentConfig config,
        string question,
        string runDirectory,
        CancellationToken cancellationToken)
    {
        runDirectory = Path.GetFullPath(runDirectory);
        PrepareNewRun(config, question, runDirectory);
        using var trace = TraceScope.Start(Path.Combine(runDirectory, RunFiles.Trace));
        await RunSingleAsync(
            config.Baseline!,
            "baseline",
            PromptComposer.Baseline(question),
            runDirectory,
            RunFiles.FinalBaseline,
            cancellationToken);
    }

    public async Task ContinueAsync(
        ExperimentConfig config,
        string runDirectory,
        string downstream,
        CancellationToken cancellationToken)
    {
        runDirectory = Path.GetFullPath(runDirectory);
        if (!Directory.Exists(runDirectory))
        {
            throw new ProtocolException($"Run directory was not found: {runDirectory}");
        }

        using var trace = TraceScope.Start(Path.Combine(runDirectory, RunFiles.Trace));
        var question = ReadRequired(Path.Combine(runDirectory, RunFiles.Question));
        var analysts = ReadAnalysts(runDirectory, config.Analysts.Count);
        var disagreement = ReadRequired(Path.Combine(runDirectory, RunFiles.Disagreement));
        var marker = RequireMarker(disagreement);
        string? critic = null;
        var criticPath = Path.Combine(runDirectory, RunFiles.Critic);
        if (marker == DisagreementMarker.Present)
        {
            critic = ReadRequired(criticPath);
        }
        else if (File.Exists(criticPath))
        {
            throw new ProtocolException("critic.md exists even though disagreement ended with DISAGREEMENT: none.");
        }

        if (downstream is "pro-like" or "both")
        {
            await RunProLikeAsync(config, question, analysts, disagreement, critic, runDirectory, cancellationToken);
        }

        if (downstream is "fable-like" or "both")
        {
            await RunFableLikeAsync(config, question, analysts, disagreement, critic, runDirectory, cancellationToken);
        }
    }

    private async Task RunProLikeAsync(
        ExperimentConfig config,
        string question,
        IReadOnlyList<string> analysts,
        string disagreement,
        string? critic,
        string runDirectory,
        CancellationToken cancellationToken)
    {
        await RunSingleAsync(
            config.Arbiter!,
            "arbiter",
            PromptComposer.Judgment(ReadPrompt(config, "arbiter.md"), question, analysts, disagreement, critic),
            runDirectory,
            RunFiles.FinalProLike,
            cancellationToken);
    }

    private async Task RunFableLikeAsync(
        ExperimentConfig config,
        string question,
        IReadOnlyList<string> analysts,
        string disagreement,
        string? critic,
        string runDirectory,
        CancellationToken cancellationToken)
    {
        var adjudication = await RunSingleAsync(
            config.Adjudicator!,
            "adjudicator",
            PromptComposer.Judgment(ReadPrompt(config, "adjudicator.md"), question, analysts, disagreement, critic),
            runDirectory,
            RunFiles.Adjudication,
            cancellationToken);
        var analystTexts = config.IncludeAnalystTextsInSynthesizer ? analysts : null;
        await RunSingleAsync(
            config.Synthesizer!,
            "synthesizer",
            PromptComposer.Synthesizer(ReadPrompt(config, "synthesizer.md"), question, adjudication, analystTexts),
            runDirectory,
            RunFiles.FinalFableLike,
            cancellationToken);
    }

    private async Task<IReadOnlyList<string>> RunAnalystsAsync(
        ExperimentConfig config,
        string question,
        string runDirectory,
        CancellationToken cancellationToken)
    {
        var outputs = new string[config.Analysts.Count];
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Exception? first = null;
        var gate = new object();
        var tasks = new Task[config.Analysts.Count];
        for (var i = 0; i < config.Analysts.Count; i++)
        {
            var index = i;
            tasks[i] = Task.Run(async () =>
            {
                try
                {
                    cts.Token.ThrowIfCancellationRequested();
                    var prompt = PromptComposer.Analyst(ReadPrompt(config, "analyst.md"), question);
                    var published = Path.Combine(runDirectory, RunFiles.AnalystDirectory(index + 1), "output.md");
                    outputs[index] = await RunSingleAsync(
                        config.Analysts[index],
                        "analyst-" + (index + 1).ToString(CultureInfo.InvariantCulture),
                        prompt,
                        runDirectory,
                        published,
                        cts.Token);
                }
                catch (Exception ex)
                {
                    var sibling = false;
                    lock (gate)
                    {
                        if (first is null)
                        {
                            first = ex;
                            cts.Cancel();
                            Trace.TraceError("Analyst " + (index + 1).ToString(CultureInfo.InvariantCulture) + " failed. Killing sibling Facade CLI processes.\n" + ex);
                        }
                        else
                        {
                            sibling = true;
                        }
                    }

                    if (sibling)
                    {
                        Trace.TraceError(
                            "Analyst " + (index + 1).ToString(CultureInfo.InvariantCulture)
                            + " stopped because a sibling failed. Missing Facade JSON after a forced kill is expected.\n"
                            + ex);
                        return;
                    }

                    throw;
                }
            }, CancellationToken.None);
        }

        try
        {
            await Task.WhenAll(tasks);
        }
        catch (Exception ex)
        {
            Trace.TraceError(ex.ToString());
        }

        if (first is not null)
        {
            throw new ProtocolException("One or more analysts failed. Downstream stages were not started.", first);
        }

        return outputs;
    }

    private async Task<string> RunSingleAsync(
        RoleBinding binding,
        string roleName,
        string prompt,
        string runDirectory,
        string publishedRelativePath,
        CancellationToken cancellationToken)
    {
        var workDirectory = CreateFreshWorkDirectory(runDirectory, roleName);
        var request = new RoleExecutionRequest(
            roleName,
            binding.Agent,
            prompt,
            workDirectory,
            binding.Model,
            string.IsNullOrWhiteSpace(binding.ReasoningEffort) ? null : binding.ReasoningEffort,
            binding.Fast,
            binding.TimeoutSeconds,
            binding.AutoApprove!.Value);
        var roleDirectory = Path.Combine(runDirectory, "roles", roleName);
        Directory.CreateDirectory(roleDirectory);
        WriteText(Path.Combine(roleDirectory, "prompt.md"), prompt);
        WriteText(Path.Combine(roleDirectory, "request.json"), FacadeRequests.BuildJson(request));
        var execution = await _executor.ExecuteAsync(request, cancellationToken);
        var entries = ListWorkEntries(workDirectory);
        var result = new RoleResultRecord
        {
            Agent = binding.Agent,
            Model = binding.Model,
            SessionId = execution.SessionId,
            OutputKind = execution.OutputKind,
            RunId = execution.RunId,
            ExitCode = execution.ExitCode,
            EventsLogPath = execution.EventsLogPath,
            TextLogPath = execution.TextLogPath,
            WorkingDirectory = workDirectory,
            WorkingDirectoryEntryCount = entries.Count,
            WorkingDirectoryEntries = entries,
        };
        WriteText(Path.Combine(roleDirectory, "result.json"), JsonSerializer.Serialize(result, AppJsonContext.Default.RoleResultRecord));
        WriteText(Path.Combine(roleDirectory, "output.md"), execution.OutputText);
        var published = Path.IsPathRooted(publishedRelativePath)
            ? publishedRelativePath
            : Path.Combine(runDirectory, publishedRelativePath);
        WriteText(published, execution.OutputText);
        return execution.OutputText;
    }

    // 出力先の末尾名だけだと、別の親にある同名 run が作業場所を共有する。
    // 呼び出しごとに新しい空 directory を作り、過去の作業ファイルは残すが次の呼び出しへ渡さない。
    private string CreateFreshWorkDirectory(string runDirectory, string roleName)
    {
        var workDirectory = Path.Combine(_workRoot, WorkRunKey(runDirectory), roleName, Guid.NewGuid().ToString("N"));
        if (Directory.Exists(workDirectory))
        {
            throw new ProtocolException($"Work directory already exists: {workDirectory}");
        }

        Directory.CreateDirectory(workDirectory);
        if (Directory.EnumerateFileSystemEntries(workDirectory).Any())
        {
            throw new ProtocolException($"Work directory was not empty: {workDirectory}");
        }

        return workDirectory;
    }

    private static string WorkRunKey(string runDirectory)
    {
        var full = Path.GetFullPath(runDirectory);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(full)))[..16].ToLowerInvariant();
        var leaf = Path.GetFileName(full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(leaf.Select(ch => Array.IndexOf(invalid, ch) >= 0 ? '_' : ch).ToArray()).Trim();
        if (safe.Length > 40)
        {
            safe = safe[..40];
        }

        return (string.IsNullOrEmpty(safe) ? "run" : safe) + "-" + hash;
    }

    private static void PrepareNewRun(ExperimentConfig config, string question, string runDirectory)
    {
        if (Directory.Exists(runDirectory) && Directory.EnumerateFileSystemEntries(runDirectory).Any())
        {
            throw new ProtocolException($"Run directory is not empty: {runDirectory}");
        }

        Directory.CreateDirectory(runDirectory);
        WriteText(Path.Combine(runDirectory, RunFiles.Question), question + "\n");
        WriteText(Path.Combine(runDirectory, RunFiles.ConfigSnapshot), JsonSerializer.Serialize(config, AppJsonContext.Default.ExperimentConfig));
    }

    private static List<string> ReadAnalysts(string runDirectory, int count)
    {
        if (count == 0)
        {
            throw new ProtocolException("Config snapshot has no analysts.");
        }

        var outputs = new List<string>(count);
        for (var i = 1; i <= count; i++)
        {
            outputs.Add(ReadRequired(Path.Combine(runDirectory, RunFiles.AnalystDirectory(i), "output.md")));
        }

        return outputs;
    }

    private static string ReadRequired(string path)
    {
        if (!File.Exists(path))
        {
            throw new ProtocolException($"Required run file was not found: {path}");
        }

        var text = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ProtocolException($"Required run file is empty: {path}");
        }

        return text.Trim();
    }

    private static DisagreementMarker RequireMarker(string disagreement)
    {
        var marker = DisagreementMarkers.Parse(disagreement);
        if (marker == DisagreementMarker.Invalid)
        {
            throw new ProtocolException("Disagreement output must end with 'DISAGREEMENT: present' or 'DISAGREEMENT: none'.");
        }

        return marker;
    }

    private static string ReadPrompt(ExperimentConfig config, string fileName)
    {
        var path = Path.Combine(config.PromptsDirectory, fileName);
        if (!File.Exists(path))
        {
            throw new ProtocolException($"Prompt file not found: {path}");
        }

        var text = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ProtocolException($"Prompt file is empty: {path}");
        }

        return text.Trim();
    }

    private static List<string> ListWorkEntries(string workDirectory)
    {
        try
        {
            return Directory.EnumerateFiles(workDirectory, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(workDirectory, path))
                .Take(200)
                .ToList();
        }
        catch (Exception ex)
        {
            Trace.TraceError(ex.ToString());
            throw new ProtocolException($"Working directory could not be listed: {workDirectory}", ex);
        }
    }

    private static void WriteText(string path, string text)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }
}

internal sealed class TraceScope : IDisposable
{
    private readonly TextWriterTraceListener _listener;

    private TraceScope(TextWriterTraceListener listener)
    {
        _listener = listener;
    }

    public static TraceScope Start(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var listener = new TextWriterTraceListener(path, "pro-like-run");
        Trace.Listeners.Add(listener);
        Trace.AutoFlush = true;
        return new TraceScope(listener);
    }

    public void Dispose()
    {
        Trace.Flush();
        Trace.Listeners.Remove(_listener);
        _listener.Dispose();
    }
}

internal static class ProcessControlTests
{
    public static void KillSleepProcess()
    {
        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("/d");
        psi.ArgumentList.Add("/c");
        psi.ArgumentList.Add("ping");
        psi.ArgumentList.Add("-n");
        psi.ArgumentList.Add("30");
        psi.ArgumentList.Add("127.0.0.1");
        using var process = Process.Start(psi) ?? throw new ProtocolException("Test process did not start.");
        ProcessControl.KillTree(process);
        process.WaitForExit(5000);
        if (!process.HasExited)
        {
            throw new ProtocolException("KillTree did not stop the child process.");
        }
    }
}

internal static class SelfTest
{
    public static async Task<int> RunAsync()
    {
        var failures = new List<string>();
        await CheckAsync(failures, "marker", MarkerTests);
        await CheckAsync(failures, "request-json", RequestJsonTests);
        await CheckAsync(failures, "prompts", PromptTests);
        await CheckAsync(failures, "config", ConfigTests);
        await CheckAsync(failures, "comparison", ComparisonTests);
        await CheckAsync(failures, "kill", () =>
        {
            ProcessControlTests.KillSleepProcess();
            return Task.CompletedTask;
        });

        if (failures.Count > 0)
        {
            foreach (var failure in failures)
            {
                Console.Error.WriteLine(failure);
            }

            return 1;
        }

        Console.WriteLine("self-test passed");
        return 0;
    }

    private static async Task CheckAsync(List<string> failures, string name, Func<Task> test)
    {
        try
        {
            await test();
        }
        catch (Exception ex)
        {
            Trace.TraceError(ex.ToString());
            failures.Add(name + ": " + ex);
        }
    }

    private static Task MarkerTests()
    {
        Assert(DisagreementMarkers.Parse("body\nDISAGREEMENT: present\n") == DisagreementMarker.Present, "present");
        Assert(DisagreementMarkers.Parse("body\r\nDISAGREEMENT: none") == DisagreementMarker.None, "none");
        Assert(DisagreementMarkers.Parse("DISAGREEMENT: present\nextra") == DisagreementMarker.Invalid, "trailing text");
        Assert(DisagreementMarkers.Parse("DISAGREEMENT: maybe") == DisagreementMarker.Invalid, "unknown");
        Assert(DisagreementMarkers.Parse("   ") == DisagreementMarker.Invalid, "blank");
        return Task.CompletedTask;
    }

    private static Task RequestJsonTests()
    {
        var json = FacadeRequests.BuildJson(new RoleExecutionRequest(
            "analyst-1",
            "github-copilot",
            "prompt",
            @"C:\work",
            "gpt-5-mini",
            null,
            null,
            1200,
            false));
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert(root.GetProperty("auto_approve").GetBoolean() == false, "auto_approve false");
        Assert(root.GetProperty("model").GetString() == "gpt-5-mini", "model");
        Assert(!root.TryGetProperty("session_id", out _), "no session_id");
        Assert(!root.TryGetProperty("request_id", out _), "no request_id");
        Assert(!root.TryGetProperty("skills", out _), "no skills");
        Assert(!root.TryGetProperty("fast", out _), "fast omitted");
        var withFast = FacadeRequests.BuildJson(new RoleExecutionRequest(
            "analyst-1", "codex", "p", @"C:\work", "gpt-6-luna", "low", false, 10, false));
        using var fastDocument = JsonDocument.Parse(withFast);
        Assert(fastDocument.RootElement.GetProperty("fast").GetBoolean() == false, "fast false sent");
        Assert(fastDocument.RootElement.GetProperty("reasoning_effort").GetString() == "low", "effort sent");

        var completed = """
            {"status":"completed","result":{"sessionId":"s","exitCode":0,"outputText":"answer","outputKind":"assistant_transcript","runId":"r"}}
            """;
        var parsed = FacadeRequests.ParseStdout(completed, 0);
        Assert(parsed.OutputText == "answer", "transcript kept");
        Assert(parsed.OutputKind == "assistant_transcript", "kind");
        AssertThrows(() => FacadeRequests.ParseStdout("""{"status":"failed","failure":{"kind":"non_zero_exit","summary":"nope"}}""", 1));
        return Task.CompletedTask;
    }

    private static Task PromptTests()
    {
        var analyst = PromptComposer.Analyst("ROLE", "QUESTION");
        Assert(analyst.Contains("QUESTION", StringComparison.Ordinal), "analyst question");
        Assert(!analyst.Contains("OTHER", StringComparison.Ordinal), "analyst isolation");
        var disagreement = PromptComposer.Disagreement("ROLE", "QUESTION", ["A1", "A2"]);
        Assert(disagreement.Contains("A1", StringComparison.Ordinal) && disagreement.Contains("A2", StringComparison.Ordinal), "disagreement sees analysts");
        var critic = PromptComposer.Critic("ROLE", "QUESTION", "GAP");
        Assert(critic.Contains("GAP", StringComparison.Ordinal), "critic sees gap");
        Assert(!critic.Contains("A1", StringComparison.Ordinal), "critic hides analysts");
        var judgment = PromptComposer.Judgment("ROLE", "QUESTION", ["A1"], "GAP", null);
        Assert(judgment.Contains("起動していない", StringComparison.Ordinal), "missing critic stated");
        var withCritic = PromptComposer.Judgment("ROLE", "Q", ["A1"], "GAP", "CRIT");
        Assert(withCritic.Contains("CRIT", StringComparison.Ordinal), "critic included");
        var synthesis = PromptComposer.Synthesizer("ROLE", "Q", "JUDGE", null);
        Assert(!synthesis.Contains("A1", StringComparison.Ordinal), "synthesizer default");
        var withAnalysts = PromptComposer.Synthesizer("ROLE", "Q", "JUDGE", ["A1"]);
        Assert(withAnalysts.Contains("A1", StringComparison.Ordinal), "synthesizer optional analysts");
        Assert(PromptComposer.Baseline("  Q  ") == "Q", "baseline");
        return Task.CompletedTask;
    }

    private static Task ConfigTests()
    {
        var root = CreateFixture();
        try
        {
            var missingModel = Path.Combine(root, "missing-model.json");
            File.WriteAllText(missingModel, """
                {
                  "facadeCliPath": "facade.exe",
                  "promptsDirectory": "prompts",
                  "analysts": [{ "agent": "github-copilot", "timeoutSeconds": 10, "autoApprove": false }]
                }
                """);
            File.WriteAllText(Path.Combine(root, "facade.exe"), "");
            Directory.CreateDirectory(Path.Combine(root, "prompts"));
            AssertThrows(() => ConfigLoader.Load(missingModel, requireComparison: true, requireBaseline: false));

            var missingExe = Path.Combine(root, "missing-exe.json");
            File.WriteAllText(missingExe, """
                {
                  "facadeCliPath": "missing.exe",
                  "promptsDirectory": "prompts",
                  "baseline": { "agent": "github-copilot", "model": "gpt-5-mini", "timeoutSeconds": 10, "autoApprove": false }
                }
                """);
            AssertThrows(() => ConfigLoader.Load(missingExe, requireComparison: false, requireBaseline: true));

            var roundtripPath = Path.Combine(root, "roundtrip.json");
            var facadePath = Path.Combine(root, "facade.exe");
            var promptsPath = Path.Combine(root, "prompts");
            var roundtrip = new ExperimentConfig
            {
                FacadeCliPath = facadePath,
                PromptsDirectory = promptsPath,
                Analysts =
                [
                    new RoleBinding
                    {
                        Agent = "github-copilot",
                        Model = "gpt-5-mini",
                        TimeoutSeconds = 15,
                        AutoApprove = false,
                    },
                ],
                Disagreement = new RoleBinding { Agent = "github-copilot", Model = "gpt-5-mini", TimeoutSeconds = 15, AutoApprove = false },
                Critic = new RoleBinding { Agent = "github-copilot", Model = "gpt-5-mini", TimeoutSeconds = 15, AutoApprove = false },
                Arbiter = new RoleBinding { Agent = "github-copilot", Model = "gpt-5-mini", TimeoutSeconds = 15, AutoApprove = false },
                Adjudicator = new RoleBinding { Agent = "github-copilot", Model = "gpt-5-mini", TimeoutSeconds = 15, AutoApprove = false },
                Synthesizer = new RoleBinding { Agent = "github-copilot", Model = "gpt-5-mini", TimeoutSeconds = 15, AutoApprove = false },
            };
            File.WriteAllText(roundtripPath, JsonSerializer.Serialize(roundtrip, AppJsonContext.Default.ExperimentConfig));
            var loaded = ConfigLoader.Load(roundtripPath, requireComparison: true, requireBaseline: false);
            Assert(loaded.Config.Analysts[0].AutoApprove == false, "roundtrip autoApprove");
            Assert(loaded.Config.Analysts[0].Model == "gpt-5-mini", "roundtrip model");
            Assert(loaded.Config.FacadeCliPath == facadePath, "roundtrip facade path");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }

        return Task.CompletedTask;
    }

    private static async Task ComparisonTests()
    {
        var root = CreateFixture();
        try
        {
            var prompts = Path.Combine(root, "prompts");
            Directory.CreateDirectory(prompts);
            foreach (var name in new[] { "analyst", "disagreement", "critic", "arbiter", "adjudicator", "synthesizer" })
            {
                File.WriteAllText(Path.Combine(prompts, name + ".md"), "PROMPT-" + name);
            }

            var config = SampleConfig(prompts);
            var executor = new ScriptedRoleExecutor();
            var runner = new ProtocolRunner(executor, Path.Combine(root, "work"));
            var runDirectory = Path.Combine(root, "run");
            await runner.RunComparisonAsync(config, "QUESTION", runDirectory, CancellationToken.None);
            Assert(File.ReadAllText(Path.Combine(runDirectory, RunFiles.FinalProLike)).Contains("arbiter-output", StringComparison.Ordinal), "pro final");
            Assert(File.ReadAllText(Path.Combine(runDirectory, RunFiles.FinalFableLike)).Contains("synthesizer-output", StringComparison.Ordinal), "fable final");
            Assert(File.Exists(Path.Combine(runDirectory, RunFiles.Critic)), "critic file");
            Assert(executor.Calls.Any(call => call.RoleName == "critic"), "critic called");
            var analystCalls = executor.Calls.Where(call => call.RoleName.StartsWith("analyst-", StringComparison.Ordinal)).ToList();
            Assert(analystCalls.Count == 3, "three analysts");
            Assert(!analystCalls[1].Prompt.Contains("analyst-1-output", StringComparison.Ordinal), "analyst prompt isolation");
            Assert(!executor.Calls.Single(call => call.RoleName == "synthesizer").Prompt.Contains("analyst-1-output", StringComparison.Ordinal), "synthesizer isolation");
            Assert(executor.Calls.Single(call => call.RoleName == "critic").Prompt.Contains("DISAGREEMENT: present", StringComparison.Ordinal), "critic input");
            Assert(!executor.Calls.Single(call => call.RoleName == "critic").Prompt.Contains("analyst-1-output", StringComparison.Ordinal), "critic isolation");

            var noneExecutor = new ScriptedRoleExecutor { Disagreement = "same\nDISAGREEMENT: none" };
            var noneRunner = new ProtocolRunner(noneExecutor, Path.Combine(root, "work-none"));
            var noneDirectory = Path.Combine(root, "run-none");
            await noneRunner.RunComparisonAsync(config, "QUESTION", noneDirectory, CancellationToken.None);
            Assert(!noneExecutor.Calls.Any(call => call.RoleName == "critic"), "critic skipped");
            Assert(File.Exists(Path.Combine(noneDirectory, RunFiles.CriticSkipped)), "critic skipped file");

            var badExecutor = new ScriptedRoleExecutor { Disagreement = "no marker" };
            var badRunner = new ProtocolRunner(badExecutor, Path.Combine(root, "work-bad"));
            await AssertThrowsAsync(() => badRunner.RunComparisonAsync(config, "QUESTION", Path.Combine(root, "run-bad"), CancellationToken.None));
            Assert(!badExecutor.Calls.Any(call => call.RoleName == "critic"), "bad marker stops critic");

            var failing = new ScriptedRoleExecutor { FailAnalyst = 2 };
            var failingRunner = new ProtocolRunner(failing, Path.Combine(root, "work-fail"));
            await AssertThrowsAsync(() => failingRunner.RunComparisonAsync(config, "QUESTION", Path.Combine(root, "run-fail"), CancellationToken.None));
            Assert(!failing.Calls.Any(call => call.RoleName == "disagreement"), "failure stops downstream");
            Assert(failing.CancelledRoles.Contains("analyst-3"), "sibling cancelled");

            var sharedWork = Path.Combine(root, "work");
            foreach (var call in executor.Calls)
            {
                File.WriteAllText(Path.Combine(call.WorkingDirectory, "leftover.txt"), "old");
            }

            var otherExecutor = new ScriptedRoleExecutor();
            var otherParent = Path.Combine(root, "other");
            Directory.CreateDirectory(otherParent);
            await new ProtocolRunner(otherExecutor, sharedWork).RunComparisonAsync(config, "OTHER", Path.Combine(otherParent, "run"), CancellationToken.None);
            Assert(otherExecutor.Calls.Count > 0, "other run called roles");
            Assert(otherExecutor.Calls.All(call => !executor.Calls.Any(previous => previous.WorkingDirectory == call.WorkingDirectory)), "same leaf uses another work directory");
            Assert(otherExecutor.Calls.All(call => !File.Exists(Path.Combine(call.WorkingDirectory, "leftover.txt"))), "same leaf does not inherit leftover");

            var continueExecutor = new ScriptedRoleExecutor();
            continueExecutor.RejectUpstream = true;
            var continueRunner = new ProtocolRunner(continueExecutor, sharedWork);
            await continueRunner.ContinueAsync(config, runDirectory, "both", CancellationToken.None);
            Assert(continueExecutor.Calls.All(call => call.RoleName is "arbiter" or "adjudicator" or "synthesizer"), "continue downstream only");
            Assert(continueExecutor.Calls.All(call => !executor.Calls.Any(previous => previous.WorkingDirectory == call.WorkingDirectory)), "continue uses a new work directory");
            Assert(continueExecutor.Calls.All(call => !File.Exists(Path.Combine(call.WorkingDirectory, "leftover.txt"))), "continue does not inherit leftover");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static ExperimentConfig SampleConfig(string prompts)
    {
        RoleBinding Binding() => new()
        {
            Agent = "github-copilot",
            Model = "gpt-5-mini",
            TimeoutSeconds = 30,
            AutoApprove = false,
        };
        return new ExperimentConfig
        {
            FacadeCliPath = "unused",
            PromptsDirectory = prompts,
            Analysts = [Binding(), Binding(), Binding()],
            Disagreement = Binding(),
            Critic = Binding(),
            Arbiter = Binding(),
            Adjudicator = Binding(),
            Synthesizer = Binding(),
            Baseline = Binding(),
        };
    }

    private static string CreateFixture()
    {
        var root = Path.Combine(Path.GetTempPath(), "pro-like-self-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void Assert(bool condition, string name)
    {
        if (!condition)
        {
            throw new ProtocolException("Assertion failed: " + name);
        }
    }

    private static void AssertThrows(Action action)
    {
        try
        {
            action();
        }
        catch (ProtocolException)
        {
            return;
        }

        throw new ProtocolException("Expected ProtocolException.");
    }

    private static async Task AssertThrowsAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (ProtocolException)
        {
            return;
        }

        throw new ProtocolException("Expected ProtocolException.");
    }
}

internal sealed class ScriptedRoleExecutor : IRoleExecutor
{
    public List<RoleExecutionRequest> Calls { get; } = [];

    public List<string> CancelledRoles { get; } = [];

    public string Disagreement { get; set; } = "gaps\nDISAGREEMENT: present";

    public int? FailAnalyst { get; set; }

    public bool RejectUpstream { get; set; }

    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task<RoleExecution> ExecuteAsync(RoleExecutionRequest request, CancellationToken cancellationToken)
    {
        if (Directory.EnumerateFileSystemEntries(request.WorkingDirectory).Any())
        {
            throw new ProtocolException("Work directory was not empty: " + request.WorkingDirectory);
        }

        lock (Calls)
        {
            Calls.Add(request);
        }

        if (RejectUpstream && request.RoleName is not ("arbiter" or "adjudicator" or "synthesizer"))
        {
            throw new ProtocolException("Upstream role was called during continue: " + request.RoleName);
        }

        if (FailAnalyst is int failed && request.RoleName == "analyst-" + failed.ToString(CultureInfo.InvariantCulture))
        {
            await Started.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            throw new ProtocolException(request.RoleName + " failed");
        }

        if (request.RoleName == "analyst-3" && FailAnalyst is not null)
        {
            Started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            catch (OperationCanceledException ex)
            {
                CancelledRoles.Add(request.RoleName);
                Trace.TraceError(ex.ToString());
                throw;
            }
        }

        var text = request.RoleName == "disagreement" ? Disagreement : request.RoleName + "-output";
        return new RoleExecution(text, "session", "final_response", "run", 0, null, null);
    }
}

internal sealed class ProtocolException : Exception
{
    public ProtocolException(string message)
        : base(message)
    {
    }

    public ProtocolException(string message, Exception inner)
        : base(message, inner)
    {
    }
}
