namespace PGPM.Config;

/// <summary>A score configuration's problem; <c>Input</c> is the input (or cap key) it is about.</summary>
public sealed record ScoreConfigError(string Code, string? Input = null);

/// <summary>The codes of <see cref="ScoreConfigError"/>, the same as score.ts's.</summary>
public static class ScoreErrorCodes
{
    public const string NotAScore = "not_a_score";
    public const string InvalidOp = "invalid_op";
    public const string InvalidNa = "invalid_na";
    public const string InvalidDecimals = "invalid_decimals";
    public const string NoInputs = "no_inputs";
    public const string DuplicateInput = "duplicate_input";
    public const string UnknownInput = "unknown_input";
    public const string ArchivedInput = "archived_input";
    public const string NotNumeric = "not_numeric";
    public const string OptionsWithoutPoints = "options_without_points";
    public const string InvalidCap = "invalid_cap";
    public const string Cycle = "cycle";
    public const string MaxRequired = "max_required";
    public const string InputMaxRequired = "input_max_required";
}

/// <summary>
/// Validation of a score field's configuration on save (docs/03 §16), as score.ts's <c>validateScoreConfig</c>: inputs
/// exist, are not archived, have numeric meaning, no cycles; rescale (of a sum) and normalize need maxima.
/// </summary>
public static class ScoreConfigValidator
{
    /// <summary>
    /// The problems of <paramref name="score"/> among the stored <paramref name="allFields"/>, where it replaces the
    /// definition with its key. None: an empty list.
    /// </summary>
    public static IReadOnlyList<ScoreConfigError> Validate(FieldSpec score, IEnumerable<FieldSpec> allFields)
    {
        if (score.Type != "score")
        {
            return [new ScoreConfigError(ScoreErrorCodes.NotAScore)];
        }
        var byKey = new Dictionary<string, FieldSpec>();
        foreach (var field in allFields)
        {
            byKey[field.Key] = field;
        }
        byKey[score.Key] = score;

        var c = score.Settings;
        var errors = new List<ScoreConfigError>();
        if (c.Op is null || !ScoreEvaluator.Ops.Contains(c.Op))
        {
            errors.Add(new ScoreConfigError(ScoreErrorCodes.InvalidOp));
        }
        if (c.Na is not null && !ScoreEvaluator.NaModes.Contains(c.Na))
        {
            errors.Add(new ScoreConfigError(ScoreErrorCodes.InvalidNa));
        }
        if (c.Decimals is { } decimals && !ScoreEvaluator.IsValidDecimals(decimals))
        {
            errors.Add(new ScoreConfigError(ScoreErrorCodes.InvalidDecimals));
        }
        var inputs = c.Inputs?.Where(k => k is not null).ToList() ?? [];
        if (inputs.Count == 0)
        {
            errors.Add(new ScoreConfigError(ScoreErrorCodes.NoInputs));
        }
        var seen = new List<string>();
        foreach (var key in inputs)
        {
            if (!seen.Contains(key))
            {
                seen.Add(key);
            }
            else if (!errors.Contains(new ScoreConfigError(ScoreErrorCodes.DuplicateInput, key)))
            {
                errors.Add(new ScoreConfigError(ScoreErrorCodes.DuplicateInput, key));
            }
        }
        var cap = c.Cap ?? new Dictionary<string, decimal>();
        foreach (var key in cap.Keys.Where(k => !seen.Contains(k)))
        {
            errors.Add(new ScoreConfigError(ScoreErrorCodes.InvalidCap, key));
        }

        var usable = new List<FieldSpec>();
        foreach (var key in seen)
        {
            if (!byKey.TryGetValue(key, out var field))
            {
                errors.Add(new ScoreConfigError(ScoreErrorCodes.UnknownInput, key));
            }
            else if (field.IsArchived)
            {
                errors.Add(new ScoreConfigError(ScoreErrorCodes.ArchivedInput, key));
            }
            else if (!ScoreEvaluator.NumericTypes.Contains(field.Type))
            {
                errors.Add(new ScoreConfigError(ScoreErrorCodes.NotNumeric, key));
            }
            else if (NeedsPoints(field))
            {
                errors.Add(new ScoreConfigError(ScoreErrorCodes.OptionsWithoutPoints, key));
            }
            else if (field.Type == "score" && ReachesCycle(field, score.Key, byKey, []))
            {
                errors.Add(new ScoreConfigError(ScoreErrorCodes.Cycle, key));
            }
            else
            {
                usable.Add(field);
            }
        }

        var hasMax = c.Max is > 0;
        var rescales = (c.Na ?? "ignore") == "rescale" && c.Op == "sum";
        if ((rescales || c.Normalize == true) && !hasMax)
        {
            errors.Add(new ScoreConfigError(ScoreErrorCodes.MaxRequired));
        }
        if (rescales)
        {
            foreach (var field in usable.Where(f => ScoreEvaluator.InputMax(f) is null && !cap.ContainsKey(f.Key)))
            {
                errors.Add(new ScoreConfigError(ScoreErrorCodes.InputMaxRequired, field.Key));
            }
        }
        return errors;
    }

    /// <summary>A select (or a multiselect summing points) whose options in use don't all carry points.</summary>
    private static bool NeedsPoints(FieldSpec field)
    {
        var c = field.Settings;
        if (field.Type != "select" && !(field.Type == "multiselect" && c.MultiPoints == "sum"))
        {
            return false;
        }
        return (c.Options ?? []).Any(o => !o.Archived && o.Points is null);
    }

    /// <summary>True when evaluating <paramref name="field"/> leads back to <paramref name="root"/> or into another cycle.</summary>
    private static bool ReachesCycle(FieldSpec field, string root, Dictionary<string, FieldSpec> byKey, HashSet<string> path)
    {
        if (field.Key == root || !path.Add(field.Key))
        {
            return true;
        }
        try
        {
            foreach (var key in field.Settings.Inputs ?? [])
            {
                if (key is not null && byKey.TryGetValue(key, out var input) && input.Type == "score"
                    && ReachesCycle(input, root, byKey, path))
                {
                    return true;
                }
            }
            return false;
        }
        finally
        {
            path.Remove(field.Key);
        }
    }
}
