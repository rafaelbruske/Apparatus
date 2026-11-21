using Apparatus.Core.Input;

namespace Apparatus.Core.Actions;

/// <summary>A hotkey as the router sees it: which action, which keys, and where it applies.</summary>
public sealed record HotkeyBinding(long ActionId, HotkeySpec Spec, ScopeMode Scope, IReadOnlyList<string> Apps)
{
    /// <summary>"OnlyIn" beats "ExceptIn" beats "Everywhere" when several bindings share the same keys.</summary>
    public int Specificity => Scope switch
    {
        ScopeMode.OnlyIn => 2,
        ScopeMode.ExceptIn => 1,
        _ => 0,
    };

    public bool AppliesTo(string? process)
    {
        string p = NormalizeProcess(process);
        return Scope switch
        {
            ScopeMode.Everywhere => true,
            ScopeMode.OnlyIn => Apps.Any(a => NormalizeProcess(a).Equals(p, StringComparison.OrdinalIgnoreCase)),
            ScopeMode.ExceptIn => !Apps.Any(a => NormalizeProcess(a).Equals(p, StringComparison.OrdinalIgnoreCase)),
            _ => false,
        };
    }

    public static string NormalizeProcess(string? name)
    {
        name = (name ?? "").Trim();
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }

    public static HotkeyBinding? From(ActionDefinition action)
    {
        if (!action.Enabled || !HotkeySpec.TryParse(action.Hotkey, out var spec)) return null;
        return new HotkeyBinding(action.Id, spec, action.Scope, action.ScopeApps.Select(NormalizeProcess).Where(a => a.Length > 0).ToList());
    }
}

/// <summary>
/// Decides which hotkeys are armed for the application in front. A Windows hotkey consumes its keys, so a
/// scoped one has to be registered only while its application is active; that is what makes the same keys
/// do different things in different programs (and pass through untouched everywhere else).
/// </summary>
public static class HotkeyPlan
{
    /// <summary>For each key combination, the single binding that wins while <paramref name="foregroundProcess"/> has the focus.</summary>
    public static IReadOnlyList<HotkeyBinding> ActiveFor(IEnumerable<HotkeyBinding> all, string? foregroundProcess) =>
        all.Where(b => b.AppliesTo(foregroundProcess))
           .GroupBy(b => b.Spec)
           .Select(g => g.OrderByDescending(b => b.Specificity).ThenBy(b => b.ActionId).First())
           .ToList();

    /// <summary>
    /// The binding that would fight with <paramref name="candidate"/>, or null. Two bindings only conflict when
    /// they use the same keys, are equally specific and can be active in the same application: a global hotkey and
    /// an app-specific one with the same keys coexist, the specific one simply wins in its app.
    /// </summary>
    public static HotkeyBinding? FindConflict(HotkeyBinding candidate, IEnumerable<HotkeyBinding> others)
    {
        foreach (var other in others)
        {
            if (other.ActionId == candidate.ActionId || other.Spec != candidate.Spec) continue;
            if (other.Specificity != candidate.Specificity) continue;
            if (CanOverlap(candidate, other)) return other;
        }
        return null;
    }

    private static bool CanOverlap(HotkeyBinding a, HotkeyBinding b)
    {
        switch (a.Specificity)
        {
            case 0: return true;                                   // both global
            case 1: return true;                                   // "everywhere except…" lists always share almost every app
            default:                                               // both OnlyIn: they overlap only on a shared app
                return a.Apps.Any(x => b.Apps.Any(y => HotkeyBinding.NormalizeProcess(x).Equals(HotkeyBinding.NormalizeProcess(y), StringComparison.OrdinalIgnoreCase)));
        }
    }
}
