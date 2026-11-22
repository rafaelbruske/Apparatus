using System.Text.Json;

namespace Apparatus.Core.Start;

/// <summary>Whether the UI thread is still answering. The hooks consult it so that a frozen Apparatus never leaves you without a Start menu.</summary>
public sealed class UiHeartbeat
{
    private readonly Func<long> _nowMs;
    private long _lastBeat;

    public UiHeartbeat(Func<long>? nowMs = null)
    {
        _nowMs = nowMs ?? (() => Environment.TickCount64);
        _lastBeat = _nowMs();
    }

    /// <summary>Called by the UI thread on a short timer.</summary>
    public void Beat() => Volatile.Write(ref _lastBeat, _nowMs());

    public bool IsAlive(int maxSilenceMs = 2000) => _nowMs() - Volatile.Read(ref _lastBeat) <= maxSilenceMs;
}

public sealed class FailsafeState
{
    /// <summary>The Windows build (with revision) on which the replacement was last seen working twice in a row.</summary>
    public string VerifiedBuild { get; set; } = "";

    /// <summary>Consecutive problems seen since it last worked.</summary>
    public int Failures { get; set; }

    /// <summary>Set when the replacement was switched off by itself; empty otherwise.</summary>
    public string TrippedReason { get; set; } = "";

    public string TrippedAtBuild { get; set; } = "";

    public DateTime? TrippedUtc { get; set; }
}

public enum StartProblem
{
    /// <summary>Windows' own Start menu opened as well, so the Windows key (or the click) was not hidden from it.</summary>
    OfficialAlsoOpened,

    /// <summary>The interception fired but Apparatus' menu never appeared.</summary>
    MenuDidNotAppear,
}

public sealed record FailsafeVerdict(bool Trip, string? Message)
{
    public static readonly FailsafeVerdict Continue = new(false, null);
}

/// <summary>
/// Decides when to give the Start menu back to Windows by itself, the way StartAllBack disables itself on a Windows version it was
/// not made for. Apparatus injects nothing, so an update cannot break it silently; the failures it can have are visible ones:
/// the Windows menu opens together with Apparatus', or Apparatus' never shows. The rule is strict right after Windows changed
/// (the first problem switches the replacement off) and tolerant otherwise (two in a row). Two clean uses on a new Windows build
/// mark that build as verified.
/// </summary>
public sealed class StartFailsafe
{
    public const int CleanUsesToVerify = 2;
    public const int FailuresToTrip = 2;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly string _path;
    private readonly object _gate = new();
    private readonly FailsafeState _state;
    private int _cleanUses;

    public StartFailsafe(string path, string currentBuild)
    {
        _path = path;
        CurrentBuild = currentBuild;
        _state = Load();
    }

    public string CurrentBuild { get; }

    /// <summary>True until this Windows build has been seen working: the first problem trips the failsafe.</summary>
    public bool InProbation
    {
        get { lock (_gate) return _state.VerifiedBuild != CurrentBuild; }
    }

    /// <summary>True when Windows was updated since the replacement was last verified (as opposed to a first run).</summary>
    public bool WindowsChangedSinceVerified
    {
        get { lock (_gate) return _state.VerifiedBuild.Length > 0 && _state.VerifiedBuild != CurrentBuild; }
    }

    public bool IsTripped
    {
        get { lock (_gate) return _state.TrippedReason.Length > 0; }
    }

    public string TrippedReason
    {
        get { lock (_gate) return _state.TrippedReason; }
    }

    public string VerifiedBuild
    {
        get { lock (_gate) return _state.VerifiedBuild; }
    }

    public string TrippedAtBuild
    {
        get { lock (_gate) return _state.TrippedAtBuild; }
    }

    /// <summary>The replacement did its job: the Windows menu stayed closed and Apparatus' opened.</summary>
    public void RecordCleanUse()
    {
        lock (_gate)
        {
            _state.Failures = 0;
            if (_state.VerifiedBuild == CurrentBuild) return;
            if (++_cleanUses >= CleanUsesToVerify)
            {
                _state.VerifiedBuild = CurrentBuild;
                _cleanUses = 0;
                Save();
            }
        }
    }

    public FailsafeVerdict RecordProblem(StartProblem problem)
    {
        lock (_gate)
        {
            _cleanUses = 0;
            _state.Failures++;
            bool strict = _state.VerifiedBuild != CurrentBuild;
            if (!strict && _state.Failures < FailuresToTrip)
            {
                Save();
                return FailsafeVerdict.Continue;
            }

            _state.TrippedReason = Describe(problem, strict);
            _state.TrippedAtBuild = CurrentBuild;
            _state.TrippedUtc = DateTime.UtcNow;
            Save();
            return new FailsafeVerdict(true, _state.TrippedReason);
        }
    }

    /// <summary>The user switched the replacement on again: forget the trip and start counting afresh (the next uses are scrutinised again).</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _state.TrippedReason = "";
            _state.TrippedAtBuild = "";
            _state.TrippedUtc = null;
            _state.Failures = 0;
            _state.VerifiedBuild = "";   // prove it again
            _cleanUses = 0;
            Save();
        }
    }

    private string Describe(StartProblem problem, bool afterWindowChange) => (problem, afterWindowChange) switch
    {
        (StartProblem.OfficialAlsoOpened, true) =>
            $"o Windows ({CurrentBuild}) abriu o menu dele junto com o do Apparatus. É comum depois de uma atualização do Windows.",
        (StartProblem.OfficialAlsoOpened, false) =>
            "o menu do Windows abriu junto com o do Apparatus mais de uma vez seguida.",
        (StartProblem.MenuDidNotAppear, true) =>
            $"o menu do Apparatus não apareceu depois de uma atualização do Windows ({CurrentBuild}).",
        _ => "o menu do Apparatus deixou de aparecer mais de uma vez seguida.",
    };

    private FailsafeState Load()
    {
        try
        {
            if (File.Exists(_path)) return JsonSerializer.Deserialize<FailsafeState>(File.ReadAllText(_path), Json) ?? new FailsafeState();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            AppLog.Warn($"Failsafe state unreadable: {ex.Message}");
        }
        return new FailsafeState();
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
            string temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_state, Json));
            File.Move(temp, _path, overwrite: true);
        }
        catch (IOException ex)
        {
            AppLog.Warn($"Saving the failsafe state failed: {ex.Message}");
        }
    }
}
