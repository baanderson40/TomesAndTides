using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using TomesAndTides.Debugging;

namespace TomesAndTides.Integrations;

public sealed record RetainerReadinessSnapshot(
    bool Known,
    bool Ready,
    int RegisteredCharacters,
    int ReadyCharacters,
    long? ClosestEligibleSeconds,
    bool? Busy,
    bool? MultiModeActive,
    DateTimeOffset CapturedUtc,
    string? Error,
    string Fingerprint);

/// <summary>AutoRetainer IPC boundary. All methods are called on framework updates.</summary>
public sealed class AutoRetainerClient : IRetainerProvider, IDisposable
{
    private readonly ICallGateSubscriber<List<ulong>> registeredCharacters;
    private readonly ICallGateSubscriber<ulong, object> offlineCharacterData;
    private readonly ICallGateSubscriber<Dictionary<ulong, HashSet<string>>> enabledRetainers;
    private readonly ICallGateSubscriber<ulong, long?> closestRetainer;
    private readonly ICallGateSubscriber<object> retainerConfiguration;
    private readonly ICallGateSubscriber<bool> isBusy;
    private readonly ICallGateSubscriber<bool> multiModeStatus;
    private readonly ICallGateSubscriber<int?, object> enableSingleMultiMode;
    private readonly ICallGateSubscriber<object> singleMultiModeFinished;
    private readonly IPluginLog log;
    private readonly IUtcClock clock;
    private string? lastReadinessError;
    private string? lastReadinessState;

    private sealed class RetainerConfigurationDto
    {
        public int? AdvanceTimer { get; set; }
        public bool? MultiWaitForAll { get; set; }
    }

    private sealed class OfflineCharacterDto
    {
        public ulong CID { get; set; }
        public string? Name { get; set; }
        public string? World { get; set; }
        public bool? Enabled { get; set; }
        public List<OfflineRetainerDto>? RetainerData { get; set; }
    }

    private sealed class OfflineRetainerDto
    {
        public string? Name { get; set; }
        public bool? HasVenture { get; set; }
        public long? VentureEndsAt { get; set; }
    }

    public AutoRetainerClient(IDalamudPluginInterface pluginInterface, IPluginLog log, IUtcClock? clock = null)
    {
        this.log = log;
        this.clock = clock ?? SystemUtcClock.Instance;
        registeredCharacters = pluginInterface.GetIpcSubscriber<List<ulong>>("AutoRetainer.GetRegisteredCIDs");
        offlineCharacterData = pluginInterface.GetIpcSubscriber<ulong, object>("AutoRetainer.GetOfflineCharacterData");
        enabledRetainers = pluginInterface.GetIpcSubscriber<Dictionary<ulong, HashSet<string>>>("AutoRetainer.PluginState.GetEnabledRetainers");
        closestRetainer = pluginInterface.GetIpcSubscriber<ulong, long?>("AutoRetainer.PluginState.GetClosestRetainerVentureSecondsRemaining");
        retainerConfiguration = pluginInterface.GetIpcSubscriber<object>("AutoRetainer.GetConfig.MultiModeRetainerConfiguration");
        isBusy = pluginInterface.GetIpcSubscriber<bool>("AutoRetainer.PluginState.IsBusy");
        multiModeStatus = pluginInterface.GetIpcSubscriber<bool>("AutoRetainer.PluginState.GetMultiModeStatus");
        enableSingleMultiMode = pluginInterface.GetIpcSubscriber<int?, object>("AutoRetainer.PluginState.EnableSingleMultiMode");
        singleMultiModeFinished = pluginInterface.GetIpcSubscriber<object>("AutoRetainer.PluginState.OnSingleMultiModeFinished");
        singleMultiModeFinished.Subscribe(OnSinglePassFinished);
    }

    public event Action? SinglePassFinished;

    public bool TryGetRegisteredCharacters(out IReadOnlyList<RetainerCharacterIdentity> characters)
    {
        try
        {
            characters = registeredCharacters.InvokeFunc()
                .Select(cid => DeserializeIpc<OfflineCharacterDto>(offlineCharacterData.InvokeFunc(cid)))
                .Where(data => !string.IsNullOrWhiteSpace(data.Name) && !string.IsNullOrWhiteSpace(data.World))
                .Select(data => new RetainerCharacterIdentity(data.CID, data.Name!, data.World!))
                .OrderBy(character => character.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(character => character.World, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return true;
        }
        catch (Exception error)
        {
            characters = [];
            log.Warning(error, "AutoRetainer character roster unavailable; selected-character fishing is blocked.");
            return false;
        }
    }

    public bool TryGetProviderState(out bool busy, out bool multiModeActive)
    {
        try
        {
            busy = isBusy.InvokeFunc();
            multiModeActive = multiModeStatus.InvokeFunc();
            return true;
        }
        catch
        {
            busy = false;
            multiModeActive = false;
            return false;
        }
    }

    public RetainerReadinessSnapshot ReadReadiness(ulong currentCharacterContentId = 0)
    {
        var captured = clock.UtcNow;
        try
        {
            var characters = registeredCharacters.InvokeFunc() ?? throw new InvalidOperationException("AutoRetainer returned no character roster.");
            var selected = enabledRetainers.InvokeFunc() ?? throw new InvalidOperationException("AutoRetainer returned no enabled-retainer map.");
            var rawConfig = retainerConfiguration.InvokeFunc() ?? throw new InvalidOperationException("AutoRetainer retainer configuration is unavailable.");
            var config = DeserializeIpc<RetainerConfigurationDto>(rawConfig);
            var advance = config.AdvanceTimer ?? throw new InvalidOperationException("AutoRetainer AdvanceTimer is missing from its IPC configuration.");
            var waitForAll = config.MultiWaitForAll ?? throw new InvalidOperationException("AutoRetainer MultiWaitForAll is missing from its IPC configuration.");
            if (advance is < 0 or > 300)
                throw new InvalidOperationException($"AutoRetainer AdvanceTimer is outside the documented range: {advance}.");
            int readyCount = 0;
            long? closestEligible = null;
            var fingerprintParts = new List<string>();
            foreach (var cid in characters)
            {
                // AutoRetainer Multi Mode skips the currently logged-in character.
                if (currentCharacterContentId != 0 && cid == currentCharacterContentId)
                    continue;
                var rawData = offlineCharacterData.InvokeFunc(cid) ?? throw new InvalidOperationException("AutoRetainer character data is unavailable.");
                var data = DeserializeIpc<OfflineCharacterDto>(rawData);
                if (data.Enabled is null || data.RetainerData is null)
                    throw new InvalidOperationException("AutoRetainer character IPC data is missing Enabled or RetainerData.");
                if (!data.Enabled.Value)
                    continue;
                var remaining = closestRetainer.InvokeFunc(cid);
                if (remaining is { } seconds && (closestEligible is null || seconds < closestEligible))
                    closestEligible = seconds;
                selected.TryGetValue(cid, out var names);
                if (names is null || names.Count == 0)
                    continue;
                fingerprintParts.Add(BuildCharacterFingerprint(cid, data.RetainerData, names));
                var ventureRemaining = ReadVentureRemaining(data.RetainerData, names);
                if (!waitForAll)
                {
                    if (remaining is { } closest && closest <= advance)
                        readyCount++;
                    continue;
                }

                // AutoRetainer's aggregate timer includes assignment-eligible retainers, but the
                // public IPC does not expose their per-retainer countdowns. Avoid claiming
                // WaitForAll readiness unless every selected retainer's venture timer is known.
                if (ventureRemaining.Count != names.Count)
                    throw new InvalidOperationException("Cannot verify every selected retainer timer for AutoRetainer WaitForAll mode.");
                if (ventureRemaining.All(seconds => seconds <= advance))
                    readyCount++;
            }

            var busy = isBusy.InvokeFunc();
            var active = multiModeStatus.InvokeFunc();
            var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", fingerprintParts.Order(StringComparer.Ordinal)))));
            lastReadinessError = null;
            var state = readyCount > 0 ? "ready" : "not-ready";
            if (lastReadinessState != state)
            {
                log.Information("AutoRetainer readiness changed: {State}; registered={Registered}, readyCharacters={ReadyCharacters}.",
                    state, characters.Count, readyCount);
                lastReadinessState = state;
            }
            return new(true, readyCount > 0, characters.Count, readyCount, closestEligible,
                busy, active, captured, null, fingerprint);
        }
        catch (Exception ex)
        {
            var error = ex.GetBaseException().Message;
            if (lastReadinessError != error)
                log.Warning(ex, "AutoRetainer readiness became unknown; retainer handoff remains blocked.");
            lastReadinessError = error;
            lastReadinessState = "unknown";
            return new(false, false, 0, 0, null, null, null, captured, error, string.Empty);
        }
    }

    /// <summary>Issues one Retainers-only pass. A successful return is not proof of provider acceptance.</summary>
    public void RequestSingleRetainerPass() => enableSingleMultiMode.InvokeAction((int?)0);

    public void Dispose() => singleMultiModeFinished.Unsubscribe(OnSinglePassFinished);

    private void OnSinglePassFinished() => SinglePassFinished?.Invoke();

    private List<long> ReadVentureRemaining(IEnumerable<OfflineRetainerDto> retainers, IReadOnlySet<string> selectedNames)
    {
        var now = clock.UtcNow.ToUnixTimeSeconds();
        var result = new List<long>();
        foreach (var retainer in retainers)
        {
            var name = retainer.Name;
            if (string.IsNullOrWhiteSpace(name))
                throw new InvalidOperationException("AutoRetainer retainer IPC data is missing Name.");
            if (!selectedNames.Contains(name))
                continue;
            if (retainer.HasVenture is null)
                throw new InvalidOperationException("AutoRetainer retainer IPC data is missing HasVenture.");
            if (!retainer.HasVenture.Value)
                continue;
            var end = retainer.VentureEndsAt ?? throw new InvalidOperationException("AutoRetainer retainer IPC data is missing VentureEndsAt.");
            if (end <= 0)
                throw new InvalidOperationException("AutoRetainer venture end time is invalid.");
            result.Add(end - now);
        }
        return result;
    }

    private static string BuildCharacterFingerprint(ulong contentId, IReadOnlyList<OfflineRetainerDto> retainers,
        IReadOnlySet<string> selectedNames)
    {
        var byName = retainers.ToDictionary(retainer => retainer.Name ??
            throw new InvalidOperationException("AutoRetainer retainer IPC data is missing Name."), StringComparer.Ordinal);
        var entries = new List<string>();
        foreach (var name in selectedNames.Order(StringComparer.Ordinal))
        {
            if (!byName.TryGetValue(name, out var retainer) || retainer.HasVenture is null)
                throw new InvalidOperationException("AutoRetainer selected-retainer data is incomplete.");
            var endsAt = retainer.HasVenture.Value
                ? retainer.VentureEndsAt?.ToString(System.Globalization.CultureInfo.InvariantCulture)
                  ?? throw new InvalidOperationException("AutoRetainer selected venture end time is missing.")
                : "no-venture";
            entries.Add($"{name}:{endsAt}");
        }
        return $"{contentId}:{string.Join(';', entries)}";
    }

    private static T DeserializeIpc<T>(object value)
    {
        if (value is T typed)
            return typed;

        var type = value.GetType();
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, IncludeFields = true };
        var json = type.FullName?.StartsWith("Newtonsoft.Json.Linq.", StringComparison.Ordinal) == true
            ? value.ToString()
            : JsonSerializer.Serialize(value, type, options);
        if (string.IsNullOrWhiteSpace(json))
            throw new InvalidOperationException($"AutoRetainer IPC {type.Name} produced empty configuration data.");
        return JsonSerializer.Deserialize<T>(json, options)
               ?? throw new InvalidOperationException($"AutoRetainer IPC {type.Name} could not be decoded as {typeof(T).Name}.");
    }
}
