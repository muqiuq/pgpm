namespace PGPM.Mcp;

/// <summary>The MCP server's settings (docs/03 §7, §14), from <c>Mcp__…</c>.</summary>
public sealed class McpOptions
{
    public const string Section = "Mcp";

    /// <summary>The server at <c>/mcp</c> and the MCP card on Account → Security.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How long the write token stays on once switched on.</summary>
    public int WriteHours { get; set; } = 12;

    /// <summary>The read token stays on until this many days after its user was last online in the web UI.</summary>
    public int ReadDaysAfterOnline { get; set; } = 7;

    /// <summary>The write token needs a second factor: TOTP, or a linked Microsoft account (Entra's MFA, docs/03 §15 #12).</summary>
    public bool WriteRequiresTotp { get; set; } = true;

    /// <summary>The MCP log keeps the newest this many entries (at least 100).</summary>
    public int LogEntries { get; set; } = 10_000;

    /// <summary>A tool's result is shortened to this many characters.</summary>
    public int MaxResultChars { get; set; } = 40_000;

    public int CallsPerMinute { get; set; } = 120;

    public int WritesPerMinute { get; set; } = 20;

    /// <summary>This many refused calls within 24 hours block a token (docs/03 §7.1).</summary>
    public int StrikesToBlock { get; set; } = 3;
}
